# Day 7 — INT8, INT4, FP8, AWQ, GPTQ, and When Quantization Hurts Quality

**Time:** ~3.5 h · **Prereqs:** Days 3–5 · **Lab needs:** Ollama with 2–3 quants

---

## 0. Warm-up review

1. Decode step time floor formula. Why does quantization help decode? (Day 3)
2. Why does quantization barely help prefill? (Day 3)
3. What must be true for a prompt cache to hit? (Day 6)
4. Which caching type can return a wrong answer, and by what mechanism? (Day 6)

---

## 1. The one-paragraph version

Quantization stores weights (and sometimes activations and KV cache) in fewer
bits. Because decode is memory-bandwidth-bound, **halving the bytes roughly
halves the decode step time** — the speedup is close to the compression ratio,
and the memory freed buys you more concurrent sequences on top. The cost is
approximation error, and the critical fact is that this error is **not uniformly
distributed**: aggregate benchmarks barely move while specific capabilities —
long-context recall, rare entities, multi-step arithmetic, low-resource
languages, strict format adherence — degrade sharply. INT8 is nearly free. INT4
is usually acceptable with a good method. INT4 with a bad method, or on a small
model, or on a task you didn't test, is where systems quietly get worse and
nobody notices for a quarter.

---

## 2. Why this bites you

The pattern is almost always the same:

A team quantizes to INT4 to cut serving costs by 60%. They validate on MMLU: 68.2%
→ 67.6%. Well within noise. Ship it.

Three weeks later: the JSON output failure rate has gone from 0.5% to 4%. Tool
calls with long argument lists get truncated. The Spanish-language experience is
noticeably worse. A customer complains that the model no longer finds information
in the second half of long documents. None of these appear on MMLU, because MMLU
is 4-way multiple choice on short prompts in English — it exercises almost none
of the capabilities that quantization damages first.

**The failure isn't that quantization hurt quality. It's that the eval didn't
measure the quality that quantization hurts.** That's a Day 16 problem showing up
on Day 7, and it's the reason these two days are linked in the syllabus.

---

## 3. Core concepts

### 3.1 What quantization actually does

A weight matrix in FP16 stores each value in 16 bits. Quantization maps a group
of values onto a small integer grid plus metadata:

```
w_quantized = round(w / scale) + zero_point       # store as INT8 / INT4
w_dequantized = (w_quantized − zero_point) × scale
```

- **scale** — the step size of the grid, per group of weights.
- **zero_point** — offset, for asymmetric schemes (needed when the distribution
  isn't centred on zero).
- **group size** — how many weights share one scale. 128 is the common default.
  Smaller groups = better accuracy, more metadata overhead.

The metadata matters more than people expect. INT4 with group size 128 and FP16
scales costs about `4 + 16/128 ≈ 4.125` bits per weight, not 4. With group size
32 it's ~4.5 bits. That's the accuracy/size dial, and it's why "4-bit" models
vary in real size.

### 3.2 The formats

| Format | Bits | Range behaviour | Typical use | Notes |
| --- | --- | --- | --- | --- |
| **FP16 / BF16** | 16 | Floating | Baseline | BF16 has wider exponent, less prone to overflow in training |
| **FP8 (E4M3)** | 8 | 4 exp / 3 mantissa | Weights + activations | Hardware support on Hopper/Ada+; near-lossless |
| **FP8 (E5M2)** | 8 | 5 exp / 2 mantissa | Gradients, wide-range tensors | More range, less precision |
| **INT8** | 8 | Fixed-point | Weights, sometimes activations | Very mature; near-lossless with good handling |
| **INT4** | 4 | Fixed-point | Weights only, almost always | Needs a good method; where quality risk lives |
| **NF4** | 4 | Normal-float, nonuniform | QLoRA fine-tuning | Grid matched to a normal distribution |
| **INT2/3** | 2–3 | Fixed-point | Research | Usually not production-viable |

**Integer vs. float at the same bit width:** FP8 spends bits on an exponent, so
it handles wide dynamic range gracefully — important because LLM activations have
extreme outliers. INT8 has uniform steps, so a single outlier stretches the scale
and crushes precision for everything else. That single sentence explains most of
the difficulty in §3.3.

### 3.3 The outlier problem — the core difficulty

Empirically, transformer activations contain a small number of channels with
magnitudes 10–100× the rest, and these outliers are **systematic** (same channels
across inputs) and **load-bearing** — zeroing them destroys the model.

With a per-tensor scale, one outlier at 100× forces a scale large enough to
represent it, so ordinary values quantize to a handful of distinct levels.
Quality collapses. This is why naive INT8 activation quantization failed for
years and why the successful methods are all, fundamentally, ways of handling
outliers:

| Method | Outlier strategy |
| --- | --- |
| **LLM.int8()** (Dettmers et al.) | Detect outlier channels; compute those in FP16, the rest in INT8 (mixed-precision decomposition) |
| **SmoothQuant** (Xiao et al.) | Mathematically migrate activation difficulty into the weights, which quantize more easily |
| **GPTQ** (Frantar et al.) | Weights only; use second-order (Hessian) information to compensate error as it quantizes column by column |
| **AWQ** (Lin et al.) | Weights only; identify *salient* weight channels via activation magnitude and protect them with per-channel scaling |
| **Per-group / finer granularity** | Give outliers a smaller scope to distort |

### 3.4 GPTQ vs. AWQ — the comparison you'll actually be asked about

Both are **post-training, weight-only, ~4-bit** methods requiring a small
calibration dataset. They differ in mechanism and in practical character.

**GPTQ** — *Accurate Post-Training Quantization for Generative Pre-trained
Transformers* (Frantar, Ashkboos, Hoefler, Alistarh). Quantizes weights one
column at a time; after each column, uses approximate second-order (inverse
Hessian) information from the calibration data to adjust the *remaining*
un-quantized weights so they compensate for the error just introduced. It's a
layer-wise reconstruction that solves "minimize output error given this layer's
inputs."

- Strong accuracy, well-established, wide tooling support.
- Requires the Hessian computation — slower to produce than AWQ.
- **More sensitive to calibration data**: it's explicitly fitting error
  compensation to the calibration distribution, so calibrating on Wikipedia and
  serving code is a real risk.

**AWQ** — *Activation-aware Weight Quantization* (Lin, Tang, Tang, Yang, Dang,
Han). Observes that not all weights matter equally: the ~1% of weight channels
that multiply large-magnitude activations dominate output error. Rather than
keeping them in higher precision (which complicates kernels), AWQ finds a
per-channel scaling that reduces their relative quantization error, then
quantizes everything uniformly.

- Comparable or better accuracy than GPTQ at 4-bit on many models.
- **Less calibration-sensitive** — it only needs activation *magnitude*
  statistics, not a full second-order fit.
- Hardware-friendly: uniform format, no mixed precision, fast kernels.

**Practical guidance:** default to AWQ for 4-bit weight-only serving, especially
if your calibration data doesn't closely match production traffic. Use GPTQ where
your tooling supports it better or where you've measured it winning on *your*
eval. Do not choose based on a leaderboard number — the differences are within
noise on aggregate benchmarks and diverge on the specific capabilities in §3.6.

**Calibration data is the underrated variable.** Both methods use it, and both
degrade when it's unrepresentative. If you serve code, calibrate on code. If you
serve six languages, calibrate on six languages. If you serve 30k-token contexts,
calibrate with long sequences. A team that calibrates on 128 random Wikipedia
paragraphs and serves a JSON-emitting code assistant has introduced a distribution
mismatch, and will spend weeks blaming the model.

### 3.5 What gets quantized: three separate decisions

| Target | Typical | Effect | Risk |
| --- | --- | --- | --- |
| **Weights** | INT4/INT8/FP8 | Shrinks model → faster decode, more KV room | Moderate at 4-bit |
| **Activations** | FP8/INT8 | Enables faster matmul kernels → helps prefill | Higher — outliers live here |
| **KV cache** | FP8/INT8/INT4 | More concurrency (Day 4) | Degrades long-context recall specifically |

These are independent, and people conflate them constantly. "W4A16" means 4-bit
weights, 16-bit activations — the most common serving configuration, and it
helps decode but not prefill. "W8A8" quantizes both and speeds up prefill too.
KV cache quantization is a third axis entirely, and it's the one whose damage is
hardest to see because it only appears at long context.

**One consequence worth internalizing:** since weight-only quantization doesn't
speed up prefill (Day 3), a team with prefill-dominated traffic can quantize to
INT4, take real quality risk, and see almost no latency improvement. Check which
phase dominates *before* you accept quality risk. This is the single most common
wasted quantization project.

### 3.6 Where quality actually goes — the damage profile

This is the part that isn't in most write-ups. Ordered roughly by how early they
degrade:

1. **Long-context recall.** First and worst, especially with KV quantization.
   Small errors compound across attention over many positions. A model that
   retrieves reliably at 4k may fail at 32k after 4-bit quantization while
   scoring identically on short benchmarks.
2. **Rare tokens and entities.** Proper nouns, product SKUs, uncommon identifiers,
   and code symbols live in low-density regions of the weight space and are the
   first casualties. Expect more hallucinated IDs (which connects directly to
   Day 11).
3. **Format adherence.** Strict JSON, exact schema fields, closing brackets in
   deeply nested structures. Degrades because it depends on low-probability-margin
   decisions at specific positions. **This is why "we quantized and our JSON
   failure rate tripled" is a recurring story** (Day 10).
4. **Multi-step arithmetic and chained reasoning.** Errors compound across steps;
   a small per-step probability shift produces a large end-to-end shift.
5. **Low-resource languages.** Underrepresented in both training and calibration
   data; less redundancy to absorb error.
6. **Calibration/confidence.** The model's probability distribution flattens or
   sharpens, which breaks any threshold you tuned on logprobs — including
   confidence-gated routing (Day 13) and abstention behaviour.
7. **Aggregate benchmark accuracy.** Moves last, and least. **This is the one
   most teams measure.**

Read that list top to bottom, then note that the standard validation approach
tests item 7. The mismatch is the whole problem.

### 3.7 When quantization hurts most

- **Small models.** A 70B model has redundancy to absorb error; a 1–3B model does
  not. INT4 on a 1.5B model is often visibly worse. Rule of thumb: the smaller
  the model, the higher the bit width you should keep.
- **Already-distilled or already-pruned models.** Redundancy has been spent
  already. Compression techniques don't stack for free.
- **Long-context workloads.** See §3.6 item 1.
- **Strict-output workloads.** Structured output, tool calling, code generation.
- **Anything where you tuned a threshold on model probabilities.** Quantization
  moves the distribution; your threshold no longer means what it meant.

### 3.8 The decision procedure

```
1. Establish which phase dominates (Day 3). Prefill-dominated? Weight-only
   quantization will not help you much — stop and reconsider.
2. Build a task-specific eval that exercises your actual failure surface:
   long context, strict format, rare entities, every language you serve,
   multi-step reasoning. NOT just an aggregate benchmark. (Day 16)
3. Baseline at FP16/BF16. Record per-category scores, not one number.
4. Try FP8 first if your hardware supports it. Near-lossless, minimal risk.
5. Then INT8 weight-only. Still near-lossless on most models.
6. Then 4-bit via AWQ (or GPTQ) with calibration data drawn from YOUR traffic.
7. Compare per-category. Accept only if every category is within your tolerance —
   not just the average.
8. Ship behind a flag. Run both in shadow for a week (Day 17). Watch the
   category metrics, not the aggregate.
9. Re-run the eval when the base model, kernels, or serving stack change.
   Quantization quality is not a one-time measurement.
```

Step 7 is where discipline is required, and step 2 is where the work is.

---

## 4. Napkin math

### 4.1 The speed and capacity win

70B model, H100 (3.35 TB/s), 80 layers, 8 KV heads, head_dim 128, 8k context.

| Precision | Weights | Decode floor | KV/token | KV per 8k seq | Free KV on 2×H100 (160GB) | Concurrency |
| --- | --- | --- | --- | --- | --- | --- |
| FP16 | 140 GB | 24 tok/s | 320 KiB | 2.5 GiB | ~14 GiB | **5** |
| FP8 W | 70 GB | 48 tok/s | 320 KiB | 2.5 GiB | ~84 GiB | **33** |
| INT4 W | 35 GB | 96 tok/s | 320 KiB | 2.5 GiB | ~119 GiB | **47** |
| INT4 W + FP8 KV | 35 GB | 96 tok/s | 160 KiB | 1.25 GiB | ~119 GiB | **95** |

From 5 concurrent sequences to 95 — **19×** — plus 4× the per-stream decode
speed. Combined with Day 5's batching curve, the aggregate throughput difference
between row 1 and row 4 is well over an order of magnitude on identical hardware.

That's the case for quantization, and it's overwhelming. Which is exactly why
§3.6 needs to be taken seriously — a 19× incentive makes people skip the
validation.

### 4.2 Pricing the quality risk

Your service does 400k requests/day. Quantizing INT4 saves 65% of serving cost:
$9,000/day → $3,150/day = **$2.1M/year**.

Your JSON failure rate goes 0.5% → 3.5%. Each failure triggers a repair retry
(Day 10) at 1.4× cost, and 15% of failures escape to the user.

```
extra failures/day  = 400,000 × 0.03 = 12,000
escaped to users    = 12,000 × 0.15  = 1,800/day
retry cost          ≈ 12,000 × $0.008 × 0.4 ≈ $38/day   (negligible)
```

The $38 is noise. The question is entirely **what 1,800 bad user experiences per
day are worth**, and that's a product judgment, not an engineering one — but it
must be *made*, explicitly, with the number in front of the person making it.
The engineering failure isn't choosing wrong; it's shipping without ever
producing the 1,800.

### 4.3 The metadata correction

"We're using 4-bit, so a 70B model is 35 GB." Check it:

```
group size 128, FP16 scale + FP16 zero-point per group:
  bits/weight = 4 + (16 + 16)/128 = 4.25
  70e9 × 4.25 / 8 = 37.2 GB          ← 6% more than the naive estimate

group size 32:
  bits/weight = 4 + 32/32 = 5.0
  70e9 × 5 / 8 = 43.75 GB            ← 25% more
```

If you sized a GPU allocation on "35 GB," group size 32 puts you 8.75 GB over
budget. Always compute from the actual group size and metadata format.

---

## 5. Failure modes

| Symptom | Cause | Detection |
| --- | --- | --- |
| Benchmarks fine, users unhappy | Eval doesn't cover the damaged capabilities | Per-category eval (Day 16) |
| JSON failure rate tripled | Format adherence degraded | Schema-validation failure rate metric (Day 10) |
| More hallucinated IDs | Rare-token precision lost | Tool-argument validation failure rate (Day 11) |
| Long documents worse, short fine | Long-context recall / KV quantization | Length-stratified eval |
| Non-English degraded | Calibration data was English-only | Per-language eval |
| Confidence routing misfires | Probability distribution shifted | Recalibrate thresholds after any precision change |
| "Quantized but no speedup" | Prefill-dominated workload, or dequantizing to FP16 at runtime | TTFT/decode split; bandwidth efficiency check |
| OOM despite "4-bit" | Ignored group metadata overhead | Compute from real bits/weight |
| Worked at 70B, broke at 3B | Small models lack redundancy | Model-size-aware bit-width policy |

---

## 6. Lab (90 min)

### Part A — Verify the bandwidth theory (25 min)

Pull the same model at multiple quantizations:

```powershell
ollama pull llama3.2:3b-instruct-fp16    # ~6.4 GB
ollama pull llama3.2:3b-instruct-q8_0    # ~3.4 GB
ollama pull llama3.2:3b-instruct-q4_K_M  # ~2.0 GB
ollama list                               # note the actual on-disk sizes
```

**⚠ Size the base model to your VRAM, or this lab measures the wrong thing.**
The comparison only works if **every** variant fits in VRAM. The moment one spills
to system RAM, its decode speed is set by PCIe rather than by HBM, the ratio
collapses, and you'll conclude quantization is far more effective than it is.

| Your VRAM | Use this base | Because fp16 needs |
| --- | --- | --- |
| 8 GB (e.g. RTX 3070 Ti) | **3B** | 6.4 GB — fits; an 8B fp16 at ~16 GB does not |
| 12–16 GB | 7–8B | ~16 GB — tight at 16, fine above |
| 24 GB+ | 8B, or 13B | ~26 GB for 13B |

Check with `nvidia-smi` while a model is loaded: if VRAM use plateaus below the
model size, part of it is on the CPU and this lab is invalid. Verify before you
trust any number in it.

Reuse `day03_phases.py`. For each variant, fixed 512-token prompt and 256-token
output, five runs, record median `decode_tok_s` and `prefill_tok_s`.

| Variant | On-disk GB | decode tok/s | ratio vs fp16 | prefill tok/s | ratio |
| --- | --- | --- | --- | --- | --- |

**Predict first.** Then check the two claims from Day 3:
1. `decode_ratio ≈ size_ratio` — the bandwidth bound.
2. `prefill_ratio ≈ 1` — weight-only quantization barely helps prefill.

Both should hold within tens of percent. Where they don't, work out why (fixed
per-step overheads dominate at small models; dequantization cost; kernel quality).
Write the explanation down — this is the moment the theory becomes yours.

Also: divide on-disk size by (params × 1/8) to get actual bits/weight. Is q4_K_M really
4 bits? (It isn't. Find the real number.)

### Part B — Find the damage (45 min)

Build a five-category mini-eval and run it against fp16 and q4.

```python
# lab-output/day07_damage.py
import httpx, json, re

MODELS = ["llama3.2:3b-instruct-fp16", "llama3.2:3b-instruct-q4_K_M"]

def gen(model, prompt, n=300):
    r = httpx.post("http://localhost:11434/api/generate",
        json={"model": model, "prompt": prompt, "stream": False,
              "options": {"temperature": 0, "num_predict": n}}, timeout=600)
    return r.json()["response"]

# --- Category 1: strict JSON format adherence (25 trials) -------------------
SCHEMA_PROMPT = ('Return ONLY valid JSON matching exactly: '
                 '{"name": string, "tags": [string], "score": number, '
                 '"nested": {"a": number, "b": [number]}}. '
                 'Subject: %s. No prose, no markdown fence.')

def json_ok(model, subject):
    out = gen(model, SCHEMA_PROMPT % subject, 200).strip()
    out = re.sub(r"^```(?:json)?|```$", "", out, flags=re.M).strip()
    try:
        d = json.loads(out)
        return (isinstance(d.get("name"), str)
                and isinstance(d.get("tags"), list)
                and isinstance(d.get("score"), (int, float))
                and isinstance(d.get("nested", {}).get("b"), list))
    except Exception:
        return False

# --- Category 2: rare-entity recall ----------------------------------------
# Give a passage containing invented SKUs, ask for one back verbatim.
# --- Category 3: long-context needle (reuse day02_dilution.py at 1k vs 8k) --
# --- Category 4: multi-step arithmetic (chained word problems, exact answer) -
# --- Category 5: instruction adherence under a negative constraint ---------
#     ("Answer without using the letter 'e'" style — measures constraint tracking)

for m in MODELS:
    subjects = [f"Revenant module {i}" for i in range(25)]
    rate = sum(json_ok(m, s) for s in subjects) / len(subjects)
    print(f"{m:38s} json_ok={rate:.0%}")
```

Implement categories 2–5 yourself; 20–25 trials each is enough to see a real
effect. Then fill this table in `lab-notebook.md`:

| Category | fp16 | q4_K_M | Δ |
| --- | --- | --- | --- |
| Aggregate (mean of all) | | | |
| Strict JSON | | | |
| Rare entities | | | |
| Long-context needle | | | |
| Multi-step arithmetic | | | |
| Negative constraint | | | |

**The expected result:** the aggregate row moves less than the worst individual
row, probably much less. If you had only measured the aggregate, you'd have
concluded quantization was free. Write down which category degraded most and
whether you'd have caught it with a standard benchmark.

### Part C — The decision memo (20 min)

Write the memo you'd actually send. Half a page:

> **Recommendation:** [ship / don't ship] q4_K_M for [workload].
> **Savings:** [derived from Part A + your traffic].
> **Measured quality delta:** per-category table.
> **Risk accepted:** [category] degrades by [x]%, affecting an estimated [n]
> requests/day.
> **Mitigations:** [repair loop / route strict-format requests to fp16 / …]
> **Rollback trigger:** [metric] exceeds [threshold] for [duration].

That last line is the one that separates an engineer from a benchmark-runner.
Every compression decision needs a rollback trigger defined *before* deploy, or
you have shipped an unfalsifiable change.

---

## 7. Quiz

**1.** Why does weight quantization speed up decode roughly in proportion to the
compression ratio, but barely speed up prefill?

**2.** Explain the outlier problem. Why is FP8 more robust to it than INT8 at the
same bit width?

**3.** Compare GPTQ and AWQ: mechanism, calibration sensitivity, and when you'd
pick each.

**4.** What is "W4A16"? What does it help, and what does it not help?

**5.** List the damage profile from §3.6 in order. Which item do standard
benchmarks measure, and where does it sit in the order?

**6.** A 13B model is quantized to INT4 with group size 32 and FP16
scale+zero-point. Compute actual bits per weight and total size. How far off is
"6.5 GB"?

**7.** A team quantizes to INT4 and reports "no latency improvement." Give two
distinct explanations and the measurement that distinguishes them.

**8.** Why does quantization break a confidence-gated routing threshold, and what
must you do after any precision change?

**9.** Your calibration set is 128 English Wikipedia paragraphs. You serve a
code-generation product handling 20k-token contexts. Name three specific ways this
mismatch will hurt you.

**10.** A 1.5B model and a 70B model are both quantized to INT4. Which degrades
more, and why? What policy follows?

---

## 8. Review hooks

- *Quantization's decode speedup ≈ the compression ratio. That's the bandwidth bound.*
- *Weight-only quantization doesn't help prefill. Check which phase dominates first.*
- *The error isn't uniform. Long context, rare tokens, and format go first.*
- *Aggregate benchmarks move last and least. They're the wrong instrument.*
- *Calibrate on your traffic, not on Wikipedia.*
- *Small models have no redundancy to spend.*
- *Every compression decision needs a rollback trigger defined before deploy.*

---

## 9. Answer key

**1.** Decode is memory-bandwidth-bound — the step time floor is
`model_bytes / bandwidth`, so halving bytes halves the floor. Prefill is
compute-bound: it's limited by FLOPs, and weight-only quantization typically
dequantizes to a higher precision for the matmul, so the FLOP count is unchanged.
Only quantizing *activations* too (e.g. W8A8) enables genuinely faster matmul
kernels and helps prefill.

**2.** Transformer activations contain a few systematic channels 10–100× larger
than the rest. A shared quantization scale must span the outlier's magnitude, so
ordinary values collapse into very few distinct levels and precision is destroyed.
FP8 allocates bits to an exponent, giving it wide dynamic range with relative
(rather than absolute) precision — so a large value and a small value can both be
represented with reasonable relative accuracy. INT8's uniform grid cannot do this.

**3.** **GPTQ:** column-by-column weight quantization using approximate
second-order (Hessian) information to adjust remaining weights so they compensate
for accumulated error; more calibration-sensitive because it's fitting error
compensation to that data; slower to produce. **AWQ:** identifies salient weight
channels by activation magnitude and applies per-channel scaling to protect them,
then quantizes uniformly; needs only activation magnitude statistics, so it's
less calibration-sensitive and hardware-friendly. **Pick AWQ** by default,
especially when calibration data may not match production; **pick GPTQ** when
tooling favours it or your own eval shows it winning on your tasks.

**4.** 4-bit weights, 16-bit activations. It **helps** decode speed and memory
footprint (hence concurrency). It **does not help** prefill meaningfully, because
activations and the matmuls remain at 16-bit.

**5.** (1) long-context recall, (2) rare tokens/entities, (3) format adherence,
(4) multi-step arithmetic/chained reasoning, (5) low-resource languages,
(6) probability calibration, (7) aggregate benchmark accuracy. Standard
benchmarks measure **item 7 — dead last**, which is why they report "no
regression" for a model that has visibly degraded on items 1–3.

**6.** `bits/weight = 4 + (16+16)/32 = 5.0`. `13e9 × 5 / 8 = 8.125 GB`. The
naive "6.5 GB" estimate is **1.625 GB / 25% low** — enough to break a
tightly-sized GPU allocation.

**7.** (i) The workload is **prefill-dominated**, so shrinking decode time barely
moves E2E — distinguish by splitting TTFT from decode time; if prefill is >70% of
E2E, this is it. (ii) The kernels are **dequantizing to FP16 before the matmul**
without a fused low-precision path, so bytes moved didn't actually drop —
distinguish with the bandwidth efficiency check from Day 3 Part D: compute
`measured_tok_s / (bandwidth / quantized_model_bytes)`; if measured throughput
matches the *FP16* floor rather than the INT4 floor, the weights are being
expanded.

**8.** Quantization perturbs the output logits, changing the shape of the
probability distribution — a logprob that meant "85% confident" pre-quantization
no longer corresponds to the same empirical accuracy. Any threshold tuned on the
old distribution now fires at the wrong rate in an unknown direction. **You must
re-tune every probability-derived threshold** (routing gates, abstention rules,
confidence displays) against a fresh labelled set after any precision change.

**9.** Any three: (a) code tokenizes and activates very differently from prose, so
the calibration statistics misrepresent the served distribution — GPTQ's error
compensation in particular is fitted to the wrong thing; (b) 128 short paragraphs
give no long-sequence statistics, so exactly the long-context behaviour that
degrades first is uncalibrated; (c) English-only calibration degrades any other
language you serve; (d) prose calibration underweights the rare-symbol and
identifier vocabulary that code generation depends on, worsening hallucinated
symbol names; (e) strict-format (JSON/syntax) behaviour is unrepresented, so
format adherence damage goes uncompensated.

**10.** The **1.5B** model degrades more. Larger models carry substantial
parameter redundancy, so quantization error is absorbed by many weakly-correlated
weights; small models encode more information per parameter with less slack, so
the same relative error causes a larger behavioural change. **Policy:** scale bit
width with model size — 4-bit is routine at 30B+, questionable below ~7B, and
generally a bad trade below ~3B, where you should prefer INT8/FP8 or a different
efficiency lever entirely (Day 8).

---

## 10. Going deeper

- **"LLM.int8(): 8-bit Matrix Multiplication for Transformers at Scale"** —
  Dettmers, Lewis, Belkada, Zettlemoyer (arXiv 2208.07339). The outlier-channel
  discovery. Read §3 even if you never use the method.
- **"GPTQ: Accurate Post-Training Quantization for Generative Pre-trained
  Transformers"** — Frantar, Ashkboos, Hoefler, Alistarh (arXiv 2210.17323).
- **"AWQ: Activation-aware Weight Quantization for LLM Compression and
  Acceleration"** — Lin, Tang, Tang, Yang, Dang, Han (arXiv 2306.00978). The
  "1% of weights matter" argument is the most useful intuition in the area.
- **"SmoothQuant: Accurate and Efficient Post-Training Quantization for Large
  Language Models"** — Xiao, Lin, Seznec, Wu, Demouth, Han (arXiv 2211.10438).
- **"FP8 Formats for Deep Learning"** — Micikevicius et al. (arXiv 2209.05433).
  E4M3 vs E5M2 and why the split exists.
- **"QLoRA: Efficient Finetuning of Quantized LLMs"** — Dettmers, Pagnoni,
  Holtzman, Zettlemoyer (arXiv 2305.14314). NF4, and the bridge to Day 9.

---

**Next:** [Day 8 — Speculative Decoding vs. Quantization vs. Distillation](day-08-speculative-decoding.md).
One of the three is lossless. Tomorrow you find out which, and why that changes
how you choose.
