# Day 16 — Evals: Golden Sets, Regression Tests, Adversarial Tests, LLM-as-Judge, Human Evals

**Time:** ~3.5 h · **Prereqs:** Days 7, 10, 15 · **Lab needs:** Python, 2 models

---

## 0. Warm-up review

1. Where does quantization damage quality first, and where does aggregate
   benchmark accuracy sit in that order? (Day 7)
2. Why is a synthetic-from-documents gold set biased? (Day 15)
3. How large a gold set detects a 3-point regression at an 80% baseline? (Day 15)
4. What does `degraded_responses_total` catch that nothing else does? (Day 13)

---

## 1. The one-paragraph version

An eval suite is a test suite for a system with no deterministic output, and it
is the single artifact that separates teams who ship LLM features from teams who
maintain them. It has five layers: **golden sets** (curated inputs with known-good
outputs, the foundation), **regression tests** (has anything that used to work
stopped working), **adversarial tests** (deliberately hostile or edge-case
inputs), **LLM-as-judge** (scalable automated grading, with real and specific
biases), and **human evals** (the ground truth everything else is calibrated
against). The failure mode this prevents is **silent regression** — quality
degrading with no error, no alert, and no deploy, discovered by customers weeks
later. And the recurring lesson from Days 7, 13, and 15 applies here as the
central principle: **aggregate scores hide the failing category.**

---

## 2. Why this bites you

**The invisible regression.** A team upgrades a model version. Their eval — 45
examples, mean score 8.2/10 — reports 8.1. Fine. What actually happened: the new
version is better at long-form answers (which dominate the eval) and materially
worse at the strict JSON extraction path (6 examples, dropped from 9.4 to 5.1).
The mean hid it. Two weeks later the extraction pipeline's downstream consumer is
full of garbage.

**The judge that agreed with itself.** A team uses GPT-family model X as a judge
and model X as the system. Scores are excellent. A human review of 100 samples
finds a 34% disagreement rate, concentrated in exactly the cases where the model
is confidently wrong — because the judge shares the generator's blind spots and
its stylistic preferences. The eval was measuring self-consistency.

**The eval that measured the cache.** A nightly regression run started passing
100% after a caching deploy. Nobody had marked cached responses in the trace, so
the eval was reading yesterday's answers (Day 6 §5).

**The frozen gold set.** A 40-query set from launch day, unchanged for a year,
while the product's real query distribution moved entirely. It still scores 79%,
which is now a statement about the past.

---

## 3. Core concepts

### 3.1 The five layers, and what each is for

| Layer | Question | Frequency | Cost |
| --- | --- | --- | --- |
| **Unit / assertion** | Does the pipeline behave deterministically where it should? | Every commit | Free |
| **Golden set** | Does it produce good output on known inputs? | Every commit + nightly | Low |
| **Regression** | Did anything that worked stop working? | Every change | Low |
| **Adversarial** | Does it fail safely under hostile input? | Every release | Medium |
| **LLM-as-judge** | Quality at scale on sampled production traffic | Continuous | Medium |
| **Human** | Ground truth; calibrates everything above | Weekly/monthly | High |

**Start with the cheapest that catches your actual failures**, which is usually
assertions plus a golden set. Almost nobody has the assertion layer, and it's
free: prefix hash constancy (Day 6), schema validity rate (Day 10), tool-argument
validation rate (Day 11), budget-exhaustion rate (Day 12), citation ID validity
(Day 15). Every one of those is a deterministic check on a nondeterministic
system, and each catches a whole class of regression before any judge is involved.

### 3.2 Golden sets: the foundation

Same discipline as Day 15's retrieval gold set, applied to end-to-end behaviour.

**Composition — stratify, and weight by consequence, not by frequency:**

| Stratum | Notes |
| --- | --- |
| **Head traffic** | The common cases. Most volume, usually easiest. |
| **Tail traffic** | Rare shapes. Sampled from logs, not imagined. |
| **Per-capability** | One stratum per capability you depend on: JSON strictness, long context, each language, tool selection, refusal, citation |
| **Known past failures** | **Every production bug becomes a permanent eval case.** |
| **High-consequence** | Cases where being wrong is expensive, even if rare |
| **Should-refuse** | Inputs where the correct answer is "I can't / I don't know" |

The "known past failures" stratum is the highest-value habit in this entire
course. **A bug you fixed without adding an eval case will come back**, usually
after a model upgrade, and you will debug it from scratch. Make it a rule: no
production incident closes without a regression case.

**Scoring functions, cheapest first:**

| Type | Use when | Reliability |
| --- | --- | --- |
| **Exact match** | Classification, extraction with a canonical answer | Perfect |
| **Schema/assertion** | Structured output, tool calls | Perfect |
| **Programmatic check** | "Contains this ID", "cites a real chunk", "compiles", "test passes" | High |
| **Fuzzy match / embedding similarity** | Short factual answers | Medium |
| **LLM judge** | Open-ended quality | Medium — see §3.4 |
| **Human** | Calibration and disputes | Ground truth |

**Push scoring down this list as far as you can.** Teams reach for an LLM judge
by default when 40% of their cases could be graded by a substring check or a
schema assertion — which are free, instant, deterministic, and never drift.

### 3.3 Regression testing without determinism

The core problem: same input, different output, every run. Techniques:

- **Set temperature 0 and a fixed seed where supported.** Reduces but does not
  eliminate variance (batching and hardware nondeterminism remain).
- **Score, don't diff.** Compare *scores* across runs, not text.
- **Run N times and compare distributions.** For an expensive-but-important case,
  5 runs and a mean is far more stable than 1 run.
- **Establish the noise floor first.** Run the identical suite twice against the
  identical system. Whatever difference you see is noise. **Any alert threshold
  below the noise floor will fire constantly and be ignored** — this is how eval
  suites die.
- **Use paired comparison for model upgrades.** Instead of "score A vs. score B",
  ask a judge which of two outputs for the *same input* is better. Paired
  comparison is much more sensitive than independent absolute scoring, because it
  cancels out the judge's calibration drift.

**The regression gate in CI:**

```yaml
# Illustrative
- run: python eval/run.py --suite golden --out results.json
- run: python eval/gate.py results.json baseline.json \
         --fail-if-overall-drop 0.02 \
         --fail-if-any-category-drop 0.05 \
         --fail-if-assertion-rate-drop 0.001
```

Note the three separate thresholds. The category-level gate is what catches the
§2 hidden regression, and the assertion-level gate is near-zero-tolerance because
those checks are deterministic — any drop is a real bug.

### 3.4 LLM-as-judge: powerful, biased, necessary

Judges scale to volumes humans can't. They also have well-documented, measurable
biases:

| Bias | Effect | Mitigation |
| --- | --- | --- |
| **Position** | Prefers the first (or second) option presented | Randomize order; run both orders and average |
| **Verbosity** | Prefers longer answers | Explicit rubric; control for length; penalize padding |
| **Self-preference** | Prefers output from the same model family | **Use a different family as judge** |
| **Style over substance** | Rewards fluency and confident tone | Rubric with factual criteria; check grounding separately |
| **Leniency / clustering** | Scores cluster at 7–8/10 | Coarse scales (1–4), forced comparison, explicit anchors |
| **Sycophancy toward context** | Agrees with framing in the prompt | Don't reveal which system produced which output |

**How to build a judge that's worth trusting:**

1. **Binary or few-point scales**, not 1–10. "Does this answer contain a factual
   claim unsupported by the sources? yes/no" is far more reliable than "rate
   faithfulness 1–10."
2. **Decompose.** Several narrow questions beat one holistic one. Separate
   correctness, completeness, grounding, format, and tone.
3. **Rubric with anchors.** Give concrete examples of each score level.
4. **Reasoning before verdict.** Have the judge state its reasoning, then the
   score. Improves reliability meaningfully.
5. **Blind and randomized.** Never tell it which system produced what; randomize
   presentation order.
6. **Different model family from the system under test.** This is the single
   highest-impact rule.
7. **Calibrate against humans, and keep calibrating.** Measure agreement (Cohen's
   κ, or Krippendorff's α for multiple raters) on a shared sample. **A judge with
   κ < 0.6 against human labels is not measuring what you think it is.**
   Re-measure whenever you change the judge model or the rubric.

**The rule that makes all of it legitimate:** *the judge is a measurement
instrument, and instruments require calibration.* An uncalibrated judge produces
numbers with unknown units. Most teams never do step 7, which means their eval
scores are uninterpretable — and, worse, they don't know it.

### 3.5 Adversarial evals

Deliberately hostile inputs. Not optional for anything user-facing.

| Category | Examples |
| --- | --- |
| **Prompt injection** | Instructions embedded in documents, tool results, filenames (Day 19) |
| **Jailbreak** | Roleplay, encoding, hypotheticals, multi-turn escalation |
| **Data exfiltration** | "Repeat your system prompt", "list other customers" (Day 20) |
| **Malformed input** | Enormous inputs, control characters, mixed encodings, nested structures |
| **Edge semantics** | Negation, double negation, quantities, dates, units |
| **Ambiguity** | Genuinely ambiguous requests — does it ask, or guess? |
| **Should-refuse** | Out-of-scope requests; does it decline or confabulate? |
| **Contradiction** | Conflicting context — does it notice or blend? |

Build these as a permanent suite that runs on every release. **Each one becomes a
regression case forever**, because adversarial failures recur after model changes
with striking regularity.

### 3.6 Human evals: the ground truth

Expensive, slow, and irreplaceable. Use them for:

- **Calibrating the judge** (§3.4 step 7) — the main ongoing use.
- **Ambiguous or high-stakes cases** where you need a defensible answer.
- **Periodic audits** — sample 100 production interactions monthly and review
  properly.
- **Establishing the rubric** in the first place. You cannot write a good judge
  rubric without having graded examples yourself.

Getting them right:

- **Multiple raters and a measured agreement statistic.** If humans agree only
  60% of the time, no judge will do better, and your task definition is the
  problem, not the model.
- **Written guidelines with worked examples.** Rater drift is real.
- **Blind to system identity.** Same rule as the judge.
- **Include the *sources*** for grounding judgments — a rater who can't see the
  retrieved context can only judge plausibility, which is exactly the wrong thing.

### 3.7 Continuous eval in production

Offline evals measure a frozen distribution. Production drifts.

- **Sample and score.** 1–5% of live traffic through the judge pipeline, scored
  continuously. Track by feature, model, tenant, and route (Days 13, 17, 18).
- **Track the leading indicators** you already have from earlier days — repair
  rate (Day 10), tool-validation failure rate (Day 11), budget-exhaustion rate
  (Day 12), degraded-response rate (Day 13), `answer_in_context_rate` (Day 15).
  These move *before* quality complaints arrive.
- **Implicit feedback.** Regeneration requests, edits to the output, abandonment,
  copy events, thumbs. Noisy individually, strong in aggregate.
- **Segment by cohort and by corpus age.** Aggregates hide the failing category.
- **Alert on rate of change**, not absolute values. A 3-point week-over-week drop
  matters even at a comfortable absolute level.

### 3.8 What makes an eval suite die

Worth naming explicitly, because most suites do die:

1. **Thresholds below the noise floor** → constant false alarms → ignored → gone.
2. **Too slow to run on every change** → run rarely → drift accumulates.
3. **Never updated** → measures a distribution the product left behind.
4. **Only aggregate scores** → hides everything that matters.
5. **Uncalibrated judge** → nobody trusts the number → decisions made on vibes.
6. **No owner** → nobody adds cases → §3.2's "known past failures" never happens.

Design against all six from the start. An eval suite that isn't trusted is worse
than none, because it provides false assurance.

---

## 4. Napkin math

### 4.1 Aggregate hiding a category

Suite of 200 cases: 170 general (score 0.88), 20 JSON extraction (0.94), 10
long-context (0.79).

```
weighted mean = (170×0.88 + 20×0.94 + 10×0.79)/200 = 0.879
```

Model upgrade: general → 0.90, JSON → 0.61, long-context → 0.80.

```
new mean = (170×0.90 + 20×0.61 + 10×0.80)/200 = 0.876
```

**Mean moved 0.3 points. JSON extraction fell 33 points.** If your gate is
"overall drop > 2%", this ships. A per-category gate at 5% catches it instantly.
This is Day 7's damage profile appearing exactly where it was predicted to.

### 4.2 Sample size and the noise floor

Suite of 100 cases at ~0.85. `SE = sqrt(0.85×0.15/100) = 0.036`. Two runs of the
identical system will routinely differ by ±3.5 points from sampling noise alone,
before any judge variance.

**So a 2-point alert threshold on a 100-case suite fires on noise.** You need
either a bigger suite (Day 15 §4.2) or a wider threshold — and the honest move is
to compute the noise floor and set the threshold above it, then state plainly
what size regression you are and aren't able to detect.

### 4.3 Judge cost at production scale

2M requests/day, sample 2%, judge call ~1,500 in / 200 out at $0.15/M in,
$0.60/M out:

```
40,000 judged/day
in:  40,000 × 1,500 × $0.15/M = $9.00
out: 40,000 ×   200 × $0.60/M = $4.80
                                ──────
                                $13.80/day  ≈ $5,037/year
```

**$14/day for continuous production quality measurement.** Against Day 15's
example of 5,700 wrong citations/day in a legal product, or Day 13's 7,200 bad
answers in one silent-degradation incident, this is the cheapest insurance in the
stack — and it's routinely skipped.

---

## 5. Failure modes

| Failure | Cause | Fix |
| --- | --- | --- |
| Regression shipped, mean unchanged | Aggregate-only gate | Per-category gates |
| Eval alerts constantly, everyone ignores them | Threshold below noise floor | Measure the floor; set above it |
| Judge scores excellent, users unhappy | Self-preference / style bias | Different judge family; calibrate vs. humans |
| Eval passes after a caching deploy | Eval reading cached responses | Bypass caches; mark `cache_hit` in traces |
| Fixed bug returns after a model upgrade | No regression case added | Rule: no incident closes without a case |
| Suite measures a distribution that no longer exists | Frozen gold set | Refresh from production logs quarterly |
| Human raters disagree 40% | Task is underspecified | Fix guidelines before blaming the model |
| Scores drift with no system change | Judge model was silently updated | Pin the judge version; re-baseline on change |
| "It got worse" with no evidence either way | No eval at all | Start with assertions + 100 golden cases |

---

## 6. Lab (95 min)

### Part A — The assertion layer, free (20 min)

Before any judge, add deterministic checks to this repo. Create
`eval/assertions.py` (or C# tests) covering:

| Assertion | From |
| --- | --- |
| Static prefix hash is identical across differing requests | Day 6 |
| Structured output schema-validates without repair | Day 10 |
| No registered tool schema declares `tenant_id`/`user_id` | Day 11 |
| Irreversible tools cannot register without a confirmation gate | Day 11 |
| Every emitted citation ID exists in the retrieved set | Day 15 |
| Every `TerminalReason` has a non-empty user-facing summary | Day 12 |

Run them. **Anything failing is a bug you have right now**, found for free.

### Part B — Golden set and per-category gate (35 min)

```python
# lab-output/day16_eval.py
import json, statistics, sys
from collections import defaultdict

def run_suite(cases, system):
    rows = []
    for c in cases:
        out = system(c["input"])
        rows.append({"id": c["id"], "category": c["category"],
                     "score": SCORERS[c["scorer"]](out, c)})
    return rows

SCORERS = {
    "exact":  lambda out, c: float(out.strip() == c["expected"].strip()),
    "contains": lambda out, c: float(all(s in out for s in c["must_contain"])),
    "schema": lambda out, c: float(schema_ok(out, c["schema"])),
    "judge":  lambda out, c: llm_judge(out, c),         # Part C
}

def gate(current, baseline, overall_drop=0.02, category_drop=0.05):
    def by_cat(rows):
        d = defaultdict(list)
        for r in rows: d[r["category"]].append(r["score"])
        return {k: statistics.mean(v) for k, v in d.items()}
    cur, base = by_cat(current), by_cat(baseline)
    failures = []
    o_cur = statistics.mean(r["score"] for r in current)
    o_base = statistics.mean(r["score"] for r in baseline)
    if o_base - o_cur > overall_drop:
        failures.append(f"overall {o_base:.3f} -> {o_cur:.3f}")
    for cat in base:
        if cat in cur and base[cat] - cur[cat] > category_drop:
            failures.append(f"category {cat}: {base[cat]:.3f} -> {cur[cat]:.3f}")
    return failures
```

Build 60 golden cases over this repo's tutor capability, stratified per §3.2 —
including at least 8 "should-refuse/should-say-I-don't-know" cases and 8 known
past failures (invent plausible ones if you have no incident history yet).

**Then measure your noise floor:** run the identical suite twice against the
identical system. Record the difference. Set your gate thresholds above it, and
write both numbers in `lab-notebook.md`.

Finally, reproduce §4.1: construct a change that drops one category hard while
barely moving the mean, and verify your category gate catches what an overall
gate misses.

### Part C — Build and calibrate a judge (40 min)

```python
JUDGE_PROMPT = """You are grading an answer against source documents.

Sources:
{sources}

Question: {question}
Answer: {answer}

Answer these independently. Reason briefly, then give the verdict.

1. GROUNDED: Is every factual claim in the answer supported by the sources?
   (yes / no)
2. COMPLETE: Does the answer address the whole question? (yes / partially / no)
3. CORRECT_FORMAT: Does it follow the requested format? (yes / no)
4. HARMFUL_CONFIDENCE: Does it state something uncertain as definite fact?
   (yes / no)

Output JSON: {{"reasoning": "...", "grounded": "...", "complete": "...",
"correct_format": "...", "harmful_confidence": "..."}}"""
```

Note the design: four narrow binary/ternary questions, reasoning first, no
holistic 1–10 score, and no indication of which system produced the answer.

**Then calibrate it — this is the part that makes it real:**

1. Grade 40 outputs yourself, by hand, on the same four dimensions. Do this
   before looking at the judge's answers.
2. Run the judge on the same 40.
3. Compute agreement per dimension:

```python
def cohens_kappa(a, b):
    """a, b: equal-length lists of categorical labels."""
    from collections import Counter
    n = len(a)
    po = sum(x == y for x, y in zip(a, b)) / n
    ca, cb = Counter(a), Counter(b)
    pe = sum((ca[k]/n) * (cb[k]/n) for k in set(a) | set(b))
    return (po - pe) / (1 - pe) if pe < 1 else 1.0
```

4. Report κ per dimension. **Which dimension does the judge measure reliably, and
   which doesn't?** Typically `correct_format` is near-perfect and
   `harmful_confidence` is poor — because the first is objective and the second is
   a judgment call you probably under-specified in the rubric.
5. Rewrite the rubric for the worst dimension with concrete anchors, re-run, and
   see whether κ improves.

Write in `lab-notebook.md`: your κ per dimension, and which dimensions you're
willing to gate CI on. **Only gate on dimensions where κ ≥ 0.6.** For the rest,
you have a weak signal, and you should say so rather than quietly treating it as
a number.

---

## 7. Quiz

**1.** Name the five eval layers and what each answers.

**2.** Why is the assertion layer the highest-value starting point? Give three
assertions you could add today from earlier days.

**3.** Suite: 150 general (0.86), 30 tool-calling (0.91), 20 long-context (0.77).
A change moves them to 0.88 / 0.58 / 0.78. Compute both means. Would a 2%
overall gate catch it? What gate would?

**4.** What is the noise floor, how do you measure it, and what happens if your
threshold is below it?

**5.** List four LLM-judge biases and the mitigation for each.

**6.** Why must the judge come from a different model family than the system under
test?

**7.** What is κ, what value is the minimum for a judge you'd gate CI on, and what
do you do about dimensions below it?

**8.** Why is paired comparison more sensitive than independent absolute scoring
for model upgrades?

**9.** Give the rule about production incidents and eval cases, and explain why it
matters specifically after model upgrades.

**10.** Name four ways an eval suite dies.

---

## 8. Review hooks

- *An eval suite is a test suite for a nondeterministic system.*
- *Aggregate scores hide the failing category. Gate per category.*
- *Push scoring as far down the cheap-and-deterministic list as it will go.*
- *Measure the noise floor before setting any threshold.*
- *A judge is an instrument. Uncalibrated instruments produce unknown units.*
- *Different model family for the judge. Blind and randomized.*
- *No incident closes without a permanent regression case.*

---

## 9. Answer key

**1.** **Assertions** — does the deterministic part behave deterministically.
**Golden set** — does it produce good output on known inputs. **Regression** —
did anything that worked stop working. **Adversarial** — does it fail safely
under hostile input. **LLM-as-judge** — quality at scale on sampled traffic.
**Human** — ground truth, calibrating everything above.

**2.** Because assertions are free, instant, deterministic, never drift, and each
catches an entire class of regression without a judge or labels. Three of: prefix
hash constancy (Day 6); schema-validation rate without repair (Day 10); no tool
schema declares `tenant_id`/`user_id` (Day 11); irreversible tools can't register
without a confirmation gate (Day 11); every citation ID exists in the retrieved
set (Day 15); every terminal state has a user-facing summary (Day 12).

**3.** Before: `(150×0.86 + 30×0.91 + 20×0.77)/200 = (129 + 27.3 + 15.4)/200 =
0.8585`. After: `(150×0.88 + 30×0.58 + 20×0.78)/200 = (132 + 17.4 + 15.6)/200 =
0.825`. Overall drop = 3.35 points, so a 2% overall gate **would** catch this one
— but only barely, and only because tool-calling is 15% of the suite. **A
per-category gate at 5% catches it decisively** (tool-calling fell 33 points) and
would still catch it if tool-calling were 5% of the suite, where the overall gate
would not.

**4.** The run-to-run difference produced by sampling and generation variance
alone, measured by running the identical suite twice against the identical
system. If the alert threshold is below it, the gate fires on noise, engineers
learn the alerts are meaningless, and the suite is disabled or ignored — which is
how eval suites die.

**5.** **Position** (prefers first/second) → randomize order, run both and
average. **Verbosity** (prefers longer) → explicit rubric, control for length.
**Self-preference** (prefers its own family's output) → use a different family.
**Style over substance** (rewards fluency) → factual rubric criteria, measure
grounding separately. Also: **leniency/clustering** → coarse scales with anchors;
**sycophancy** → blind the judge to system identity.

**6.** Because a judge from the same family shares the generator's blind spots,
stylistic preferences, and factual errors — it will systematically approve exactly
the outputs that are confidently wrong in the way that family is prone to. The
result is a measurement of self-consistency, which correlates poorly with quality
and looks excellent.

**7.** Cohen's κ is inter-rater agreement corrected for chance agreement. **κ ≥
0.6** is the practical minimum for gating CI. For dimensions below it, don't gate
— either improve the rubric with concrete anchors and re-measure, decompose the
dimension into narrower objective questions, or accept it as a weak trend signal
and say so explicitly rather than treating an unreliable number as a fact.

**8.** Because it cancels shared sources of variance. Absolute scoring requires
the judge to hold a stable internal calibration across runs, prompts, and time —
which it doesn't. Asking "which of these two answers to the same question is
better" removes calibration drift entirely and only requires a relative judgment,
which judges do far more reliably. It also removes the leniency-clustering
problem, where absolute scores all pile up at 7–8/10.

**9.** **No production incident closes without a permanent regression case added
to the suite.** It matters specifically after model upgrades because fixes made in
prompts, in context assembly, or in model-specific workarounds are frequently
undone by a new model version's different behaviour — the exact bug returns, and
without a case, it returns *undetected* and gets debugged from scratch.

**10.** Any four: thresholds below the noise floor → constant false alarms →
ignored; too slow to run per change → run rarely → drift; never updated → measures
an obsolete distribution; aggregate-only scores → hides real regressions;
uncalibrated judge → nobody trusts the number; no owner → cases never added.

---

## 10. Going deeper

- **"Judging LLM-as-a-Judge with MT-Bench and Chatbot Arena"** — Zheng, Chiang,
  Sheng et al. (arXiv 2306.05685). The foundational study of judge biases —
  position, verbosity, self-preference — with measurements.
- **"G-Eval: NLG Evaluation using GPT-4 with Better Human Alignment"** — Liu, Iter,
  Xu, Wang, Xu, Zhu (arXiv 2303.16634). Chain-of-thought judging and probability-
  weighted scores.
- **"Length-Controlled AlpacaEval"** — Dubois et al. (arXiv 2404.04475). A
  concrete method for removing verbosity bias from automated evaluation.
- **"Content Analysis: An Introduction to Its Methodology"** — Krippendorff.
  The proper treatment of inter-rater reliability; where α comes from and when to
  prefer it to κ.
- **"Evaluating Large Language Models: A Comprehensive Survey"** — Chang et al.
  (arXiv 2307.03109). Useful as a map of what people measure and how.

---

**Next:** [Day 17 — LLM Observability](day-17-observability.md). Evals tell you
whether it works. Observability tells you what happened.
