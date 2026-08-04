# Capstone — Design Exercise (optional, paper)

**Companion to [Day 22](day-22-production-failure-modes.md). Do the build capstone
first.**

The build capstone turns the Warden's tutor mode into a production system. It
exercises most of the course, but a single-user desktop application cannot
exercise **Day 4** (KV cache under concurrency), **Day 5** (batching and
throughput), **Day 18** (cost attribution across tenants and features), or
**Day 20** (multi-tenant isolation). Those need a system with tenants, a fleet,
and a bill.

This exercise supplies one. It is fictional precisely so that it can be graded:
every constraint is stated, so a model answer exists. It is also seeded with all
twelve failure modes from Day 22 §2 at once, which no real system obliges you by
doing.

**Budget 2 hours. Write it out properly** — the value is in the writing, not in
reading the model answer. Cover the page below before you scroll.

---

### The brief

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

### Deliverables

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

## Model answer

Compare against yours. Where you differ, ask whether you had a reason.

### Failure triage

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

### Binding constraint

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

### Architecture

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

### Cost plan: $310k → under $140k

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

### Eval and telemetry plan

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

### 90-day sequence

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
