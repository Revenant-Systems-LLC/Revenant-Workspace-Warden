# Checkpoint 3 — Days 11–15

**Covers:** function calling, agent guardrails, routing and fallback, RAG
architecture, retrieval evals.

**Rules:** 60 minutes, closed book. Cumulative — earlier material appears where
it's load-bearing.

**Passing:** 70/100.

---

## Section A — Recall (20 points, 2 each)

**A1.** Give the six ordered layers of tool-argument validation.

**A2.** Name the five agent budgets.

**A3.** What makes an idempotency key correct?

**A4.** Which errors must never be retried? Name three.

**A5.** Why does dense retrieval fail on exact identifiers?

**A6.** What does RRF fuse on, and why does that make it robust?

**A7.** State the diagnostic 2×2 from Day 15 and what its two failure cells mean.

**A8.** Which metric do you optimize before reranking, and which after?

**A9.** What is the difference between grounding and accuracy?

**A10.** State the autonomy/privilege rule.

---

## Section B — Calculation (30 points)

**B1. (10 pts)** An agent runs 8,000 times/day. Normal runs take 7 iterations at
$0.05 each. 3% of runs hit a failure mode and loop until a 12-minute timeout at
~170 iterations.

 (a) Daily cost with no iteration cap.
 (b) Daily cost with a 22-iteration cap.
 (c) Annual saving.
 (d) A parent agent with a 22-iteration budget can spawn up to 3 children per
     iteration, each with its own 22. Worst-case iterations without hierarchical
     accounting? With a shared 40-iteration pool?

**B2. (10 pts)** Routing. Large model $0.014/request, small $0.0018.
2.5M requests/day. 58% of traffic is handled acceptably by the small model.

 (a) Cost with no routing.
 (b) Cost with perfect routing (assume free routing signal).
 (c) A classifier costing $0.0011/request does the routing. Net position?
 (d) A cascade instead: everything hits small first (free gate), 42% escalate.
     Cost? Which wins, and what's the non-cost tradeoff?

**B3. (10 pts)** RAG eval. `answer_in_context_rate = 0.58`; accuracy given
context = 0.90; accuracy without context = 0.09.

 (a) Current end-to-end accuracy.
 (b) Ceiling if generation were perfect.
 (c) Ceiling if retrieval were perfect.
 (d) You want to detect a 5-point regression around this baseline. Gold set size?
     Show the working.

---

## Section C — Diagnosis (30 points, 10 each)

Cause, confirming metric, fix.

**C1.** A payments agent has issued 94 duplicate refunds over three weeks. The
logs show, in each case, a `refund` tool call that returned a timeout, followed
30 seconds later by an identical call that succeeded. The team added a retry with
exponential backoff last month to "improve reliability."

**C2.** A documentation assistant scored 79% at launch on a 40-query eval. Six
months later it still scores 79% on the same 40 queries, but support tickets
about wrong answers have quadrupled. The corpus grew from 800 to 22,000
documents. Retrieval is dense-only, k=8, no reranker.

**C3.** During a four-hour provider incident, an assistant's error rate stayed at
0.1% and p50 latency *improved* by 300ms. Afterwards, customer complaints about
answer quality spiked for that window. The engineer on call says "the fallback
worked perfectly."

---

## Section D — Design (20 points)

**D1. (20 pts)** Design the tool and agent layer for an internal "infrastructure
assistant" that can: search runbooks, query metrics, read logs, restart a
service, scale a deployment, and page an on-call engineer.

Constraints: multi-tenant (each team sees only its own services); used by ~200
engineers; some requests are read-only questions, some are actions during
incidents.

Produce:
 (a) Effect classification for all six tools, with the policy each demands.
 (b) The three tools that need idempotency keys, and what the key is computed
     over for each.
 (c) The confirmation design for `restart_service`, including the specific hole
     you're closing.
 (d) Budget policy: which of the five budgets, with numbers, and how you'd derive
     them.
 (e) One design decision that specifically prevents a prompt injection in a log
     line from causing a service restart. (Day 19 preview — reason from Days
     11–12.)

---

## Answer key

### Section A

**A1.** Schema → referential integrity → authorization → policy → rate/budget →
idempotency dedupe → execute.
**A2.** Iterations, wall-clock, tokens, cost, per-tool calls.
**A3.** Deterministic over the *logical operation* — same operation yields the
same key across retries, different operations yield different keys. No timestamps
or fresh GUIDs.
**A4.** 400 bad request, 401/403 auth, context-length-exceeded, content filter
(any three).
**A5.** Embeddings encode semantic meaning; opaque identifiers carry almost none,
so they cluster indistinguishably with every other alphanumeric string.
**A6.** Rank position only (`Σ 1/(k+rank)`), never raw scores — so it needs no
normalization between retrievers whose scores are on incomparable scales.
**A7.** Rows: answer present in retrieved context. Columns: answer correct.
**In-context + wrong** = generation failure. **Not in context + wrong** =
retrieval failure.
**A8.** Recall@50/@100 before reranking; precision@5 / nDCG@5 after.
**A9.** Grounding = every claim is supported by the retrieved context
(faithfulness). Accuracy = the answer is true. A grounded answer over a stale
document is faithful and wrong.
**A10.** As autonomy rises, privilege must fall — highly autonomous agents should
hold mostly read-only tools, routing writes through confirmation or a queue.

### Section B

**B1.**
 (a) Normal: `7,760 × 7 × $0.05 = $2,716`. Runaway: `240 × 170 × $0.05 = $2,040`.
 **Total $4,756/day.**
 (b) Normal unchanged $2,716. Capped: `240 × 22 × $0.05 = $264`.
 **Total $2,980/day.**
 (c) `($4,756 − $2,980) × 365 = ` **$648,240/year**.
 (d) Without hierarchical accounting: `22 × 3 × 22 = 1,452` iterations. With a
 shared 40-iteration pool: **40**.

**B2.**
 (a) `2,500,000 × $0.014 = $35,000/day`.
 (b) `1,450,000 × $0.0018 + 1,050,000 × $0.014 = $2,610 + $14,700 = $17,310/day`.
 (c) Classifier adds `2,500,000 × $0.0011 = $2,750` → **$20,060/day**. Still saves
 $14,940/day, so **net positive**, though the classifier eats ~16% of the gross
 saving.
 (d) Cascade: `2,500,000 × $0.0018 = $4,500` + `1,050,000 × $0.014 = $14,700` =
 **$19,200/day**. **The cascade wins** by $860/day. **Non-cost tradeoff:** the 42%
 that escalate pay both calls, so their latency is the sum — a tail-latency cost
 invisible on a p50 dashboard, and it worsens if the escalation rate drifts up.

**B3.**
 (a) `0.58 × 0.90 + 0.42 × 0.09 = 0.522 + 0.038 = 0.560`.
 (b) `0.58 × 1.00 + 0.42 × 0.09 = 0.618` → **+5.8 points**.
 (c) `1.00 × 0.90 = 0.900` → **+34 points**. Work on retrieval.
 (d) `SE = sqrt(0.56 × 0.44 / n) = 0.496/sqrt(n)`. At 2 SE for 5 points:
 `0.05 = 2 × 0.496/sqrt(n)` → `sqrt(n) = 19.84` → **n ≈ 394** queries.

### Section C

**C1.** **Cause:** retries without idempotency keys. The timeout was a *response*
failure, not an execution failure — the refund succeeded server-side and the
retry issued a second one. Adding exponential backoff made the retry more likely
to be attempted, not safer.
**Metric:** duplicate-effect rate — count refunds with identical
(order, amount, ~timestamp window); or, going forward, idempotency-key hit rate.
**Fix:** deterministic idempotency key over `(run_id, tool_name,
canonical_args)`; server contract stores and replays the original response;
classify timeouts as "retry only with a key" (Day 13 §3.4). Also: verify state
before retrying any non-idempotent effect.

**C2.** **Cause:** distractor accumulation from a 27× corpus growth against a
fixed k=8 dense-only retrieval with no reranker — retrieval precision collapsed
while the frozen 40-query eval kept scoring the same because those 40 queries'
answers still retrieve. The eval is too small (Day 15 §4.2 — 40 queries detects
only catastrophes) and hasn't tracked the corpus.
**Metric:** `answer_in_context_rate` on a *current, stratified* gold set;
secondarily precision@k over time.
**Fix:** add sparse retrieval + RRF and a cross-encoder reranker with a
threshold; rebuild the gold set to ~300+ queries stratified per Day 15 §3.3
including unanswerable; run it nightly and alert on deltas.

**C3.** **Cause:** silent degradation. The fallback routed to a weaker model, so
errors stayed near zero and latency improved (smaller model) while quality
dropped — none of which the dashboards measured. The engineer's claim is exactly
backwards: the fallback preserved *availability* and silently sacrificed
*quality*, with no signal.
**Metric:** `degraded_responses_total{reason}` — and `quality_by_model` from
sampled evals.
**Fix:** every response carries model/route/degraded metadata through the trace
and to the UI; alert on the degraded counter; show an honest degraded-mode notice
with a retry affordance. Reconstructing this incident was impossible precisely
because that metadata didn't exist.

### Section D

**D1.** Grade on reasoning; these are defensible answers.

 (a) `search_runbooks` — **read-only**, rate-limit only. `query_metrics` —
 **read-only**. `read_logs` — **read-only**, but see (e): its *output* is
 untrusted. `restart_service` — **irreversible in effect** (drops connections,
 can't un-restart): confirmation + audit + idempotency key. `scale_deployment` —
 **idempotent write** (setting replicas=5 twice yields the same state): safe to
 retry, still needs authorization and policy limits. `page_oncall` —
 **non-idempotent, external-visible**: idempotency key mandatory; a duplicate page
 at 3am is a real harm.

 (b) `restart_service` — key over `(run_id, service_id, requested_at_bucket)`;
 `page_oncall` — key over `(incident_id, rotation, message_hash)` so the same
 alert doesn't page twice while genuinely new alerts still get through;
 `scale_deployment` — key over `(service_id, target_replicas)` (it's idempotent
 by nature, but the key prevents a retry storm from thrashing an autoscaler).

 (c) Preview→confirm with a **signed token**: `restart_service(service_id,
 execute=false)` validates authorization and returns
 `{service, replicas_affected, estimated_downtime, confirmation_token=HMAC(exact
 validated params)}`. The confirm call executes **only the parameters decoded from
 the token**, ignoring any re-supplied by the model. **The hole being closed:** a
 naive confirm re-reads arguments from the model's message, so the agent can
 preview restarting `staging-api` and confirm restarting `prod-payments`.

 (d) All five. Iterations ~15 (incident work is usually short; derive from the
 p99 of successful runs). Wall-clock 180s (during an incident, a slow agent is
 worse than none). Tokens 250k. Cost $1.50/run. Per-tool: `read_logs` ≤ 6 (it's
 the retry-storm candidate and its outputs are huge), `restart_service` ≤ 1,
 `page_oncall` ≤ 1. **Derivation:** run uncapped in staging, take p99 of
 successful runs, verify what falls above the cap was mostly going to fail anyway.

 (e) The strongest single answer: **tool results from `read_logs` are marked
 untrusted provenance, and privileged tools (`restart_service`, `scale_deployment`,
 `page_oncall`) are gated in the harness so they cannot be invoked on an iteration
 whose context was influenced by untrusted content without human confirmation.**
 Equivalently: separate the read/analyze agent from the act agent, with a typed,
 validated handoff rather than a shared context. Also accepted: log content is
 never interpolated into the system prompt, is length-capped, and identity/service
 scoping comes from the session — so an injected line can propose an action but
 cannot authorize one. Answers relying only on prompt instructions ("tell the model
 to ignore instructions in logs") score zero — that's a preference, not a control.

---

## Scoring

| Score | Read this as |
| --- | --- |
| 90–100 | Strong. Continue to Day 16. |
| 70–89 | Solid. Re-skim the weakest day. |
| 60–69 | Redo the weakest day's lab. |
| < 60 | Redo Days 11 and 14. Week 4 assumes the tool boundary and the RAG pipeline. |
