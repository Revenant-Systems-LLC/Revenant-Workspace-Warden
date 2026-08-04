# Day 8 — Speculative Decoding vs. Quantization vs. Distillation

**Time:** ~3 h · **Prereqs:** Days 3, 5, 7 · **Lab needs:** Ollama with a 1B and 8B model

---

## 0. Warm-up review

1. Where does quality go first under quantization? Name the top three. (Day 7)
2. Why doesn't weight-only quantization help prefill? (Day 3, 7)
3. Batch 1 → 32 gave 26× throughput for a 21% per-user slowdown. Why? (Day 5)
4. What's the one thing a semantic cache key must never omit? (Day 6)

---

## 1. The one-paragraph version

Three ways to make inference faster, with fundamentally different risk profiles.
**Speculative decoding** uses a cheap draft model to propose several tokens, then
verifies them in a single forward pass of the target model — accepting only what
the target would have produced anyway. It is **mathematically output-preserving**:
the distribution of generated text is identical to running the target alone.
**Quantization** shrinks the weights and changes the model's outputs by a small,
unevenly distributed amount. **Distillation** trains a genuinely different,
smaller model — the largest speedup and the largest, most permanent quality
change. The right mental model: speculation buys latency at the cost of *compute
and memory*; quantization buys latency and memory at the cost of *quality*;
distillation buys all three at the cost of *quality plus a training project*.
They compose, and choosing between them starts with which resource you actually
have to spare.

---

## 2. Why this bites you

A team needs to halve latency. Someone proposes quantizing to INT4. Someone else
proposes speculative decoding. The argument goes in circles because nobody has
stated the four things that decide it:

1. **Which phase dominates?** Speculation helps decode only, and does nothing for
   TTFT. Quantization helps decode only. If you're prefill-bound (Day 3), *both*
   proposals are wrong and the answer is context engineering or prefix caching.
2. **What's your batch size?** Speculative decoding's benefit **shrinks as batch
   size grows** — at high batch you're already compute-bound, and speculation
   spends compute to save memory traffic you're no longer bottlenecked on. At
   batch 1 it's a huge win; at batch 128 it can be a *loss*. This single fact
   resolves most arguments and is the least-known thing in the lesson.
3. **Can you afford quality risk?** Speculation: none. Quantization: some,
   unevenly. Distillation: a lot, permanently.
4. **Do you have spare memory?** Speculation needs the draft model resident
   *alongside* the target, competing for KV cache space (Day 4). Quantization
   *frees* memory. These push in opposite directions.

Without those four, it's a taste debate. With them, it's usually a five-minute
decision.

---

## 3. Core concepts

### 3.1 Speculative decoding: the mechanism

Decode is memory-bandwidth-bound: verifying *k* tokens in one forward pass costs
almost the same as generating 1, because either way you load all the weights
once. That slack is the entire opportunity.

```
1. DRAFT:  a small model generates γ candidate tokens autoregressively.
             cheap — small model, γ sequential steps, but tiny weights
2. VERIFY: the target model runs ONE forward pass over [prefix + γ drafts],
             producing its own distribution at each of the γ+1 positions.
3. ACCEPT: walk left to right; accept each draft token per a rejection-sampling
             rule that preserves the target's exact output distribution.
             Stop at the first rejection; emit a correction token from the
             target's (adjusted) distribution at that position.
4. Result: between 1 and γ+1 tokens produced, for the cost of ONE target pass
             plus γ cheap draft steps.
```

The acceptance rule (Leviathan et al.; Chen et al.) is the clever part: if the
draft proposes token *x* with probability `q(x)` and the target assigns `p(x)`,
accept with probability `min(1, p(x)/q(x))`; on rejection, sample from the
normalized residual `max(0, p − q)`. **The resulting token distribution is
provably identical to sampling from the target directly.** Speculative decoding
is not an approximation. That's worth saying twice, because people assume any
speedup must cost quality, and here it genuinely doesn't.

### 3.2 The speedup formula

Let α = acceptance rate (probability a drafted token is accepted), γ = draft
length. Expected tokens per verification step:

```
E[tokens] = (1 − α^(γ+1)) / (1 − α)
```

And the wall-clock speedup, with *c* = cost ratio of one draft step to one target
step:

```
speedup ≈ E[tokens] / (1 + γ·c)
```

**Worked example.** α = 0.8, γ = 4, draft is 1/10 the cost (c = 0.1):

```
E[tokens] = (1 − 0.8^5)/(1 − 0.8) = (1 − 0.328)/0.2 = 3.36
speedup   = 3.36 / (1 + 4×0.1) = 3.36 / 1.4 = 2.4×
```

Now α = 0.6 (a poorly matched draft model):

```
E[tokens] = (1 − 0.6^5)/0.4 = (1 − 0.0778)/0.4 = 2.31
speedup   = 2.31 / 1.4 = 1.65×
```

And α = 0.4:

```
E[tokens] = (1 − 0.01024)/0.6 = 1.65
speedup   = 1.65 / 1.4 = 1.18×   ← barely worth the complexity
```

**Acceptance rate is everything**, and it is the number to measure first. Note
also that γ has an optimum: raising it increases `E[tokens]` with diminishing
returns (α^(γ+1) → 0) while the denominator grows linearly. With α = 0.8 and
c = 0.1, γ = 4–6 is near optimal; with α = 0.5, γ = 2–3 is. **Tune γ to your
measured α**, don't copy a default.

### 3.3 What drives acceptance rate

- **Draft/target similarity.** Same family and tokenizer is close to mandatory —
  e.g. Llama-3.2-1B drafting for Llama-3.1-8B. Different tokenizers make the
  scheme awkward at best.
- **Task predictability.** Boilerplate code, structured formats, templated text,
  and repetitive summaries draft extremely well (α often 0.85+). Creative,
  high-entropy, or highly technical text drafts poorly (α 0.4–0.6). **The same
  system can have very different speedups per endpoint** — measure per workload,
  not once globally.
- **Temperature.** Higher temperature flattens the target distribution and
  reduces agreement, lowering α. Greedy decoding tends to accept best.
- **Context.** With strong context (e.g. RAG passages the answer copies from),
  drafting improves markedly — extraction and quoting tasks draft very well.

### 3.4 Variants worth knowing by name

| Variant | Draft source | Notes |
| --- | --- | --- |
| **Standard (two-model)** | A separate small model | Simple; needs a well-matched small model and its memory |
| **Self-speculative / layer-skip** | The target model with layers skipped | No second model to host |
| **Medusa** | Extra decoding heads on the target predicting positions t+1..t+k | No separate model; needs training the heads |
| **EAGLE** | Lightweight autoregressive head over the target's features | Higher α than Medusa in reported results |
| **Prompt lookup / n-gram** | Copy candidate spans from the prompt itself | **Free**, no model, excellent when output quotes input |
| **Lookahead decoding** | Jacobi-style parallel refinement | No draft model |

**Prompt lookup decoding deserves special attention** because it costs nothing
and is dramatically effective on the tasks you probably have: summarization,
RAG-grounded answering, code editing, translation-in-place, structured
extraction. Whenever the output substantially copies from the input, an n-gram
match on the prompt is a superb draft, and there's no second model to host, tune,
or version. If you're going to try one thing from this lesson, try this.

### 3.5 The batch size interaction — the crucial caveat

At batch 1, decode is deeply memory-bound: the GPU's compute units idle while
weights stream. Speculation converts idle compute into throughput. Big win.

As batch grows (Day 5), the batch itself amortizes weight loading and the kernel
moves toward compute-bound. Speculation's extra verification FLOPs now compete
with real work, and rejected drafts are pure waste.

```
Illustrative speedup from speculation vs. batch size (α=0.8, γ=4):

 batch   1  →  2.3×
 batch   8  →  1.9×
 batch  32  →  1.4×
 batch  64  →  1.1×
 batch 128  →  0.9×   ← now a net LOSS
```

Therefore:

- **Speculation is a latency tool for low-concurrency, latency-critical
  serving** — on-device, single-user, interactive coding assistants, real-time
  agents, anything where you cannot batch.
- **Batching is a throughput tool for high-concurrency serving.** They are
  substitutes for the same underlying slack, and at scale batching wins.
- Some servers implement dynamic speculation: enable it when batch occupancy is
  low, disable it when high. That's the correct behaviour and it tells you the
  tradeoff is real.

If you serve a busy multi-tenant API at batch 64, speculative decoding is
probably the wrong project. If you serve one developer's IDE, it's probably the
best one available.

### 3.6 Distillation: the third lever

Train a smaller **student** to reproduce a larger **teacher's** behaviour.

- **Response / sequence-level KD** — generate outputs with the teacher, fine-tune
  the student on them. Simple, effective, and what most people mean by
  "distillation" today. Kim & Rush formalized the sequence-level version.
- **Logit-based KD** — train the student to match the teacher's full output
  distribution (Hinton et al.). Richer signal, needs teacher logits.
- **On-policy KD** (e.g. GKD) — train on sequences the *student* generates, with
  teacher feedback, avoiding the exposure-bias mismatch of training only on
  teacher outputs. Generally stronger, more involved.

Character of distillation:

- **Biggest possible speedup.** A 70B → 8B student is ~9× smaller; per Day 3
  that's ~9× decode speed *and* it frees enormous KV room.
- **Task-specific quality can match or exceed the teacher** on the distilled
  distribution — the student specializes.
- **General capability drops sharply.** The student is good at what you distilled
  and worse at everything else. This is a *feature* if your task is narrow and a
  disaster if your users wander.
- **It's a training project.** Data generation, training runs, eval
  infrastructure, versioning, and a permanent maintenance obligation: every
  teacher upgrade means redistilling. Budget in weeks, not days, and budget
  ongoing.
- **The teacher's licence may restrict using its outputs to train models.** Check
  before you generate a million samples. This has bitten real companies.

### 3.7 The comparison table — memorize this

| | Speculative decoding | Quantization | Distillation |
| --- | --- | --- | --- |
| **Output quality** | **Identical** (provably) | Slightly changed, unevenly | Substantially changed |
| **Typical decode speedup** | 1.5–3× (batch 1) | ~compression ratio (2–4×) | 3–10× |
| **Prefill speedup** | None | Little (weight-only) | Yes — smaller model |
| **Memory** | **Costs more** (draft resident) | **Frees a lot** | **Frees a lot** |
| **Helps at high batch?** | No — can hurt | Yes | Yes |
| **Effort** | Low–medium (config) | Low (offline, tooling exists) | **High** (training project) |
| **Reversible?** | Instantly (flag) | Instantly (swap weights) | No — it's a different model |
| **Ongoing cost** | Draft model versioning | Re-validate per model change | Re-distill per teacher change |
| **Fails when** | Draft mismatched; batch high | Small model; long context; strict format | Task drifts outside distilled distribution |

### 3.8 They compose

These are not mutually exclusive, and the strongest production setups stack them:

- **Quantized target + quantized draft + speculation** — common on-device. The
  draft's memory cost is small once quantized.
- **Distilled student, then quantized** — but be careful: distillation already
  spent the model's redundancy, so the student tolerates quantization *worse*
  than the teacher did (Day 7 §3.7). Validate the combination, never assume the
  effects are independent.
- **Distilled student as the draft model for the original teacher** — an elegant
  pattern: your distillation work produces a high-α draft, giving you lossless
  speedup on the big model *and* a cheap standalone model for easy requests
  (Day 13 routing).

---

## 4. Napkin math

### 4.1 Choosing under constraints

> Interactive coding assistant. Single user per session, so batch ≈ 1–4.
> Currently 70B FP16 on 2×H100: 24 tok/s. Users say it's too slow; target 60 tok/s.

**Option A — INT4 quantization.** 140 GB → 35 GB, decode floor ~96 tok/s, realistic
~70 tok/s. **Meets target.** Risk: Day 7 §3.6 says code generation is exactly
where format adherence and rare-symbol precision degrade. Requires the
per-category eval.

**Option B — Speculative decoding** with a 1B draft. Code is highly predictable,
so expect α ≈ 0.75–0.85. At α = 0.8, γ = 4, c = 0.05 (1B vs 70B is ~1/70, but
draft steps have fixed overheads, so use a measured c):
`E[tokens] = 3.36`, `speedup = 3.36/1.2 = 2.8×` → **~67 tok/s. Meets target, zero
quality change.** Cost: 2 GB more resident memory, and the benefit evaporates if
you later batch this service.

**Option C — Both.** INT4 target + INT4 draft: ~70 × 2.5 ≈ **175 tok/s**. Massive,
with quantization's quality risk (and note the draft's own quantization lowers α
slightly).

**Recommendation:** start with **B**. It meets the target with *no* quality risk
and is reversible with a flag. Only add quantization if you later need the memory
for concurrency or if the target moves higher — and if you do, bring the Day 7
per-category eval with you.

Notice the reasoning: *lossless first, then lossy, and only as far as needed.*
That ordering is the durable takeaway.

### 4.2 The same problem at scale

> Same model, but now a multi-tenant API at batch 64. Target: cut cost per token.

Speculation at batch 64 gives ~1.1× and consumes memory that would otherwise hold
KV cache for more sequences — likely a **net loss**. Quantization gives ~4×
decode *and* frees 105 GB of HBM, which per Day 4 multiplies concurrency, which
per Day 5 multiplies throughput. **Quantization wins decisively here**, and it's
the same model and the same speedup levers as §4.1 — only the batch size changed.

**Write this down:** the correct answer to "speculation or quantization?" is
different for the same model depending only on concurrency. That's why question 2
in §2 is on the list.

### 4.3 When distillation pays

> Classification/extraction endpoint: 20M requests/day, narrow task, strict output
> schema. Currently 70B at $0.0021/request = $42,000/day.

Distill to an 8B student on 200k teacher-labelled examples. Student runs at
roughly 1/9 the cost: **$4,700/day**, saving **$13.6M/year**.

Training cost: maybe 2–6 engineer-weeks plus compute — call it $150k all-in with
eval infrastructure. **Payback: about four days.**

This is why distillation dominates for high-volume narrow tasks and is nearly
always wrong for low-volume broad ones. The break-even is a volume question:
below roughly 100k requests/day on a narrow task, the engineering and maintenance
cost usually exceeds the savings, and you should be routing (Day 13) instead.

---

## 5. Failure modes

| Symptom | Cause | Fix |
| --- | --- | --- |
| Speculation gives <1.2× | Low acceptance rate | Measure α; try a better-matched draft or prompt-lookup |
| Speculation helped in dev, not prod | Dev batch 1, prod batch 64 | Expected — disable at high occupancy |
| Speculation caused OOM | Draft model competing for KV memory | Quantize the draft, or shrink `max_num_seqs` |
| Output differs from non-speculative | **Bug** — it must not | Check the acceptance/rejection sampling implementation |
| Distilled model great in eval, bad in prod | Eval matched the distillation distribution | Eval on real traffic, including out-of-distribution |
| Distilled model degraded after teacher upgrade | Student is frozen at the old teacher | Re-distill; budget it as recurring |
| Quantized-then-distilled model much worse | Compression effects stack non-linearly | Validate the combination, never assume independence |
| α good on one endpoint, poor on another | Task predictability differs | Per-endpoint γ tuning, or per-endpoint enable/disable |

That fourth row is important: **if speculative decoding changes your outputs at
temperature 0, you have an implementation bug, not a tradeoff.** It's one of the
few properties in this course you can assert absolutely, which makes it a great
test.

---

## 6. Lab (75 min)

### Part A — Measure acceptance rate (30 min)

Estimate α empirically without a full speculative implementation, by checking how
often the draft's greedy token matches the target's greedy token.

```python
# lab-output/day08_acceptance.py
import httpx

DRAFT, TARGET = "llama3.2:1b", "llama3.1:8b"

def next_token(model, prompt):
    r = httpx.post("http://localhost:11434/api/generate",
        json={"model": model, "prompt": prompt, "stream": False,
              "options": {"temperature": 0, "num_predict": 1}}, timeout=180)
    return r.json()["response"]

def acceptance(seed_prompt, steps=60):
    ctx, agree = seed_prompt, 0
    for _ in range(steps):
        d = next_token(DRAFT,  ctx)
        t = next_token(TARGET, ctx)
        agree += (d.strip() == t.strip() and d != "")
        ctx += t                      # always advance with the TARGET's token
    return agree / steps

WORKLOADS = {
  "boilerplate_code": "Write a C# class with properties Id, Name, CreatedAt and a ToString override:\n",
  "creative":         "Write an unusual opening paragraph for a novel about a lighthouse keeper:\n",
  "extraction":       ("Extract the fields as JSON.\nTicket: Order 88372 shipped 2024-03-04 "
                       "to Berlin via DHL, tracking JJD00123456.\nJSON:\n"),
  "technical_prose":  "Explain why decode is memory-bandwidth-bound in transformer inference:\n",
}

for name, p in WORKLOADS.items():
    a = acceptance(p)
    for gamma in (2, 4, 6, 8):
        E = (1 - a**(gamma+1)) / (1 - a) if a < 1 else gamma + 1
        print(f"{name:18s} α={a:.2f} γ={gamma}: E[tok]={E:.2f} "
              f"speedup≈{E/(1+gamma*0.1):.2f}×")
    print()
```

**Predict first:** rank the four workloads by expected α. Then run it.

Record in `lab-notebook.md`: measured α per workload, the optimal γ for each, and
the predicted speedup. Note how large the spread is — that's the argument for
per-endpoint configuration.

*(This greedy-agreement measure approximates α; true speculative sampling accepts
probabilistically and will differ somewhat. It's the right order of magnitude and
the right ranking, which is what you need for a decision.)*

### Part B — Prompt lookup, the free variant (20 min)

Implement n-gram drafting from the prompt and measure how often it would hit.

```python
# lab-output/day08_prompt_lookup.py
def lookup_draft(context_tokens, n=3, gamma=6):
    """If the last n tokens appeared earlier, draft the following gamma tokens."""
    if len(context_tokens) < n: return []
    key = context_tokens[-n:]
    for i in range(len(context_tokens) - n - 1, -1, -1):
        if context_tokens[i:i+n] == key:
            return context_tokens[i+n : i+n+gamma]
    return []
```

Run it (word-level is fine for the demonstration) against two generation
transcripts you produce: (a) a summarization of a long document, (b) a free-form
creative answer. Measure what fraction of generated tokens the lookup would have
correctly drafted.

**Expected:** summarization and extraction hit far more often, because the output
copies from the input. Write down the hit rate for each — and then note that this
technique required no model, no memory, and no training.

### Part C — The decision matrix (25 min)

Fill this in for three real workloads (invent them if needed — one interactive,
one batch, one high-volume narrow):

| Workload | Phase-dominant | Batch size | Quality tolerance | Spare memory | **Choice** | Why |
| --- | --- | --- | --- | --- | --- | --- |
| | | | | | | |

Then write the one-paragraph justification for each, in the form:

> "For [workload], decode dominates at [x]% of E2E and we run at batch [n].
> Speculation would give ≈[y]× based on measured α=[z]. Quantization would give
> ≈[w]× but risks [category] per our Day 7 eval. We choose [X] because [the
> binding constraint]."

That paragraph is the deliverable. If you can write it for a real system, you can
have this argument with anyone.

---

## 7. Quiz

**1.** Why is speculative decoding lossless? What property of the acceptance rule
guarantees it?

**2.** α = 0.7, γ = 3, c = 0.1. Compute expected tokens per step and speedup.

**3.** Same α and c, but γ = 8. Compute again. Why isn't larger γ always better?

**4.** Explain why speculative decoding's benefit shrinks as batch size grows.
At what point can it become a net loss?

**5.** Name four factors that raise or lower acceptance rate.

**6.** What is prompt-lookup decoding, and for which task families is it
unusually effective? Why does it cost nothing?

**7.** Your speculative implementation produces different text than
non-speculative at temperature 0. Is this a tradeoff or a bug? Justify.

**8.** Compare the three levers on: quality impact, memory impact, effort, and
reversibility.

**9.** A distilled 7B student matches its 70B teacher on your eval but users
report it's worse. Give the most likely explanation and the fix.

**10.** You distill 70B → 8B, then quantize the student to INT4, and quality
collapses even though each step was fine alone. Explain.

---

## 8. Review hooks

- *Speculation is lossless. Quantization is lossy-and-uneven. Distillation is a new model.*
- *Acceptance rate is everything; tune γ to your measured α.*
- *Speculation is a low-batch latency tool. Batching is the high-batch throughput tool. Substitutes.*
- *Speculation costs memory; quantization frees it. Opposite directions.*
- *Lossless first, then lossy, and only as far as needed.*
- *Prompt lookup is free and works whenever output copies input.*
- *Compression effects stack non-linearly. Validate the combination.*

---

## 9. Answer key

**1.** The draft token is accepted with probability `min(1, p(x)/q(x))`, and on
rejection a token is drawn from the normalized residual distribution
`max(0, p − q)`. This is a rejection-sampling scheme whose stationary output
distribution is exactly the target's `p` — so the text distribution is identical
to sampling from the target alone. The draft model only affects *how fast* tokens
are produced, never *which* distribution they come from.

**2.** `E = (1 − 0.7^4)/(1 − 0.7) = (1 − 0.2401)/0.3 = 2.53`.
`speedup = 2.53/(1 + 3×0.1) = 2.53/1.3 = 1.95×`.

**3.** `E = (1 − 0.7^9)/0.3 = (1 − 0.0404)/0.3 = 3.20`.
`speedup = 3.20/(1 + 0.8) = 1.78×` — **worse than γ=3.** Because `E[tokens]`
saturates (α^(γ+1) → 0, so E → 1/(1−α) = 3.33) while the draft cost `γ·c` grows
linearly forever. Past the optimum you're paying for drafts that will almost
certainly be rejected.

**4.** At batch 1, decode is memory-bound and the compute units are idle, so
speculation's extra verification FLOPs are nearly free. As batch grows, batching
itself amortizes the weight loading and the kernel becomes compute-bound — now
speculation's extra FLOPs compete with real work, and every rejected draft token
is wasted compute. It becomes a **net loss** once the marginal FLOPs spent on
rejected drafts exceed the marginal time saved, typically somewhere around batch
64–128 depending on α and hardware.

**5.** Draft/target similarity (same family and tokenizer); task predictability
(boilerplate/structured high, creative low); sampling temperature (higher →
lower α); and strength of context (grounded/extractive tasks draft better). Also
accepted: draft model size, and whether the draft is itself quantized.

**6.** Drafting by copying an n-gram continuation from earlier in the prompt or
generated text, rather than from a model. Unusually effective for summarization,
RAG-grounded answering, extraction, code editing, and any in-place
transformation — tasks where the output substantially quotes the input. It costs
nothing because there is no second model: no extra weights resident, no extra
memory, no training, no versioning — just a string search.

**7.** A **bug.** Speculative decoding is provably output-distribution-preserving;
at temperature 0 (greedy) the outputs must be token-for-token identical. A
difference means the acceptance/rejection sampling or the residual distribution
is implemented incorrectly. This makes greedy-equivalence an excellent regression
test for any speculative implementation.

**8.** **Quality:** speculation none; quantization small and unevenly
distributed; distillation large and task-shaped. **Memory:** speculation costs
(draft resident); quantization frees substantially; distillation frees
substantially. **Effort:** speculation low-to-medium (configuration + a matched
draft); quantization low (offline, mature tooling); distillation high (a training
project with ongoing obligations). **Reversibility:** speculation instant (flag);
quantization instant (swap weights); distillation not reversible — it's a
different artifact, and reverting means going back to the teacher's cost.

**9.** The eval set was drawn from the same distribution as the distillation
data, so it measures exactly what the student was trained to reproduce and
nothing else. Real traffic includes out-of-distribution requests where the
student's lost general capability shows. **Fix:** evaluate on sampled *production*
traffic (Day 16), stratified to include rare and out-of-distribution cases, and
route low-confidence or unusual requests back to the teacher (Day 13).

**10.** Distillation compresses the teacher's knowledge into far fewer
parameters, spending the redundancy that made the teacher robust to quantization
error. The student encodes more information per weight with less slack, so the
same relative quantization error causes a much larger behavioural change — the
Day 7 §3.7 "small models degrade more" effect, arrived at by a different route.
Compression techniques are not independent; their quality costs compound.

---

## 10. Going deeper

- **"Fast Inference from Transformers via Speculative Decoding"** — Leviathan,
  Kalman, Matias (arXiv 2211.17192). The acceptance-rule proof is short and worth
  reading properly.
- **"Accelerating Large Language Model Decoding with Speculative Sampling"** —
  Chen, Borgeaud, Irving, Lespiau, Sifre, Jumper (arXiv 2302.01318). The
  concurrent DeepMind formulation.
- **"Medusa: Simple LLM Inference Acceleration Framework with Multiple Decoding
  Heads"** — Cai, Li, Geng, Peng, Lee, Chen, Dao (arXiv 2401.10774).
- **"EAGLE: Speculative Sampling Requires Rethinking Feature Uncertainty"** — Li,
  Wei, Zhang, Zhang (arXiv 2401.15077).
- **"Sequence-Level Knowledge Distillation"** — Kim & Rush (arXiv 1606.07947).
  Predates LLMs; the core argument still holds.
- **"On-Policy Distillation of Language Models" (GKD)** — Agarwal et al.
  (arXiv 2306.13649). Why training on student-generated sequences beats training
  only on teacher outputs.

---

**Next:** [Day 9 — Fine-tuning vs. In-Context Learning vs. RAG vs. Distillation](day-09-adaptation-levers.md).
Today was three ways to go faster. Tomorrow: four ways to make the model *know*
something, and when each is the wrong one.
