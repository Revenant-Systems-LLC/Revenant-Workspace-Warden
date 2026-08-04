# Day 9 — Fine-Tuning vs. In-Context Learning vs. RAG vs. Distillation

**Time:** ~3 h · **Prereqs:** Days 2, 7, 8 · **Lab needs:** Ollama, Python

---

## 0. Warm-up review

1. Why is speculative decoding lossless? (Day 8)
2. When does speculation become a net loss? (Day 8)
3. Where does quantization damage quality first? (Day 7)
4. Name the four costs of a context token. (Day 2)

---

## 1. The one-paragraph version

Four ways to make a model do something it doesn't do out of the box, and they
solve different problems that people constantly confuse. **In-context learning**
puts the information or examples in the prompt: instant, per-request cost, no
training. **RAG** retrieves the relevant information at query time: handles large
and changing knowledge, adds a retrieval failure mode. **Fine-tuning** adjusts
weights to change *behaviour, format, and style*: durable, cheap at inference,
bad at teaching facts. **Distillation** trains a smaller model to imitate a bigger
one: the cost lever, not the capability lever. The single most common and most
expensive mistake in applied LLM work is **fine-tuning to teach facts** — it's
slow, expensive, unverifiable, impossible to update, and RAG does it better. The
second most common is **RAG-ing to teach behaviour** — stuffing style examples
into retrieved context when a fine-tune or a better system prompt was the answer.

---

## 2. Why this bites you

A company wants a support bot that knows their 12,000 help articles. An engineer
proposes fine-tuning on the articles. Six weeks and $40k later:

- The model produces text that *sounds* like the documentation and is factually
  unreliable — fine-tuning on documents teaches the *style* of the corpus far more
  reliably than its *contents*.
- It can't cite anything. There's no source to point at, so there's no way for a
  user or an auditor to verify a claim.
- Articles updated last week aren't reflected, and won't be until the next
  training run.
- Nobody can answer "why did it say that?"
- Deleting a customer's data from the model is not possible without retraining.
  That's a compliance problem with no engineering answer.

A RAG system would have taken two weeks, cost a fraction, updated in real time,
cited sources, and been debuggable. The fine-tune wasn't a bad *execution* — it
was the wrong lever, chosen before anyone asked what the actual gap was.

The mirror-image mistake is subtler and just as common: a team whose model won't
adhere to their house tone or their exact output schema keeps adding more
few-shot examples until 8,000 tokens of every request is exemplars, at enormous
recurring cost — when a small fine-tune would have baked the behaviour in for
free at inference time.

---

## 3. Core concepts

### 3.1 The diagnostic question

Before choosing anything, answer this precisely:

> **What, exactly, does the model not do?**

The four answers map to four levers:

| The gap | Lever | Why |
| --- | --- | --- |
| "It doesn't **know** this information" | **RAG** | Facts belong in retrievable storage, not weights |
| "It doesn't **behave** this way" (format, tone, structure, decision policy) | **Fine-tuning** (or ICL first) | Behaviour is a function of weights, or of examples |
| "It doesn't know a **small, stable** amount of information" | **In-context learning** | Just put it in the prompt |
| "It works, but it's **too expensive or slow**" | **Distillation** (or Day 7/8 levers) | This isn't a capability gap at all |

Most bad decisions come from skipping this question. "The model isn't good enough"
is not a diagnosis — it's the absence of one.

### 3.2 Facts vs. behaviour — the load-bearing distinction

**Facts** are propositions with truth values that can change: prices, policies,
customer records, API signatures, yesterday's incident. Properties: numerous,
volatile, must be verifiable, must be attributable, must be deletable, and access
often depends on who's asking.

**Behaviour** is how the model acts regardless of content: output format,
reasoning style, tone, refusal policy, when to call a tool, how to structure an
answer. Properties: few, stable, doesn't need citation, applies to everyone.

Now map each property onto the levers:

| Property | Weights (fine-tune) | Context (RAG/ICL) |
| --- | --- | --- |
| Update latency | Weeks | Seconds |
| Verifiable / citable | No | Yes |
| Per-user access control | No | **Yes** |
| Deletable (GDPR/right-to-erasure) | No — needs retraining | Yes |
| Cost at inference | Free | Per-token, per request |
| Scale limit | Whatever you can train on | Retrieval corpus can be unbounded |
| Debuggable ("why did it say that?") | Barely | Yes — inspect the retrieved chunks |

**Read the "per-user access control" and "deletable" rows again.** Those aren't
performance considerations; they're the reason fine-tuning on customer data is
often outright disqualified in regulated environments. Weights can't have an ACL.
If tenant A's data is in the weights, every tenant queries a model that has seen
it (Day 20).

### 3.3 In-context learning: the underrated default

Start here. Always. It's the only lever with a same-day feedback loop.

**Strong at:** format demonstration (few-shot beats prose descriptions for
structure), small stable facts, edge-case handling, quick iteration, and anything
you're still figuring out.

**Weak at:** large knowledge (window limits — Day 2), consistency (examples
influence but don't guarantee), and recurring cost (you pay for those tokens on
every single request, forever).

**The economics.** 3,000 tokens of exemplars, 1M requests/day, $3/M input:
**$9,000/day = $3.3M/year**. Prefix caching (Day 6) cuts that to ~$330/day. A
fine-tune that removes the need entirely takes it to ~$0 — *and* frees 3,000
tokens of window. That comparison is the honest case for fine-tuning, and it's an
economic argument, not a capability one.

**Rule:** if you've been running the same few-shot block unchanged for three
months and you're at volume, you're paying rent on something you could own.

### 3.4 RAG: the right lever for knowledge

Full architecture is Day 14. Today, just its *decision* properties.

**Strong at:** large corpora, frequently-changing facts, citation and
attribution, per-user permission filtering, deletion, debuggability, and adding
knowledge without touching the model.

**Weak at:** teaching behaviour or style; tasks where the answer requires
synthesizing across many documents at once; anything where retrieval itself is
hard (ambiguous queries, implicit questions); and it adds a whole new failure
surface — retrieval can miss, and when it does the model confabulates over
whatever it got (Day 15).

**The non-obvious point:** RAG is the only lever that gives you **permission-aware
knowledge**. Filter the retrieval index by the caller's ACL and the model
literally cannot cite what the user can't see. No other lever can do this. In any
multi-tenant or permissioned product, that alone usually decides it (Days 19–20).

### 3.5 Fine-tuning: the right lever for behaviour

**Strong at:** consistent output format and schema adherence; domain tone and
register; a decision policy that's hard to articulate but easy to demonstrate;
compressing a long system prompt into weights; teaching a specialized task with
abundant examples; reducing latency and cost by removing prompt overhead.

**Weak at, or outright wrong for:** facts (they go stale and can't be cited);
anything needing per-user access control; anything requiring deletion; small
datasets (a few hundred examples usually won't move behaviour reliably); and
rapidly changing requirements — every change is another training run.

**The methods, briefly:**

| Method | What it changes | Cost | Notes |
| --- | --- | --- | --- |
| **Full fine-tuning** | All weights | Highest | Risks catastrophic forgetting |
| **LoRA** | Low-rank adapters | Much lower | Adapters are swappable and composable |
| **QLoRA** | LoRA on a quantized base | Lowest | Trains large models on modest hardware |
| **Preference tuning (DPO etc.)** | Behaviour from preferred/rejected pairs | Medium | Good for tone/judgment where "correct" is comparative |

**LoRA's operational advantage is underrated:** adapters are small, so you can host
one base model and swap per-tenant or per-task adapters, rather than hosting N
full models. That changes the economics of per-customer customization completely.

**Catastrophic forgetting is the real risk.** Fine-tune hard on a narrow task and
general capability degrades — often in ways your task-specific eval can't see.
Always keep a **general capability eval** in the suite and run it before and
after every fine-tune (Day 16). This is the same lesson as Day 7 §3.6, arriving
from a different direction: *specialized training damages unspecialized
capabilities, and your specialized eval is blind to it by construction.*

### 3.6 Distillation: the cost lever

Covered on Day 8. The point for today: **distillation is not on the same axis as
the other three.** RAG, ICL, and fine-tuning close *capability* gaps.
Distillation closes a *cost/latency* gap for a capability you already have. If
your model can't do the thing, distillation cannot help — you'll distil the
failure.

The sequencing that follows:

```
1. Make it work at all           → ICL, RAG, fine-tuning
2. Make it work reliably         → evals, validation, guardrails (Days 10-12, 16)
3. THEN make it cheap            → distillation, quantization, routing
```

Distilling before step 2 produces a small model that reliably reproduces
behaviour you hadn't yet verified was correct — and now it's baked in.

### 3.7 The combinations that actually ship

Real systems use several. The common good ones:

- **RAG + fine-tuned formatting.** RAG supplies facts; a light fine-tune enforces
  citation format and answer structure. Each lever on the thing it's good at.
- **RAG + ICL.** Retrieved facts plus a few static exemplars for output shape.
  The default starting point for most products.
- **Fine-tune to compress a long prompt.** Bake a 5,000-token system prompt into
  weights; keep RAG for facts. Pure economics (§3.3).
- **Distil a RAG pipeline's outputs.** Use the big model + RAG to generate
  training data, then distil into a small model that keeps RAG at inference time.
  Cost of a small model, knowledge of the corpus.
- **Router + specialists** (Day 13). A cheap fine-tuned model for the common
  case, the big general model for the tail.

And the anti-patterns:

- **Fine-tuning on documents to teach facts.** §2. The single most expensive
  mistake in the field.
- **Fine-tuning before ICL.** You skipped the cheap experiment that would have
  told you whether the behaviour was even achievable.
- **RAG for behaviour.** Retrieving "style examples" per request — costly, and
  less consistent than either a system prompt or a fine-tune.
- **Distilling an unvalidated pipeline.** §3.6.
- **Fine-tuning on 200 examples and concluding "fine-tuning doesn't work."**
  You measured your dataset size, not the method.

### 3.8 The decision tree

```
What's the gap?
│
├─ COST/LATENCY only, capability is fine
│   └─ Route (Day 13) → quantize (Day 7) → speculate (Day 8) → distil (Day 8)
│       in that order: cheapest and most reversible first.
│
├─ KNOWLEDGE
│   ├─ Small (<5k tokens) and stable? ────────────→ ICL. Put it in the prompt.
│   ├─ Large, changing, or needs citation/ACL? ───→ RAG.
│   └─ Large, static, no citation needed, and
│      retrieval is genuinely infeasible? ────────→ continued pretraining
│                                                    (rare; usually a wrong turn)
│
└─ BEHAVIOUR (format, tone, policy, structure)
    ├─ Have you tried a better system prompt? ────→ do that first
    ├─ Have you tried few-shot? ──────────────────→ do that second
    ├─ Works with few-shot but costs too much,
    │  or isn't consistent enough? ───────────────→ fine-tune (LoRA first)
    └─ Judgment/preference, no single right answer?→ preference tuning
```

The ordering within each branch is deliberate: **cheapest and most reversible
first.** You can undo a prompt change in a minute; you cannot undo a fine-tune.

---

## 4. Napkin math

### 4.1 Few-shot rent vs. fine-tune purchase

Feature at 2M requests/day using 4,000 tokens of exemplars, $3/M input.

```
ICL, no caching:  2M × 4,000 × $3/M      = $24,000/day = $8.76M/yr
ICL, with caching: 2M × 4,000 × $0.30/M  =  $2,400/day = $876k/yr
Fine-tune:  training ~$8k + eval/infra ~$40k, then ~$0/request
            (+ ~$15k/yr maintenance for retraining on model upgrades)
```

Against the cached figure, payback is under a month, and you also recover 4,000
tokens of context window — which per Day 4 means more concurrent sequences on
self-hosted infrastructure. **Fine-tuning here is obviously correct**, and note
that the argument never once mentioned quality.

Now the same at 5,000 requests/day: cached ICL is $6/day = $2,190/yr against
$48k of engineering. **Obviously wrong.** Same technique, same task, opposite
answer, determined entirely by volume.

### 4.2 Fine-tuning facts, priced honestly

12,000 documents, updated ~200/week.

**Fine-tune approach:** train ~$10k, and to stay current you retrain — say
monthly. $120k/year in training alone, plus the eval work to confirm each run
didn't regress. Facts are up to 4 weeks stale. No citations. No per-user
filtering. No deletion story.

**RAG approach:** embedding ~$50 for the initial corpus, then a few dollars a
week for updates. Latency +150 ms. Facts current within minutes. Citations
included. Per-user ACL filtering possible. Deletion is a row delete.

**RAG is ~100× cheaper and strictly better on every non-latency dimension.** The
only thing the fine-tune buys is 150 ms — and it doesn't reliably buy the facts
at all, which was the entire point.

### 4.3 When fine-tuning facts is *actually* defensible

For completeness, the narrow case: the knowledge is small, static, universally
accessible, needs no citation, and the latency of retrieval is unacceptable —
e.g. baking a fixed product taxonomy into an on-device model with no network.
That's a real scenario. It is also rare, and it is not what most teams proposing
a fine-tune are facing. Check the five conditions before agreeing.

---

## 5. Failure modes

| Symptom | Wrong lever chosen | Right lever |
| --- | --- | --- |
| Model states outdated policies confidently | Fine-tuned on documents | RAG |
| Can't answer "where did that come from?" | Knowledge in weights | RAG with citations |
| Can't delete a customer's data from the system | Knowledge in weights | RAG |
| User sees data they lack permission for | Knowledge in weights (no ACL possible) | RAG with permission-filtered index |
| Output format inconsistent despite huge prompts | Trying ICL for behaviour at scale | Fine-tune, or constrained decoding (Day 10) |
| $3M/yr on exemplar tokens | Renting behaviour forever | Fine-tune |
| Fine-tune "didn't work" on 200 examples | Dataset far too small | More data, or use ICL |
| Fine-tuned model worse at everything else | Catastrophic forgetting | General capability eval; LoRA; lower LR |
| Distilled model reproduces a bug perfectly | Distilled before validating | Validate, then distil |
| RAG retrieves style examples every request | Using RAG for behaviour | System prompt or fine-tune |

---

## 6. Lab (75 min)

### Part A — Diagnose ten scenarios (25 min)

For each, name the gap (knowledge / behaviour / cost), the lever, and the single
strongest reason. Answers in §9.

1. A legal assistant must cite the exact clause it relied on.
2. Outputs must always be JSON matching a fixed schema; currently 94% compliant.
3. The model must know the 400 internal service names, which change monthly.
4. Answers must adopt the company's distinctive brand voice.
5. The 70B model works perfectly but costs $80k/month for a narrow
   classification task at 30M requests/day.
6. The model must know today's stock prices.
7. The model must follow a 15-step approval workflow that's hard to describe but
   easy to demonstrate with examples.
8. The model must never reveal data from another tenant.
9. The model must know the six constants in your pricing formula.
10. Answers are good, but p95 latency is 9s and users are leaving.

### Part B — Prove the facts/behaviour split (30 min)

Demonstrate that ICL teaches behaviour far more reliably than it teaches facts at
scale, and infer what that implies about fine-tuning.

```python
# lab-output/day09_facts_vs_behaviour.py
import httpx, json, random, re

def gen(prompt, n=200):
    r = httpx.post("http://localhost:11434/api/generate",
        json={"model": "llama3.1:8b", "prompt": prompt, "stream": False,
              "options": {"temperature": 0, "num_predict": n}}, timeout=300)
    return r.json()["response"]

# 60 invented facts. Vary how many are shown; always ask about one that WAS shown.
FACTS = {f"RWW-{i:03d}": f"{random.Random(i).choice(['Berlin','Osaka','Denver','Lima'])}"
         for i in range(60)}

def facts_trial(n_shown, probe_key):
    keys = list(FACTS)[:n_shown]
    block = "\n".join(f"Module {k} is hosted in {FACTS[k]}." for k in keys)
    out = gen(f"{block}\n\nQuestion: Where is module {probe_key} hosted? "
              f"Answer with just the city.")
    return FACTS[probe_key].lower() in out.lower()

# Behaviour: a distinctive, arbitrary output convention shown by example.
BEHAVIOUR_SHOTS = [
    ("summarize: the build failed", "» BUILD :: failed :: investigate"),
    ("summarize: tests passed",     "» TESTS :: passed :: proceed"),
    ("summarize: deploy rolled back","» DEPLOY :: rolled-back :: investigate"),
]
def behaviour_trial(n_shots, probe):
    shots = "\n".join(f"IN: {a}\nOUT: {b}" for a, b in BEHAVIOUR_SHOTS[:n_shots])
    out = gen(f"{shots}\nIN: {probe}\nOUT:", 60).strip()
    return bool(re.match(r"^»\s+\w+\s+::\s+\S+\s+::\s+\S+", out))

for n in (5, 15, 30, 60):
    probes = random.Random(n).sample(list(FACTS)[:n], k=min(8, n))
    acc = sum(facts_trial(n, p) for p in probes) / len(probes)
    print(f"facts shown={n:3d}  recall={acc:.0%}")

for n in (0, 1, 3):
    probes = ["summarize: the linter found 4 issues",
              "summarize: coverage dropped", "summarize: release tagged"]
    acc = sum(behaviour_trial(n, p) for p in probes) / len(probes)
    print(f"behaviour shots={n}  format_adherence={acc:.0%}")
```

**Predict first:** how does fact recall change from 5 to 60 shown facts? How many
shots does the arbitrary format convention need?

The expected shape: **behaviour is learned from 1–3 examples and holds;
fact recall degrades as the fact count grows** (Day 2's dilution, again). Write
the connection down: if 60 facts in context already degrade recall, the intuition
that 12,000 documents in *weights* will be reliably recalled has no support.

### Part C — Write the decision memo (20 min)

Pick a real gap in a system you care about (this repo's tutor mode is a fine
subject — it needs to know the Maestro syllabus *and* behave like a specific
instructor persona, which is a textbook two-lever problem).

> **Gap:** [precisely what the model doesn't do]
> **Classification:** knowledge / behaviour / cost
> **Levers considered:** [with the cheapest tried first]
> **Choice:** [lever], because [binding constraint]
> **Cost:** [engineering + recurring]
> **How I'd know it worked:** [the eval — Day 16]
> **What would make me reverse it:** [trigger]

Note that this repo's own case splits cleanly: the syllabus content is
**knowledge → RAG** (it changes, needs citing, and students should see sources);
the Axiom persona is **behaviour → system prompt, then few-shot, then maybe a
LoRA if volume justifies it**. Writing that out is the exercise.

---

## 7. Quiz

**1.** State the diagnostic question and the four gap types it distinguishes.

**2.** Give four properties of facts that make weights the wrong place for them.

**3.** Why can RAG enforce per-user access control when fine-tuning fundamentally
cannot?

**4.** 800k requests/day with 5,000 tokens of exemplars at $3/M input, prefix
caching enabled at $0.30/M. Annual ICL cost? Fine-tune costs $60k up front and
$20k/year. Payback period?

**5.** Same numbers but 3,000 requests/day. Recompute and give the recommendation.

**6.** What is catastrophic forgetting, and what must be in your eval suite to
detect it?

**7.** Why is distillation not on the same axis as the other three levers?

**8.** A team fine-tuned on 12,000 support articles. List four distinct problems
they now have, beyond quality.

**9.** When is fine-tuning on facts genuinely defensible? Give all five conditions.

**10.** Name the LoRA property that changes the economics of per-customer
customization, and explain why.

---

## 8. Review hooks

- *Ask what the model doesn't do. "Not good enough" isn't a diagnosis.*
- *Facts go in retrievable storage. Behaviour goes in weights. Confusing these is the expensive mistake.*
- *Weights can't have an ACL, and can't be un-taught.*
- *ICL is rent; fine-tuning is purchase. Volume decides.*
- *Distillation closes a cost gap, never a capability gap. Don't distil a bug.*
- *Cheapest and most reversible first, in every branch.*
- *A specialized eval is blind to specialization damage by construction.*

---

## 9. Answer key

### Part A answers

1. **Knowledge → RAG.** Citation is impossible from weights; the clause must be a
   retrievable, quotable object.
2. **Behaviour → constrained decoding first (Day 10), then fine-tune.** 94% is a
   format problem; schema-constrained generation gets to ~100% with no training.
3. **Knowledge → RAG.** Monthly change makes weights stale by construction.
4. **Behaviour → system prompt, then few-shot, then fine-tune if volume justifies.**
5. **Cost → distillation** (with routing as the cheaper first step). Narrow task
   plus 30M req/day is the textbook distillation case.
6. **Knowledge, real-time → tool call, not RAG.** Retrieval over a static index
   can't be current to the second; this needs a live API (Day 11).
7. **Behaviour → few-shot first; fine-tune if it doesn't hold.** "Hard to
   describe, easy to demonstrate" is the fine-tuning signature.
8. **Neither — this is isolation, not adaptation.** Enforce it in the harness with
   permission-filtered retrieval and tenant-scoped keys (Days 19–20). No amount of
   training makes this a model property.
9. **Knowledge, tiny and stable → ICL.** Six constants go in the system prompt.
10. **Cost/latency → measure the phase split first (Day 3)**, then route/quantize/
    speculate. It may not be a model problem at all — it may be queueing.

### Quiz answers

**1.** "What, exactly, does the model not do?" It distinguishes: missing
knowledge (→ RAG), missing behaviour (→ ICL then fine-tuning), a small stable
knowledge gap (→ ICL), and a cost/latency gap with no capability gap (→ routing,
quantization, speculation, distillation).

**2.** Any four: they change over time (weights update in weeks, facts in
seconds); they must be citable (weights offer no source); access is often
per-user (weights have no ACL); they must be deletable on request (weights
require retraining); they are numerous (more than any training set can reliably
encode); and they must be debuggable (you can inspect a retrieved chunk, not a
weight).

**3.** RAG filters the retrieval index by the caller's permissions *before*
anything reaches the context, so material the user can't access is never
available to be quoted. Fine-tuning bakes all training data into a single set of
weights shared by every caller — there is no per-request mechanism to make a
weight invisible to one user and visible to another.

**4.** 800,000 × 5,000 = 4e9 tokens/day × $0.30/M = **$1,200/day = $438,000/yr**.
Fine-tune year 1 = $80k. Payback ≈ $60k / $1,200 = **50 days**; ongoing it saves
~$418k/yr. Clear yes.

**5.** 3,000 × 5,000 = 1.5e7 tokens/day × $0.30/M = $4.50/day = **$1,643/yr**.
Against $60k up front and $20k/yr, payback is over 13 years and the ongoing cost
alone exceeds the savings 12×. **Recommendation: keep ICL.** Same technique, same
task, opposite answer — volume is the deciding variable.

**6.** Degradation of general capabilities caused by narrow fine-tuning: the model
gets better at the target task and worse at things it previously did well. Detect
it with a **general capability eval held constant across runs** — a broad
benchmark plus a sample of real out-of-distribution traffic — run before and after
every fine-tune. The task-specific eval cannot detect it, because it only measures
the thing you optimized.

**7.** RAG, ICL, and fine-tuning close *capability* gaps — they make the system
able to do something it couldn't. Distillation transfers an existing capability
into a cheaper model; it closes a *cost/latency* gap. If the capability isn't
there, distillation propagates the failure into a smaller, faster model.

**8.** Any four: facts go stale between training runs; no citations, so claims
can't be verified; no per-user access control; no deletion path for
right-to-erasure requests; no debuggability ("why did it say that?"); high
recurring retraining cost; and the model learned the corpus's *style* far more
reliably than its *content*, producing authoritative-sounding unreliable text.

**9.** All five must hold: the knowledge is (1) small, (2) static, (3) universally
accessible — no per-user filtering needed, (4) requires no citation or
attribution, and (5) retrieval latency or connectivity makes RAG infeasible
(e.g. offline on-device). Rare in practice; check all five before agreeing.

**10.** LoRA adapters are small and swappable, so you host **one** base model and
attach a per-tenant or per-task adapter at request time, instead of hosting N
full fine-tuned models. Per-customer customization goes from N× the serving
infrastructure to roughly 1× plus a small amount of adapter memory, which is what
makes it economically viable at all.

---

## 10. Going deeper

- **"Retrieval-Augmented Generation for Knowledge-Intensive NLP Tasks"** — Lewis
  et al. (arXiv 2005.11401). The original framing of parametric vs.
  non-parametric knowledge is exactly today's facts/behaviour split.
- **"LoRA: Low-Rank Adaptation of Large Language Models"** — Hu, Shen, Wallis,
  Allen-Zhu, Li, Wang, Wang, Chen (arXiv 2106.09685).
- **"QLoRA: Efficient Finetuning of Quantized LLMs"** — Dettmers, Pagnoni,
  Holtzman, Zettlemoyer (arXiv 2305.14314).
- **"Direct Preference Optimization"** — Rafailov, Sharma, Mitchell, Ermon,
  Manning, Finn (arXiv 2305.18290). The practical preference-tuning route.
- **"Fine-Tuning or Retrieval? Comparing Knowledge Injection in LLMs"** —
  Ovadia, Brief, Mishaeli, Elisha (arXiv 2312.05934). Direct empirical support
  for the §2 argument, if you need to win it with a citation.
- **"An Empirical Study of Catastrophic Forgetting in LLMs During Continual
  Fine-tuning"** — Luo et al. (arXiv 2308.08747).

---

**Next:** [Day 10 — Structured Output](day-10-structured-output.md). You've decided
where knowledge lives. Now make the model's output something a program can
actually consume.
