# Glossary

Every term the course uses, defined once, with the day it's introduced. Terms are
grouped by area; use Ctrl-F.

---

## Serving and inference mechanics

**Arithmetic intensity** *(Day 3)* — FLOPs performed per byte loaded from memory.
High for prefill (each weight is reused across all prompt tokens), low for decode
(each weight is used once per step). Determines whether an operation is
compute-bound or memory-bound.

**Batch occupancy** *(Day 5)* — mean number of running sequences divided by the
configured maximum. One of the three metrics that should replace "GPU
utilization." Healthy range roughly 0.6–0.9.

**Chunked prefill** *(Day 3)* — splitting a long prefill into fixed-size chunks
interleaved with other requests' decode steps. Trades the long request's TTFT for
everyone else's ITL stability, and usually improves total throughput.

**Compute-bound** *(Day 3)* — limited by available FLOPs rather than memory
bandwidth. Prefill is compute-bound.

**Continuous batching** *(Day 5)* — scheduling at the granularity of a single
decode iteration rather than a whole request, so finished sequences leave the
batch immediately and waiting requests join at the next step. Also called
iteration-level scheduling.

**Decode** *(Day 3)* — the autoregressive generation phase; one token per forward
pass, memory-bandwidth-bound, determines ITL.

**Disaggregated serving** *(Day 5)* — running prefill and decode on separate
hardware pools so each can be sized and scaled independently. Costs a KV cache
transfer between pools.

**E2E** *(Day 3)* — end-to-end latency. `≈ queue_time + prefill_time +
(output_tokens × decode_step_time)`.

**Goodput** *(Day 5)* — output tokens per second actually delivered to a client
that still wants them. Excludes tokens for preempted/recomputed sequences and
abandoned clients. Distinct from throughput, and the one that matters.

**GQA (grouped-query attention)** *(Day 4)* — sharing each key/value head across
several query heads, cutting KV cache size by that ratio. MQA is the extreme case
of one KV head.

**Head-of-line blocking** *(Day 5)* — in static batching, short requests waiting
for the longest request in their batch to finish. The problem continuous batching
solves.

**ITL (inter-token latency)** *(Day 3)* — the gap between successive output
tokens. Governed by decode step time. Never merge with TTFT.

**KV cache** *(Day 4)* — stored key and value vectors for every processed token
in every layer, making decode O(1) per token. Size per token =
`2 × n_layers × n_kv_heads × head_dim × bytes_per_element`. Usually the binding
constraint on concurrency.

**Memory-bound** *(Day 3)* — limited by memory bandwidth rather than compute.
Decode is memory-bound; the step time floor is `model_bytes / bandwidth`.

**MFU (model FLOPs utilization)** *(Day 3)* — the fraction of a device's peak
FLOPs actually achieved. Typically 0.3–0.5 in practice.

**PagedAttention** *(Days 4–5)* — allocating KV cache in fixed-size blocks
(typically 16 tokens) with a per-sequence block table, reducing fragmentation
waste from 60–80% to under 4% and enabling copy-on-write prefix sharing.

**Preemption thrashing** *(Day 4)* — repeatedly preempting and re-admitting
sequences so the same prefill is paid several times. GPU utilization looks high;
goodput collapses.

**Prefill** *(Day 3)* — processing the entire prompt in one forward pass;
compute-bound, scales with prompt length, determines TTFT.

**TTFT (time to first token)** *(Day 3)* — request sent to first token received.
Governed by queueing plus prefill. The metric that matters for streaming products.

---

## Context and caching

**Attention dilution** *(Day 2)* — degradation in answer quality caused by
additional, competing content in the context, even when that content is relevant.
The reason adding correct information can make an answer worse.

**Compaction** *(Day 2)* — replacing older conversation turns with a summary.
Loses identifiers and negations; compounds when summaries are summarized;
invalidates cached prefixes below the rewrite point.

**Context budget** *(Day 2)* — fixed per-region token caps enforced in code
before the call, with output space explicitly reserved.

**Contextual chunking** *(Day 14)* — prepending document- and section-level
context to each chunk before embedding, so the chunk's vector encodes what it's
about rather than only its literal words.

**Exact-match caching** *(Day 6)* — caching responses keyed on a normalized exact
query plus the full scope namespace. Lower hit rate than semantic caching, zero
similarity-matching risk. Usually the right middle option.

**Lost in the middle** *(Day 2)* — the empirically observed U-shaped reliability
of retrieval from long contexts: content at the start and end is used more
reliably than content in the middle.

**Prefix cache / prompt caching** *(Days 4, 6)* — reusing the KV cache for a
byte-identical token prefix. Correctness-preserving. Requires exact match from
token zero; a single differing character invalidates everything after it.

**Semantic caching** *(Day 6)* — reusing a previous *answer* for a query judged
similar by embedding distance. Skips the model entirely. Can return a wrong
answer; requires a scoped key, a precision-tuned threshold, negation/quantity
guards, TTL, invalidation, and shadow sampling.

**Structured state** *(Day 2)* — carrying extracted facts in a typed object
rendered deterministically into the prompt, instead of carrying the transcript.
Testable, compact, cannot self-contradict.

---

## Compression and adaptation

**AWQ (activation-aware weight quantization)** *(Day 7)* — identifies salient
weight channels by activation magnitude and applies per-channel scaling to protect
them before uniform quantization. Less calibration-sensitive than GPTQ.

**Acceptance rate (α)** *(Day 8)* — probability that a drafted token is accepted
in speculative decoding. The dominant variable in the speedup formula.

**Calibration data** *(Day 7)* — the sample used by post-training quantization
methods to estimate error. Must match production traffic in language, format, and
sequence length, or the quantized model degrades where you didn't look.

**Catastrophic forgetting** *(Day 9)* — loss of general capability caused by
narrow fine-tuning. Invisible to a task-specific eval by construction.

**Distillation** *(Days 8–9)* — training a smaller student model to reproduce a
larger teacher's behaviour. Closes a cost gap, never a capability gap.

**GPTQ** *(Day 7)* — column-by-column post-training quantization using approximate
second-order information to compensate accumulated error. Accurate, more
calibration-sensitive than AWQ.

**In-context learning (ICL)** *(Day 9)* — supplying information or examples in the
prompt. Instant feedback loop, recurring per-request cost. The correct default
starting point.

**LoRA / QLoRA** *(Day 9)* — low-rank adapters trained instead of full weights
(QLoRA on a quantized base). Adapters are small and swappable, which makes
per-tenant customization economically viable.

**Prompt lookup decoding** *(Day 8)* — drafting by copying an n-gram continuation
from the prompt rather than from a model. Free, and highly effective whenever the
output quotes the input.

**Speculative decoding** *(Day 8)* — a cheap draft model proposes γ tokens which
the target verifies in one pass, accepting per a rejection-sampling rule that
provably preserves the target's output distribution. Lossless. Benefit shrinks as
batch size grows.

**W4A16 / W8A8** *(Day 7)* — notation for weight and activation precision. W4A16
(4-bit weights, 16-bit activations) helps decode and memory but not prefill; W8A8
also helps prefill.

---

## Structure, tools, and agents

**Constrained decoding** *(Day 10)* — masking tokens at each decoding step so only
grammar-valid continuations are possible. Guarantees syntax and structural schema
conformance; guarantees nothing about meaning.

**Effect class** *(Day 11)* — the taxonomy of tool side effects: read-only,
idempotent write, non-idempotent write, irreversible, external-visible. Determines
what ceremony a tool requires.

**Grounding validation** *(Days 10, 15)* — checking that claims or quotes in the
output are present in the provided source material.

**Idempotency key** *(Day 11)* — a deterministic identifier over the *logical*
operation such that the server executes at most once per key and replays the
original response on retry. Must not include timestamps or fresh GUIDs.

**Loop / tool / token / cost / wall-clock budgets** *(Day 12)* — the five bounds
every agent run needs, checked in code before every model and tool call.

**Progress detection** *(Day 12)* — noticing that an agent loop is spinning rather
than advancing (repeated identical calls, cyclic state, no artifact change), and
escalating: inject the observation, restrict tools, then terminate.

**Referential integrity check** *(Day 11)* — verifying that entities named in
model output actually exist. Catches hallucinated identifiers.

**Repair loop** *(Day 10)* — re-asking with specific validation errors after a
failure, capped (2 attempts), instructing the model to change only what the errors
require.

**Semantic validation** *(Day 10)* — the third output layer: referential
integrity, range/plausibility, cross-field consistency, grounding, and policy.
No library does this; it's your domain code.

**Terminal state** *(Day 12)* — the explicit, exhaustive set of ways an agent run
can end: Completed, CompletedPartial, BudgetExhausted, Blocked, NoProgress,
Failed, Escalated. Each needs user-facing text.

---

## Retrieval

**BM25 / sparse retrieval** *(Day 14)* — lexical term-frequency retrieval.
Necessary for exact identifiers, codes, and proper nouns, which dense retrieval
cannot match.

**Cross-encoder reranker** *(Day 14)* — a model scoring `(query, passage)`
together, able to judge whether a passage *answers* the question rather than
merely sharing its topic. Usually the largest single quality gain available to a
naive RAG system.

**Hybrid search** *(Day 14)* — combining dense and sparse retrieval, typically
fused with RRF.

**Hit rate@k / recall@k / precision@k / nDCG@k** *(Day 15)* — retrieval metrics.
Optimize recall before reranking, precision and nDCG after.

**Parent-document retrieval** *(Day 14)* — searching over small chunks for
precision while passing the parent section to the model for context. Dissolves the
chunk-size tradeoff.

**RRF (reciprocal rank fusion)** *(Day 14)* — combining ranked lists by
`Σ 1/(k + rank)`. Uses rank position only, so it needs no score normalization
between retrievers with incomparable scales.

**`answer_in_context_rate`** *(Day 15)* — the fraction of queries where the answer
was present in the retrieved context. The most valuable single measurement in a
RAG system; it sets the ceiling on end-to-end accuracy.

---

## Evaluation and observability

**Adversarial eval** *(Day 16)* — a permanent suite of deliberately hostile inputs:
injections, jailbreaks, exfiltration attempts, malformed input, negation, and
should-refuse cases.

**Canary eval** *(Day 17)* — a small deterministic suite run hourly against the
production configuration, to detect silent model changes and regressions fast.

**Cohen's κ** *(Day 16)* — inter-rater agreement corrected for chance. Minimum
~0.6 for a judge dimension you'd gate CI on.

**Drift** *(Day 17)* — quality or cost change with no deploy: input drift, corpus
drift, model drift, behavioural drift, cost drift.

**Golden set** *(Days 15–16)* — curated, stratified, versioned inputs with known-
good outputs. Size it to the regression you need to detect.

**LLM-as-judge** *(Day 16)* — automated grading by a model. Requires a different
model family, narrow binary questions, blinding, randomized order, and calibration
against human labels.

**Noise floor** *(Day 16)* — the run-to-run score variance of an identical system.
Alert thresholds below it fire constantly and get ignored.

**Silent regression** *(Days 16, 22)* — quality degradation with no error, no
alert, and no deploy. The characteristic LLM failure.

**Span / trace** *(Day 17)* — one user action is a trace; every stage inside it is
a typed span carrying tokens, cost, model, cache status, and validation outcomes.

**Tail sampling** *(Day 17)* — deciding what telemetry to keep after seeing the
outcome, so 100% of failures and a small fraction of successes are retained.

---

## Cost, safety, and isolation

**Attribution dimensions** *(Day 18)* — feature, workflow, tenant, user journey,
outcome, attempt type, cache status, route tier, environment. Per-model cost is
insufficient.

**Capability separation** *(Day 19)* — ensuring no single agent context holds both
"read untrusted/sensitive data" and "send data outward." Structurally prevents
exfiltration.

**Confused deputy** *(Days 11, 19)* — a component using its own broader authority
on behalf of a less-privileged caller. The reason credentials must be
request-scoped.

**Context contamination** *(Day 20)* — data from another request or user present
in the assembled context. Caused by shared mutable state, pooling without reset,
or ambient context leaking across async boundaries.

**Cost per outcome** *(Day 18)* — total cost divided by completed business
outcomes. The unit that matters; per-call cost routinely flips decisions the wrong
way.

**Degraded response** *(Day 13)* — a response served by a fallback path.
`degraded_response_rate` is the counter that catches silent quality loss.

**Indirect prompt injection** *(Day 19)* — instructions embedded in content the
system reads (documents, tool results, log lines) rather than typed by the user.

**Least privilege / request-scoped credentials** *(Day 19)* — the agent holds only
the calling user's permissions. Converts a successful injection from a
catastrophic breach into a survivable bug.

**Never-downgrade list** *(Day 13)* — explicit code listing request classes that
always get the strong model, regardless of difficulty routing.

**`PermissionScopeHash`** *(Day 20)* — a hash of the caller's effective permissions
included in every cache key, so two users in the same tenant with different access
cannot share a cached answer.

**Trust level** *(Day 19)* — the label on every content block: system-authored
(trusted), authenticated user (semi-trusted), retrieved content (untrusted), tool
results (untrusted), model output (untrusted).

---

## Cross-cutting principles

**Control vs. preference** *(Day 1)* — a control is code that returns an error; a
preference is a request expressed in prose to a probabilistic system. "The model
shouldn't do that" is never a control.

**Determinism sandwich** *(Day 1)* — deterministic code on both sides of every
model call: before it controls what the model can know, after it controls what the
model can do.

**Free lunch** *(Day 21)* — a change that improves multiple dimensions with no
real trade: context engineering, prefix caching, reranking, continuous batching,
speculation at low batch, streaming. Exhaust these before paying for anything.

**Harness** *(Day 1)* — the program around the model: input contract, context
assembly, routing, output contract, effects, control loop, telemetry. The prompt
is one input to one call inside it.

**Multiplicative quality chain** *(Day 1)* — end-to-end success is the product of
per-stage success rates, so it's dominated by the worst term. Find the worst term
before optimizing anything.

**Rollback trigger** *(Days 7, 21)* — the metric, threshold, and duration that
would reverse a decision, defined before deploying it. A change without one cannot
be shown to be wrong.
