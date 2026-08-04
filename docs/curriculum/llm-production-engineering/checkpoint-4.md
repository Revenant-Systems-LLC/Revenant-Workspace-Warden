# Checkpoint 4 — Days 16–21

**Covers:** evals, observability, cost attribution, safety engineering,
multi-tenant isolation, full-stack tradeoffs.

**Rules:** 75 minutes, closed book. Heavily cumulative — this is the last gate
before the capstone, and it deliberately mixes every week.

**Passing:** 70/100.

---

## Section A — Recall (20 points, 2 each)

**A1.** Name the five eval layers.

**A2.** Why must a judge come from a different model family than the system under
test?

**A3.** What is the noise floor and how do you measure it?

**A4.** Name six attributes that belong on every LLM call span.

**A5.** Why must `user_id` never be a metric label?

**A6.** Why is cost per outcome different from cost per call?

**A7.** State the central rule of safety engineering.

**A8.** Why can't prompt injection be solved at the model layer?

**A9.** State the diagnostic question of multi-tenant isolation.

**A10.** Name four "free lunch" changes from Day 21.

---

## Section B — Calculation (30 points)

**B1. (10 pts)** An eval suite has 220 general cases (0.87), 40 structured-output
cases (0.93), and 40 long-context cases (0.81). A model upgrade moves them to
0.89 / 0.64 / 0.83.

 (a) Weighted mean before and after.
 (b) Would a 2% overall gate catch the regression?
 (c) What gate would, and what value?
 (d) Which Day 7 concept predicted exactly this pattern?

**B2. (10 pts)** 4M requests/day at $0.004 base cost. Repair rate 7% with 1.3
extra calls at $0.005. Fallback escalation 3% at $0.016. 12% of total spend goes
to journeys with no completed outcome.

 (a) Base daily cost.
 (b) Repair and fallback cost.
 (c) Total, and the failure-and-waste fraction.
 (d) Which two attribution dimensions make (c) visible, and what would you see
     without them?

**B3. (10 pts)** A multi-tenant system with 600 tenants and 5M requests/day.
Unscoped semantic cache hit rate 32%; tenant-and-permission-scoped hit rate 11%.
Saving per hit $0.007.

 (a) Annual cost of scoping the cache.
 (b) Give the two-part argument for scoping anyway.
 (c) Which caching mechanism is *unaffected* by tenant scoping, and under what
     condition?

---

## Section C — Diagnosis (30 points, 10 each)

Cause, confirming signal, structural fix.

**C1.** A team's nightly eval has reported 0.91 ± 0.01 for three months. In that
time: two model upgrades, a caching deploy, and a 4× corpus growth. Users report
declining quality. The eval has 45 cases and reports one overall number.

**C2.** An agent product's inference bill went from $52k to $147k over six weeks.
Cost is tracked by model. Per-request token counts look flat. The team shipped 23
PRs in that window and cannot identify which one caused it.

**C3.** A support engineer, debugging a complaint, searched the trace store by
keyword and read prompts from a different customer containing personal health
information. The trace store was built four months ago as part of an
observability project.

---

## Section D — Design (20 points)

**D1. (20 pts)** You inherit an LLM feature with these properties:

- 900k requests/day, $46k/month, p95 E2E 6.2s, no streaming.
- Quality: 71% acceptable, measured by a 30-case eval reporting one number.
- Reliability: 96.8% reach a successful terminal state.
- Architecture: one large model, RAG over a shared index with a post-retrieval
  tenant filter, unconstrained JSON output with a `try/catch`, no budgets on the
  agent path, fixed 3× retry.
- 200 tenants; three of them are 41% of the cost.
- Telemetry: request logs with status codes and durations.

Produce:
 (a) The three things you fix in week one, and why those three.
 (b) Your diagnosis of the binding constraint, the measurement that confirms it,
     and your predicted answer.
 (c) The cost sequence to get under $20k/month, per Day 21's free-lunch-first
     rule, naming what each step trades.
 (d) The eval redesign: size, strata, gates, and why 30 cases reporting one
     number is worse than no eval at all.
 (e) One thing in this system that is a live compliance risk, and the specific
     line of code that would fix it.

---

## Answer key

### Section A

**A1.** Assertions, golden set, regression, adversarial, LLM-as-judge, human.
(Any five of these six; assertions + golden + regression + adversarial + judge +
human is the full list.)
**A2.** A same-family judge shares the generator's blind spots, factual errors,
and stylistic preferences, so it systematically approves exactly the outputs that
are confidently wrong — the eval measures self-consistency, not quality.
**A3.** The run-to-run score difference from sampling and generation variance
alone; measured by running the identical suite twice against the identical
system. Thresholds below it fire on noise and the suite gets ignored.
**A4.** Six of: `gen_ai.response.model`, input/output tokens, cache read/write
tokens, `prompt_prefix_hash`, `ttft_ms`, `finish_reason`, `attempt`, route tier +
degraded flag, `prompt_template_version`, `cost_usd`.
**A5.** Each distinct label value creates a time series; per-user labels produce
millions of series, exploding storage, query time, and cost. IDs belong on trace
attributes, not metric labels.
**A6.** Cost per call ignores how many calls a completed business outcome
requires and ignores downstream costs (escalations, human handling). A cheaper
model that needs more turns and escalates more can be far more expensive per
outcome while looking cheaper per call.
**A7.** The model is inside the trust boundary of nothing — treat everything it
reads and everything it emits as untrusted, and put authorization on the
deterministic side of every action.
**A8.** System instructions, user input, retrieved documents, and tool results all
arrive as one undifferentiated token sequence; there is no structural channel
separating instruction from data, so "which is which" is a statistical inference,
not a guarantee.
**A9.** "Name the specific channel through which tenant A's data could reach
tenant B in your system."
**A10.** Four of: context engineering, prefix caching, reranking, continuous
batching / paged attention, speculative decoding at low batch, streaming.

### Section B

**B1.**
 (a) Before: `(220×0.87 + 40×0.93 + 40×0.81)/300 = (191.4 + 37.2 + 32.4)/300 =
 0.870`. After: `(220×0.89 + 40×0.64 + 40×0.83)/300 = (195.8 + 25.6 + 33.2)/300 =
 0.848`.
 (b) Drop is 2.2 points — **marginally yes**, but only because structured output
 is 13% of the suite. At 5% of the suite it would be missed entirely.
 (c) A **per-category gate at 5%** — structured output fell 29 points and would
 be caught decisively regardless of its share of the suite.
 (d) Day 7's **damage profile**: format adherence degrades early and sharply while
 aggregate accuracy barely moves. (Any model or precision change producing this
 signature.)

**B2.**
 (a) `4,000,000 × $0.004 = $16,000/day`.
 (b) Repairs: `4,000,000 × 0.07 × 1.3 × $0.005 = $1,820`. Fallbacks:
 `4,000,000 × 0.03 × $0.016 = $1,920`. **$3,740/day.**
 (c) Total `$19,740/day`. Waste on abandoned journeys = `0.12 × $19,740 =
 $2,369`. Failure and waste = `$3,740 + $2,369 = $6,109` of `$19,740` =
 **~31%**, ≈$2.23M/year.
 (d) **`attempt_type`** (original/repair/fallback/retry) and **outcome**
 (completed/abandoned/failed). Without them you'd see a flat cost per request and
 a slowly rising total, with no way to attribute either — the classic "costs went
 up, don't know why."

**B3.**
 (a) `5,000,000 × (0.32 − 0.11) = 1,050,000` fewer hits/day × $0.007 =
 **$7,350/day = $2.68M/year**.
 (b) **Part one:** that figure is not a saving forgone, it's a premium charged
 against unbounded cross-tenant disclosure liability across 600 tenants — one
 incident brings breach notification, regulatory exposure, contractual penalties,
 and churn that dwarfs it. **Part two:** the real tradeoff is smaller than the
 headline, because prompt/prefix caching is unaffected and retains most of the
 cost benefit (see (c)).
 (c) **Prompt/prefix caching**, provided the shared prefix is genuinely
 tenant-invariant — a globally-identical system prompt, tool schemas, and few-shot
 block can be cached across all tenants safely, with a second tenant-scoped
 boundary below it for tenant-specific instructions.

### Section C

**C1.** **Cause:** the eval is measuring nothing useful. 45 cases with one
aggregate number cannot detect anything smaller than a catastrophe (Day 15 §4.2 —
detecting a 3-point regression needs several hundred cases), a single number hides
per-category collapse, and the score's stability across two model upgrades and a
4× corpus growth is itself the tell: a live eval should have *moved*. It may also
be reading cached responses after the caching deploy.
**Signal:** rebuild as a stratified 300+ case set with per-category scores; check
`cache_hit` on eval traffic; compare `answer_in_context_rate` now vs. at launch.
**Fix:** Day 16 in full — assertion layer, stratified sized gold set,
per-category gates above a measured noise floor, cache bypass on eval runs,
scheduled runs with delta alerting, and every incident added as a permanent case.

**C2.** **Cause:** no cost attribution beyond model. "Per-request token counts
look flat" is the clue — the token count per *call* didn't change, but the number
of *calls* per request did (repairs, fallbacks, agent iterations), and those are
attributed to the same request. Alternatively, or additionally, a broken cache
prefix (Day 6) shifted cached input to full-price input, which also leaves token
counts flat while cost rises.
**Signal:** cost by `attempt_type`; calls-per-request over time; cache hit rate
and `prompt_prefix_hash` constancy.
**Fix:** full attribution dimensions (feature, workflow, tenant, outcome,
attempt type, cache status, environment); a cost regression gate in CI that fails
on cache-hit-rate drop and cost-per-outcome increase — which would have failed the
specific PR at review time rather than leaving 23 to bisect.

**C3.** **Cause:** the trace store was built without tenant scoping (Day 20 §3.6).
It is now the largest unrestricted copy of customer content in the company, and
this is a data-protection incident, not a process lapse — the engineer did nothing
wrong that the system prevented.
**Signal:** an access audit of the trace store; and going forward, audited
cross-tenant reads.
**Fix:** tenant-scoped trace access with elevated, logged permission for
cross-tenant search; redaction by default in the general tier; short content
retention with long metadata retention; and inclusion of the trace store in the
data-deletion pipeline. Treat the incident as reportable and scope the exposure
from the access logs — if those don't exist either, that is the second finding.

### Section D

**D1.** Grade on reasoning.

 (a) **Three week-one fixes:** (1) **Move the tenant filter into the retrieval
 query** — post-filtering means foreign content already entered the context and
 shaped answers; this is a live leak across 200 tenants. (2) **Enable
 streaming** — free, no risk, and it converts a 6.2s wait into a sub-second felt
 latency, which is the largest available UX improvement at zero cost. (3) **Add
 constrained decoding plus a capped repair loop and a typed terminal failure** —
 the unconstrained-JSON-with-`try/catch` path is almost certainly a large share of
 the 3.2% failure rate, and `catch → null` is silently corrupting downstream
 behaviour. Rationale: one live compliance risk, one free win, one reliability bug
 with a known cheap fix.

 (b) **Binding constraint: quality** (71% against a presumably higher target),
 with reliability close behind. **Measurement:** Day 15's 2×2 —
 `answer_in_context_rate` on a stratified gold set. **Predicted answer:** roughly
 0.6–0.7, because a shared index with post-filtering and (implied) dense-only
 retrieval is a weak retrieval architecture; retrieval, not generation, will be the
 ceiling. Confirming this redirects effort away from prompt and model work.

 (c) **$46k → under $20k, free lunches first:** (1) **Context engineering +
 reranking** — fewer, better chunks; trades nothing (quality should rise).
 (2) **Prefix caching** with a tenant-scoped second boundary; trades nothing.
 (3) **Fix the repair/failure rate** via constrained decoding; trades nothing.
 (4) **Cascade routing**, measured per successful outcome not per call; trades
 tail latency on escalations. (5) **INT8 quantization** with a per-category eval;
 trades ~1 quality point, with a rollback trigger. Steps 1–3 should reach most of
 the target alone.

 (d) **Eval redesign:** ~350–400 cases (Day 15 §4.2 — enough to detect a 4-point
 regression), stratified into head traffic, tail traffic, per-capability (JSON
 strictness, long context, each language), known past failures, high-consequence
 cases, and should-refuse cases. Gates: overall drop > 2%, any category drop > 5%,
 assertion layer 100%, all above a measured noise floor. **Why 30 cases with one
 number is worse than no eval:** it provides false assurance. Its noise floor is
 roughly ±8 points, so it can only detect catastrophes, and the single aggregate
 hides every category collapse — but the team believes they are covered and stops
 looking, which is strictly worse than knowing they are uninstrumented.

 (e) **Live compliance risk: the post-retrieval tenant filter.** The fix is
 moving the tenant predicate from the result-filtering step into the query itself
 — concretely, changing the retriever's public API so an unfiltered search cannot
 be expressed:
 ```csharp
 // before:  var hits = index.Search(query, k: 20).Where(c => c.TenantId == tenant);
 // after:   var hits = await retriever.SearchAsync(TenantId.FromSession(ctx),
 //                                                 ctx.Permissions, query, k: 20);
 ```
 with no `Search(query)` overload existing (Day 20 §3.2). Also acceptable as the
 answer: the trace/log store lacking tenant scoping, or the absence of a deletion
 path for retrieval and cache entries.

---

## Scoring

| Score | Read this as |
| --- | --- |
| 90–100 | You're ready for the capstone. Do it properly — 2 hours, written out. |
| 70–89 | Solid. Note the weak section and re-skim before the capstone. |
| 60–69 | Redo the weakest day's lab. The capstone touches all six of these days. |
| < 60 | Redo Days 16, 19, and 20 in full before attempting Day 22. |

Log the score, and compare it against your Checkpoint 1 score. That delta is the
course.
