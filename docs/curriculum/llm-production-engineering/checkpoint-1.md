# Checkpoint 1 — Days 1–5

**Covers:** harness engineering, context engineering, prefill vs. decode, KV
cache, batching and throughput.

**Rules:** 60 minutes, closed book, no calculator app that shows you the formula.
Scratch paper and a plain calculator are fine. Grade yourself honestly — the only
person harmed by generosity here is you on Day 20.

**Passing:** 70/100. Below 60 on any single section means re-read that day before
continuing.

---

## Section A — Recall (20 points, 2 each)

**A1.** Write the KV cache size formula per token.

**A2.** Write the E2E latency identity.

**A3.** Write the decode step time floor.

**A4.** Name the four costs of a context token.

**A5.** Which phase is compute-bound, and why?

**A6.** What is the typical PagedAttention block size?

**A7.** State the determinism sandwich principle.

**A8.** Name the three serving metrics that should replace "GPU utilization."

**A9.** What does GQA reduce, and by what ratio in a model with 64 query heads
and 8 KV heads?

**A10.** What is goodput?

---

## Section B — Calculation (30 points)

**B1. (10 pts)** A model has 48 layers, 8 KV heads, head_dim 128, and is served
in FP16. Weights are 28 GB. You have two 80 GB GPUs (tensor-parallel, so treat
memory as pooled at 160 GB, and assume 8 GB of framework overhead).

 (a) KV bytes per token.
 (b) Bytes for one 16,000-token sequence.
 (c) Maximum concurrent 16k sequences.
 (d) Maximum concurrent 4k sequences.
 (e) You quantize the KV cache to FP8. Recompute (c).

**B2. (10 pts)** Same model, FP16 weights (28 GB), on hardware with 3.0 TB/s
aggregate memory bandwidth.

 (a) Single-stream decode ceiling in tokens/sec.
 (b) The same after quantizing weights to INT4.
 (c) A vendor claims 340 tok/s single-stream at FP16. What are the three most
     likely explanations?

**B3. (10 pts)** A feature does 300,000 requests/day. Prompts average 18,000
tokens; outputs average 120 tokens. Input costs $3/M tokens; output costs
$15/M tokens.

 (a) Daily cost.
 (b) You introduce retrieval, cutting input to 3,000 tokens. New daily cost, and
     annual saving.
 (c) 4,000 of the remaining 3,000... — trick check: 2,200 of the remaining 3,000
     tokens are a fixed system prefix that now caches at 10% of input price.
     Recompute the daily cost.
 (d) Someone adds `"Request ID: {guid}"` to the top of the system prompt.
     What is the new daily cost, and what is the annual cost of that one line?

---

## Section C — Diagnosis (30 points, 10 each)

For each, state the most likely cause, the *one metric* you'd check to confirm,
and the fix.

**C1.** A summarization endpoint has p50 E2E of 1.1s and p99 of 26s. Prompt
lengths are tightly clustered at 3,000 ± 400 tokens. Output lengths are clustered
at 200 ± 30 tokens. The service is at 40% of its provisioned request rate.

**C2.** After a migration to a new inference server, aggregate throughput dropped
from 2,400 tok/s to 380 tok/s. GPU utilization reads 97% in both cases. KV
utilization reads 0.11. The queue has 60 requests waiting.

**C3.** A RAG feature scored 88% on its eval set at launch. Six weeks later, with
no code deploys, users report worse answers. The knowledge base grew from 4,000
to 60,000 documents. Retrieval still returns k=20 chunks. Latency and error rate
dashboards are green.

---

## Section D — Design (20 points)

**D1. (20 pts)** You're asked to serve an internal code-review assistant.
Requirements:

- 8B model, FP16, self-hosted.
- 25 requests/sec at peak.
- Input: p50 = 4,000 tokens, p99 = 45,000 tokens (people paste whole files).
- Output: p50 = 300 tokens, p99 = 1,200 tokens.
- SLA: TTFT p95 ≤ 2s, ITL p95 ≤ 60ms.
- Hardware available: A100 80GB at 2.0 TB/s, ~312 TFLOPS BF16, assume 35% MFU.
  Model weights: 16 GB. KV: 128 KiB/token.

Produce:
 (a) Per-GPU decode capability at a batch size that respects the ITL SLA.
 (b) Number of GPUs for the token demand, with justification for headroom.
 (c) The KV memory check — does the p99 input length break your batch size? Show
     the arithmetic.
 (d) Two configuration choices you'd make to protect TTFT p95 given the 45k p99
     inputs, and what each costs.
 (e) One *architectural* change (not a config change, not more hardware) that
     would materially reduce cost, and its risk.

---

## Answer key

### Section A

**A1.** `2 × n_layers × n_kv_heads × head_dim × bytes_per_element`
**A2.** `E2E ≈ queue_time + prefill_time + (output_tokens × decode_step_time)`
**A3.** `decode_step_time ≥ model_bytes / memory_bandwidth`
**A4.** Money, prefill latency, KV cache memory, attention dilution.
**A5.** Prefill — all N prompt tokens are processed in one pass as matrix-matrix
multiplies, so each loaded weight byte is reused across N tokens (high arithmetic
intensity), saturating compute before bandwidth.
**A6.** 16 tokens.
**A7.** Deterministic code on both sides of every model call — before it controls
what the model can know; after it controls what the model can do.
**A8.** Batch occupancy, KV cache utilization, goodput. (Queue depth and
preemption rate also accepted.)
**A9.** The KV cache size (and the KV memory bandwidth read per step), by 8×.
**A10.** Output tokens per second actually delivered to a client that still wants
them — excluding tokens for preempted/recomputed sequences and abandoned clients.

### Section B

**B1.**
 (a) `2 × 48 × 8 × 128 × 2 = 196,608 B` = **192 KiB/token**
 (b) `192 KiB × 16,000 = 3,072,000 KiB` = **2.93 GiB**
 (c) Free = 160 − 28 − 8 = 124 GiB. `124 / 2.93` = **42 sequences**
 (d) Per-seq at 4k = `192 KiB × 4,000` = 0.73 GiB. `124 / 0.73` = **169 sequences**
 (e) FP8 halves it: 96 KiB/token → 1.46 GiB/seq → **84 sequences**

**B2.**
 (a) `28 GB / 3.0 TB/s = 9.33 ms` → **~107 tok/s**
 (b) INT4 = 7 GB → `7/3000 s = 2.33 ms` → **~429 tok/s**
 (c) They're batching (aggregate, not single-stream); they're quantized and not
 saying so; or they're using speculative decoding (Day 8). Also acceptable:
 higher-bandwidth hardware than stated, or measuring on a shorter/smaller model
 variant.

**B3.**
 (a) Input: 300,000 × 18,000 = 5.4e9 tokens → 5,400 × $3 = **$16,200**.
 Output: 300,000 × 120 = 3.6e7 → 36 × $15 = **$540**. Total **$16,740/day**.
 (b) Input: 300,000 × 3,000 = 9e8 → 900 × $3 = $2,700. Plus $540 = **$3,240/day**.
 Saving = $13,500/day = **$4.93M/year**.
 (c) Cached 2,200 tokens at $0.30/M: 300,000 × 2,200 = 6.6e8 → 660 × $0.30 =
 $198. Uncached 800 tokens: 300,000 × 800 = 2.4e8 → 240 × $3 = $720. Plus $540
 output = **$1,458/day**.
 (d) The GUID breaks the prefix, so all 3,000 input tokens pay full price:
 $2,700 + $540 = **$3,240/day**. The line costs $1,782/day = **$650,430/year**.

### Section C

**C1.** **Cause:** not prefill (input length is tight) and not decode (output
length is tight) — it's **queueing**, or per-request preemption. At 40% of
provisioned rate with a 24× p50/p99 spread, the most likely single culprit is
head-of-line blocking from static batching, or long-prompt interference from a
different endpoint sharing the pool.
**Metric:** TTFT split out from E2E, correlated against concurrent in-flight
requests — if TTFT p99 tracks load rather than prompt length, it's queue time.
(Preemption count is the accepted alternative.)
**Fix:** continuous batching + chunked prefill; if endpoints share a pool,
isolate the long-prompt workload.

**C2.** **Cause:** the new server is running at batch size ~1 — KV utilization of
0.11 with 60 queued requests means it is *not admitting* work it has room for, so
this is a scheduler/config problem, not a memory problem. Continuous batching is
off, or `max_num_seqs` is set to a tiny value.
**Metric:** batch occupancy (mean running sequences ÷ max).
**Fix:** enable continuous batching / raise `max_num_seqs` to fill the available
KV pool. Note that GPU utilization at 97% in *both* cases is exactly the trap
from Day 5 §2.

**C3.** **Cause:** attention dilution / distractor accumulation. With a 15×
larger corpus and a fixed k=20, the ratio of near-miss distractors to correct
chunks rose sharply; retrieval precision fell even if recall held. Nothing
deployed because nothing *needed* to — the data changed.
**Metric:** retrieval precision@k (or the fraction of returned chunks that are
relevant) tracked over time — not answer latency. (Day 15.)
**Fix:** add a reranker, apply a relevance-score threshold, and re-tune k against
the dilution curve. Long-term: a scheduled regression eval so this is detected by
the dashboard rather than the users (Day 16).

### Section D

**D1.** A defensible answer looks like this; grade on method, not exact numbers.

 (a) Decode step floor at batch 1 = `16 GB / 2.0 TB/s` = **8 ms**. ITL SLA of
 60 ms allows step time to grow ~7×, which on the Day 5 curve supports a batch
 around **64–96**. At batch 80 with a ~35 ms step: `80 / 0.035` ≈ **2,290 tok/s**.

 (b) Token demand = 25 req/s × 300 tokens (p50) = 7,500 tok/s. `7,500 / 2,290`
 = 3.3 → **4 GPUs** for the mean, but output p99 is 4× p50 and traffic is peaky,
 so **6 GPUs** with the extra two justified as headroom for output-length tail and
 for absorbing a failed node without breaching SLA.

 (c) At batch 80 with p50 inputs: `128 KiB × 4,300 tokens × 80` = **44 GiB**, fits
 in the ~62 GiB free. But a single p99 request is `128 KiB × 46,200` = **5.6 GiB**.
 Ten concurrent p99 requests = 56 GiB — **alone they nearly exhaust the pool**,
 forcing batch size down and collapsing throughput. So yes, the p99 input breaks
 the batch size, and the deployment must be sized or gated for it.

 (d) Two of: **chunked prefill** (protects everyone else's ITL; costs the long
 request's own TTFT); **a separate queue or pool for requests over ~16k tokens**
 (isolates the blast radius; costs hardware and complexity); **a hard input cap
 with an explicit user-facing error** (bounds the problem; costs a feature);
 **admission control lowering `max_num_seqs` when large requests are resident**
 (prevents thrashing; costs peak throughput). Each must name its cost to score.

 (e) Architectural options: chunk the pasted file and review it per-function with
 a map-reduce pass, so no single request carries 45k tokens (**risk:** loses
 cross-file/whole-file context, and the reduce step can miss interactions);
 pre-index the repository and retrieve only relevant sections (**risk:** retrieval
 misses — now you own a retrieval quality problem, Days 14–15); cache the
 repository's stable portions as a shared prefix (**risk:** only works if the
 prefix is genuinely stable and shared across requests). Any of these with a
 stated risk scores full marks; "use a bigger GPU" or "use a smaller model" does
 not — those are hardware and quality changes, not architecture.

---

## Scoring and what to do next

| Score | Read this as |
| --- | --- |
| 90–100 | Solid. Move on to Day 6. |
| 70–89 | Fine. Note which section lost points and re-skim that day's §3. |
| 60–69 | Re-read the weakest day fully and redo its lab before Day 6. |
| < 60 | Redo Days 3–5 including labs. This material is load-bearing for Days 6–8 and Day 21; skipping forward will not work. |

Log your score in `lab-notebook.md`. You'll want the comparison at Checkpoint 4.
