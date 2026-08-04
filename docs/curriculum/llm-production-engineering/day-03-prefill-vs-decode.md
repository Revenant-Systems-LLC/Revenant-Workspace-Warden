# Day 3 — Prefill vs. Decode Latency, and Why They Optimize Differently

**Time:** ~3 h · **Prereqs:** Days 1–2 · **Lab needs:** Ollama running

---

## 0. Warm-up review

1. Name the four costs of a context token. (Day 2)
2. Why does putting the current timestamp in a system prompt cost real money?
   (Day 2)
3. State the harness quality ladder's distinguishing property at stage 5. (Day 1)

---

## 1. The one-paragraph version

An LLM request has two phases with completely different hardware behaviour.
**Prefill** processes your entire prompt in parallel — it is *compute-bound*,
scales with prompt length, and determines time-to-first-token. **Decode**
generates output one token at a time, each step re-reading the entire model from
memory — it is *memory-bandwidth-bound*, scales with output length, and is
almost totally insensitive to how long your prompt was. They respond to opposite
optimizations: prefill wants bigger matrices and more FLOPs; decode wants faster
memory and larger batches. If you have one latency number, you have no
information, because you cannot tell which phase to fix.

---

## 2. Why this bites you

Your dashboard says p95 latency is 8 seconds. What do you do?

Without the split, every answer is a guess. With it:

- **TTFT 7.4s / total 8.0s** → prefill-dominated. Your prompts are enormous.
  The fix is context engineering (Day 2), prefix caching (Day 6), or chunked
  prefill. Buying faster memory does nothing.
- **TTFT 0.3s / total 8.0s** → decode-dominated. The model is generating 900
  tokens. The fix is shorter outputs, a smaller model, quantization (Day 7), or
  speculative decoding (Day 8). Trimming the prompt does nothing.
- **TTFT 6.0s, and it's 0.3s when traffic is low** → not prefill at all. That's
  *queueing*. Your server is saturated and the fix is capacity or batching
  (Day 5). Both prompt trimming and model swaps would be wasted work.

Three completely different engineering programs, indistinguishable from a single
p95 number. This is why "measure TTFT and ITL separately" is the first
instrumentation you add, and why Day 17 treats it as non-negotiable.

---

## 3. Core concepts

### 3.1 The two phases

**Prefill (prompt processing).** All *N* prompt tokens are processed in one
forward pass. Because every token is available simultaneously, the operations
are large matrix-matrix multiplies (GEMM). Each weight matrix is loaded from
memory once and reused across all N tokens — high **arithmetic intensity**
(FLOPs per byte loaded). Modern accelerators have far more FLOPs than bandwidth,
so this phase saturates the compute units. Output: the first token, plus the KV
cache for all N positions (Day 4).

**Decode (generation).** Tokens are produced autoregressively — token *t+1*
needs token *t*. Each step processes exactly **one** token, so the operations are
matrix-*vector* multiplies (GEMV). You load the entire weight matrix to multiply
it by one vector: arithmetic intensity ≈ 1–2 FLOPs/byte, catastrophically low.
The GPU spends nearly all its time waiting on memory. Output: one token, plus one
more KV entry per layer.

```
PREFILL                          DECODE
─────────                        ──────
N tokens at once                 1 token at a time
GEMM (matrix × matrix)           GEMV (matrix × vector)
Compute-bound                    Memory-bandwidth-bound
Arithmetic intensity ~N          Arithmetic intensity ~1
Determines TTFT                  Determines ITL / tokens-per-second
Scales with prompt length        ~Independent of prompt length
Batching: modest gains           Batching: enormous gains
Helped by: more FLOPs,           Helped by: faster HBM, smaller weights,
  chunking, prefix cache           fewer sequential steps
```

That last row is the whole lesson. **Quantization (Day 7) speeds up decode
dramatically and prefill barely at all** — because it shrinks bytes moved, and
decode is bounded by bytes moved. **Prefix caching (Day 6) speeds up prefill
dramatically and decode not at all** — because it skips prompt processing, and
decode isn't prompt processing. People routinely apply the wrong one.

### 3.2 The four latency metrics you must have

| Metric | Definition | Governed by |
| --- | --- | --- |
| **TTFT** — time to first token | request sent → first token received | queueing + prefill |
| **ITL** — inter-token latency | gap between successive output tokens | decode step time |
| **TPOT** — time per output token | mean ITL over the response | same |
| **E2E** — end-to-end | request sent → last token | TTFT + (output_len × TPOT) |

The identity you should be able to write from memory:

```
E2E ≈ queue_time + prefill_time + (output_tokens × decode_step_time)
```

Every optimization in the next six days attacks exactly one of those four terms.
When someone proposes a change, ask which term. If they can't say, they don't
know what they're doing yet.

### 3.3 Napkin math for decode: the bandwidth bound

This is the most useful calculation in the course. Decode step time has a hard
floor set by memory bandwidth:

```
                    model_size_in_bytes
decode_step_time ≥ ─────────────────────
                    memory_bandwidth
```

Because generating one token requires reading every weight at least once.

**Worked example — 70B parameters, FP16, on an H100 (3.35 TB/s HBM3):**

```
weights   = 70e9 params × 2 bytes = 140 GB
step_time = 140 GB / 3.35 TB/s   = 41.8 ms
max rate  = 1 / 0.0418           ≈ 24 tokens/sec     (single stream, no batching)
```

Now quantize to INT8 (1 byte/param):

```
weights   = 70 GB
step_time = 70 / 3350 GB/ms      = 20.9 ms
max rate  ≈ 48 tokens/sec        — exactly 2×
```

**Quantization's decode speedup is essentially the compression ratio.** Not a
coincidence, not a benchmark artifact — it's the bandwidth bound. INT4 gives ~4×.
This is the single strongest argument for quantization, and Day 7 is about what
it costs you in quality.

Reference bandwidths, for re-deriving with whatever you actually have:

| Device | HBM bandwidth | 70B FP16 ceiling |
| --- | --- | --- |
| A100 80GB | ~2.0 TB/s | ~14 tok/s |
| H100 SXM | ~3.35 TB/s | ~24 tok/s |
| H200 | ~4.8 TB/s | ~34 tok/s |
| Consumer RTX 4090 | ~1.0 TB/s | won't fit; 8B FP16 → ~62 tok/s |
| Apple M-series (unified) | ~0.4–0.8 TB/s | why local 70B is slow |

Two sanity checks this gives you for free:
- If a vendor claims 200 tok/s single-stream on a 70B FP16 model, they're
  batching, quantizing, speculating, or lying. The bound says ~24.
- If your local 8B model runs at 12 tok/s on a 4090 (bound ≈ 62), you have a
  configuration problem, not a hardware problem.

### 3.4 Napkin math for prefill: the compute bound

Prefill FLOPs ≈ `2 × params × prompt_tokens` (the 2 is multiply + accumulate;
this ignores attention, which we'll add).

**8B model, 4,000-token prompt, H100 (~990 TFLOPS dense BF16, assume 40% MFU):**

```
FLOPs      = 2 × 8e9 × 4000 = 6.4e13 = 64 TFLOP
achieved   = 990 × 0.40     = 396 TFLOPS
prefill    = 64 / 396       ≈ 162 ms
```

So TTFT ≈ 160 ms + queueing. Double the prompt to 8k → ~320 ms. **Linear.**

Except attention isn't linear. Attention FLOPs scale as `O(N²)` in sequence
length. For small N the linear MLP term dominates and you won't notice; the
quadratic term takes over somewhere in the 8k–32k range depending on model shape.
Practically: **prefill looks linear until it suddenly doesn't.** If you measure
TTFT at 1k and 4k and extrapolate to 100k, you will be badly wrong. Measure the
curve (that's the lab).

### 3.5 Why they need opposite optimizations

| Lever | Prefill | Decode | Day |
| --- | --- | --- | --- |
| Prefix / prompt caching | **huge win** (skips it entirely) | none | 6 |
| Quantizing weights | small (compute-bound already) | **~linear win** | 7 |
| Speculative decoding | none | **big win** (fewer sequential steps) | 8 |
| Larger batch | modest (already saturated) | **huge win** (amortizes weight loads) | 5 |
| Shorter prompt | **linear win** | none | 2 |
| Shorter output | none | **linear win** | 10 |
| Faster HBM | small | **linear win** | — |
| More FLOPs (better GPU compute) | **linear win** | none | — |
| Chunked prefill | reshapes it (see below) | protects it | 5 |

Memorize the shape of this table. It converts "should we try X?" from a debate
into a lookup.

### 3.6 The interference problem, and chunked prefill

Here's the conflict that shapes modern serving. A long prefill occupies the GPU
for hundreds of milliseconds. Any decode steps for *other users already
streaming* have to wait. So one user pasting a 50k-token document introduces a
visible stutter in everyone else's stream. Prefill and decode compete for the
same hardware, and prefill wins by brute force.

**Chunked prefill** (Sarathi / Sarathi-Serve, Agrawal et al.) splits a long
prefill into fixed-size chunks — say 512 tokens — and interleaves them with
decode steps from other requests in the same batch. Effects:

- The long request's TTFT gets slightly **worse** (it's now interrupted).
- Everyone else's ITL gets dramatically **smoother** — no multi-hundred-ms stalls.
- Total throughput usually **improves**, because each batch now mixes a
  compute-heavy chunk with bandwidth-heavy decodes and uses both resources.

This is a deliberate, tunable trade of one user's TTFT for everyone's ITL
stability. Knowing it exists is what lets you answer "why does our p99 ITL spike
at 11am" with "someone is pasting a book, and we haven't enabled chunked prefill"
instead of a shrug.

---

## 4. Worked problem

> A support-summarization endpoint: prompts average 12,000 tokens (long ticket
> threads), outputs average 150 tokens. Running an 8B model, FP16, on one A100
> 80GB (2.0 TB/s, ~312 TFLOPS BF16 dense, assume 35% MFU). Where does the time
> go, and what do you fix?

**Prefill:**
```
FLOPs   = 2 × 8e9 × 12,000 = 1.92e14 = 192 TFLOP
rate    = 312 × 0.35       = 109 TFLOPS
prefill = 192 / 109        ≈ 1.76 s
```

**Decode:**
```
weights   = 16 GB
step      = 16 / 2000 GB/ms ≈ 8 ms
150 tokens × 8 ms           ≈ 1.2 s
```

**E2E ≈ 3.0 s, of which ~59% is prefill.**

Now evaluate proposals:

- *"Quantize to INT8."* Decode 1.2s → 0.6s. E2E 3.0 → 2.4s. **20% gain**, and
  you've accepted quality risk (Day 7) for it.
- *"Cache the prefix."* Only if there *is* a stable prefix. If 11k of the 12k is
  the unique ticket thread, prefix caching saves nothing. **Check the composition
  before proposing this** — this is the most common mistake at this level.
- *"Summarize the thread before summarizing."* Two calls, but the second sees 2k
  instead of 12k. Prefill 1.76s → 0.29s. E2E ≈ 1.5s + the first call. Whether
  this wins depends entirely on how you do the first pass — possibly with a
  much smaller model, or incrementally as messages arrive.
- *"Incremental summarization."* Summarize each new message as it arrives and
  keep a rolling summary. At query time, prompt is ~1.5k. **E2E ≈ 0.22 + 1.2 ≈
  1.4s**, and the cost is amortized into the write path where nobody is waiting.
  This is the winner, and it's an *architecture* change — no model, prompt, or
  hardware change at all.

Note what happened: the best answer came from arithmetic done in four minutes,
and it was invisible without the prefill/decode split.

---

## 5. Failure modes

| Symptom | Likely phase | Diagnostic |
| --- | --- | --- |
| p95 latency high, p50 fine | queueing | Is TTFT high only under load? |
| TTFT high always, scales with input | prefill | Plot TTFT vs prompt tokens |
| First token fast, response crawls | decode | Check ITL; is batch size 1? |
| Stutter mid-stream for some users | prefill interference | Enable chunked prefill (Day 5) |
| Latency fine solo, terrible in prod | batching/queueing | Load-test, don't bench solo |
| "Quantization didn't help latency" | prefill-dominated workload | You optimized the wrong phase |
| "Caching didn't help" | decode-dominated, or unstable prefix | Measure prefix hit rate (Day 6) |
| TTFT nonlinear above 30k tokens | quadratic attention | Expected; plan for it |

---

## 6. Lab (75 min) — Measure the two phases on your own machine

Ollama reports both phases in its response. You're going to use that to build the
curve.

### Part A — Instrument (15 min)

```python
# lab-output/day03_phases.py
import httpx, json, statistics

OLLAMA = "http://localhost:11434/api/generate"
MODEL  = "llama3.1:8b"

def measure(prompt: str, num_predict: int) -> dict:
    r = httpx.post(OLLAMA, json={
        "model": MODEL, "prompt": prompt, "stream": False,
        "options": {"temperature": 0, "num_predict": num_predict},
    }, timeout=600).json()
    ns = 1e-9
    return {
        "prompt_tokens":  r["prompt_eval_count"],
        "output_tokens":  r["eval_count"],
        "prefill_s":      r["prompt_eval_duration"] * ns,
        "decode_s":       r["eval_duration"] * ns,
        "total_s":        r["total_duration"] * ns,
        "prefill_tok_s":  r["prompt_eval_count"] / (r["prompt_eval_duration"] * ns),
        "decode_tok_s":   r["eval_count"] / (r["eval_duration"] * ns),
    }
```

`prompt_eval_duration` is prefill. `eval_duration` is decode. Everything today
is in those two fields.

### Part B — The prefill curve (25 min)

Hold output fixed at 32 tokens. Sweep prompt length: 128, 512, 1k, 2k, 4k, 8k,
16k tokens (pad with filler text, then ask a fixed short question at the end).

**Predict first, in `lab-notebook.md`:** is prefill time linear in prompt length?
At what length, if any, does it bend upward?

Plot `prefill_s` vs `prompt_tokens`. Then plot `prefill_tok_s` — prefill
*throughput*. You should see throughput rise at first (small prompts don't fill
the GPU) then flatten, then possibly decline at long context (quadratic
attention). Record where each transition happens on your hardware.

### Part C — The decode independence check (15 min)

Now the money experiment. Four combinations:

| | 128-token prompt | 8,000-token prompt |
| --- | --- | --- |
| **32 output tokens** | A | B |
| **512 output tokens** | C | D |

Predict all four `decode_tok_s` values before running.

**The result to internalize:** decode throughput in A ≈ C, and B ≈ D. Prompt
length barely moves decode speed. (It moves it a *little* — attention over a
longer KV cache costs more per step — and quantifying that small effect is
exactly what sets up Day 4.) Meanwhile `total_s` differs by seconds across the
grid, entirely explained by which cell you're in.

Compute for each cell: what fraction of E2E was prefill? Write it down. This is
the table you'll reach for the next time someone asks "why is it slow?"

### Part D — Bandwidth reality check (20 min)

Compute the theoretical decode floor for your model and hardware using §3.3.
Look up your GPU's bandwidth (or for Apple Silicon, unified memory bandwidth).
Compare to measured `decode_tok_s`.

```
efficiency = measured_tok_s / theoretical_max_tok_s
```

You should land somewhere in 0.4–0.8. If you're above 1.0, your model is
quantized and you didn't account for it (check `ollama show llama3.1:8b` —
default tags are usually Q4). If you're below 0.2, something is wrong: model
partially on CPU, thermal throttling, or a tiny batch on a huge GPU.

Record the number. On Day 7 you'll re-run this with a different quantization and
check whether the ratio moves the way the theory says it must.

---

## 7. Quiz

**1.** Why is prefill compute-bound and decode memory-bound? Answer in terms of
arithmetic intensity, not by restating the labels.

**2.** Write the E2E latency identity from memory.

**3.** A 32B model, FP16, on an H100 (3.35 TB/s). Compute the single-stream decode
ceiling in tokens/sec. Now for INT4. Show the work.

**4.** A team quantizes their model to INT8 and reports "latency barely changed."
Give the most likely explanation and the measurement that would confirm it.

**5.** Prompt 500 tokens, output 2,000 tokens. Which phase dominates? Name two
optimizations that would help and two that would not.

**6.** Prompt 40,000 tokens, output 40 tokens. Same question.

**7.** Explain chunked prefill: what it costs, what it buys, and whose experience
changes.

**8.** Your TTFT p50 is 200ms and p99 is 4,000ms, while prompt lengths are tightly
distributed around 2k tokens. Prefill can't explain the spread. What can, and how
would you confirm it?

**9.** True or false, defend: "Speculative decoding will improve our TTFT."

**10.** You measure prefill throughput at 1k, 2k, 4k, and 8k tokens and get
9,800 / 11,200 / 11,400 / 9,100 tokens/sec. Explain both the rise and the fall.

---

## 8. Review hooks

- *Prefill is compute-bound, decode is bandwidth-bound. Everything follows.*
- *Decode floor = model bytes ÷ memory bandwidth.*
- *Quantization ≈ decode speedup. Prefix caching ≈ prefill speedup. Don't swap them.*
- *One latency number is no information.*
- *Prefill looks linear until attention goes quadratic.*
- *Chunked prefill trades one user's TTFT for everyone's ITL.*

---

## 9. Answer key

**1.** Prefill multiplies each weight matrix by an N-token activation matrix, so
each byte of weights loaded is used for ~N tokens' worth of FLOPs — high
arithmetic intensity, and accelerators have far more FLOPs than bandwidth, so the
compute units bind. Decode multiplies the same matrix by a single vector: each
byte loaded is used for ~1–2 FLOPs, so the memory system binds and the compute
units idle.

**2.** `E2E ≈ queue_time + prefill_time + (output_tokens × decode_step_time)`

**3.** FP16: 32e9 × 2 = 64 GB; 64 / 3350 GB/s = 19.1 ms → **~52 tok/s**.
INT4: 32e9 × 0.5 = 16 GB; 16 / 3350 = 4.8 ms → **~209 tok/s**. (Real systems land
below these; the ratio is the durable part.)

**4.** Their workload is prefill-dominated — long prompts, short outputs — so
shrinking decode time barely moves E2E. Confirm by splitting TTFT from decode
time: if prefill is >70% of E2E, INT8's decode gain is diluted to near nothing.
(Second possibility: the INT8 kernels aren't actually being used and it's
dequantizing to FP16 at runtime — check with a bandwidth-efficiency calculation.)

**5.** Decode-dominated (2,000 sequential steps vs one 500-token prefill).
**Help:** quantization, speculative decoding, a smaller model, larger batch,
shorter outputs. **Won't help:** prefix caching, prompt trimming, chunked prefill,
faster compute.

**6.** Prefill-dominated. **Help:** prompt trimming/summarization, prefix caching
(if a stable prefix exists), more FLOPs, chunked prefill for others' sake.
**Won't help:** quantization (much), speculative decoding, shorter outputs.

**7.** Splits a long prefill into fixed-size chunks interleaved with other
requests' decode steps in the same batch. **Costs:** the long request's own TTFT
rises. **Buys:** other users stop seeing multi-hundred-ms stream stalls, and
overall throughput usually rises because each batch mixes compute-bound and
bandwidth-bound work. **Whose experience:** the long-prompt user is slightly
worse off; every concurrent streaming user is substantially better off.

**8.** Queueing. Confirm by correlating TTFT with concurrent in-flight requests
and with batch occupancy — if p99 TTFT tracks load rather than prompt length,
it's queue time, not prefill. (Day 5 is the fix; Day 17 is the instrumentation.)

**9.** **False.** Speculative decoding reduces the number of sequential *decode*
steps. The first token is produced by prefill, which speculation does not touch.
It improves TPOT/E2E for long outputs and leaves TTFT essentially unchanged.

**10.** Rise from 1k→4k: small batches of tokens don't saturate the GPU's compute
units, so per-token efficiency improves as the matrices get bigger — classic
small-GEMM inefficiency. Fall at 8k: the O(N²) attention term has grown large
enough to matter, adding FLOPs that scale faster than the token count, so
throughput per token declines.

---

## 10. Going deeper

- **"Efficient Memory Management for Large Language Model Serving with
  PagedAttention"** — Kwon, Li, Zhuang, Sheng, Zheng, Yu, Gonzalez, Zhang, Stoica
  (arXiv 2309.06180). The vLLM paper. Read §2 today for the prefill/decode
  characterization; the rest is Days 4–5.
- **"Sarathi-Serve: Taming Throughput-Latency Tradeoff in LLM Inference"** —
  Agrawal et al. (arXiv 2403.02310). Chunked prefill and the stall-free
  scheduling argument.
- **"Efficiently Scaling Transformer Inference"** — Pope, Douglas, Chowdhery et
  al. (arXiv 2211.05102). The analytical model for arithmetic intensity and the
  memory/compute roofline. Dense, worth the effort.
- **"Making Deep Learning Go Brrrr From First Principles"** — Horace He, 2022.
  The best plain-English explanation of memory-bound vs compute-bound anywhere.
  Read this if §3.1 didn't fully land.

---

**Next:** [Day 4 — KV Cache](day-04-kv-cache.md). Decode is fast because of the KV
cache. Tomorrow you find out what it costs you.
