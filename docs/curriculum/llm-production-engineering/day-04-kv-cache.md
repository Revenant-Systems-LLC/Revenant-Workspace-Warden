# Day 4 — KV Cache: Management, Eviction, Reuse, and Memory Pressure at Scale

**Time:** ~3 h · **Prereqs:** Days 2–3 · **Lab needs:** Ollama, calculator

---

## 0. Warm-up review

1. Decode step time floor — write the formula. (Day 3)
2. Which phase does prefix caching accelerate, and which does quantization
   accelerate? (Day 3)
3. Why can adding a relevant document make an answer worse? (Day 2)
4. What does the `verify()` stage of context assembly catch? (Day 2)

---

## 1. The one-paragraph version

For every token the model has processed, it stores a key and value vector in
every layer — the **KV cache**. This is what makes decode O(1) per token instead
of O(N): without it, generating token 1,000 would require re-processing all 999
previous tokens. The cache is enormous, it grows linearly with sequence length
*and* with concurrency, and it lives in the same HBM as your weights. In
production, **KV cache memory — not model size, not FLOPs — is what determines
how many users you can serve at once.** Understanding its size formula, its
eviction policy, and its reuse opportunities is the difference between a server
that handles 200 concurrent sessions and one that OOMs at 30.

---

## 2. Why this bites you

The characteristic incident: your service runs fine for months. Then you launch a
feature that lets users attach documents. Average context goes from 1,500 tokens
to 20,000. Throughput doesn't degrade gracefully — it **falls off a cliff**.
Requests start getting preempted and restarted. p99 latency goes from 3s to 90s.
The GPU utilization graph shows the GPU is *idle* a lot, which makes no sense
until you realize the scheduler can't admit new requests because there's no room
to put their KV cache, so it's running a batch of 4 instead of a batch of 48.

Nothing about the model changed. Nothing about the prompts changed in a way that
would show up in a prompt review. A **13× increase in context length caused a
13× reduction in how many sequences fit in memory**, and everything downstream
of that fell over. You cannot see this coming without the formula in §3.2.

---

## 3. Core concepts

### 3.1 What's actually stored

In self-attention, each token's query attends to the keys and values of all
previous tokens. During decode, the previous tokens' keys and values are
identical to what they were on the previous step — so you compute them once and
keep them. Per token, per layer, you store one key vector and one value vector.

What's *not* cached: the query vectors (recomputed each step, only needed for the
current token) and the MLP activations (not needed across steps). Only K and V.

### 3.2 The size formula — memorize this

```
kv_bytes_per_token = 2 × n_layers × n_kv_heads × head_dim × bytes_per_element
                     ↑
                     K and V
```

Then:

```
kv_bytes_per_sequence = kv_bytes_per_token × sequence_length
total_kv_bytes        = kv_bytes_per_sequence × concurrent_sequences
```

The `n_kv_heads` term is where **grouped-query attention (GQA)** lives. In
classic multi-head attention, `n_kv_heads == n_heads`. GQA shares each KV head
across several query heads, cutting the cache by that ratio. Multi-query
attention (MQA) is the extreme: `n_kv_heads == 1`.

**Worked example — Llama-3-70B-class (80 layers, 64 query heads, 8 KV heads,
head_dim 128, FP16):**

```
per token = 2 × 80 × 8 × 128 × 2 bytes
          = 327,680 bytes
          = 320 KiB per token
```

At 8,192 tokens: `320 KiB × 8192` = **2.5 GiB for one sequence.**

Now the punchline. On an 80 GB H100 holding a 140 GB FP16 70B model — it doesn't
fit; use 2×H100 = 160 GB, leaving ~20 GB for cache after overhead:

```
20 GiB / 2.5 GiB = 8 concurrent sequences at 8k context
```

**Eight.** That's your concurrency, and it has nothing to do with FLOPs.

**Contrast — an MHA model without GQA** (Llama-2-13B: 40 layers, 40 heads,
head_dim 128, FP16):

```
per token = 2 × 40 × 40 × 128 × 2 = 819,200 bytes = 800 KiB/token
```

A 13B model with **2.5× the per-token cache cost of a 70B model with GQA.** This
is why GQA became universal, and why "how big is the model" tells you almost
nothing about how many users it serves.

### 3.3 Fragmentation: the problem PagedAttention solved

Naively, you allocate a contiguous buffer per sequence sized to `max_seq_len`.
Three kinds of waste follow:

- **Internal fragmentation.** You reserved 8,192 tokens; the request used 340.
  The other 7,852 slots are reserved and unusable by anyone.
- **Reservation waste.** Space reserved for future tokens of an active sequence
  that may never be generated.
- **External fragmentation.** Free memory exists but not in a contiguous block
  big enough for a new sequence.

The vLLM paper measured naive systems wasting **60–80%** of KV memory this way.

**PagedAttention** applies the operating-systems solution: split the KV cache
into fixed-size **blocks** (typically 16 tokens), keep a per-sequence **block
table** mapping logical positions to physical blocks, and allocate blocks on
demand. The attention kernel is rewritten to gather from non-contiguous blocks.
Waste drops to **under 4%**, which translates directly into more concurrent
sequences on the same hardware. We'll cover the throughput consequences tomorrow;
today's point is that it's a *memory management* technique, and its win is
measured in sequences-per-GB.

The bonus property: because blocks are indirected through a table, **two
sequences can point at the same physical block.** That's copy-on-write sharing,
and it's the mechanism behind the next section.

### 3.4 Reuse — where the free performance lives

Three distinct reuse mechanisms, often confused:

**1. Prefix sharing / automatic prefix caching.** Two requests with the same
leading tokens can share the KV blocks for that prefix. If 500 concurrent
requests share a 2,000-token system prompt, you store it **once** instead of 500
times, and — the bigger win — you skip prefill for it entirely. This is what your
hosted provider's "prompt caching" exposes as a product (Day 6). Requirements:
the prefix must match **exactly, from token zero**. One character difference at
position 3 destroys sharing for everything after it.

**2. Fork / copy-on-write.** Beam search, parallel sampling, and
generate-N-candidates all share the prompt's blocks and diverge only where they
actually differ. Sampling 8 candidates costs ~1 prompt's worth of cache, not 8.

**3. Cross-request cache persistence.** Keeping a conversation's KV cache alive
between turns so turn 5 doesn't re-prefill turns 1–4. Enormous win for chat, and
the reason it isn't universal is that holding cache for an idle user consumes
memory that a live user needs — which is exactly the eviction problem below.

### 3.5 Eviction and preemption: what happens when you run out

You've admitted a batch. A sequence needs one more block. There is none. The
scheduler must choose:

| Policy | Mechanism | Cost | When it's right |
| --- | --- | --- | --- |
| **Reject** | 429 the new request | User-visible failure | Protecting SLA of in-flight work |
| **Queue** | Don't admit until space frees | Higher TTFT | Almost always the default |
| **Recompute (preempt)** | Drop a victim's KV blocks; re-prefill later | Wasted prefill compute | Cheap for short sequences |
| **Swap** | Move victim's blocks to CPU RAM, copy back later | PCIe transfer time | Cheap-ish for long sequences over fast links |
| **Evict prefix cache** | Drop shared/idle cached prefixes first | Future cache misses | Always try this first |

Victim selection is usually last-arrived (preempt the newest, protecting work
already invested) or longest-remaining. The pathology to know by name is
**preemption thrashing**: the system preempts a sequence, admits another,
preempts *that* to resume the first, and so on — every request repeatedly paying
prefill it already paid. Throughput collapses while GPU utilization looks
*high*, because it's busy doing the same prefill over and over. Detect it with a
preemption-count metric (Day 17); fix it by admitting fewer sequences.

Two hard-won operational rules:

- **Admission control beats eviction.** Refusing to start work you can't finish
  is cheaper than starting and abandoning it. Set `max_num_seqs` so the *typical*
  batch fits with headroom, and let requests queue.
- **The dangerous config is the optimistic one.** A server tuned to run at 97%
  KV utilization on average will thrash the moment context length distribution
  shifts a little — and it always shifts.

### 3.6 Reducing the cache itself

When memory is the binding constraint, four levers:

| Lever | Typical reduction | Cost |
| --- | --- | --- |
| **GQA / MQA** | 4–8× vs MHA | Architectural — you don't choose it at serving time |
| **KV cache quantization** (FP8/INT8, e.g. KIVI) | 2× (FP16→FP8) | Small quality loss, concentrated in long-context recall |
| **Sliding-window / local attention** | Caps cache at window size | Model can't attend beyond the window |
| **Shorter contexts** (Day 2) | Proportional | It's free. Do this first. |

Note the ordering of effort: three of these are hard, and the fourth is Day 2's
lesson. **Context engineering is also a memory optimization**, which is a point
almost nobody makes and which makes the Day 2 work pay off twice.

---

## 4. Napkin math drills

Do these with the formula, not a calculator app you trust blindly.

**Drill A.** Model: 32 layers, 8 KV heads, head_dim 128, FP16. Context 4,096.
Serving on one 24 GB GPU; weights take 16 GB.

```
per token = 2 × 32 × 8 × 128 × 2 = 131,072 B = 128 KiB
per seq   = 128 KiB × 4096       = 512 MiB
free      = 24 − 16 − ~1 (overhead) = 7 GiB
concurrency = 7 GiB / 512 MiB    = 14 sequences
```

**Drill B.** Same model and hardware, but users start pasting documents: context
rises to 32,768.

```
per seq = 128 KiB × 32768 = 4 GiB
concurrency = 7 / 4 = 1 sequence
```

**From 14 to 1.** An 8× context increase caused an 8× concurrency collapse, and
at concurrency 1 you also lose every batching benefit from Day 5 — so throughput
falls by far more than 8×. **This is the cliff from §2, in three lines of
arithmetic.** Any team that runs this calculation before launching a
document-upload feature avoids the incident entirely.

**Drill C.** Same as B, but quantize the KV cache to FP8 and cap context at 8,192
via context engineering.

```
per token = 2 × 32 × 8 × 128 × 1 = 64 KiB
per seq   = 64 KiB × 8192        = 512 MiB
concurrency = 14 sequences — restored, with 2× the context of Drill A
```

Two levers, applied with the formula, recovered the whole cliff.

---

## 5. Failure modes

| Symptom | Cause | Diagnostic | Fix |
| --- | --- | --- | --- |
| OOM at a fixed concurrency | KV exceeds free HBM | Compute per-seq size | Lower `max_num_seqs`, shorten context |
| Throughput collapses, GPU busy | Preemption thrashing | Preemption/restart counter | Admission control |
| GPU idle, requests queued | Can't admit — no KV room | KV utilization metric | More memory, shorter context, KV quant |
| Cache hit rate near zero | Prefix not byte-identical | Log prefix hash (Day 2 lab) | Prefix discipline (Day 6) |
| Fine in dev, dies in prod | Dev concurrency 1 | Load test at real concurrency | — |
| Long-context recall degrades after KV quantization | FP8/INT4 KV error accumulates over distance | Long-context eval (Day 16) | Keep K in higher precision, or revert |
| p99 latency spikes at a traffic threshold | Crossing into preemption | Correlate p99 with KV utilization | Headroom |

---

## 6. Lab (75 min)

### Part A — Compute before you measure (20 min)

For `llama3.1:8b`, find the real architecture numbers:

```powershell
ollama show llama3.1:8b --modelfile
# or inspect the model card: n_layers, n_kv_heads (GQA), head_dim
```

Llama-3.1-8B: 32 layers, 32 query heads, **8 KV heads**, head_dim 128.

Compute and record in `lab-notebook.md`:
1. KV bytes per token at FP16.
2. Per-sequence bytes at 2k, 8k, 32k, 128k context.
3. Given your GPU's free VRAM after weights, the max concurrency at each length.
4. The same, if the model had no GQA (32 KV heads). How much did GQA buy you?

### Part B — Watch memory move (25 min)

Run `nvidia-smi -l 1` (or `rocm-smi`, or Activity Monitor on Apple Silicon) in
one terminal. In another, issue requests with growing context using the Day 3
harness, and record VRAM after each.

```python
# lab-output/day04_kvgrowth.py — extends day03_phases.py
for ctx in [512, 2048, 8192, 16384, 32768]:
    prompt = ("The Revenant Warden system log entry. " * (ctx // 8))[:ctx * 4]
    m = measure(prompt, num_predict=16)
    print(f"ctx={m['prompt_tokens']:6d}  prefill={m['prefill_s']:.2f}s  "
          f"decode_tok_s={m['decode_tok_s']:.1f}")
    input("record VRAM from nvidia-smi, then Enter...")
```

Plot measured VRAM growth against your predicted `kv_bytes_per_token × tokens`.
The slope should match within tens of percent. If it doesn't, the likely causes
are: the runtime pre-allocates a fixed pool (very common — llama.cpp/Ollama
size the KV cache from `num_ctx` up front, not on demand), or the model is
quantized and the KV cache isn't (or vice versa). **Figuring out which is the
real lab.** Write down your conclusion and the evidence.

### Part C — Find the cliff (30 min)

Send concurrent requests at increasing concurrency (4, 8, 16, 32) at two context
lengths (1k and 16k). Measure aggregate throughput (total output tokens/sec
across all streams) and p95 TTFT.

```python
# lab-output/day04_concurrency.py
import asyncio, httpx, time

async def one(client, prompt, n_predict):
    t0 = time.perf_counter()
    r = await client.post("http://localhost:11434/api/generate",
        json={"model":"llama3.1:8b","prompt":prompt,"stream":False,
              "options":{"temperature":0,"num_predict":n_predict}}, timeout=900)
    d = r.json()
    return {"e2e": time.perf_counter()-t0, "out": d["eval_count"]}

async def run(conc, ctx_tokens):
    prompt = ("log entry " * (ctx_tokens // 2))[:ctx_tokens*4]
    async with httpx.AsyncClient() as c:
        t0 = time.perf_counter()
        rs = await asyncio.gather(*[one(c, prompt, 128) for _ in range(conc)])
        wall = time.perf_counter() - t0
    tput = sum(r["out"] for r in rs) / wall
    print(f"conc={conc:3d} ctx={ctx_tokens:6d} throughput={tput:7.1f} tok/s "
          f"max_e2e={max(r['e2e'] for r in rs):.1f}s")

for ctx in (1024, 16384):
    for conc in (1, 4, 8, 16, 32):
        asyncio.run(run(conc, ctx))
```

**Predict first:** at which (concurrency, context) cell does throughput stop
rising? Then find where the 16k row stops scaling and the 1k row keeps going —
that gap is KV memory pressure, visible in a number you produced yourself.

If Ollama serializes rather than batches (depends on `OLLAMA_NUM_PARALLEL`), set
it and re-run — and note that discovering *that* is itself the Day 5 lesson
arriving early.

---

## 7. Quiz

**1.** Write the KV cache size formula from memory, and say what each term means.

**2.** Model: 60 layers, 8 KV heads, head_dim 128, FP16. Compute KV bytes per
token, and per-sequence bytes at 16k context.

**3.** Same model but MHA with 64 heads instead of GQA with 8. What's the
per-token cost, and what's the ratio?

**4.** You have 30 GB of HBM free after weights. Using Q2's model, how many
concurrent 16k-context sequences fit? What if you cut context to 4k?

**5.** Explain internal fragmentation in a naive KV allocator, and how block-based
allocation fixes it. What block size is typical?

**6.** Two users send requests sharing the first 1,900 tokens, then differing.
User B's request differs at token 3 instead. How much prefix sharing does each
get, and why?

**7.** Define preemption thrashing. What does GPU utilization look like during it,
and why is that misleading?

**8.** Rank the four eviction/preemption responses by cost to an in-flight user,
cheapest first. Justify the top and bottom.

**9.** Your context length distribution shifts from p50=2k/p99=8k to
p50=2k/p99=60k — the median didn't move at all. Explain how this can still take
the service down.

**10.** Name three ways to reduce KV cache footprint, and state the cost of each.
Which one did you already learn on Day 2?

---

## 8. Review hooks

- *KV cache, not model size, sets your concurrency.*
- *`2 × layers × kv_heads × head_dim × bytes` per token. Times length. Times users.*
- *GQA is a memory optimization that happens to be architectural.*
- *Admission control beats eviction.*
- *Preemption thrashing looks like high utilization and is the opposite.*
- *Context engineering is also a memory optimization.*

---

## 9. Answer key

**1.** `2 × n_layers × n_kv_heads × head_dim × bytes_per_element` per token. The
2 is K and V; `n_kv_heads` is the GQA-reduced head count (equal to `n_heads`
under MHA); `head_dim` is per-head width; `bytes_per_element` is 2 for FP16, 1
for FP8/INT8.

**2.** `2 × 60 × 8 × 128 × 2 = 245,760 B = 240 KiB/token`. At 16,384 tokens:
`240 KiB × 16384 = 3.75 GiB`.

**3.** `2 × 60 × 64 × 128 × 2 = 1,966,080 B = 1.875 MiB/token` — **8× more**,
exactly the ratio 64/8.

**4.** 30 GiB / 3.75 GiB = **8 sequences**. At 4k context, per-seq = 960 MiB, so
30 / 0.9375 = **32 sequences**. Four times the context length costs four times the
concurrency, linearly.

**5.** A naive allocator reserves `max_seq_len` contiguous slots per sequence;
requests that generate fewer tokens leave the remainder reserved and unusable —
measured at 60–80% waste. Block-based allocation (PagedAttention) hands out
fixed-size blocks on demand via a per-sequence block table, so waste is bounded
by at most one partly-filled block per sequence — under 4%. Typical block size:
**16 tokens**.

**6.** User A shares ~1,900 tokens of prefix (rounded down to the block boundary
— with 16-token blocks, 1,888). User B shares essentially nothing: the prefix
match is exact-from-token-zero, so a divergence at token 3 means only the first
block (at most) can be shared, and if the difference falls inside block 0, none
of it. Every downstream KV entry depends on all preceding tokens, so a mismatch
poisons everything after it.

**7.** The scheduler repeatedly preempts sequences to admit others, then
re-admits and re-prefills the preempted ones, so the same prefill work is paid
many times. GPU utilization appears **high** — the GPU is genuinely busy — but
goodput (useful completed tokens) collapses. Utilization measures busy-ness, not
usefulness, which is why you need a preemption counter and a goodput metric.

**8.** Cheapest to most expensive for the in-flight user: (1) evict idle prefix
cache — costs only future cache misses, no in-flight user is harmed; (2) queue
new arrivals — raises TTFT for newcomers only; (3) swap to CPU — the victim
pauses for a PCIe round trip but keeps its work; (4) recompute/preempt — the
victim loses all prefill work and pays it again. (Rejecting is worst for the
rejected user but protects everyone else, so its ranking depends on whose SLA
you're defending.)

**9.** Concurrency is set by *actual allocated* KV memory across the in-flight
batch, not by the median. Once even a couple of 60k-token sequences are resident,
they consume the memory that dozens of median requests would have used, so the
scheduler admits far fewer sequences, batch size collapses, per-request
throughput falls, queues build, and TTFT rises for everyone — including the p50
requests that never got longer. **Tail context length is a capacity input, not a
tail-latency detail.**

**10.** Any three: GQA/MQA (architectural — you can't apply it to an existing
checkpoint at serving time); KV cache quantization to FP8/INT8 (~2×, costs
long-context recall accuracy); sliding-window attention (caps footprint, model
loses the ability to attend beyond the window); shorter contexts via context
engineering (free, proportional — **this is Day 2**).

---

## 10. Going deeper

- **"Efficient Memory Management for Large Language Model Serving with
  PagedAttention"** — Kwon et al. (arXiv 2309.06180). §3 (fragmentation
  measurements) and §4 (block tables, sharing) are today's material.
- **"GQA: Training Generalized Multi-Query Transformer Models from Multi-Head
  Checkpoints"** — Ainslie et al. (arXiv 2305.13245). Why the KV head count is
  the number that matters.
- **"Fast Transformer Decoding: One Write-Head is All You Need"** — Shazeer
  (arXiv 1911.02150). The original MQA paper; short, and the motivation is
  entirely about memory bandwidth.
- **"KIVI: A Tuning-Free Asymmetric 2bit Quantization for KV Cache"** — Liu et al.
  (arXiv 2402.02750). Note the asymmetry: keys and values tolerate quantization
  differently. That detail matters when you try it.

---

**Next:** [Day 5 — Continuous Batching and Paged Attention](day-05-batching-paged-attention.md).
You now know why you can only fit 8 sequences. Tomorrow: how to get 20× the
throughput out of them.
