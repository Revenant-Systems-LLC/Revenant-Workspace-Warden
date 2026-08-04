# Day 13 — Model Routing, Graceful Fallback, and Degraded-Mode UX

**Time:** ~3 h · **Prereqs:** Days 1, 7–9, 12 · **Lab needs:** 2+ providers in this repo

---

## 0. Warm-up review

1. Name the five agent budgets. (Day 12)
2. Why is "the model said it's done" not a terminal state? (Day 12)
3. Why does quantization break a confidence-gated threshold? (Day 7)
4. Which adaptation lever closes a cost gap rather than a capability gap? (Day 9)

---

## 1. The one-paragraph version

Not every request needs your best model, and your best model will not always be
available. **Routing** sends each request to the cheapest model that will handle
it acceptably — typically 50–80% cost reduction with quality within noise, when
the routing signal is good. **Fallback** is what happens when the chosen model is
rate-limited, erroring, or slow — a chain of alternatives with an explicit
terminal state, exactly like Day 10's repair chain. **Degraded-mode UX** is the
part everyone skips: what the user actually sees when the system is running on
its third choice. The unifying principle is that **degradation must be visible to
you and honest to the user** — a silent downgrade to a weaker model is a quality
regression that no dashboard will report, and a fallback that fabricates an
answer is worse than an error.

---

## 2. Why this bites you

**The silent downgrade.** A provider returns 529s during a regional incident.
The fallback quietly routes to a small model. Users get noticeably worse answers
for four hours. No alert fires, because the fallback "worked" — error rate is
zero. The incident is discovered from a support ticket, and reconstructing it is
impossible because the traces don't record which model served each request.

**The retry storm.** The primary gets rate-limited. Every request retries three
times with a fixed 1-second delay. The retries multiply load on an already
saturated provider, extending the outage and turning a 30-second blip into a
10-minute one. No jitter, no backoff, no circuit breaker.

**The router that costs more.** Someone builds a routing layer that calls a model
to classify difficulty, then calls the chosen model. The classifier costs
$0.0008; the saving from routing to the small model is $0.0006. The router is a
net loss and nobody notices because they measured "% routed to small model"
instead of total cost per request.

**The fabricated fallback.** All models fail; the fallback returns a canned
"Here's what I found: ..." template with empty content. The UI renders it as a
normal answer. Users act on nothing.

---

## 3. Core concepts

### 3.1 Routing is not one thing

Four distinct decisions get called "routing":

| Type | Question | Driven by |
| --- | --- | --- |
| **Capability routing** | Which model *can* do this? | Task type, modality, context length |
| **Cost routing** | What's the cheapest that's good enough? | Difficulty estimate |
| **Load routing** | Which instance/region has capacity? | Queue depth, latency |
| **Failure routing (fallback)** | What do I use now that this failed? | Error class |

They compose but should be implemented separately — mixing capability and
failure routing in one tangle of `if` statements is how you end up serving a
1B model for a task requiring vision.

### 3.2 The routing signal — how you decide

Ordered from cheapest and most reliable to most expensive:

**1. Deterministic features (free, use first).** Input length, presence of code,
detected language, requested output schema complexity, the feature/endpoint
itself, user tier, whether tools are needed. A surprising fraction of routing
value is available here at zero cost:

```csharp
ModelTier Route(Request r) =>
      r.RequiresVision                       ? ModelTier.Multimodal
    : r.InputTokens > 100_000                ? ModelTier.LongContext
    : r.Feature == Feature.Autocomplete      ? ModelTier.Small
    : r.Feature == Feature.SecurityReview    ? ModelTier.Large      // never downgrade
    : r.InputTokens < 400 && !r.NeedsTools   ? ModelTier.Small
    :                                          ModelTier.Default;
```

**2. A cheap classifier.** A small fine-tuned model or even a logistic regression
on embeddings, predicting "will the small model handle this?" Costs a fraction of
a cent. **Only viable if the classifier is much cheaper than the saving** — see
§2's failed router and §4.2.

**3. Cascade with a quality gate.** Run the small model; check the result; escalate
if it's inadequate. Escalation triggers: schema validation failure (Day 10), an
explicit low-confidence or `sufficient_information: false` field, a refusal, or a
cheap checker model's verdict.

```
small model → validate → good?  → return  (70-85% of traffic)
                       → bad?   → large model → return
```

Cascades are attractive because the gate is *observed behaviour* rather than a
prediction. The cost is latency on escalated requests — you pay for both calls.
Worth it when the small model handles the majority and the escalation gate is
cheap and accurate.

**4. Logprob/confidence thresholds.** Use with care. They're model-specific, they
shift with any precision change (Day 7 §3.6 item 6), and they measure fluency
more than correctness. If you use them, re-calibrate after **every** model or
quantization change, and never make them the only gate.

### 3.3 The rule that keeps routing safe

> **Route on the task, not on the user's phrasing — and maintain a
> never-downgrade list.**

Some request classes must always get the strong model regardless of how simple
they look: anything triggering an irreversible tool (Day 11), anything in a
regulated or safety-relevant path, anything a paying enterprise tier is
guaranteed, anything currently under an eval regression watch. This list should
be **explicit code**, not an emergent property of your difficulty classifier —
because "delete all customer records for tenant X" is a *short, simple-looking*
request, and a difficulty router will happily send it to the 1B model.

### 3.4 Fallback chains

Same structure as Day 10's repair chain: ordered alternatives, explicit terminal.

```
1. Primary model, primary region
2. Primary model, secondary region       ← regional outages are common
3. Equivalent model, different provider  ← requires prompt portability
4. Smaller/faster model, same provider   ← degraded quality, MUST be marked
5. Cached or precomputed response        ← if one is valid (Day 6)
6. Deterministic non-LLM path            ← keyword search, templated response
7. Explicit typed failure                ← honest terminal state
```

**Error classification decides which step you jump to** — this is the part most
implementations get wrong by treating all errors the same:

| Error | Retry same? | Next step | Backoff |
| --- | --- | --- | --- |
| 429 rate limit | Yes, later | 2 or 3 | Exponential + jitter, honour `Retry-After` |
| 529 / 503 overloaded | Yes, later | 2 | Exponential + jitter |
| 500 / transient | Yes, once | 2 | Short |
| Timeout | **Carefully** — the request may have succeeded | 2 | Idempotency matters (Day 11) |
| 400 bad request | **No** | Fix or fail | None — retrying is pure waste |
| 401 / 403 auth | **No** | Alert | None |
| Context length exceeded | **No** | Reduce context or use long-context tier | None |
| Content filter | **No** | Terminal, with a specific message | None |

**Never retry a 400.** It will fail identically every time, and retry logic that
doesn't classify errors turns a deterministic bug into a 3× deterministic bug
plus latency.

### 3.5 Retry mechanics: backoff, jitter, and the circuit breaker

**Exponential backoff with full jitter.** Fixed delays synchronize all your
clients into a thundering herd that arrives simultaneously at the recovering
service:

```csharp
// Full jitter: sleep uniformly in [0, base * 2^attempt], capped.
static TimeSpan Backoff(int attempt, Random rng, TimeSpan cap) {
    var window = Math.Min(cap.TotalMilliseconds, 250 * Math.Pow(2, attempt));
    return TimeSpan.FromMilliseconds(rng.NextDouble() * window);
}
```

Honour `Retry-After` when the provider sends it — it's better information than
your formula.

**Circuit breaker.** After N consecutive failures to a provider, stop calling it
for a cooldown window and route straight to the fallback. This protects both you
(no latency spent on calls that will fail) and them (no load on a struggling
service). Three states: closed (normal), open (fail fast to fallback), half-open
(let one probe through; success closes it).

Note what this repo currently does: `Providers/OllamaProvider.cs:28-52` retries
three times with a fixed 2-second delay and no jitter, no error classification,
and no breaker. That's today's lab.

**Budget the whole chain.** The user's patience is finite. If your SLA is 8
seconds, a chain that can spend 3 × 5s retries plus a fallback has already lost.
Give the *chain* a deadline, not just each attempt, and check remaining time
before each step:

```csharp
if (deadline - DateTimeOffset.UtcNow < EstimatedDurationOf(nextStep))
    return DegradedResult.OutOfTime(partial);
```

### 3.6 Degraded-mode UX — the neglected half

Four principles:

**1. Be honest, be specific, be brief.**

> ❌ "An error occurred."
> ❌ *(silently serve a worse answer)*
> ✅ "Our main model is unavailable right now, so this answer came from a faster
> backup and may be less detailed. [Retry with the full model]"

**2. Degrade the feature, not the product.** If citations are unavailable,
disable the citation UI rather than showing empty brackets. If the agent can't
use tools, say what it can still do.

**3. Preserve user work.** Never lose the user's input to a failure. Keep the
draft, keep the uploaded file, offer resume — this connects directly to Day 12's
resumable run state.

**4. Make recovery one click.** "Retry" should be visible and should route to the
primary if it's healthy again.

And the engineering requirement that makes all of it possible:

**Every response must carry which model, which route, and whether it was degraded
— through the trace and through to the UI layer.** Without that, you cannot alert
on degradation, cannot correlate quality complaints with routing decisions, and
cannot answer "what did the user actually get?" It is a two-field change that
becomes impossible to retrofit once fifty call sites exist. Add it on day one.

### 3.7 The metrics that make routing legible

| Metric | Why |
| --- | --- |
| `requests_by_model{feature,tier}` | The routing distribution — the basic picture |
| `degraded_responses_total{reason}` | **Alert on this.** Silent degradation is the failure mode |
| `escalation_rate{from,to}` | Cascade health; a rising rate means the small model is slipping |
| `router_overhead_cost` | Is the router paying for itself? (§2) |
| `quality_by_model{feature}` | Sampled evals per route — are the cheap routes actually fine? |
| `fallback_depth_histogram` | How deep is the chain going in practice? |
| `circuit_breaker_state{provider}` | Open breakers explain a lot of otherwise-confusing data |

The two that teams almost never have and most need: **`degraded_responses_total`**
and **`quality_by_model`**. Without the first you don't know you degraded; without
the second you don't know whether your cost routing quietly cost you quality.

---

## 4. Napkin math

### 4.1 Cost routing value

1M requests/day. Large model $0.012/request; small model $0.0015. Measurement
says 65% of traffic is handled acceptably by the small model.

```
All large:      1,000,000 × $0.012                      = $12,000/day
Routed:  650,000 × $0.0015 + 350,000 × $0.012           =  $5,175/day
```

**$6,825/day = $2.49M/year.** This is why routing is usually the *first* cost
lever, before quantization or distillation (Day 9 §3.8): no quality risk to the
requests that stay on the large model, no training project, and reversible with a
config change.

### 4.2 When a classifier router loses

Same numbers, but routing decided by a classifier call costing $0.0006:

```
Routed + classifier: $5,175 + (1,000,000 × $0.0006 = $600)    = $5,775/day
```

Still fine — $600 to save $6,825. But at a classifier cost of $0.008 (a mid-size
model doing the classification):

```
$5,175 + $8,000 = $13,175/day  → WORSE than not routing at all.
```

**The router must cost far less than the saving per routed request.** Rule of
thumb: classifier cost should be under ~10% of the gap between tiers. Better
still, use deterministic features (§3.2 step 1) or a cascade, both of which have
near-zero routing overhead.

### 4.3 Cascade economics

Small model $0.0015, large $0.012, escalation gate (schema validation) free.
Small model handles 78% acceptably.

```
All requests hit small:  1,000,000 × $0.0015          = $1,500
22% escalate to large:     220,000 × $0.012           = $2,640
                                                        ──────
                                                        $4,140/day
```

Cheaper than the classifier route ($5,175) **and** the gate is observed behaviour
rather than a prediction. The cost is latency: 22% of requests pay both calls.
If the small model takes 400ms and the large 1.8s, escalated requests see 2.2s.
Whether that's acceptable is an SLA question — and note it's a *tail* problem,
not a median one, so it looks fine on a p50 dashboard.

### 4.4 What silent degradation costs

Four-hour incident. 40,000 requests served by the fallback small model.
Suppose quality drops from 92% to 74% acceptable:

```
extra bad answers = 40,000 × (0.92 − 0.74) = 7,200
```

With `degraded_responses_total` alerting, you know at minute two, can surface an
honest notice, and can decide to fail loudly instead. Without it, you find out
from a support ticket on day three and cannot even confirm the cause. **The
metric is one counter.**

---

## 5. Failure modes

| Failure | Cause | Fix |
| --- | --- | --- |
| Quality dropped, no alert | Silent fallback | `degraded_responses_total` + response metadata |
| Retry storm extends an outage | Fixed delay, no jitter, no breaker | Exponential + full jitter + circuit breaker |
| 400s retried three times | No error classification | Classify; never retry non-retryable |
| Duplicate side effect after timeout retry | Timeout retried without idempotency | Idempotency key (Day 11) |
| Router costs more than it saves | Classifier too expensive | Deterministic features or cascade |
| Simple-looking destructive request routed to a weak model | Difficulty routing with no never-downgrade list | Explicit never-downgrade list |
| Chain exceeds the user's patience | Per-attempt timeouts, no chain deadline | Deadline for the whole chain |
| Fallback returns an empty template as an answer | No honest terminal state | Typed failure; UI renders it as failure |
| Can't reconstruct what a user got | Model/route not in the trace | Response metadata from day one |
| Cascade escalation rate climbing unnoticed | No escalation metric | `escalation_rate{from,to}` |

---

## 6. Lab (85 min)

### Part A — Fix this repo's retry logic (30 min)

`Providers/OllamaProvider.cs:28-52` currently retries three times with a fixed
2-second delay, no error classification, no jitter, no breaker. Replace it with a
shared, correct policy.

Create `Providers/ResiliencePolicy.cs`:

```csharp
namespace RevenantWorkspaceWarden.Providers;

internal enum ErrorClass { Retryable, RetryableAfterDelay, NotRetryable, Fatal }

internal sealed class ResiliencePolicy
{
    // TODO(you):
    //  1. Classify(HttpResponseMessage/Exception) -> ErrorClass per §3.4.
    //  2. Exponential backoff with FULL jitter; honour Retry-After.
    //  3. Circuit breaker per provider: closed/open/half-open, N failures to
    //     open, cooldown, single probe to close.
    //  4. A chain deadline: ExecuteAsync takes a DateTimeOffset deadline and
    //     refuses to start an attempt it cannot finish in time.
    //  5. Emit: attempts, error class per attempt, breaker state transitions.
}
```

Then apply it in `OllamaProvider` and `AnthropicProvider` so both share it.

Tests:
1. A 400 response results in **exactly one** attempt.
2. A 429 with `Retry-After: 5` waits at least 5 seconds (use a fake clock).
3. Five consecutive failures open the breaker; subsequent calls fail fast without
   an HTTP request.
4. Two clients failing simultaneously produce different sleep durations (jitter).
5. A call started 200ms before the deadline with a 3s estimated duration is not
   attempted.

### Part B — Build the router (35 min)

Create `Providers/ModelRouter.cs` sitting in front of `ILLMProvider`:

```csharp
internal sealed record RouteDecision(
    string ProviderName, string Model, ModelTier Tier,
    string Reason, bool IsDegraded);

internal sealed record RoutedResponse(
    string? Content, RouteDecision Route, int FallbackDepth, bool WasDegraded);

internal sealed class ModelRouter
{
    // TODO(you):
    //  1. Deterministic feature routing (§3.2 step 1).
    //  2. An explicit NeverDowngrade set — features/tools that always get the
    //     strong tier. Assert in a test that a request carrying an irreversible
    //     tool cannot be routed to the small tier.
    //  3. A fallback chain with error-class-driven step selection.
    //  4. RoutedResponse ALWAYS carries route + degraded flag, all the way out.
    //  5. Metrics from §3.7.
}
```

Wire `RoutedResponse.WasDegraded` through `IWardenHost` so `MainWindow` can show
a banner. That last step is the one that matters — it's the difference between
having degraded-mode UX and having a plan for it.

### Part C — Measure a cascade (20 min)

```python
# lab-output/day13_cascade.py
# Run 100 real prompts through llama3.2:1b, gate on schema validity + a
# `sufficient_information` flag, escalate failures to llama3.1:8b.
# Record: escalation rate, cost with/without cascade, added p50 and p95 latency.
```

Then compute the break-even: **at what escalation rate does the cascade stop
saving money?** (Answer form: when `small_cost + rate × large_cost ≥ large_cost`,
i.e. `rate ≥ 1 − small_cost/large_cost`.) Write your measured rate and the
break-even rate side by side — the margin between them is your safety headroom,
and if it's thin, the cascade is one model-quality drift away from being a loss.

---

## 7. Quiz

**1.** Name the four kinds of routing and what drives each.

**2.** Why should capability routing and failure routing be implemented
separately?

**3.** Give the routing safety rule and an example of a request that violates it
if you route purely on difficulty.

**4.** Classify these for retry behaviour: 429, 400, timeout, 529, context length
exceeded, 403. For each, say retry-or-not and why.

**5.** Why is full jitter necessary? What specifically goes wrong without it?

**6.** Describe the three circuit breaker states and the transitions.

**7.** Large $0.010, small $0.002, classifier $0.0015, 60% routable to small,
2M requests/day. Is the classifier router worth it? Show the arithmetic.

**8.** Same numbers as a cascade with a free gate and a 35% escalation rate.
Cost? Which wins, and what's the non-cost tradeoff?

**9.** Why is `degraded_responses_total` one of the most valuable single counters
in an LLM system?

**10.** Give the four degraded-mode UX principles and the one engineering
requirement that makes all of them possible.

---

## 8. Review hooks

- *Degradation must be visible to you and honest to the user.*
- *Route on the task, not the phrasing. Keep a never-downgrade list in code.*
- *Classify errors before retrying. Never retry a 400.*
- *Exponential backoff with full jitter, plus a circuit breaker.*
- *Give the whole chain a deadline, not just each attempt.*
- *The router must cost far less than what it saves.*
- *Every response carries its model, its route, and whether it was degraded.*

---

## 9. Answer key

**1.** **Capability** — which model can do this at all (modality, context length,
tool support). **Cost** — cheapest model that's good enough (difficulty signal).
**Load** — which instance or region has capacity (queue depth, latency).
**Failure/fallback** — what to use now that the chosen one failed (error class).

**2.** They answer different questions with different inputs and different failure
consequences. Mixing them produces a decision tree where a fallback path can
select a model that is *incapable* of the task — e.g. falling back from a vision
model to a text-only one during an outage. Keeping capability as a hard filter
applied first, and failure routing as a selection *within* the capable set,
makes that impossible by construction.

**3.** **Route on the task, not the user's phrasing, and maintain an explicit
never-downgrade list.** Violating example: "delete all records for tenant X" is
short, simple, and unambiguous, so a difficulty classifier scores it easy and
routes it to the weakest model — precisely the request where you want maximum
reliability, because it triggers an irreversible tool.

**4.** **429** — retry after a delay (honour `Retry-After`); the condition is
temporary. **400** — never; the request is malformed and will fail identically.
**Timeout** — retry carefully, only with an idempotency key, because the operation
may have succeeded server-side. **529/503** — retry with exponential backoff; the
service is overloaded and needs time. **Context length exceeded** — never; it's
deterministic, fix by reducing context or moving to a long-context tier.
**403** — never; retrying an auth failure wastes calls and should raise an alert.

**5.** Without jitter, every client that failed at the same moment sleeps the same
duration and retries at the same instant, producing a synchronized thundering herd
that re-saturates the recovering service and can extend or repeat the outage. Full
jitter spreads retries uniformly across the backoff window, smoothing the load
into a recovering service.

**6.** **Closed** — normal operation, calls pass through, failures counted.
**Open** — after N consecutive failures, calls fail fast to the fallback without
contacting the provider, for a cooldown period. **Half-open** — after cooldown, a
single probe call is allowed; success transitions to closed, failure returns to
open with a fresh cooldown.

**7.** All large: `2,000,000 × $0.010 = $20,000`. Routed:
`1,200,000 × $0.002 + 800,000 × $0.010 = $2,400 + $8,000 = $10,400`, plus
classifier `2,000,000 × $0.0015 = $3,000` → **$13,400/day**. Saving $6,600/day, so
**yes, worth it** — though the classifier consumes 31% of the gross saving, which
is high enough to prefer deterministic features or a cascade if either is viable.

**8.** Cascade: `2,000,000 × $0.002 = $4,000` (all hit small) plus
`700,000 × $0.010 = $7,000` (35% escalate) = **$11,000/day**. **The cascade
wins** by $2,400/day over the classifier router. **Non-cost tradeoff:** the 35% of
requests that escalate pay both calls, so their latency is the sum — a tail-latency
cost invisible on a p50 dashboard, and one that grows if the escalation rate
drifts up.

**9.** Because silent degradation is the failure mode that no other metric
catches: error rate is zero (the fallback succeeded), latency may even improve
(smaller model), and quality — the thing that actually got worse — is not measured
in real time. That counter is the only signal that distinguishes "healthy" from
"healthy-looking while serving worse answers," and it turns a support-ticket
discovery three days later into an alert in two minutes.

**10.** **Principles:** be honest, specific, and brief about what happened;
degrade the feature rather than the whole product; preserve the user's work and
input; make recovery one click. **Engineering requirement:** every response must
carry its model, route, and degraded flag through the trace and all the way to the
UI layer — without that metadata none of the four can be implemented, and it
cannot be retrofitted cheaply once many call sites exist.

---

## 10. Going deeper

- **"Exponential Backoff and Jitter"** — Marc Brooker, AWS Architecture Blog.
  Short, with the simulations. Full jitter wins.
- **Release It!, 2nd ed.** — Nygard. Circuit Breaker, Bulkhead, Timeout, Fail
  Fast, and Steady State. Chapters 4–5 are the source material for §3.5.
- **"RouteLLM: Learning to Route LLMs with Preference Data"** — Ong et al.
  (arXiv 2406.18665). Learned routers and how they're evaluated.
- **"FrugalGPT: How to Use Large Language Models While Reducing Cost and
  Improving Performance"** — Chen, Zaharia, Zou (arXiv 2305.05176). The cascade
  argument, with numbers.
- **"Site Reliability Engineering"** — Beyer et al., ch. 22 ("Addressing Cascading
  Failures"). Why retries make outages worse, in general, not just for LLMs.

---

**Next:** [Day 14 — RAG Architecture](day-14-rag-architecture.md). The biggest
subsystem in the course, and the most commonly built wrong.
