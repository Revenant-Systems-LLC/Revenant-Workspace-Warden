# Day 18 — Cost Attribution: Per Feature, Workflow, Tenant, and User Journey

**Time:** ~3 h · **Prereqs:** Days 2, 6, 12, 17 · **Lab needs:** the Day 17 telemetry

---

## 0. Warm-up review

1. What must be on every trace root to make attribution possible? (Day 17)
2. Why must cost be computed at emit time rather than later? (Day 17)
3. What does a broken cache prefix cost, mechanically? (Day 6)
4. Name the five agent budgets. (Day 12)

---

## 1. The one-paragraph version

"We spent $180,000 on inference last month" is not a fact you can act on. The
actionable version is "the document-summarization feature spent $94,000, of which
$71,000 came from eleven enterprise tenants whose documents average 60,000 tokens,
and 40% of that was repair attempts." Cost attribution is the practice of tagging
every token with the **business dimensions** — feature, workflow, tenant, user
journey, request outcome — that let you find the answer. Per-model breakdowns are
nearly useless: they tell you *what* you bought, never *why*. The two things this
unlocks are the ability to say no to unprofitable usage patterns, and the ability
to find the small number of code paths responsible for a large fraction of the
bill — which, in every system I've seen the data for, exist.

---

## 2. Why this bites you

**The bill with no owner.** Inference spend triples over a quarter. Finance asks
why. Engineering can produce cost per model. Nobody can say which feature, which
customers, or which change caused it. The investigation takes three weeks and
concludes "usage grew." What actually happened: one feature shipped a change that
added retrieved context to a prompt that was previously static, breaking the cache
prefix (Day 6) — a single PR responsible for 60% of the increase, findable in ten
minutes with the right dimension.

**The unprofitable whale.** One enterprise tenant is 3% of revenue and 34% of
inference cost, because their workflow uploads 200-page documents and asks
follow-up questions that re-send the whole thing. Nobody knows, because cost isn't
tracked per tenant. The account is renewed at the same price.

**The invisible retry tax.** Repair attempts (Day 10), fallback escalations
(Day 13), and agent retries (Day 12) are all extra model calls. If they're
attributed to "the model" rather than to "failed first attempts," you see a
gradually rising cost per request and no explanation. In systems with an 8% repair
rate, this is routinely 15–25% of the bill.

---

## 3. Core concepts

### 3.1 The dimensions that matter

Per-model cost is the default and the least useful. Track these instead:

| Dimension | Question it answers | Who acts on it |
| --- | --- | --- |
| **Feature** | Which product surface costs what? | Product, eng |
| **Workflow** | Which multi-step process? (`onboarding`, `weekly_report`) | Product |
| **Tenant / account** | Which customers are profitable? | Finance, sales |
| **User journey** | Cost from first touch to completed outcome | Product |
| **Outcome** | Cost of successes vs. failures vs. abandonment | Eng |
| **Attempt type** | Original / repair / fallback / retry | Eng |
| **Cache status** | Hit / miss / partial | Eng |
| **Route tier** | Which model tier served it | Eng |
| **Environment** | prod / staging / eval / dev | Everyone |

That last one catches an embarrassing and common problem: **eval runs, load tests,
and development traffic billed against production with no way to separate them.**
Tag environment on day one.

**The two most valuable and least implemented:** *outcome* and *attempt type*.
Cost per *successful* outcome is the number that actually matters — if 30% of
your spend produces failed or abandoned interactions, that's the finding, and it's
invisible in every other view.

### 3.2 Unit economics: cost per outcome, not per call

Cost per API call is an engineering number. Cost per **completed business
outcome** is the number the business runs on.

```
cost_per_resolved_ticket = total_inference_cost / tickets_resolved_without_escalation
cost_per_document_processed = total_cost / documents_successfully_extracted
cost_per_active_user_month = total_cost / MAU
```

The difference is not cosmetic. Consider:

```
Feature A: $0.004/call, 5.1 calls per resolved ticket  → $0.020/resolution
Feature B: $0.011/call, 1.4 calls per resolved ticket  → $0.015/resolution
```

Per call, A looks 2.75× cheaper. Per outcome, **B is 25% cheaper** — because A's
cheap model needs more turns, more repairs, and more clarification. Optimizing
cost-per-call would push you toward the more expensive system. This is the single
most common way cost optimization backfires, and it connects directly to Day 13:
a cascade or a cheap-model route that raises the turn count can be a net loss.

**Always compute cost per outcome before making a routing or model decision.**

### 3.3 Implementation: how attribution actually works

The mechanics are simple once the Day 17 telemetry exists.

**Step 1 — carry a cost context through the request.**

```csharp
internal sealed record CostContext(
    string TenantId,
    string UserId,
    string Feature,        // "code_review", "tutor_chat"
    string Workflow,       // "pr_review_full", "lesson_walkthrough"
    string? JourneyId,     // stable across a multi-request journey
    string Environment,    // "prod" | "staging" | "eval" | "dev"
    string AttemptType);   // "original" | "repair" | "fallback" | "retry"
```

This travels with the request — as an AsyncLocal/ambient context, or explicitly.
**Explicit is better**: it makes it impossible to make a model call without
declaring who's paying.

**Step 2 — compute cost at emit time, with the price version.**

```csharp
internal sealed record TokenCost(
    decimal InputUsd, decimal CachedInputUsd, decimal OutputUsd,
    decimal TotalUsd, string PriceTableVersion);

internal static TokenCost Compute(string model, Usage u, PriceTable prices)
{
    var p = prices.For(model);                       // throws on unknown model
    var input  = (u.InputTokens - u.CacheReadTokens) / 1_000_000m * p.InputPerM;
    var cached =  u.CacheReadTokens                  / 1_000_000m * p.CachedInputPerM;
    var write  =  u.CacheWriteTokens                 / 1_000_000m * p.CacheWritePerM;
    var output =  u.OutputTokens                     / 1_000_000m * p.OutputPerM;
    return new(input, cached, output + write, input + cached + write + output,
               prices.Version);
}
```

Three details that matter:

- **`prices.For(model)` throws on an unknown model.** A new model silently costed
  at $0 is how attribution quietly becomes wrong.
- **Cache write tokens usually cost *more* than normal input.** Omitting them
  understates the cost of low-traffic endpoints where you write cache entries that
  expire before they're read.
- **Store the price table version.** When prices change, historical analysis must
  still reflect what you actually paid.

**Step 3 — emit a cost event per model call**, not per request, tagged with the
full `CostContext` plus model, route tier, cache status, and token counts.

**Step 4 — reconcile against the provider invoice, monthly.** Your computed total
should land within a couple of percent. A persistent gap means you're missing a
call path — usually background jobs, embeddings, evals, or a service someone
stood up outside the shared client. **Reconciliation is what makes the numbers
trustworthy**, and it's the step people skip.

### 3.4 The journey view

Single-request cost misses the shape of real usage. A "user journey" is a
sequence: a user uploads a document, asks four questions, regenerates one answer,
exports a summary.

```
journey_id: j_9182                              tenant=t_04  user=u_331
├─ upload_and_index                 $0.043   (embeddings, 180 chunks)
├─ question_1                       $0.021
├─ question_2                       $0.019
├─ question_3                       $0.024   (1 repair: +$0.006)
├─ regenerate_question_3            $0.024   ← signal: the answer was inadequate
├─ question_4                       $0.020
└─ export_summary                   $0.038
                                    ──────
                          journey:   $0.189   outcome: completed
```

What this view gives you that per-request cost cannot:

- **Regeneration is both a cost and a quality signal.** A journey with three
  regenerations cost double and probably disappointed the user. Correlate
  regeneration rate with cost and you find the features that are expensive
  *because* they're bad.
- **Abandoned journeys are pure loss.** Cost spent on journeys with no completed
  outcome is the cleanest waste metric you have.
- **Fixed costs amortize differently.** Indexing at $0.043 is fine over seven
  questions and terrible over one — which tells you whether to index eagerly or
  lazily, a decision you cannot make from request-level data.

### 3.5 Attribution for shared and fixed costs

Not every dollar is directly attributable:

| Cost | Nature | Allocation |
| --- | --- | --- |
| Per-request inference | Direct | Trivial — tag it |
| Embedding a shared corpus | Shared, one-time | Amortize over queries, or hold as a platform cost |
| Self-hosted GPU capacity | Fixed | Allocate by measured token share, not by request count |
| Eval and canary runs | Overhead | Its own bucket. **Never** attribute to tenants |
| Cache warming | Shared | Platform cost |
| Fine-tuning runs | Capital | Amortize over the model's serving life |

**For self-hosted infrastructure**, the honest allocation is by token share
weighted by phase, because prefill and decode consume different resources
(Day 3). A workload with huge prompts and tiny outputs consumes far more compute
per request than its token count suggests if you weight input and output equally.
A reasonable approximation: allocate by `input_tokens × w_in + output_tokens ×
w_out`, with `w_out` several times `w_in`, calibrated from your measured
throughput.

Simpler and often good enough: **allocate by GPU-seconds**, which you can measure
directly and which needs no weighting argument.

### 3.6 Turning attribution into action

Attribution that doesn't change behaviour is a report nobody reads. What to build
on top:

**Budgets and quotas per tenant.** Once cost per tenant is known, enforce it —
soft limits with warnings, hard limits with degraded mode (Day 13). This is the
same machinery as Day 12's agent budgets, one level up.

**Cost regression gates in CI.** Run a representative workload against a PR and
fail the build if cost per outcome rises more than a threshold. This catches the
§2 broken-prefix bug *before* merge rather than six weeks later. It is the single
highest-value thing in this lesson and almost nobody has it.

```yaml
- run: python cost/measure.py --suite representative --out cost.json
- run: python cost/gate.py cost.json baseline.json \
         --fail-if-cost-per-outcome-increase 0.10 \
         --fail-if-cache-hit-rate-drop 0.05 \
         --fail-if-tokens-per-request-increase 0.15
```

**Anomaly alerts.** Cost per request by feature, week over week. A 40% jump in one
feature is a deploy, a cache break, or a prompt change.

**Showback / chargeback.** Showing each team their spend changes behaviour on its
own, before anyone enforces anything.

**Pricing feedback.** If a tenant's cost-to-serve exceeds their contribution, that
is a commercial fact that should reach the people who set prices. Engineering
often holds this data and never routes it.

---

## 4. Napkin math

### 4.1 Finding the whale

500 tenants, $180,000/month total. Distribution is typically far from uniform:

```
Top 1% (5 tenants):       $61,000   (34%)
Next 9% (45 tenants):     $58,000   (32%)
Bottom 90% (450):         $61,000   (34%)
```

Now cross with revenue:

```
Tenant A: $2,400/mo revenue, $19,800/mo inference cost   → -$17,400  ← act
Tenant B: $40,000/mo revenue, $8,100/mo inference cost   → healthy
```

Tenant A is a specific, fixable situation: their workflow re-sends whole documents
(Day 2), or they're on the wrong tier, or the feature needs a per-tenant quota.
**None of this is visible without tenant-dimension attribution**, and the fix is
usually a conversation rather than an engineering project.

### 4.2 The failure tax

2M requests/day at $0.006 base = $12,000/day.

```
repair attempts (Day 10):    8% of requests × 1.3 extra calls × $0.007  = $1,456/day
fallback escalations (13):   3% × $0.018                                =   $1,080/day
agent retries (Day 12):      2% × 6 extra iterations × $0.004           =     $960/day
abandoned journeys:          11% of spend produces no completed outcome ≈ $1,590/day
                                                                          ─────────
                                                                          $5,086/day
```

**~30% of the bill is failure and waste** — $1.86M/year — and in a
model-dimension-only view it is completely invisible. Every line here maps to a
metric you already added on Days 10–13; attribution is what turns those rates into
dollars, and dollars are what get the work prioritized.

### 4.3 Cost per outcome flipping a decision

Proposal: route 60% of traffic to a small model, saving $0.008/call.

```
Per-call view:   1M × 0.6 × $0.008 = $4,800/day saved.   Ship it.
```

Then measure outcomes. The small model needs 1.9 calls per resolution instead of
1.2, and its escalation rate to human support rises 4 points:

```
extra calls:      1M × 0.6 × 0.7 × $0.004                = $1,680/day
extra escalations: 1M × 0.6 × 0.04 × $6 (human handling) = $144,000/day
```

**Catastrophically negative** — and the per-call dashboard shows a clean win the
whole time. The escalation term dominates everything, and it only appears if you
attribute cost to *outcomes* and count the downstream human cost as part of the
system's cost.

This is the argument for §3.2, and it's why cost work should never be signed off
on a per-call metric.

---

## 5. Failure modes

| Failure | Cause | Fix |
| --- | --- | --- |
| "Costs went up, don't know why" | Model-only dimension | Feature + workflow + attempt-type tags |
| Unprofitable tenant undetected | No tenant dimension | Tag `tenant_id` at trace root |
| Cost optimization made things worse | Optimized per call | Cost per outcome |
| Eval/load-test spend billed to prod | No environment tag | Tag environment |
| Computed cost ≠ invoice | Missing call paths | Monthly reconciliation |
| New model costed at $0 | Price lookup returns a default | Throw on unknown model |
| Historical costs shift after a price change | No price version stored | Store `price_table_version` |
| Repairs invisible in cost | Attributed to the parent request | `attempt_type` dimension |
| Cache-break PR found six weeks later | No cost gate in CI | Cost regression gate |
| Low-traffic endpoint costs more than expected | Cache *write* tokens ignored | Include write cost in the model |

---

## 6. Lab (85 min)

### Part A — Add the cost dimension to this repo (40 min)

Extend the Day 17 telemetry with real attribution.

Create `Telemetry/CostAttribution.cs`:

```csharp
namespace RevenantWorkspaceWarden.Telemetry;

internal sealed record PriceEntry(
    decimal InputPerM, decimal CachedInputPerM,
    decimal CacheWritePerM, decimal OutputPerM);

internal sealed class PriceTable
{
    public string Version { get; init; } = "2026-08-01";
    private readonly Dictionary<string, PriceEntry> _prices = new(StringComparer.Ordinal);

    public PriceEntry For(string model) =>
        _prices.TryGetValue(model, out var p) ? p
        : throw new InvalidOperationException(
            $"No price entry for model '{model}'. Add it before serving traffic — " +
            $"a missing entry silently costs the model at $0 and corrupts attribution.");
}

// TODO(you):
//  1. CostContext flowing through every provider call (explicit parameter).
//  2. Compute(model, usage, prices) per §3.3, including cache write cost.
//  3. Emit a cost event per model call with the full dimension set.
//  4. A CostReport aggregating by any dimension combination.
//  5. Journey tracking: a stable journey_id across related requests.
```

**Then make it impossible to skip:** change `ILLMProvider.ChatAsync` to require a
`CostContext` parameter. Every call site must now declare who's paying. That's a
small refactor that permanently prevents unattributed spend — the type system
doing the policy work.

Write tests:
1. An unknown model throws rather than costing $0.
2. Cache-read tokens are priced at the cached rate, not the input rate.
3. A repair attempt emits a separate cost event with `attempt_type="repair"`.
4. Two requests in the same journey share a `journey_id`.

### Part B — Build the reports (25 min)

```python
# lab-output/day18_report.py
import json, collections

def load(path): return [json.loads(l) for l in open(path, encoding="utf-8")]

def by(events, *dims):
    agg = collections.defaultdict(lambda: {"usd": 0.0, "calls": 0, "in": 0, "out": 0})
    for e in events:
        k = tuple(e[d] for d in dims)
        agg[k]["usd"]   += e["cost_usd"]
        agg[k]["calls"] += 1
        agg[k]["in"]    += e["input_tokens"]
        agg[k]["out"]   += e["output_tokens"]
    return dict(sorted(agg.items(), key=lambda kv: -kv[1]["usd"]))

def cost_per_outcome(events, outcomes):
    """outcomes: {journey_id: 'completed'|'abandoned'|'failed'}"""
    spend = collections.defaultdict(float)
    for e in events: spend[outcomes.get(e["journey_id"], "unknown")] += e["cost_usd"]
    completed = sum(1 for v in outcomes.values() if v == "completed")
    total = sum(spend.values())
    return {"spend_by_outcome": dict(spend),
            "cost_per_completed": total / max(completed, 1),
            "waste_fraction": 1 - spend["completed"] / max(total, 1e-9)}
```

Produce four reports from your (or synthetic) data:
1. Top 10 by feature.
2. Top 10 tenants, with cost per request and per completed outcome.
3. Spend by `attempt_type` — what fraction is repairs, fallbacks, retries?
4. Waste fraction: spend on journeys with no completed outcome.

Write the answer to this in `lab-notebook.md`: **which single number would you put
in front of a VP, and why that one?**

### Part C — The cost regression gate (20 min)

Build `cost/gate.py` per §3.6. Then prove it works:

1. Baseline a representative workload.
2. Introduce the Day 6 bug — interpolate a timestamp into the static prefix.
3. Confirm the gate fails, and that it fails on the **cache-hit-rate** check
   specifically, not just on total cost.

That specificity matters: a gate that says "cost went up 40%" starts an
investigation; a gate that says "cache hit rate fell from 0.91 to 0.02" ends one.

---

## 7. Quiz

**1.** Why is per-model cost breakdown insufficient? What does it fail to tell you?

**2.** Name six attribution dimensions and who acts on each.

**3.** Feature A: $0.003/call, 6.2 calls/resolution. Feature B: $0.014/call,
1.3 calls/resolution. Which is cheaper per outcome? By how much?

**4.** Why must a price lookup throw on an unknown model rather than default to
zero?

**5.** Why store a price table version with each cost event?

**6.** Give three things the journey view shows that per-request cost cannot.

**7.** Why does regeneration rate belong in a cost analysis?

**8.** How should fixed self-hosted GPU cost be allocated across tenants, and why
isn't request count adequate?

**9.** Compute the failure tax: 3M requests/day at $0.005 base; 6% repair rate
with 1.4 extra calls at $0.006; 4% fallback at $0.020; 9% of spend on abandoned
journeys. Total and percentage.

**10.** Describe the cost regression gate, and explain why the cache-hit-rate
check is more useful than the total-cost check.

---

## 8. Review hooks

- *Per-model cost tells you what you bought, never why.*
- *Cost per outcome, not cost per call. The per-call view flips decisions.*
- *Tag environment on day one, or eval spend hides in production.*
- *Throw on unknown models. A $0 model silently corrupts attribution.*
- *Reconcile against the invoice monthly, or the numbers aren't trustworthy.*
- *~30% of a typical bill is failure and waste, invisible without attempt-type tags.*
- *Make `CostContext` a required parameter. Let the type system enforce it.*

---

## 9. Answer key

**1.** It tells you which models you spent money on, which is an input you already
chose, not an explanation. It cannot tell you which feature, workflow, tenant, or
user journey drove the spend, what fraction went to failed attempts, or which
change caused an increase — so it supports no decision beyond "use a cheaper
model," which may be exactly the wrong move (§4.3).

**2.** Six of: **feature** (product/eng — which surface costs what); **workflow**
(product — which process); **tenant** (finance/sales — profitability);
**user journey** (product — cost to a completed outcome); **outcome** (eng —
success vs. failure vs. abandonment); **attempt type** (eng — repair/fallback/retry
tax); **cache status** (eng — cache health); **route tier** (eng — routing
effectiveness); **environment** (everyone — separating eval/dev from prod).

**3.** A: `$0.003 × 6.2 = $0.0186`/resolution. B: `$0.014 × 1.3 = $0.0182`/
resolution. **B is cheaper**, by about 2% — despite costing 4.7× more per call.

**4.** Because a default of zero silently produces a cost report that is wrong in
the most damaging direction: the new model appears free, so the feature using it
looks cheap and gets scaled up, and the discrepancy only surfaces at invoice
reconciliation weeks later. Throwing fails loudly at the moment a new model is
introduced, when adding a price entry costs one line.

**5.** Because provider prices change, and historical analysis must reflect what
you actually paid at the time. Without a version, recomputing or re-aggregating
old events with today's prices silently rewrites history — making a past
optimization look better or worse than it was, and breaking any trend line that
spans a price change.

**6.** Any three: regeneration events, which are simultaneously a cost and a
quality signal; abandoned journeys, which are pure waste and invisible per
request; amortization of fixed setup costs (indexing) across the number of
downstream requests, which determines whether to index eagerly or lazily; the
true cost of achieving one business outcome rather than one API response;
cross-request patterns like a user re-uploading the same document.

**7.** Because a regeneration is a second full-cost request that produced no
additional value — the user is telling you the first answer was inadequate. It
simultaneously inflates cost and marks a quality failure, so correlating
regeneration rate with cost identifies features that are expensive *because* they
are bad, which is a different (and more fixable) problem than features that are
inherently expensive.

**8.** By measured resource share — GPU-seconds, or token counts weighted by phase
(input and output weighted differently, since prefill is compute-bound and decode
is bandwidth-bound, per Day 3). Request count is inadequate because requests vary
enormously in resource consumption: a tenant sending 60,000-token prompts consumes
orders of magnitude more prefill compute per request than one sending 500-token
prompts, so per-request allocation systematically subsidizes heavy users at the
expense of light ones.

**9.**
```
base:      3,000,000 × $0.005                        = $15,000/day
repairs:   3,000,000 × 0.06 × 1.4 × $0.006           =  $1,512/day
fallbacks: 3,000,000 × 0.04 × $0.020                 =  $2,400/day
subtotal                                               $18,912/day
abandoned: 9% of $18,912                             =  $1,702/day (already inside)
```
Failure and waste = `$1,512 + $2,400 + $1,702 = $5,614` of `$18,912` =
**~29.7% of the bill**, roughly $2.05M/year.

**10.** A CI check that runs a representative workload against a pull request,
computes cost per outcome, tokens per request, and cache-hit rate, and fails the
build if any regresses past a threshold. **The cache-hit-rate check is more
useful** because it's diagnostic rather than merely detective: "total cost rose
40%" tells you something is wrong and starts an investigation, while "cache hit
rate fell from 0.91 to 0.02" identifies the exact mechanism and usually the exact
line — the prefix was broken — so the fix takes minutes instead of days.

---

## 10. Going deeper

- **FinOps Foundation framework** — the general discipline of cloud cost
  attribution: showback, chargeback, unit economics, and the inform/optimize/
  operate cycle. Predates LLMs; maps onto them directly.
- **"Cloud FinOps"** — Storment & Fuller. Chapters on allocation of shared costs
  and on unit economics are the ones worth reading for §3.5.
- **Your provider's usage and billing API** — pull it programmatically for the
  monthly reconciliation in §3.3 step 4. This is the step that makes everything
  else trustworthy.
- **OpenTelemetry GenAI conventions** (again) — the token usage attributes are the
  raw material; attribution is what you build on top.

---

**Next:** [Day 19 — Safety Engineering](day-19-safety-engineering.md). You know
what everything costs. Now make sure it can't be turned against you.
