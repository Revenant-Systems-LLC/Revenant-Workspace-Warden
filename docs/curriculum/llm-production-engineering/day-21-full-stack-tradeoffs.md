# Day 21 — Latency, Quality, Cost, and Reliability Across the Full Inference Stack

**Time:** ~3.5 h · **Prereqs:** all prior days · **Lab needs:** your accumulated data

---

## 0. Warm-up review

1. Which cost dimension flips routing decisions, and how? (Day 18)
2. What's the diagnostic question of multi-tenant isolation? (Day 20)
3. Name three quality metrics that are leading indicators of drift. (Days 10–15)
4. Where does aggregate benchmark accuracy sit in the quantization damage
   profile? (Day 7)

---

## 1. The one-paragraph version

You have twenty days of levers. Today is about choosing between them, which
requires accepting that **latency, quality, cost, and reliability form a
constraint system where you can fix any three and the fourth becomes an
outcome.** The engineering work is not "optimize everything" — it's identifying
which dimension your product is actually constrained by, spending your budget
there, and being explicit about what you're trading. The discipline that makes
this rigorous rather than rhetorical is (a) knowing which stage of the stack a
proposal touches, (b) computing its effect in the unit that matters — cost per
successful outcome, not cost per call — and (c) writing down what you gave up.
Most bad engineering decisions in this field come from optimizing a dimension the
product wasn't constrained by, using a metric that hid the cost.

---

## 2. Why this bites you

**The wrong dimension.** A team spends a quarter cutting p50 latency from 2.1s to
1.4s. Usage doesn't move. Their users are analysts running batch reports overnight
— latency was never the constraint. Meanwhile answer quality on long documents was
the actual complaint, and nobody worked on it.

**The hidden trade.** A cost optimization ships: quantize to INT4, route 60% to a
small model, cache aggressively. Cost per call drops 71%. Six weeks later,
escalations to human support are up 4 points, cost per *resolved ticket* is up
18%, and nobody connects the two because the dashboards track cost per call.
(Day 18 §4.3, arriving as a real incident.)

**The unfalsifiable decision.** "We chose the bigger model for quality." How much
quality? Measured how? Against what alternative? At what cost per point? Without
those numbers it isn't an engineering decision, it's a preference — and it can't be
revisited when circumstances change, because nobody wrote down what it was
purchased for.

---

## 3. Core concepts

### 3.1 The four dimensions, and how they actually trade

| Dimension | Measured by | Improved by | At the cost of |
| --- | --- | --- | --- |
| **Latency** | TTFT, ITL, E2E percentiles | Smaller batch, speculation, prefix cache, shorter context, smaller model | Cost (less batching), quality (smaller model) |
| **Quality** | Task evals, grounding, citation correctness | Bigger model, better retrieval, more validation, repairs | Latency, cost |
| **Cost** | $ per successful outcome | Routing, quantization, caching, distillation, batching | Quality (compression), latency (batching) |
| **Reliability** | Success rate, availability, p99 | Fallbacks, retries, validation, budgets, redundancy | Latency (retries), cost (redundancy) |

The relationships that are genuinely fixed:

- **Batching trades per-user latency for aggregate throughput** (Day 5). Not
  negotiable — it's the same hardware serving more people more slowly each.
- **Compression trades quality for cost and latency** (Days 7–8), except
  speculative decoding, which is the one lossless lever and is therefore the one
  to reach for first.
- **Validation and repair trade latency and cost for reliability** (Days 10–11).
- **Redundancy and fallback trade cost for availability** (Day 13).

And the relationships that are *not* fixed — the free lunches, which is where you
should always look first:

- **Context engineering** (Day 2) improves cost, latency, *and* quality
  simultaneously.
- **Prefix caching** (Day 6) improves cost and latency at zero quality cost.
- **Reranking** (Day 14) improves quality and reduces context size — cheaper *and*
  better.
- **Continuous batching and paged attention** (Day 5) improve throughput with no
  quality cost.
- **Speculative decoding** (Day 8) improves latency losslessly at low batch.

**Exhaust the free lunches before spending anything on a real trade.** Teams
routinely accept quantization's quality risk while running an unoptimized context
and a broken cache prefix.

### 3.2 Where the stack's levers live

When someone proposes a change, locate it. This tells you which dimension it can
possibly move.

```
LAYER                    LEVERS                              MOVES
─────────────────────────────────────────────────────────────────────────
Product / UX             streaming, partial results,         perceived latency,
                         degraded mode, scope reduction      user-visible reliability

Application / harness    context engineering (D2),           all four
                         routing (D13), caching (D6),
                         validation + repair (D10-11),
                         budgets (D12)

Retrieval                chunking, hybrid, rerank,           quality, cost
                         freshness (D14-15)

Model selection          size, family, fine-tune,            quality, cost, latency
                         distillation (D9)

Inference / compression  quantization (D7),                  latency, cost,
                         speculation (D8)                    (quality for quant)

Serving                  batching, paged attention,          latency, cost,
                         chunked prefill (D4-5)              throughput

Hardware                 GPU class, memory bandwidth,        latency, cost
                         count
```

Two rules of thumb that hold surprisingly well:

1. **Move up the stack before moving down.** Application-layer changes are
   cheaper, faster, more reversible, and usually larger in effect than hardware
   changes. A team asking for more GPUs should first be asked what their prompt
   looks like.
2. **The highest layer with a viable fix wins.** Product-level scope reduction
   ("we don't summarize 300-page documents in one request") often beats every
   engineering fix below it, and nobody proposes it because it feels like giving
   up. It isn't; it's choosing a constraint.

### 3.3 Perceived vs. actual latency

A dimension people forget is real: users experience *perceived* latency, and it's
manipulable independently of the actual number.

| Technique | Actual E2E | Perceived | Cost |
| --- | --- | --- | --- |
| **Streaming** | unchanged | **much better** — TTFT becomes the felt latency | none |
| Optimistic UI / skeleton | unchanged | better | none |
| Progressive disclosure (answer first, citations after) | unchanged | better | small |
| Showing intermediate agent steps | slightly worse | **better** — progress is legible | small |
| Prefetching likely next requests | unchanged | better | wasted calls |

**Streaming is the highest-leverage latency work available and it costs nothing.**
It converts an 8-second wait into a 700ms wait followed by 7.3 seconds of reading.
Which is why Day 3's TTFT is the metric to optimize when the product streams, and
E2E is the metric when it doesn't. Know which product you have.

### 3.4 The decision framework

For any proposed change, answer these five. If you can't, it's not a decision yet.

```
1. WHICH DIMENSION is the product actually constrained by right now?
   Evidence, not intuition: churn reasons, support tickets, eval scores,
   the bill, the SLO burn rate.

2. WHICH LAYER does this change touch, and is there a higher-layer fix?

3. WHAT IS THE EFFECT, in the unit that matters?
   - Latency → TTFT and ITL percentiles separately (D3), not a mean
   - Quality → per-category eval deltas (D16), not an aggregate
   - Cost → per successful outcome (D18), not per call
   - Reliability → success rate at p99, not p50

4. WHAT AM I TRADING? Name it and quantify it. "Nothing" means you haven't
   looked, unless it's on the free-lunch list.

5. WHAT'S THE ROLLBACK TRIGGER? Which metric, what threshold, how long.
```

Question 5 is what makes the decision falsifiable. A change without a rollback
trigger can't be wrong, which means it can't be evaluated, which means it will
persist regardless of its effect.

### 3.5 Worked example: the same system, four constraints

One system — a document Q&A assistant — under four different binding constraints.
Note that the *correct answer changes completely* while the system stays the same.

**Constraint: latency.** Users abandon after 3s; current p95 E2E is 9s.

```
Diagnose (D3): TTFT 6.4s of 9s → prefill-dominated. Prompts average 28k tokens.
Levers, in order:
  1. Streaming (free, perceived latency: 9s → 6.4s felt)          — ship today
  2. Context engineering: rerank to 5 chunks (D14)                — 28k → 7k
     → prefill 6.4s → 1.6s. Quality likely IMPROVES (D2).
  3. Prefix caching (D6) on the 4k static block                   — further ~0.9s
  4. Only now consider quantization/speculation.
Result: p95 ~3.1s, cost DOWN, quality UP. No trades made.
```

**Constraint: cost.** $84k/month, budget is $30k.

```
Diagnose (D18): 61% of spend is one feature; 24% is repairs and fallbacks.
Levers, in order:
  1. Fix the repair rate (D10) — it's a quality bug wearing a cost costume
  2. Prefix caching + context engineering                          — free lunches
  3. Routing/cascade (D13), measured per OUTCOME not per call      — real lever
  4. Quantization (D7) with the per-category eval                  — real trade
  5. Distillation (D8-9) if volume justifies the project
Trade made at step 4: name the category that degrades and the rollback trigger.
```

**Constraint: quality.** Answers are wrong 22% of the time.

```
Diagnose (D15): the 2x2. answer_in_context = 0.64 → RETRIEVAL is the constraint.
Levers, in order:
  1. Hybrid search + reranking (D14)                — biggest single move
  2. Contextual chunking                            — cheap, one-time
  3. Freshness filters                              — correctness, not ops
  4. Only then: bigger model, better prompt
Trades: latency +150ms for the reranker; cost roughly flat (fewer chunks offsets
the rerank call).
```

**Constraint: reliability.** p99 success 91%; enterprise contract requires 99%.

```
Diagnose (D17): failures are 40% provider 529s, 35% schema validation exhaustion,
                25% agent budget exhaustion.
Levers:
  1. Fallback chain with error classification + circuit breaker (D13)
  2. Constrained decoding to kill the schema class (D10)
  3. Budget re-derivation and task decomposition (D12)
  4. Multi-provider redundancy
Trades: cost +8% (redundancy, repairs), p50 latency +120ms (validation).
        Explicitly accepted, written down.
```

**The pattern:** in three of four cases, the first moves are free lunches, and the
diagnosis step — always a measurement from Days 3, 15, 17, or 18 — determines
everything. Teams that skip the diagnosis pick a lever from the middle of the list
and get a fraction of the available benefit at a cost they didn't need to pay.

### 3.6 SLOs: making the tradeoff explicit

The mature form of this discipline is service level objectives, one per dimension,
with error budgets:

```
Latency:      p95 TTFT ≤ 800ms, p95 E2E ≤ 4s          (99% of requests)
Quality:      eval suite ≥ 0.85 overall, no category below 0.75
Cost:         ≤ $0.06 per successfully resolved session
Reliability:  99.5% of requests reach a terminal success state
```

What SLOs buy you:

- **They make "good enough" a number**, so optimization stops when it should.
- **The error budget authorizes risk.** If you're comfortably inside the latency
  SLO, you can *spend* that headroom on quality — a bigger model, more validation,
  an extra retrieval pass. This is the single most useful reframing in the lesson:
  headroom is a budget to spend, not a margin to preserve.
- **They resolve arguments with evidence.** "Does this ship?" becomes "does this
  keep every SLO green?"
- **Burn rate tells you where to work.** The dimension burning its budget fastest
  is the constrained one, by definition.

### 3.7 What to do when everything is constrained

Sometimes all four are binding. Options, in order of preference:

1. **Reduce scope.** Narrow the product's promise. A system that handles 90% of
   cases excellently and refuses the rest cleanly beats one that handles 100%
   poorly. Refusal is a feature (Days 14–15).
2. **Segment.** Different SLOs for different tiers or use cases. Enterprise gets
   the large model; free tier gets the cascade and says so.
3. **Move work off the critical path.** Precompute, index at write time, summarize
   asynchronously (Day 3 §4's winning answer). Latency you don't spend at query
   time is free.
4. **Change the interaction model.** Async delivery ("we'll email your report")
   removes latency as a constraint entirely.
5. **Buy hardware.** Last, because it's the least leveraged and the most permanent
   commitment.

Option 1 is the one engineers resist and product managers should love. Option 3 is
the one that produces the most surprising wins.

---

## 4. Napkin math

### 4.1 Cost per point of quality

Make quality purchases comparable by pricing them.

| Change | Δ quality | Δ cost/day | $/point/day |
| --- | --- | --- | --- |
| Add reranker | +12 pts | +$180 | **$15** |
| Hybrid search | +7 pts | +$40 | **$5.7** |
| Contextual chunking | +9 pts | +$3 (amortized) | **$0.33** |
| Upgrade to larger model | +4 pts | +$4,200 | **$1,050** |
| Add repair loop | +3 pts | +$120 | **$40** |
| Longer few-shot block | +1.5 pts | +$900 | **$600** |

Ranked by efficiency, the order is: contextual chunking, hybrid search, reranker,
repair loop, larger model, longer few-shot. **The model upgrade is 3,000× less
efficient than the chunking change**, and it's the one most teams do first because
it's the one that requires no engineering.

Build this table for your own system. It is the single most persuasive artifact
you can bring to a prioritization meeting.

### 4.2 Error budget as spendable headroom

SLO: p95 E2E ≤ 4s. Currently at 2.2s. Headroom: 1.8s.

Options for spending it:

```
Add a reranker:              +150ms  → +12 quality points
Add semantic validation:     +60ms   → reliability +2 points
Two-call reason-then-extract: +900ms → format adherence 94% → 99.4%
Retrieve k=100 instead of 50: +80ms  → recall +4 points
                             ───────
                             +1,190ms → still inside SLO at 3.39s
```

**You just bought a large quality and reliability improvement with latency you
weren't using.** Teams that treat SLO headroom as something to protect rather than
spend leave this on the table permanently.

### 4.3 The full-stack optimization, sequenced

Starting point: 9s p95, $84k/month, 78% quality, 91% reliability. Apply
everything, in the order the free-lunch rule dictates:

| Step | Change | Latency | Cost/mo | Quality | Reliability |
| --- | --- | --- | --- | --- | --- |
| 0 | baseline | 9.0s | $84k | 78% | 91% |
| 1 | streaming (perceived) | 6.4s felt | $84k | 78% | 91% |
| 2 | context eng. + rerank | 2.9s | $61k | 88% | 91% |
| 3 | prefix caching | 2.4s | $38k | 88% | 91% |
| 4 | constrained decoding + repair | 2.6s | $39k | 90% | 96% |
| 5 | fallback + circuit breaker | 2.7s | $41k | 90% | 99.2% |
| 6 | cascade routing (per outcome) | 2.9s | $27k | 89% | 99.2% |
| 7 | INT8 quantization | 2.5s | $23k | 88.5% | 99.2% |

**Every dimension improved**, and only steps 6 and 7 involved a real quality trade
— 1.5 points, deliberately, with a rollback trigger. Steps 1–5 were free lunches
and delivered most of the benefit.

Note what is *not* on this list: a bigger model, more GPUs, or a fine-tune. None
of them was necessary, and all three would have been proposed first in most rooms.

---

## 5. Failure modes

| Failure | Cause | Fix |
| --- | --- | --- |
| Optimized the wrong dimension | No diagnosis of the binding constraint | Measure first (D3, 15, 17, 18) |
| Cost fell, business cost rose | Optimized per call, not per outcome | Cost per successful outcome (D18) |
| Quality "improved" but users unhappy | Aggregate metric hid a category | Per-category gates (D16) |
| Latency work had no effect | Product doesn't stream; wrong metric | TTFT vs. E2E depends on the product |
| Can't revisit an old decision | No stated trade or trigger | Framework question 4 and 5 |
| Bought GPUs, needed a prompt fix | Moved down the stack first | Highest viable layer wins |
| Endless optimization, no stopping rule | No SLOs | Define "good enough" as a number |
| Sat comfortably inside SLO for a year | Headroom treated as margin | Spend the error budget on quality |
| All four constrained, team stuck | Won't reduce scope | Scope reduction is an engineering choice |

---

## 6. Lab (95 min)

### Part A — Diagnose your binding constraint (25 min)

Using everything you've built across 20 days, produce the evidence:

| Dimension | Current | Target | Evidence source | Burning? |
| --- | --- | --- | --- | --- |
| Latency (p95 TTFT / E2E) | | | Day 17 traces | |
| Quality (overall / worst category) | | | Day 16 suite | |
| Cost (per successful outcome) | | | Day 18 report | |
| Reliability (terminal success rate) | | | Day 12/17 metrics | |

Then state, in one sentence with a number: **"We are constrained by ____, because
____."** If you can't name a number, the first task is measurement, not
optimization.

### Part B — Build the efficiency table (35 min)

Reproduce §4.1 for your system. For each candidate change you've encountered
across the course, estimate:

- Δ quality (per-category, from your Day 16 suite)
- Δ latency (TTFT and E2E separately, from Day 17)
- Δ cost per successful outcome (Day 18)
- Engineering days
- Reversibility (instant / config / deploy / retrain)

Rank by `Δ(constrained dimension) / (engineering days × irreversibility)`.

**Then check your ranking against the free-lunch list in §3.1.** Anything on that
list that isn't already done should be at the top regardless of what your formula
says, because it has no downside to weigh.

Write the top five as a prioritized plan in `lab-notebook.md`.

### Part C — Write the SLOs and one decision memo (35 min)

**1. SLOs.** Write four, one per dimension, with numbers you can actually measure
today from instrumentation you actually have. Include the error budget and, for
each, one sentence on what you'd spend headroom on if you had it.

**2. Decision memo.** Take the top item from Part B and write it up using §3.4's
five questions:

> **Proposal:** ...
> **Binding constraint:** ... (with the evidence)
> **Layer:** ... (and why no higher-layer fix applies)
> **Effect:** ... (in the unit that matters — TTFT/ITL split, per-category
> quality, cost per outcome, p99 reliability)
> **Trade:** ... (named and quantified — or "free lunch," justified)
> **Rollback trigger:** [metric] exceeds [threshold] for [duration]

This memo is the deliverable of the entire course. If you can write it for a real
system with real numbers in every field, you've learned the thing this course is
for. Everything else — the KV cache formula, the acceptance rate math, the
attribution dimensions — exists to let you fill in those six lines honestly.

---

## 7. Quiz

**1.** Name the four dimensions and, for each, one lever that improves it and what
that lever costs.

**2.** List five "free lunch" changes that improve multiple dimensions with no
real trade. Why must they be exhausted first?

**3.** State the two stack-layer rules of thumb and explain the reasoning behind
each.

**4.** Why is streaming the highest-leverage latency work, and which metric does
it make important?

**5.** Give the five questions of the decision framework. Which one makes the
decision falsifiable, and why does that matter?

**6.** A system has p95 E2E of 2.1s against a 5s SLO. Argue for *increasing*
latency, with an example.

**7.** From §4.1: contextual chunking is $0.33/point/day and a model upgrade is
$1,050/point/day. Why do most teams do the model upgrade first?

**8.** A team cut cost per call 71% and cost per resolved ticket rose 18%. Explain
the mechanism and name the metric that would have prevented it.

**9.** All four dimensions are constrained. Give the five options in order of
preference, and say which one engineers resist.

**10.** Walk through the §3.5 latency example: what was the diagnosis, what was the
first lever, and why was quality unaffected — or improved?

---

## 8. Review hooks

- *Fix three dimensions and the fourth is an outcome, not a choice.*
- *Exhaust the free lunches before paying for a real trade.*
- *Move up the stack before moving down. The highest viable layer wins.*
- *Diagnose the binding constraint before picking a lever.*
- *Cost per outcome. Per-category quality. TTFT and ITL separately.*
- *Error budget headroom is a budget to spend, not a margin to preserve.*
- *A change with no rollback trigger cannot be wrong, so it cannot be evaluated.*

---

## 9. Answer key

**1.** **Latency** — prefix caching improves it at no quality cost (or: smaller
batch, at the cost of throughput/cost). **Quality** — a reranker improves it, at
the cost of ~150ms latency. **Cost** — routing/cascades improve it, at the cost of
tail latency on escalated requests and some quality risk. **Reliability** —
fallback chains and validation improve it, at the cost of latency and cost.

**2.** Context engineering (cost + latency + quality); prefix caching (cost +
latency, no quality cost); reranking (quality up, context size down → cost down);
continuous batching / paged attention (throughput, no quality cost); speculative
decoding at low batch (latency, provably lossless). **Exhaust them first** because
they have no downside to weigh — spending quality risk on quantization while your
cache prefix is broken and your context is unoptimized is paying for something you
could have had free.

**3.** (i) **Move up the stack before moving down** — application-layer changes are
cheaper, faster to ship, more reversible, and usually larger in effect than
hardware changes, so the expected value per engineering day is far higher.
(ii) **The highest layer with a viable fix wins** — including the product layer:
reducing scope ("we don't do 300-page documents in one request") often outperforms
every engineering fix below it, and is a legitimate choice rather than a
concession.

**4.** Because it costs nothing and changes the felt latency from E2E to TTFT — an
8-second response becomes a 700ms wait followed by reading. It makes **TTFT** the
metric that matters; for a non-streaming product, E2E is the one. Optimizing the
wrong one of those two is a common way to do latency work with no user-visible
effect.

**5.** (1) Which dimension is the product actually constrained by, with evidence?
(2) Which layer does this touch, and is there a higher-layer fix? (3) What's the
effect, in the unit that matters? (4) What am I trading, quantified? (5) What's
the rollback trigger? **Question 5 makes it falsifiable** — a change with no
trigger can never be shown to have failed, so it persists regardless of its
effect and consumes the option to try something else.

**6.** With 2.9s of unused headroom, that latency is a spendable budget rather
than a margin. Example: add a cross-encoder reranker (+150ms) for a large recall
and precision gain, semantic validation (+60ms) for reliability, and a
reason-then-extract two-call pattern (+900ms) taking format adherence from 94% to
99%+ — total +1.1s, still comfortably inside the SLO, in exchange for substantial
quality and reliability improvements that would otherwise never be funded.

**7.** Because it requires no engineering — it's a configuration change with an
immediate, legible story ("we upgraded the model"), whereas contextual chunking
requires understanding the retrieval pipeline, writing ingest code, and
re-indexing. The efficiency gap is invisible unless someone has built the
$/point table, which requires per-category evals (Day 16) and cost attribution
(Day 18) to exist at all.

**8.** The cost optimizations — quantization, small-model routing, aggressive
caching — degraded answer quality in ways the per-call metric couldn't see, so
more interactions required additional turns and more escalated to human support.
Human handling costs orders of magnitude more than an inference call, so a small
rise in escalation rate swamps a large fall in per-call cost. **The metric that
would have prevented it: cost per successfully resolved outcome** (Day 18 §3.2),
which includes downstream human cost.

**9.** (1) Reduce scope — narrow the product's promise and refuse cleanly outside
it. (2) Segment — different SLOs per tier or use case. (3) Move work off the
critical path — precompute, index at write time, summarize asynchronously.
(4) Change the interaction model — async delivery removes latency as a constraint.
(5) Buy hardware. **Engineers resist option 1**, because narrowing the promise
feels like failure; it is in fact the highest-leverage move available.

**10.** **Diagnosis:** the TTFT/E2E split (Day 3) showed 6.4s of the 9s was
prefill, and prompts averaged 28k tokens — so the constraint was prompt size, not
model speed. **First lever:** streaming, which is free and cut *felt* latency to
6.4s immediately; then context engineering with reranking, cutting 28k tokens to
7k and prefill from 6.4s to 1.6s. **Quality was unaffected or improved** because
per Day 2 §3.2, removing distractor chunks reduces attention dilution and
contradiction — fewer, better chunks produce better answers, so the latency fix
and the quality fix were the same change.

---

## 10. Going deeper

- **"Site Reliability Engineering"** — Beyer et al., ch. 3–4 (embracing risk,
  service level objectives). The error-budget-as-spendable-headroom framing comes
  from here and is the most transferable idea in the book.
- **"The Goal"** — Goldratt. Theory of constraints, as a novel. The "optimize the
  bottleneck, everything else is noise" argument in §3.4 is the whole book, and
  reading it will change how you run prioritization meetings.
- **"Systems Performance"** — Brendan Gregg. The USE method and the discipline of
  measuring before optimizing, from the systems side. Chapter 2 is the relevant
  one.
- **"Designing Data-Intensive Applications"** — Kleppmann, ch. 1 on reliability,
  scalability, and maintainability as explicit, tradeable properties.

---

**Checkpoint:** take **[Checkpoint 4](checkpoint-4.md)** now.

**Next:** [Day 22 — Production Failure Modes and Capstone](day-22-production-failure-modes.md).
