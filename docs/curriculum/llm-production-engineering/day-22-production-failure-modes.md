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

## 4. Capstone — build it

You've built eleven components across twenty-one days, each in isolation, each
tested alone. The capstone is the assembly: **turn the Warden's tutor mode into a
production-grade system, running over this course as its corpus, and prove it
works with numbers you produce.**

Not a design document. A thing that runs.

Budget: 12–20 hours, spread over a week. It is the largest single piece of work
in the course and the only one that produces something you keep using.

### 4.1 What you're building

The Warden already has a tutor mode. Today it is a stage-0/1 harness (Day 1
§3.3): a string goes to a provider, a string comes back. You're taking it to
stage 5 — the stage whose distinguishing property is that **you find out about
failures before your users do.**

The corpus is this course: 28 markdown files, ~13,900 lines, with a structure you
know intimately because you wrote the labs in it. That's deliberate. You can
judge retrieval quality by eye, because you know what the right answer is, which
makes building the gold set tractable in an evening instead of a week.

```
                          ┌─────────────────────────┐
  user question  ────────▶│ INPUT CONTRACT          │  D1, D19
                          │  validate, classify,     │
                          │  attach caller context   │
                          └───────────┬─────────────┘
                                      ▼
                          ┌─────────────────────────┐
                          │ RETRIEVAL               │  D14, D15
                          │  hybrid (dense + BM25)   │
                          │  → rerank → threshold    │
                          └───────────┬─────────────┘
                                      ▼
                          ┌─────────────────────────┐
                          │ CONTEXT ASSEMBLY        │  D2, D6, D20
                          │  ContextBudget, static   │
                          │  prefix first, verify()  │
                          └───────────┬─────────────┘
                                      ▼
                          ┌─────────────────────────┐
                          │ ROUTING                 │  D13
                          │  tier by task, fallback  │
                          │  chain, circuit breaker  │
                          └───────────┬─────────────┘
                                      ▼
                          ┌─────────────────────────┐
                          │ THE CALL                │  D3, D6
                          │  cache control ON        │
                          └───────────┬─────────────┘
                                      ▼
                          ┌─────────────────────────┐
                          │ OUTPUT CONTRACT         │  D10, D15
                          │  schema → semantic →     │
                          │  citation validation →   │
                          │  repair ×2 → typed fail  │
                          └───────────┬─────────────┘
                                      ▼
                          ┌─────────────────────────┐
                          │ CONTROL LOOP            │  D11, D12
                          │  tool boundary, budgets, │
                          │  terminal states         │
                          └───────────┬─────────────┘
                                      ▼
                          ┌─────────────────────────┐
                          │ TELEMETRY               │  D17, D18
                          │  spans, cost, quality    │
                          └─────────────────────────┘
                                      │
                          ┌───────────▼─────────────┐
                          │ EVAL HARNESS            │  D16
                          │  gold set, gates, CI     │
                          └─────────────────────────┘
```

### 4.2 Acceptance criteria

This is the grading. Every one is a number you measure, not a judgment you make.
Where a target is given, hit it or write down why it isn't achievable and what it
would take.

**Retrieval (D14, D15)**

- [ ] Hybrid retrieval: dense over `nomic-embed-text` plus BM25, fused with RRF.
- [ ] Cross-encoder or LLM reranker, top-100 → top-5, with a score threshold.
- [ ] Chunks carry file path, heading path, and day number as prepended context.
- [ ] **`recall@5` ≥ 0.85** on your Day 15 gold set (≥60 queries, stratified).
- [ ] **`exact_identifier_recall` ≥ 0.90** — queries like `OLLAMA_NUM_PARALLEL`,
      `ILLMProvider`, `PermissionScopeHash` must resolve. This is the row that
      proves hybrid search earned its place.
- [ ] **Abstention on unanswerable ≥ 0.90** — ask it about Kubernetes deployment
      strategy and it must return nothing rather than confabulate.

**Context and caching (D2, D6)**

- [ ] `ContextBudget` enforces per-region caps with output space reserved.
- [ ] `verify()` runs before every call and asserts total budget and provenance.
- [ ] Static prefix is byte-identical across requests — **`prefix_hash` constant,
      asserted by a test.**
- [ ] **Prefix caching enabled on the hosted path.** `AnthropicProvider.cs`
      currently sends no cache control at all; measured **cache hit rate ≥ 0.80**
      after the first request in a session.
- [ ] Conversation history uses structured state, not raw transcript.

**Output contract (D10, D15)**

- [ ] Constrained decoding to a schema: `answer`, `citations[]` (chunk IDs),
      `confidence`, `sufficient_information`.
- [ ] Semantic validation: every cited chunk ID exists in the retrieved set.
- [ ] Grounding check: sampled claims are entailed by cited chunks.
- [ ] Repair capped at 2, with specific errors fed back.
- [ ] `finish_reason == length` short-circuits to `OutputTruncated`, never repair.
- [ ] Terminal failure is a typed error. **A test asserts no code path returns a
      default-filled result.**
- [ ] **`hallucinated_chunk_id_rate == 0`** — not "low." Zero, because it's a
      referential check.
- [ ] **`repair_rate` < 3%.**

**Control and safety (D11, D12, D19)**

- [ ] At least one tool (`search_curriculum`, `get_lesson_section`) behind the
      `ToolBoundary` with all six validation layers.
- [ ] All five budgets enforced, checked before every model and tool call.
- [ ] Completion verified by code, not by the model claiming it.
- [ ] Every `TerminalReason` has non-empty user-facing text — asserted by a test.
- [ ] Curriculum content is labelled `Untrusted` and cannot reach the system
      prompt region. Prove it with the Day 19 injection corpus: **plant a payload
      in a lesson file, confirm it cannot trigger a non-read-only tool, and that
      the block came from code rather than a model refusal.**

**Routing and resilience (D13)**

- [ ] Deterministic tier routing: short factual lookups → small model; synthesis
      and multi-day questions → large.
- [ ] `ResiliencePolicy` with error classification, full jitter, circuit breaker.
- [ ] **A 400 produces exactly one attempt** — asserted by a test.
- [ ] Every response carries model, route, and degraded flag through to the UI.
- [ ] `MainWindow` shows a degraded banner when `WasDegraded` is true.

**Telemetry and cost (D17, D18)**

- [ ] One trace per question, with typed child spans for every stage above.
- [ ] TTFT recorded separately from total; `finish_reason` recorded;
      `response.model` recorded.
- [ ] **The trace answers all five Day 17 §3.1 questions for an arbitrary past
      request.** Test it by asking someone else to answer them from your trace.
- [ ] `CostContext` is a required parameter on `ILLMProvider.ChatAsync` — an
      unattributed call must not compile.
- [ ] Cost per answered question computed and broken down by attempt type.
- [ ] Price lookup throws on an unknown model.

**Evals (D16)**

- [ ] Assertion layer: all six checks from Day 16 §Part A, green.
- [ ] Gold set ≥ 60 queries, stratified, versioned in the repo.
- [ ] **Noise floor measured and written down**; gates set above it.
- [ ] Per-category CI gate, not just overall.
- [ ] Adversarial suite including the injection corpus and should-refuse cases.
- [ ] Judge calibrated against ≥ 40 of your own hand labels; **κ reported per
      dimension**, and you gate only on dimensions with κ ≥ 0.6.

### 4.3 Deliverables

**1. The working system.** It runs. You can ask it "why is decode
memory-bandwidth-bound?" and it answers with citations to Day 3 that you can
click.

**2. `docs/curriculum/CAPSTONE_RESULTS.md`** — the numbers. Every acceptance
criterion, measured, with the actual value next to the target. Where you missed,
say by how much and what it would take. **A missed target that's honestly
reported and diagnosed scores higher than a hit one you can't explain.**

**3. The eval suite in CI.** A workflow that runs the assertion layer and the
gold set, and fails on a per-category regression above your noise floor.

**4. A one-page architecture note** using Day 21's five questions: what dimension
this system is constrained by, the evidence, and the one thing you'd do next.

### 4.4 Suggested order

Not the order of the course — the order that gets you something working soonest.

| Session | Build | Why here |
| --- | --- | --- |
| 1 | Ingest + chunk the curriculum; dense + BM25 index | Nothing works without a corpus |
| 2 | Gold set (60 queries, stratified) | You need the ruler before you build the thing |
| 3 | Hybrid + RRF + rerank + threshold; measure recall | First real number |
| 4 | `ContextBudget` + prefix discipline + cache control | Cheapest big win; prefix hash test |
| 5 | Structured output + citation validation + repair | The contract |
| 6 | Telemetry spans + cost attribution | You're now flying with instruments |
| 7 | Routing + `ResiliencePolicy` + degraded UX | Resilience |
| 8 | `ToolBoundary` + budgets + terminal states | Control |
| 9 | Trust boundary + injection corpus | Safety |
| 10 | Eval harness + judge calibration + CI gate | The thing that keeps it working |

Session 2 before session 3 is the important ordering. Build the measurement
before the thing you're measuring, or you'll tune retrieval by vibes and have no
way to know whether session 4 broke it.

### 4.5 How to know you're done

Ask the finished system the twenty questions from §7 of this lesson. It should
answer all twenty from the curriculum, with correct citations, and it should
decline the ones you deliberately ask outside the corpus.

Then do the thing that actually proves it: **break something on purpose.**
Interpolate a timestamp into the static prefix and confirm the cache-hit metric
catches it. Delete the tenant assertion and confirm a test fails. Plant an
injection in `day-14-rag-architecture.md` and confirm it can't reach a tool.
Swap the model tag and confirm the canary fires.

If every deliberate break produces a signal, you built a stage-5 system. If some
of them pass silently, you found the gap, which is the same lesson arriving a
different way.

---

## 5. Grading notes

There's no model answer, because you're building against your own repo and I
can't run it. Instead, here's what separates a good result from a passing one on
each axis — use it to grade yourself honestly.

**Retrieval.** Passing: hybrid search works and recall is decent. Good: you
plotted the accuracy-vs-k dilution curve (Day 2 §4.3) for *this* corpus and chose
k from it rather than defaulting to 5; you can say which query stratum is
weakest and why.

**Caching.** Passing: cache control enabled and hit rate measured. Good: you
found something that was silently breaking the prefix and fixed it — serializer
ordering, a `Dictionary` enumeration, locale formatting. Almost every codebase
has one. If you didn't find one, look harder before believing you don't.

**Output contract.** Passing: schema validation and a capped repair loop. Good:
your semantic validation catches something a schema can't — a citation to a chunk
that exists but doesn't support the claim. That's the Day 15 gap, and finding it
in your own system is the point.

**Safety.** Passing: untrusted labelling and a read-only restriction. Good: your
injection test fails *at the boundary in code*, and you can point at the line.
If the only thing stopping the payload is that the model declined, write that
down as a finding — you have a preference, not a control (Day 1 §3.2).

**Telemetry.** Passing: spans exist. Good: someone else answered all five Day 17
questions from your trace without asking you anything. That's the only real test
of observability, and it's uncomfortable in a useful way.

**Evals.** Passing: a gold set and a CI gate. Good: you measured the noise floor,
your gate sits above it, and you can state what size regression you *cannot*
detect with 60 queries. Knowing the limits of your instrument is the difference
between measuring and pretending to.

**The whole thing.** Passing: it runs and the numbers exist. Good: your
`CAPSTONE_RESULTS.md` contains at least one honestly reported miss with a
diagnosis. A capstone where every target was hit on the first try means the
targets were set after the measurement.

### 5.1 If you want the paper exercise too

The design-on-paper version has one thing the build doesn't: it exercises Days 4,
5, 18, and 20, which a single-user desktop app cannot. If you want that coverage,
[`capstone-design-exercise.md`](capstone-design-exercise.md) has a fictional
multi-tenant brief seeded with all twelve failure modes, plus a model answer.

Do it *after* the build, not instead of it. Designing a system you've never had
to make work is how people acquire opinions that don't survive contact with a
profiler.

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

**Course complete** when §4's acceptance table is filled in and
`CAPSTONE_RESULTS.md` exists — not when you've read this page. Take
[Checkpoint 4](checkpoint-4.md) if you haven't, then go build it and break it on
purpose.
