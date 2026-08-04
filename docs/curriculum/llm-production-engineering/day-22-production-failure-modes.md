# Day 22 — Production Failure Modes, and the Capstone

**Time:** ~4 h · **Prereqs:** all 21 days · **Lab needs:** everything you've built

---

## 0. Warm-up review — the whole course

No hints. From memory:

1. KV cache bytes per token — the formula.
2. Decode step time floor — the formula.
3. Speculative decoding expected tokens — the formula.
4. The three structured-output failure layers.
5. The six ordered layers of tool-argument validation.
6. The five agent budgets.
7. The diagnostic 2×2 for RAG.
8. The central rule of safety engineering.
9. The diagnostic question of multi-tenant isolation.
10. The five questions of the tradeoff decision framework.

Then find your Day 1 answer to *"When an LLM feature gives a wrong answer in
production, what is the most likely cause?"* and read it. Write today's answer
below it. The gap is the course.

---

## 1. The one-paragraph version

Production LLM systems fail in a small, recurring, recognizable set of ways, and
almost all of them share one property: **they are silent.** No exception, no 500,
no red dashboard — just a plausible answer that is wrong, a bill that grew, or a
metric that drifted. That silence is why every prior day insisted on a specific
piece of instrumentation, and today is where those pieces assemble into a
catalog: what each failure looks like, which signal detects it, and what
structurally prevents it. The organizing principle: **for every failure mode you
should be able to name a metric that fires before a user complains.** Where you
can't, you don't have a system — you have a demo that hasn't been caught yet.

---

## 2. The catalog

Each entry: what it looks like, why it happens, the detecting signal, and the
structural prevention.

### 2.1 Hallucinated tool calls

**Looks like:** `get_customer(id="CUST-84213")` → 404 → the agent tells the user
their account doesn't exist. Or an argument in the wrong units. Or a tool that
doesn't exist being called.

**Why:** the model generates plausible-shaped identifiers because a required
field demanded one and nothing verified it (Day 10 §3.2 rule 6, Day 11 §3.3).
Amplified by quantization damaging rare-token precision (Day 7 §3.6) and by too
many similar tools (Day 11 §4.3).

**Detects:** `tool_validation_failure_rate{tool, layer="referential"}`,
`hallucinated_id_rate`.

**Prevents:** referential integrity checks at the boundary; constrained decoding
with patterns and enums; a `sufficient_information` escape hatch in the schema;
tool descriptions that say "never construct this value yourself"; fewer,
better-separated tools.

### 2.2 Malformed JSON / schema violations

**Looks like:** parse failures at a few percent, or schema-valid output with a
semantically wrong field.

**Why:** unconstrained generation; output truncation misread as a format error
(Day 10 §4.3); a schema too deeply nested; a quantization change.

**Detects:** `schema_validation_failure_rate{layer}`,
`structured_output_repair_rate`, `finish_reason=length` counts.

**Prevents:** constrained decoding; flat schemas with enums and bounds; a
2-attempt repair loop with specific errors; **checking the stop reason before
repairing**; a typed terminal failure rather than `null`.

### 2.3 Stale retrieval

**Looks like:** confidently citing a policy that changed in March; citing a
deleted document.

**Why:** no date metadata, no validity filtering, no deletion path; both old and
new versions indexed with no way to adjudicate (Day 14 §3.6).

**Detects:** `stale_chunk_retrieval_rate`, corpus age distribution, and — the
one that catches it earliest — a scheduled retrieval eval with a temporal
stratum (Day 15 §3.3).

**Prevents:** dates on every chunk; validity filtering at retrieval (a control);
recency boosting and date-aware instructions (preferences); tombstones for
deletes; conflict detection between retrieved chunks.

### 2.4 Runaway agents

**Looks like:** 1,100 tool calls, 90 minutes, $340, for one question. Or an
oscillation between two failing approaches.

**Why:** no budgets, no progress detection, or the model treated as the exit
condition (Day 12).

**Detects:** `budget_exhaustion_rate{budget}`, iteration histograms, repeated
identical tool-call hashes, cost-per-run p99.

**Prevents:** all five budgets checked every step; progress detection with the
three-step escalation ladder; code-verified completion; hierarchical budgets
across agent trees; propagated cancellation.

### 2.5 Silent eval regression

**Looks like:** nothing. Quality declined; every dashboard is green; users notice
weeks later.

**Why:** no evals, or aggregate-only gates hiding a category, or a threshold below
the noise floor so alerts were ignored, or an eval reading cached responses, or a
frozen gold set measuring an obsolete distribution (Day 16 §3.8).

**Detects:** scheduled evals with per-category gates; canary evals hourly;
sampled judge scores on production traffic; leading indicators (repair rate,
tool validation rate, `answer_in_context_rate`).

**Prevents:** the whole of Day 16 — assertions first, stratified gold sets,
per-category gates, calibrated judges, and the rule that no incident closes
without a permanent regression case.

### 2.6 Silent model change

**Looks like:** behaviour shifts with no deploy on your side.

**Why:** a provider updated a model behind a stable alias.

**Detects:** `gen_ai.response.model` changes; canary eval score deltas.

**Prevents:** pin explicit versioned model IDs; alert on any response-model
change; hourly canary; re-baseline evals deliberately on every intentional
upgrade.

### 2.7 Silent degradation

**Looks like:** error rate zero, latency *improved*, answers worse.

**Why:** a fallback routed to a weaker model and nothing recorded it (Day 13 §2).

**Detects:** `degraded_response_rate{reason}` — one counter.

**Prevents:** route and degraded flags on every response, through the trace and
to the UI; honest degraded-mode UX; alert on the counter.

### 2.8 Cost explosion

**Looks like:** the bill doubled; nobody can say why.

**Why:** a broken cache prefix (Day 6 §2); repairs and fallbacks uncounted; an
agent's context growing unboundedly; eval traffic billed to production.

**Detects:** cache hit rate; tokens per request over time; cost by
`attempt_type`; cost by environment; a cost regression gate in CI.

**Prevents:** prefix-hash constancy test in CI; cost regression gate; full
attribution dimensions; `CostContext` as a required parameter.

### 2.9 Cross-tenant contamination

**Looks like:** a user sees another customer's data in an otherwise normal answer.

**Why:** cache key missing tenant or permission scope; post-filtered retrieval;
shared mutable state under concurrency (Day 20).

**Detects:** the concurrency soak test — *before* production. In production:
tenant-mismatch assertions in context assembly, and authorization denial logs.

**Prevents:** scoped-by-construction cache and retriever APIs; tenant assertions
in `verify()`; namespace-per-tenant indexes; no mutable statics in the request
path.

### 2.10 Prompt injection

**Looks like:** an agent exfiltrates data or takes an action after reading a
document; or emits an image URL that leaks data on render.

**Why:** untrusted content and privileged capability in one context (Day 19).

**Detects:** authorization denials; anomalous tool sequences; unusual outbound
destinations; the adversarial eval suite.

**Prevents:** request-scoped credentials; capability separation (never
read-untrusted plus send-outward together); two-stage structured handoff; URL
allowlisting in the renderer; confirmation gates on irreversible effects.

### 2.11 The context cliff

**Looks like:** the service was fine; a document-upload feature launched;
throughput fell off a cliff and p99 went to 90s.

**Why:** context length rose 8–13×, KV memory per sequence rose proportionally,
concurrency collapsed, batch size collapsed, and preemption thrashing started
(Day 4 §2).

**Detects:** KV utilization, batch occupancy, preemption rate, and the p99 of the
*context length distribution* — a capacity input, not a tail-latency detail.

**Prevents:** compute the KV formula before launching any feature that changes
context length; admission control; a separate queue or pool for long-context
requests; chunked prefill.

### 2.12 Optimizing the wrong dimension

**Looks like:** a quarter of latency work, no change in any business metric.

**Why:** no diagnosis of the binding constraint (Day 21 §3.4).

**Detects:** SLO burn rates — the dimension burning its budget fastest is the
constrained one.

**Prevents:** the five-question decision framework, and refusing to start work
that can't answer question 1 with a number.

---

## 3. The unifying properties

Read the catalog again and three things are true of nearly every entry:

**1. The failure is silent.** Twelve modes; three produce an error a
conventional monitoring stack would catch. This is the defining characteristic of
the field and the reason the quality metric family (Day 17 §3.4) exists.

**2. The prevention is structural, not behavioural.** Every "prevents" row is
code — a validator, a type, a key, an assertion, a budget, a test. None is
"be careful" or "tell the model not to." The determinism sandwich (Day 1) is the
whole course compressed into one sentence.

**3. The detection existed before the failure, or it didn't exist at all.**
You cannot retroactively instrument a past incident. Every metric in the catalog
has to be there beforehand — which is why "add telemetry later" is the decision
that makes systems permanently undebuggable.

### 3.1 The pre-launch checklist

Before any LLM feature reaches users:

```
CONTRACTS
□ Output schema validated (syntax, shape, AND semantics)
□ Repair loop capped; stop reason checked; terminal state is a typed error
□ Tool arguments validated: schema, referential, authz, policy, budget, idempotency
□ Irreversible tools have confirmation gates bound to signed parameters
□ Identity comes from the session; no tenant_id/user_id in any tool schema

BOUNDS
□ All five agent budgets, checked every step, hierarchical
□ Completion verified by code, not claimed by the model
□ Every terminal state has user-facing text
□ Context budget enforced with reserved output space

ISOLATION
□ Every cache key carries tenant + permission scope + model + template version
□ Retrieval filtered in the query, from session identity
□ Concurrency soak test passing
□ Trace store tenant-scoped and included in deletion

TELEMETRY
□ Traces with typed spans; TTFT and ITL separate; finish_reason recorded
□ response.model recorded, with an alert on change
□ Cost attributed by feature, tenant, workflow, outcome, attempt type, environment
□ degraded_response_rate; repair rate; tool validation rate; answer_in_context_rate

EVALS
□ Assertion layer green
□ Stratified gold set sized for the regression you need to detect
□ Per-category CI gates above the measured noise floor
□ Adversarial suite including injection and should-refuse cases
□ Judge calibrated (κ ≥ 0.6) on any dimension you gate on
□ Canary eval on a schedule

CAPACITY
□ KV math done for p99 context length, not the mean
□ Batch limit derived from the ITL SLA
□ Admission control set below the thrashing point

TRADEOFFS
□ SLOs written, one per dimension, with error budgets
□ Rollback trigger defined for every compression or routing decision
```

If more than a few boxes are empty, you know which day to reread.

---

## 4. Capstone

This is the exam. Budget 2 hours. Write it out properly — the value is in the
writing, not in reading the model answer.

### 4.1 The brief

> **Revenant Codex** — a code-review assistant sold to engineering organizations.
>
> **What it does.** A team connects a repository. On every pull request, Codex
> reviews the diff and posts comments. Engineers can also chat with it about the
> codebase, and it can open follow-up PRs with suggested fixes.
>
> **Scale.** 400 customer organizations. 180,000 PR reviews/day. 40,000 chat
> sessions/day. Diffs: p50 = 900 lines, p99 = 14,000 lines. Repository context
> retrieved per review: p50 = 6,000 tokens, p99 = 70,000 tokens.
>
> **Constraints.**
> - Review must post within 3 minutes of PR open (p95).
> - Chat must stream, first token within 1.5s (p95).
> - Customers include regulated industries; several contractually require that
>   their code never influences another customer's output.
> - Current spend: $310,000/month. Target: under $140,000.
> - Current quality: 62% of review comments rated useful; the team wants 80%.
> - Current reliability: 4.1% of reviews fail to post.
>
> **What exists today.** One large model, one prompt, unconstrained generation,
> `try/catch` around JSON parsing, a shared vector index with a `tenant_id`
> metadata field filtered after retrieval, no evals, request logs only, a fixed
> 3× retry with a 2-second delay, and a `static` conversation cache added last
> quarter "for performance."

### 4.2 Deliverables

**1. Failure triage (20 min).** Identify every failure mode from §2 that this
system currently has or will have. For each: the specific evidence in the brief,
the severity, and whether it's live now or latent.

**2. The binding constraint (10 min).** All four dimensions look constrained.
Which is *actually* binding, what measurement would you take first to confirm it,
and what's your predicted answer?

**3. Architecture (40 min).** Redesign, covering:
 - Serving and capacity: the KV math, batch sizing, and how you handle the p99
   context length without it destroying concurrency.
 - The retrieval pipeline, including isolation architecture and the reason.
 - The output contract and tool boundary for the PR-opening capability.
 - Agent bounds for the chat and fix-PR paths.
 - Routing and fallback, with the degraded-mode UX.
 - The safety architecture — specifically, how an injection in a code comment
   cannot cause a PR to be opened.

**4. The cost plan (20 min).** Get from $310k to under $140k. Sequence the levers
per Day 21 §3.1. For each: expected saving, dimension traded, and rollback
trigger. Show that you exhausted the free lunches before spending quality.

**5. The eval and telemetry plan (20 min).** What you instrument on day one; the
gold set composition and size; the CI gates; what alerts and at what thresholds.

**6. The 90-day sequence (10 min).** What ships in weeks 1–2, 3–6, 7–12, and why
that order.

---

## 5. Capstone model answer

Compare against yours. Where you differ, ask whether you had a reason.

### 5.1 Failure triage

| Mode | Evidence | Severity | Status |
| --- | --- | --- | --- |
| **Cross-tenant contamination** | `static` conversation cache + post-filtered retrieval + shared index, with contractual isolation requirements | **Critical** | **Live.** Two independent channels. |
| Malformed JSON | Unconstrained generation, `try/catch`, no repair | High | Live — likely a large share of the 4.1% failures |
| Hallucinated tool calls | PR-opening capability, no argument validation mentioned | **Critical** | Live — writes to customer repos |
| Prompt injection | Reads diffs and code comments (untrusted), can open PRs (privileged) | **Critical** | Live — the lethal combination |
| Silent eval regression | No evals at all | High | Live by definition |
| Silent degradation | Fixed 3× retry, no error classification, no degraded metric | High | Live |
| Retry storm | Fixed 2s delay, no jitter, no breaker | Medium | Live |
| Cost explosion | No attribution, one prompt, unconstrained context | High | Live — explains part of $310k |
| Context cliff | p99 context 70k vs. p50 6k, 11.7× | **Critical** | Latent — will fire under load |
| Runaway agents | Chat and fix-PR paths, no budgets mentioned | High | Live |
| Stale retrieval | Repository context; code changes constantly | High | Live |
| Optimizing wrong dimension | No SLOs, no diagnosis | Medium | Live |

**Four criticals.** Two of them (isolation, injection→PR) are the kind that end
contracts, and both are live today.

### 5.2 Binding constraint

**Reliability and isolation are binding before anything else**, because 4.1%
failure plus a live cross-tenant channel in a product sold to regulated
industries is an existential problem, not an optimization problem. Cost and
quality are second-order until the system is safe and it works.

Among the optimization dimensions, **quality is binding** (62% → 80%), and the
first measurement is Day 15's 2×2 on review comments: *was the relevant code
context retrieved?* **Predicted answer:** `answer_in_context_rate` around 0.6–0.7
— a dense-only shared index with post-filtering, over a corpus of code where
exact identifiers matter enormously (Day 14 §3.4), is close to a worst case for
retrieval. Quality is a retrieval problem here, and the fix also reduces cost.

### 5.3 Architecture

**Serving and capacity.** Assume a self-hosted large model; run the Day 4 math for
whatever shape it is. The decisive fact is the 11.7× context spread: a handful of
70k-token reviews consume the KV memory of dozens of median ones, collapsing batch
size and starting preemption thrashing (Day 4 §2, §4). Therefore:

- Separate queue and pool for reviews over ~20k tokens, so their memory pressure
  can't starve the median path.
- Chunked prefill enabled, to protect chat's ITL from long review prefills
  (Day 3 §3.6).
- Admission control (`max_num_seqs`) set from the *p99* context distribution with
  headroom, not the mean.
- Batch limit derived from chat's 1.5s TTFT SLA; reviews are batch-tolerant and
  get a separate, larger batch limit.
- Architecturally: **don't put a 14,000-line diff in one request.** Review
  per-file or per-hunk with a map-reduce pass, which caps context length,
  parallelizes, and improves comment specificity. This is the highest-layer fix
  and it removes the cliff rather than managing it.

**Retrieval.** Namespace-per-tenant index (Day 20 §3.4) — not shared-plus-filter,
because contractual isolation makes a logical-only boundary indefensible, and the
cost delta is small. Hybrid search with BM25 is mandatory: code retrieval is
dominated by exact identifiers (Day 14 §3.4). Structural chunking on function and
class boundaries with the file path and symbol name prepended (contextual
chunking, Day 14 §3.2). Cross-encoder reranker with a score threshold, so a review
with no relevant context says so instead of inventing. Freshness: index on every
push; tombstone deleted files; a chunk from a stale commit is a correctness bug,
not a staleness annoyance.

**Output contract and tool boundary.** Review comments are structured output:
constrained decoding to a schema with `file`, `line`, `severity` (enum),
`comment`, `suggested_fix` (nullable), and `confidence`. Semantic validation:
the file exists in the diff; the line number is within the changed hunks; any
quoted code appears verbatim in the diff (grounding, Day 15). Repair capped at 2;
terminal state is a typed failure that posts nothing rather than posting garbage.

`open_pull_request` is **irreversible and external-visible** (Day 11 §3.2):
idempotency key over `(review_id, repo, branch, patch_hash)`; confirmation gate —
in this product, a human approving the suggestion in the UI, with the approval
token carrying the signed patch. The agent may never open a PR from its own
judgment alone.

**Agent bounds.** Chat: 12 iterations, 90s, 150k tokens, $0.40, per-tool caps.
Fix-PR: 20 iterations, 300s, $1.50, and `open_pull_request` capped at 1.
Completion verified by code — for fix-PR, that means the patch applies cleanly and
the build passes, not the model saying it's done (Day 12 §3.2).

**Routing and fallback.** Deterministic routing first: diffs under 200 lines with
no security-sensitive paths → small model; everything touching auth, crypto,
payments, or migrations → **never-downgrade list** (Day 13 §3.3). Cascade for the
middle, gated on schema validity and the confidence field. Fallback chain with
error classification, exponential backoff with full jitter, and a circuit breaker
per provider. Degraded mode: the PR comment says which model produced it when
degraded, and offers a re-review — honest and specific.

**Safety.** The critical one. Diffs and code comments are **untrusted content**
(Day 19 §3.2) — anyone who can open a PR can inject. So:

```
Stage 1 (untrusted zone): reads the diff and retrieved code.
  Tools: read-only, tenant-scoped.
  Output: a validated ReviewFindings schema. No free text reaches stage 2.
Stage 2 (privileged zone): takes ReviewFindings ONLY.
  May post comments (bounded, validated against the diff).
  May PREPARE a patch — never open a PR without human approval.
```

An injection in a code comment can, at most, influence a validated field in
`ReviewFindings`, subject to the grounding and line-range checks. It cannot emit
free text into the privileged stage, and it cannot open a PR because that requires
a human-signed token. Additionally: strip or allowlist any URL in posted comments
(Day 19 §3.6), because a posted comment renders in GitHub.

**Isolation.** Remove the `static` cache — today, before anything else. All caches
keyed by `(tenant, permission_scope_hash, model, template_version, corpus_version,
environment)` with a scoped-by-construction API (Day 20 §3.2). Concurrency soak
test in CI with per-tenant markers.

### 5.4 Cost plan: $310k → under $140k

Free lunches first (Day 21 §3.1):

| Step | Lever | Saving/mo | Traded | Rollback trigger |
| --- | --- | --- | --- | --- |
| 1 | Prefix caching: stable system+tool block first, tenant block second | ~$55k | nothing | cache hit rate < 0.6 for 1h |
| 2 | Context engineering: rerank to 5–8 chunks; per-file review instead of whole-diff | ~$85k | nothing (quality should rise) | comment usefulness drops > 3 pts |
| 3 | Fix the repair/failure rate via constrained decoding | ~$18k | nothing | repair rate > 3% |
| 4 | Cascade routing on small diffs, measured per useful comment | ~$32k | tail latency on escalations | cost per useful comment rises |
| 5 | INT8 quantization (not INT4 — code + strict format is exactly the damage profile, Day 7 §3.6) | ~$21k | ~1 quality pt; per-category eval required | any category drops > 5 pts |
| | | **~$211k** | | |

$310k − $211k = **~$99k/month**, under the $140k target with room, and four of the
five steps traded nothing. Note what's absent: no smaller model chosen on vibes,
no INT4, no distillation project. INT4 is specifically rejected — code generation,
strict schemas, and long-context retrieval are the three capabilities quantization
damages first.

### 5.5 Eval and telemetry plan

**Day one telemetry:** traces with typed spans; TTFT/ITL separate;
`response.model` with change alerting; `finish_reason`; cost attributed by tenant,
feature (review vs. chat vs. fix-PR), outcome (comment rated useful?), attempt
type, and environment; `degraded_response_rate`; repair rate; tool validation
failure rate; `answer_in_context_rate`; KV utilization, batch occupancy,
preemption rate.

**Gold set:** 400 reviews with human "useful/not useful" labels, stratified —
language, diff size (including the 14k-line tail), change type (feature, bugfix,
refactor, config), security-sensitive paths, and **40 cases where the correct
behaviour is to post nothing.** Plus every production incident, permanently.

**CI gates:** overall usefulness drop > 2%; any category drop > 5%; assertion
layer must be 100%; cache hit rate drop > 5%; cost per useful comment increase >
10%; the concurrency soak test must pass.

**Judge:** different model family, four narrow dimensions (is the comment correct,
is it grounded in the diff, is it actionable, is it appropriately severe),
calibrated against 100 human labels, gating only on dimensions with κ ≥ 0.6.

**Alerts:** `degraded_response_rate` > 1%; `response.model` change (any); repair
rate > 3%; preemption rate > 0; any tenant-isolation assertion firing (page
immediately); canary eval delta beyond the noise floor.

### 5.6 90-day sequence

**Weeks 1–2 — stop the bleeding.** Remove the `static` cache. Move retrieval
filtering into the query. Add the tenant assertion in context assembly. Add
argument validation and the confirmation gate to `open_pull_request`. Split the
untrusted and privileged stages. Ship the concurrency soak test. *Nothing here is
an optimization; all of it is a live-incident fix.*

**Weeks 3–6 — see the system.** Full telemetry per §5.5. Cost attribution. The
first gold set and the assertion layer. Constrained decoding and the repair loop.
Error classification, jitter, and circuit breakers. By the end of this block you
can find out about problems before customers do — which is the stage-5 property
from Day 1.

**Weeks 7–12 — make it good and cheap.** Namespace-per-tenant index, hybrid
search, contextual chunking, reranker. Per-file review decomposition. Prefix
caching. Cascade routing. Then, last and only with the per-category eval in place,
INT8 quantization. SLOs written and error budgets tracked.

**The ordering rationale:** safety and isolation before observability, because you
can't ship a known breach channel while you build dashboards. Observability before
optimization, because optimizing blind is how the §2.12 failure happens. Quality
before compression, because compression trades away quality and you must be able
to see what you're spending.

---

## 6. Where to go from here

You now have the shape of the field. What's missing is repetition against real
systems, which no course provides.

Three habits that compound:

1. **Every incident becomes an eval case.** Non-negotiable, and it's the habit
   that separates systems that improve from systems that oscillate.
2. **Every proposal answers the five questions.** Especially the rollback trigger.
   Make it a norm and watch how many proposals dissolve on question 1.
3. **Re-derive the numbers.** Hardware, prices, and model capabilities change
   annually. The formulas — KV bytes per token, the decode floor, expected tokens
   under speculation, the multiplicative quality chain — don't. Recompute with
   today's inputs rather than trusting a number from a lesson written in 2026.

And the Day 1 question again: *when an LLM feature gives a wrong answer in
production, what is the most likely cause?* The honest answer, after 22 days:
**the retrieval didn't contain the answer, and nothing in the system was measuring
that.** Not the model. Not the prompt. The harness, and the absence of the
instrument that would have said so.

---

## 7. Final quiz — 20 questions across the course

**1.** KV cache bytes/token formula. **2.** Decode step floor formula.
**3.** Why is prefill compute-bound? **4.** What does continuous batching schedule
on? **5.** What must be true for a prompt cache to hit? **6.** Which caching type
can return a wrong answer? **7.** Where does quantization damage quality first?
**8.** Why is speculative decoding lossless? **9.** When is speculation a net loss?
**10.** Facts vs. behaviour — which lever for each? **11.** The three
structured-output layers. **12.** The six tool-validation layers. **13.** Why must
identity never come from a model argument? **14.** The five agent budgets.
**15.** Why must fallback degradation be visible? **16.** Why does dense retrieval
fail on identifiers? **17.** The RAG diagnostic 2×2. **18.** Why must a judge be
from a different model family? **19.** Cost per call vs. cost per outcome — why
does it matter? **20.** The central rule of safety engineering.

*(Answers are in the respective days. If you can answer 16 of 20 from memory,
you're done.)*

---

## 8. Going deeper — the shelf

The books, rather than the papers. These stay relevant.

- **Release It!, 2nd ed.** — Michael Nygard. Stability patterns. Days 12, 13, 22.
- **Designing Data-Intensive Applications** — Martin Kleppmann. Days 6, 14, 20.
- **Site Reliability Engineering** — Beyer et al. Days 17, 21.
- **Observability Engineering** — Majors, Fong-Jones, Miranda. Day 17.
- **Introduction to Information Retrieval** — Manning, Raghavan, Schütze.
  Days 14, 15.
- **The Goal** — Goldratt. Day 21, and every prioritization meeting after it.
- **Systems Performance** — Brendan Gregg. Days 3–5.

---

**Course complete.** Take [Checkpoint 4](checkpoint-4.md) if you haven't, then go
find a real system and break it on purpose.
