# Day 5 — Continuous Batching, Paged Attention, and Throughput Optimization

**Time:** ~3 h · **Prereqs:** Days 3–4 · **Lab needs:** Ollama; optionally vLLM

---

## 0. Warm-up review

1. KV cache formula, from memory. (Day 4)
2. Why is decode memory-bandwidth-bound? (Day 3)
3. What is preemption thrashing and what does GPU utilization look like during
   it? (Day 4)
4. Name two things a prompt fundamentally cannot do. (Day 1)

---

## 1. The one-paragraph version

Decode wastes almost all of a GPU's compute because it's bandwidth-bound: you
load 140 GB of weights to do arithmetic on one vector. **Batching fixes this by
amortizing** — load the weights once, use them for 64 sequences' worth of
arithmetic. The naive version (static batching) makes every request wait for the
slowest one in its group, which is disastrous when output lengths vary by 50×.
**Continuous batching** schedules at the granularity of a single decode
iteration instead of a whole request: finished sequences leave the batch
immediately and new ones join on the next step. Combined with paged attention's
memory efficiency, this is worth roughly an order of magnitude in throughput on
identical hardware — the single largest software lever in LLM serving.

---

## 2. Why this bites you

You're serving an 8B model on an A100 and getting 40 tokens/sec aggregate.
Your GPU monitor shows 95% utilization, so you conclude you're at capacity and
request three more GPUs.

You are not at capacity. You are at **batch size 1**, where the GPU spends
~95% of every decode step waiting on memory. The "95% utilization" number counts
any cycle in which a kernel is resident, including cycles spent stalled on HBM.
The same hardware with continuous batching and a properly sized KV pool will do
**1,500–3,000 tokens/sec aggregate**. The three GPUs you asked for would have
been a 3× improvement on a number that was 40× too low.

This is the most expensive misdiagnosis in the field, and it happens because GPU
utilization is a nearly useless metric for LLM decode. The metrics that matter
are **batch occupancy**, **KV utilization**, and **goodput** — you'll add all
three on Day 17.

---

## 3. Core concepts

### 3.1 Why batching works: arithmetic intensity, again

One decode step, batch size B:

- **Bytes moved:** the model weights, **once**. Independent of B.
- **FLOPs:** proportional to B.
- **Arithmetic intensity:** proportional to B.

So until you exhaust the compute units, **increasing B is nearly free in time**.
Batch 1 and batch 32 take almost the same wall-clock per step; you get 32× the
tokens.

Illustrative shape for a 70B FP16 model on an H100:

| Batch | Step time | Tokens/step | Aggregate tok/s | Per-user tok/s |
| --- | --- | --- | --- | --- |
| 1 | 42 ms | 1 | 24 | 24 |
| 8 | 44 ms | 8 | 182 | 23 |
| 32 | 52 ms | 32 | 615 | 19 |
| 64 | 68 ms | 64 | 941 | 15 |
| 128 | 110 ms | 128 | 1,164 | 9 |

Read the last two columns together — that's the entire throughput/latency
tradeoff of LLM serving in one table.

- 1 → 32: **26× the throughput** for a 21% per-user slowdown. Free money.
- 32 → 64: 1.5× throughput, per-user drops another 21%. Judgment call.
- 64 → 128: 1.24× throughput for a 40% per-user hit. You've left the
  memory-bound regime and entered the compute-bound one — the amortization is
  exhausted and you're now just queuing work behind work.

**The knee is where the marginal throughput gain stops beating the marginal ITL
cost.** Where exactly it sits depends on your model, hardware, and — critically
— your SLA. A batch limit is not a performance setting; it's an SLA decision
encoded as a number. Treat it that way and set it from your latency budget, not
from a benchmark.

### 3.2 Static batching and why it fails

Static (request-level) batching: collect B requests, run them together, return
all B when all are done.

```
Static batch of 4, step-by-step (■ = generating, · = idle-but-occupying-a-slot):

req A (12 tok) ■■■■■■■■■■■■······································
req B (140 tok)■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
req C (30 tok) ■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■····················
req D (18 tok) ■■■■■■■■■■■■■■■■■■································
                                    ↑
                       everyone waits for B; slots wasted
```

Two compounding problems:
1. **Head-of-line blocking.** A finishes in 12 steps and waits 128 more to be
   returned. Its user's latency is set by a stranger's request length.
2. **Slot waste.** Those `·` cells are occupied KV blocks doing nothing. In real
   traffic, output lengths span 10–2,000 tokens, so utilization of a static batch
   is often under 30%.

You cannot fix this by tuning B. The structure is wrong.

### 3.3 Continuous batching

Schedule per **iteration**, not per request (introduced as *iteration-level
scheduling* in Orca, Yu et al., OSDI '22). After every decode step, the scheduler
re-evaluates: any sequence that emitted EOS is returned to its caller
immediately and its KV blocks are freed; any waiting request that fits in the
freed space is admitted and prefilled into the batch.

```
Continuous batching, same four requests plus arrivals E, F, G:

slot 0: AAAAAAAAAAAA EEEEEEEEEEEEEEEEEEEE GGGGGGGGGG···
slot 1: BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB
slot 2: CCCCCCCCCCCCCCCCCCCCCCCCCCCCCC FFFFFFFFFFFFFFF
slot 3: DDDDDDDDDDDDDDDDDD  ← freed here, refilled next step
                            ↑ A returns to its user at step 12, not step 140
```

Consequences:
- Latency is decoupled from batch-mates. A's user waits 12 steps.
- Slot occupancy stays high — freed slots refill within one iteration.
- Throughput under realistic mixed-length traffic improves several-fold over
  static batching, on top of the batching win itself.

The scheduler's decision every iteration is essentially: *given free KV blocks,
which waiting requests can I admit without preempting anyone?* Which is why
Day 4's memory math and today's throughput math are the same problem.

### 3.4 Paged attention's role here

Day 4 covered paged attention as memory management. Its throughput consequence:

Concurrency is `free_kv_memory / kv_per_sequence`. Cut waste from 70% to 4% and
you roughly **triple** the number of sequences that fit. Triple the batch size,
and per §3.1 you get most of a 3× throughput increase *for free* — no new
hardware, no quality change, no model change.

Paged attention and continuous batching are separable ideas that compound:
continuous batching keeps slots busy; paged attention creates more slots. Most
production servers (vLLM, TGI, TensorRT-LLM, SGLang) implement both, which is
why "we wrote our own serving loop" is almost always a mistake at any scale
above a demo.

### 3.5 Mixing prefill and decode: the scheduler's real dilemma

Day 3 introduced the interference problem. Here's how schedulers handle it:

| Policy | Behaviour | TTFT | ITL smoothness | Throughput |
| --- | --- | --- | --- | --- |
| **Prefill-priority** | Run waiting prefills first | Best | Worst (stalls) | Good |
| **Decode-priority** | Finish in-flight decodes first | Worst | Best | Lower |
| **Chunked prefill** | Split prefill into chunks, interleave | Slightly worse for the long request | Best | Best |
| **Disaggregated** | Separate GPU pools for prefill and decode | Tunable | Tunable | Best at scale |

**Disaggregated serving** deserves a note. Since prefill is compute-bound and
decode is bandwidth-bound, running them on the same device means one resource is
always underused. Splitting them across dedicated pools lets you (a) buy
different hardware for each, and (b) scale them independently — a workload with
long prompts and short answers needs many prefill workers and few decode workers.
The cost is transferring the KV cache between pools over the interconnect, which
is why this only pays off at scale and with fast links. Systems like DistServe
and Splitwise explore this; you should know the term and the tradeoff, and you
should not build it yourself.

### 3.6 The metrics that actually describe a serving system

Retire "GPU utilization." Track these:

| Metric | Meaning | Healthy |
| --- | --- | --- |
| **Batch occupancy** | mean running sequences ÷ `max_num_seqs` | 0.6–0.9 |
| **KV utilization** | allocated KV blocks ÷ total | 0.5–0.85 |
| **Goodput** | output tokens/sec that were actually *returned* | maximize |
| **Preemption rate** | preemptions per minute | ~0 |
| **Queue depth / wait time** | requests waiting, and for how long | bounded |
| **TTFT / ITL percentiles** | Day 3's split | inside SLA |

Two of these interact in a way worth memorizing: **high KV utilization plus
nonzero preemption rate means you are over-admitting.** Lower `max_num_seqs`.
Throughput will go *up*, which is counterintuitive until you remember thrashing
pays for prefill repeatedly.

And: **goodput ≠ throughput.** Tokens generated for a request the client already
abandoned, or for a sequence about to be preempted and recomputed, are cost
without value. If your dashboard shows throughput and not goodput, it will look
healthiest exactly when it's thrashing.

---

## 4. Napkin math

### 4.1 Sizing a deployment

> Requirement: 8B model, FP16, 40 requests/sec, average 400 output tokens,
> average 2k input tokens, p95 ITL ≤ 50 ms. A100 80GB (2.0 TB/s).

**Token demand:** 40 × 400 = **16,000 output tokens/sec**.

**Per-GPU capability.** Weights 16 GB, so ~62 GB free for KV.
Llama-3-8B-shaped: 32 layers, 8 KV heads, 128 head_dim, FP16 →
`2×32×8×128×2 = 128 KiB/token`. Sequence with 2k in + 400 out ≈ 2,400 tokens →
`128 KiB × 2400 ≈ 300 MiB`. So `62 GiB / 0.3 GiB ≈ 206` sequences fit in memory.

Memory isn't the constraint here — the ITL SLA is. Decode step floor at batch 1
is `16 GB / 2.0 TB/s = 8 ms`. To hold ITL ≤ 50 ms you have ~6× headroom in step
time; from the §3.1 shape that supports a batch in the **64–128** range.

At batch 96 with a ~30 ms step: `96 / 0.030` = **3,200 tokens/sec per GPU.**

**GPUs needed:** 16,000 / 3,200 = **5**. Add headroom for traffic spikes and
tail context lengths → **6–7**.

Now sanity-check the failure mode: at batch 96, KV use is `96 × 300 MiB = 28
GiB` of 62 — comfortable. But if the p99 input length is 30k rather than 2k, a
single such request needs `128 KiB × 30,400 ≈ 3.7 GiB`, and a handful of them
eats the pool. **Size for the tail of the context distribution, not the mean.**
(Day 4, Q9.)

### 4.2 The cost of getting batching wrong

Same workload, static batching at B=8 with mixed output lengths (~30% slot
utilization):

Effective throughput ≈ `8 × 0.30 / 0.008s` ≈ 300 tok/s per GPU →
16,000 / 300 = **54 GPUs**.

**54 versus 6.** At roughly $2/GPU-hour, that's ~$96/hr versus ~$11/hr —
**$745,000/year** for a scheduling policy. No model change, no quality change,
no prompt change.

---

## 5. Failure modes

| Symptom | Cause | Fix |
| --- | --- | --- |
| Low throughput, "95% GPU util" | Batch size 1 | Enable continuous batching |
| Throughput fine, p99 TTFT awful | Long prefills blocking | Chunked prefill |
| Throughput collapses at peak | Over-admission → thrashing | Lower `max_num_seqs` |
| One user's big request stalls everyone | No prefill chunking | Chunked prefill; consider a separate queue |
| Batch occupancy 0.2 with a full queue | KV exhausted — memory, not scheduling | Shorter context, KV quant, more HBM |
| p50 great, p99 terrible | Head-of-line blocking or preemption | Check preemption counter first |
| Great in benchmarks, bad in prod | Benchmarked uniform lengths | Benchmark with production length distribution |
| Throughput high, users complain | Measuring throughput, not goodput | Track completed-and-delivered tokens |

That second-to-last row deserves emphasis: **benchmarking with fixed-length
inputs and outputs hides every problem in this lesson.** Static batching looks
fine when all requests are the same size. Replay real length distributions.

---

## 6. Lab (80 min)

### Part A — See the batching curve (30 min)

Ollama batches across parallel requests when configured. Set it and measure:

```powershell
$env:OLLAMA_NUM_PARALLEL = "1"   # then 2, 4, 8, 16
$env:OLLAMA_MAX_LOADED_MODELS = "1"
ollama serve
```

Reuse `day04_concurrency.py`. For each `OLLAMA_NUM_PARALLEL` setting, run
concurrency 16 with a fixed 512-token prompt and 128-token output. Record:

| NUM_PARALLEL | Aggregate tok/s | Per-request tok/s | Max E2E |
| --- | --- | --- | --- |

**Predict before running:** aggregate throughput from 1 → 8. If batching is
working, you should see aggregate rise substantially while per-request falls
modestly — the §3.1 table, reproduced on your desk. Plot both columns.

If aggregate throughput doesn't move, your server is serializing. Finding out
*that* is the result — write down how you determined it.

### Part B — Static vs continuous, simulated (25 min)

You probably can't switch Ollama's scheduler, so simulate the difference. This
isolates the scheduling effect from all hardware effects:

```python
# lab-output/day05_sched_sim.py
import random, statistics

random.seed(7)
STEP_MS = 30
N = 400
# realistic: most short, long tail
lengths = [max(8, int(random.lognormvariate(4.0, 1.1))) for _ in range(N)]

def static_batching(lengths, B):
    completions, t = [], 0.0
    for i in range(0, len(lengths), B):
        group = lengths[i:i+B]
        dur = max(group) * STEP_MS          # everyone waits for the longest
        for _ in group:
            completions.append(t + dur)
        t += dur
    return completions, t

def continuous_batching(lengths, B):
    # B slots; each slot pulls the next request the instant it frees
    slots = [0.0] * B
    completions = []
    for L in lengths:
        i = slots.index(min(slots))
        finish = slots[i] + L * STEP_MS
        completions.append(finish)
        slots[i] = finish
    return completions, max(slots)

for B in (4, 16, 64):
    for name, fn in (("static", static_batching), ("continuous", continuous_batching)):
        comps, wall = fn(lengths, B)
        tput = sum(lengths) / (wall / 1000)
        p95 = statistics.quantiles(comps, n=20)[18] / 1000
        print(f"B={B:3d} {name:11s} wall={wall/1000:7.1f}s "
              f"tput={tput:7.1f} tok/s  p95_completion={p95:6.1f}s")
```

Record the ratio at each B. Then change the length distribution to uniform
(`random.randint(100, 120)`) and re-run. **The gap should nearly vanish** —
proving that continuous batching's advantage comes entirely from *length
variance*, which is exactly what synthetic benchmarks remove.

### Part C — Find your knee (25 min)

Using Part A's data, plot aggregate throughput (y) against per-request tokens/sec
(x). Mark the point where a 10% aggregate gain costs more than 10% per-user
throughput. That's your knee.

Then state it as a policy in `lab-notebook.md`:

> "Our ITL SLA is X ms. That implies batch ≤ B. At B, one GPU serves Y tok/s,
> so N req/sec needs Z GPUs, plus headroom for a p99 context of C tokens."

That paragraph is the deliverable of Days 3–5 combined. Every number in it is
one you derived.

---

## 7. Quiz

**1.** Why does increasing batch size barely increase decode step time, up to a
point? What happens at that point?

**2.** From the §3.1 table: going 32 → 128, aggregate throughput rises 1.9× while
per-user falls 2.1×. Under what business circumstance is that a good trade, and
under what circumstance is it clearly bad?

**3.** Draw the static-batching timeline for four requests of 10, 200, 25, and 15
tokens. How many slot-steps are wasted? What's the utilization?

**4.** Explain iteration-level scheduling in two sentences, without using the
phrase "continuous batching."

**5.** A colleague says "GPU utilization is 95%, we're maxed out." Give the
counter-argument and name the three metrics you'd check instead.

**6.** How do paged attention and continuous batching compound? Which creates
slots and which keeps them full?

**7.** Compute: model needs 12 GB weights; GPU has 40 GB; KV is 96 KiB/token;
average sequence is 3,000 tokens. How many sequences fit? If fragmentation wastes
65%, how many fit then?

**8.** Your KV utilization is 0.94 and preemption rate is 15/min. Throughput is
below target. What do you change, and why does throughput improve when you do the
apparently-restrictive thing?

**9.** Under what traffic pattern does continuous batching provide *almost no*
benefit over static batching? Why does this matter for how you benchmark?

**10.** Define goodput and give an example where throughput is high and goodput
is low.

---

## 8. Review hooks

- *Batching amortizes weight loading; that's the whole mechanism.*
- *Static batching's cost is length variance. Continuous batching removes it.*
- *Paged attention creates slots; continuous batching keeps them full.*
- *Batch limit is an SLA decision, not a perf setting.*
- *GPU utilization is a nearly useless LLM metric. Occupancy, KV, goodput.*
- *High KV utilization + preemptions = over-admission. Admit less, get more.*

---

## 9. Answer key

**1.** Decode is memory-bandwidth-bound: the dominant cost is loading weights,
which happens once regardless of batch size, while FLOPs scale with B. So B
grows nearly free until the compute units saturate — at which point the kernel
becomes compute-bound and step time starts rising roughly linearly with B.

**2.** Good when you're throughput-constrained and cost-sensitive with a lenient
per-user latency target — batch/offline processing, background enrichment,
overnight document pipelines. Bad for interactive streaming UX, where perceived
speed is set by ITL: a 2.1× slower stream is very visible, and the throughput
saving doesn't reach the user.

**3.** All four run for `max(10,200,25,15) = 200` steps. Useful slot-steps =
10+200+25+15 = 250. Total slot-steps = 4 × 200 = 800. Wasted = **550**.
Utilization = 250/800 = **31%**.

**4.** The scheduler makes an admission and completion decision after every
single decode step rather than once per batch of requests. Finished sequences are
returned and their memory freed immediately, and waiting requests join the
running batch at the next step.

**5.** GPU utilization counts cycles with a resident kernel, including cycles
stalled waiting on HBM — which is most of every decode step at low batch size.
Check **batch occupancy**, **KV cache utilization**, and **goodput** (plus queue
depth and preemption rate).

**6.** Paged attention reduces per-sequence memory waste from 60–80% to <4%, so
more sequences fit in the same HBM — it **creates slots**. Continuous batching
refills any freed slot within one iteration instead of at batch boundaries — it
**keeps them full**. More slots × higher occupancy multiply.

**7.** Free memory = 40 − 12 = 28 GiB. Per sequence = 96 KiB × 3,000 = 288 MiB
≈ 0.28 GiB. `28 / 0.28` = **100 sequences**. With 65% waste, only 35% is usable:
**35 sequences**.

**8.** Lower `max_num_seqs` (admission control). Throughput improves because
preemption means prefill work is being paid for repeatedly — the same tokens
processed two or three times. Admitting fewer sequences means each one completes
without recomputation, so the GPU spends its cycles on new work instead of
redone work.

**9.** When all requests have near-identical output lengths — there's no
head-of-line blocking to eliminate. **This matters because synthetic benchmarks
default to fixed lengths**, which makes static batching look competitive and
hides the single biggest scheduling win. Always benchmark with your production
length distribution, including its tail.

**10.** Goodput is output tokens per second that are actually delivered to a
client that still wants them. Example: a thrashing server generates tokens for
sequences that are then preempted and recomputed — high throughput counter, low
goodput. Also: a client disconnected at 2s while the server streams for 30 more.

---

## 10. Going deeper

- **"Orca: A Distributed Serving System for Transformer-Based Generative Models"**
  — Yu, Jeong, Kim, Kim, Chun (OSDI '22). The iteration-level scheduling paper.
  The idea is simple in hindsight and was not obvious before.
- **"Efficient Memory Management for LLM Serving with PagedAttention"** — Kwon
  et al. (arXiv 2309.06180). Read §6's throughput results now that you have the
  memory model.
- **"DistServe: Disaggregating Prefill and Decoding for Goodput-optimized LLM
  Serving"** — Zhong et al. (arXiv 2401.09670). Also **"Splitwise"** — Patel et
  al. (arXiv 2311.18677). Read for the argument, not to implement it.
- **vLLM documentation** on `max_num_seqs`, `gpu_memory_utilization`, and
  `enable_chunked_prefill`. Configuration ages fast; the parameter *meanings*
  map exactly onto Days 4–5.

---

**Checkpoint:** take **[Checkpoint 1](checkpoint-1.md)** now, before Day 6. Timed,
closed book.

**Next:** [Day 6 — Prompt Caching vs. Semantic Caching](day-06-prompt-vs-semantic-caching.md).
