# Day 15 — Retrieval Evals: Recall, Precision, Grounding, Attribution, Citation Quality

**Time:** ~3.5 h · **Prereqs:** Day 14 · **Lab needs:** Day 14's pipeline

---

## 0. Warm-up review

1. Name the six RAG stages. (Day 14)
2. Why does dense retrieval fail on identifiers? (Day 14)
3. Why does a reranker need a threshold, not just a top-k? (Day 14)
4. What's the difference between a control and a preference? (Day 1)

---

## 1. The one-paragraph version

You cannot improve a RAG system without separating **retrieval quality** from
**generation quality**, and the single most valuable measurement in the whole
pipeline is deceptively simple: *was the answer present in the retrieved
context?* That one bit splits every failure into two populations with completely
different fixes. On top of it sit four more measurements: **recall** (did we get
the right chunks), **precision** (how much of what we got was junk), **grounding**
(is every claim supported by the retrieved text), and **attribution/citation
quality** (does the citation point at the passage that actually supports the
claim). Systems that measure only end-to-end answer accuracy can tell you they're
getting worse but never why, which means every fix is a guess.

---

## 2. Why this bites you

A team's RAG assistant scores 71% on answer accuracy. They spend six weeks on
prompt engineering, few-shot examples, and a bigger model. It reaches 74%.

Then someone finally measures the split:

```
Answer in retrieved context?   YES → 88% of those answered correctly
                               NO  → 11% of those answered correctly
Fraction where answer was retrieved: 62%
```

Check: `0.62 × 0.88 + 0.38 × 0.11 = 0.586`. Close enough to the observed number
given noise. **Retrieval is the binding constraint, by a mile.** The ceiling on
generation work is `0.62 × 1.00 = 62%` — they had already exceeded what perfect
generation could deliver, because some of the 38% get lucky.

Six weeks on the wrong term. This is Day 1 §4's multiplicative argument, made
concrete — and the measurement that would have redirected them takes about a day
to build.

There's a second, nastier version. A system's answers *look* well-cited: every
paragraph has a `[3]`. Nobody checks whether source 3 actually says that. When
someone finally samples 100 citations, 23 point to a passage that doesn't support
the claim. The citations were decoration, and users trusted them precisely
because they looked rigorous.

---

## 3. Core concepts

### 3.1 The diagnostic split — build this first

Before any metric, build the two-population split:

```python
def diagnose(query, gold_answer, pipeline):
    retrieved = pipeline.retrieve(query)
    answer    = pipeline.generate(query, retrieved)
    return {
        "answer_in_context": answer_supported_by(gold_answer, retrieved),
        "answer_correct":    matches(answer, gold_answer),
    }
```

Then a 2×2:

| | Answer correct | Answer wrong |
| --- | --- | --- |
| **In context** | ✅ working | **generation failure** — fix prompt, model, context order |
| **Not in context** | ⚠️ lucky or memorized — investigate | **retrieval failure** — fix chunking, hybrid, reranking |

Three things this immediately tells you that nothing else does:

- **Where to spend the next month.** Count the two failure cells.
- **Whether you have a "lucky" problem.** If a large fraction is top-right, the
  model is answering from parametric memory, not your documents. That looks great
  until the corpus contains something the model's training data contradicts, and
  it means your citations are fiction.
- **Your realistic ceiling.** End-to-end accuracy can't meaningfully exceed the
  in-context rate.

**Build this before any other eval.** It's cheap, and it prevents the six-week
mistake.

### 3.2 Retrieval metrics, properly defined

Given a query with a set of known-relevant chunks *R*, and retrieved top-*k* set
*K*:

| Metric | Formula | Answers |
| --- | --- | --- |
| **Recall@k** | `|R ∩ K| / |R|` | Did we get the relevant material? |
| **Precision@k** | `|R ∩ K| / k` | How much of what we returned was useful? |
| **MRR** | `1 / rank of first relevant` | How high is the first good hit? |
| **nDCG@k** | discounted gain vs. ideal | Ranking quality with graded relevance |
| **Hit rate@k** | fraction of queries with ≥1 relevant in K | Coarse, but the one that predicts answers |

Which to optimize depends on the stage:

- **Retrieval stage (before reranking): optimize recall@50 or @100.** You cannot
  rerank what you didn't retrieve. Precision here is nearly irrelevant.
- **Rerank stage: optimize precision@5 and nDCG@5.** Now every slot costs you
  attention dilution (Day 2).
- **End of pipeline: hit rate@k** is the number that predicts answer accuracy.

**The most common measurement error** is optimizing precision at the retrieval
stage — tightening it until only obviously-relevant chunks come back, which caps
recall and permanently ceilings the reranker. Retrieve wide, rank narrow, and
measure each stage with the metric that matters *for that stage*.

### 3.3 Building the gold set

The hard part, and there's no way around it.

**Size.** 100 queries gets you started and detects large regressions. 300–500
gives usable per-category resolution. Below ~50, run-to-run noise exceeds the
effects you're trying to measure.

**Composition — stratify deliberately:**

| Category | Share | Why |
| --- | --- | --- |
| Common real queries | 40% | Represents actual traffic |
| Exact identifiers/codes | 15% | Where dense retrieval fails (Day 14) |
| Multi-hop / synthesis | 15% | Needs several chunks |
| Paraphrases of common queries | 10% | Robustness |
| Temporal / superseded content | 10% | Freshness correctness |
| **Unanswerable** | **10%** | **The system must return nothing** |

**The unanswerable stratum is the one everyone omits and it's the most
diagnostic.** A retriever with no threshold scores fine on the other five
categories and 0% here — and its production failure mode is confidently answering
questions your corpus can't support. If you build only one category deliberately,
build this one.

**Sourcing gold labels, cheapest to best:**

1. **Synthetic from documents** — take a chunk, have a model write a question it
   answers, label that chunk relevant. Fast, and **biased**: the questions use the
   chunk's own vocabulary, so lexical retrieval looks better than it is. Use it to
   bootstrap, never as the only source.
2. **From production logs** — real queries, hand-labelled. Much better
   distribution. This is the main investment.
3. **From user feedback** — thumbs-down with the retrieved set attached is a
   near-free stream of hard negatives.
4. **Expert-authored** — for the tricky categories (multi-hop, temporal,
   unanswerable), have someone who knows the domain write them.

**Multiple relevant chunks per query.** Real questions are often answerable from
several passages. A gold set marking exactly one relevant chunk will
systematically under-report recall and mislead you about reranking.

**Version it.** The gold set is code. It lives in the repo, it's reviewed, and
when it changes, the change is deliberate and noted — otherwise your historical
scores are incomparable and your regression detection is meaningless (Day 16).

### 3.4 Grounding — is every claim supported?

Retrieval can be perfect and the answer still wrong, because the model added
something. **Grounding** (a.k.a. faithfulness) measures whether each claim in the
answer is supported by the retrieved context.

The method:

```
1. Decompose the answer into atomic claims.
     "The refund window is 30 days and requires the original receipt."
       → claim A: the refund window is 30 days
       → claim B: a refund requires the original receipt
2. For each claim, ask: is it entailed by the retrieved context?
     - NLI model (fast, cheap, decent)
     - LLM judge with the claim and the context (better, costlier) — Day 16
     - String/semantic overlap (weak; fine as a smoke test)
3. Grounding score = supported claims / total claims.
```

Two distinct failure types this exposes, which people conflate:

- **Unsupported claim** — the model asserted something the context doesn't say.
  Usually parametric memory bleeding in, or over-generalization.
- **Contradicted claim** — the context says the opposite. Much more serious;
  usually indicates conflicting retrieved chunks (Day 14 §3.6) or an
  instruction-following failure.

Track them separately. Contradiction should be near zero and should alert.

**And note what grounding does *not* measure: correctness.** A perfectly grounded
answer is wrong if the retrieved documents are wrong or stale. Grounding says
"faithful to the sources." Accuracy against a gold answer says "true." You need
both, and confusing them is how teams ship a system that faithfully reproduces
outdated policy.

### 3.5 Attribution and citation quality

A citation makes a claim checkable. A wrong citation makes a claim *look*
checkable, which is worse than none.

Measure four things separately:

| Metric | Question | Failure |
| --- | --- | --- |
| **Citation coverage** | What fraction of claims carry a citation? | Uncited assertions |
| **Citation correctness** | Does the cited passage support the claim? | **Decorative citations** |
| **Citation precision** | Is it the *most* relevant source, or a vaguely related one? | Sloppy attribution |
| **Citation granularity** | Does it point at a passage or a whole 40-page document? | Unverifiable in practice |

The measurement:

```python
def citation_correctness(answer_claims, citations, chunks):
    ok = 0
    for claim, cited_ids in zip(answer_claims, citations):
        cited_text = " ".join(chunks[i]["text"] for i in cited_ids)
        if entails(cited_text, claim):        # NLI or LLM judge
            ok += 1
    return ok / max(len(answer_claims), 1)
```

**Two engineering approaches, and they differ in kind:**

- **Post-hoc attribution** — generate, then find a supporting chunk per claim.
  Easy to add; the citation is a *search result*, so it can be plausible and
  wrong.
- **Constrained/inline attribution** — require a citation at generation time
  (e.g. chunks carry IDs and the model must emit `[chunk_id]`), then **validate
  every emitted ID exists and that the claim is entailed**, rejecting the answer
  if not. Harder, and it's an actual control (Day 1 §3.2).

Note the pattern: this is Day 10's semantic validation and Day 11's referential
integrity applied to citations. A cited chunk ID that isn't in the retrieved set
is a **hallucinated identifier** and should be caught by exactly the same
machinery.

### 3.6 The full metric panel

What a mature RAG system tracks, per release and continuously in production:

```
RETRIEVAL
  recall@50, recall@100          (pre-rerank; the ceiling)
  precision@5, nDCG@5            (post-rerank)
  hit_rate@k                     (predicts answer accuracy)
  exact_identifier_recall        (the dense-retrieval blind spot)
  unanswerable_abstention_rate   (fraction correctly returning nothing)
  stale_chunk_retrieval_rate     (freshness)

GENERATION
  answer_accuracy                (vs. gold answers)
  grounding_score                (supported claims / total)
  contradiction_rate             (claims contradicted by context — alert on this)
  abstention_correctness         (said "I don't know" when it should have)

ATTRIBUTION
  citation_coverage
  citation_correctness           (the one that's usually never measured)
  hallucinated_chunk_id_rate     (should be zero — it's a validation bug if not)

DIAGNOSTIC
  answer_in_context_rate         (§3.1 — the most valuable single number)
  retrieval_vs_generation_split  (the 2x2)
```

Two of these deserve a permanent dashboard tile: **`answer_in_context_rate`**
(where's the constraint?) and **`citation_correctness`** (are we lying with
footnotes?).

### 3.7 Continuous evaluation, not just pre-release

Retrieval quality drifts even with zero deploys, because the *corpus* changes
(Day 14 §2, Checkpoint 1 C3). So:

- **Run the gold set on a schedule**, not just in CI. Nightly is typical.
- **Sample production traffic** and score grounding and citation correctness with
  an LLM judge on 1–5% of requests (Day 16). It's the only way to see real-query
  quality.
- **Alert on deltas, not absolutes.** A 4-point recall drop week over week is a
  signal even if the absolute number still looks acceptable.
- **Segment by corpus age and by query category.** Aggregate scores hide the
  category that's failing — the same lesson as Day 7's aggregate-benchmark trap,
  which is not a coincidence: *aggregates hide the failure, in every domain this
  course touches.*

---

## 4. Napkin math

### 4.1 Where the ceiling is

`answer_in_context_rate = 0.62`, generation accuracy given context = 0.88, and
"lucky" accuracy without context = 0.11.

```
current   = 0.62 × 0.88 + 0.38 × 0.11 = 0.587
perfect generation (1.00 given context) = 0.62 × 1.00 + 0.38 × 0.11 = 0.662
perfect retrieval (1.00), current generation = 1.00 × 0.88 = 0.880
```

Perfect generation buys 7.5 points. Perfect retrieval buys 29. **And you can move
retrieval a long way with a reranker in a week** (Day 14 §4.1), while "perfect
generation" isn't purchasable at all. The decision makes itself once the number
exists.

### 4.2 Sizing the gold set

You want to detect a 3-point regression in hit rate at ~80% baseline. Binomial
standard error:

```
SE = sqrt(p(1−p)/n) = sqrt(0.8 × 0.2 / n) = 0.4 / sqrt(n)
```

For a 3-point change to be about 2 SE:

```
0.03 = 2 × 0.4/sqrt(n)  →  sqrt(n) = 26.7  →  n ≈ 712
```

**~700 queries to reliably detect a 3-point regression.** For a 10-point
regression, n ≈ 64. This is why "we have 30 eval queries" systems can only detect
catastrophes — and why teams keep being surprised by gradual degradation. Size
the set to the regression you care about catching, and if you can't afford 700,
know that you're only instrumented for large moves and say so out loud.

### 4.3 The cost of decorative citations

Legal research assistant. 5,000 answers/day, average 6 citations each = 30,000
citations. Measured citation correctness: 0.81.

```
wrong citations/day = 30,000 × 0.19 = 5,700
```

In a domain where a citation is the entire value proposition. And every one of
them *looks* rigorous — the failure is invisible by construction, because the
whole point of a citation is that the reader doesn't re-derive the claim.

Cost of measuring: an entailment check per citation at ~$0.0002 = **$6/day**.

---

## 5. Failure modes

| Symptom | Metric that reveals it | Fix |
| --- | --- | --- |
| Months spent on prompts, no gain | `answer_in_context_rate` | Redirect to retrieval |
| Eval score good, users unhappy | Category-segmented scores | Stratify the gold set |
| Answers right for the wrong reason | "Not in context but correct" cell | Investigate parametric leakage |
| Citations look great, are wrong | `citation_correctness` | Entailment validation, inline attribution |
| Model cites a chunk that wasn't retrieved | `hallucinated_chunk_id_rate` | Validate IDs (Day 11 machinery) |
| Answers faithfully repeat stale policy | `stale_chunk_retrieval_rate` | Freshness filters (Day 14) |
| System never says "I don't know" | `unanswerable_abstention_rate` | Rerank threshold + abstention instruction |
| Regression not caught until users complain | Gold set too small / not scheduled | Size it (§4.2); run nightly |
| Recall plateaus no matter what you rerank | Optimizing precision pre-rerank | Optimize recall@100 at retrieval |
| Grounding high, accuracy low | Grounding vs. accuracy confusion | Both metrics; check corpus correctness |

---

## 6. Lab (95 min)

Uses the pipeline from Day 14.

### Part A — Build the gold set (35 min)

Create `docs/curriculum/.../eval/retrieval_gold.jsonl` with 60 queries over this
repo (aim for 300+ later; 60 is enough to see the mechanics), stratified per §3.3:

```jsonl
{"id":"q001","query":"How does the app store API keys?","relevant_chunks":["Providers/SecretsManager.cs#0","Providers/SecretsManager.cs#1"],"gold_answer":"Via SecretsManager, using SecureString and DPAPI-style protection.","category":"semantic"}
{"id":"q002","query":"OLLAMA_NUM_PARALLEL","relevant_chunks":["docs/.../day-05...#3"],"gold_answer":"Environment variable controlling Ollama's parallel request handling.","category":"exact_identifier"}
{"id":"q003","query":"What is the app's Kubernetes deployment strategy?","relevant_chunks":[],"gold_answer":null,"category":"unanswerable"}
```

Rules:
- 6 unanswerable (`relevant_chunks: []`) — these are the most important entries.
- Multiple relevant chunks where genuinely applicable.
- Include at least 5 exact-identifier queries and 5 multi-hop.
- Commit it. It's code.

### Part B — The metric harness (35 min)

```python
# lab-output/day15_eval.py
import json, statistics
from collections import defaultdict

def recall_at_k(relevant, retrieved, k):
    if not relevant: return None                 # unanswerable: N/A
    got = set(retrieved[:k]) & set(relevant)
    return len(got) / len(relevant)

def precision_at_k(relevant, retrieved, k):
    if not relevant: return None
    return len(set(retrieved[:k]) & set(relevant)) / k

def ndcg_at_k(relevant, retrieved, k):
    import math
    rel = set(relevant)
    dcg  = sum((1.0 if d in rel else 0.0) / math.log2(i + 2)
               for i, d in enumerate(retrieved[:k]))
    idcg = sum(1.0 / math.log2(i + 2) for i in range(min(len(rel), k)))
    return dcg / idcg if idcg else 0.0

def abstained(retrieved_after_threshold): return len(retrieved_after_threshold) == 0

def evaluate(gold_path, pipeline):
    by_cat = defaultdict(lambda: defaultdict(list))
    twobytwo = defaultdict(int)
    for line in open(gold_path, encoding="utf-8"):
        g = json.loads(line)
        retrieved = pipeline.retrieve(g["query"], k=100)
        reranked  = pipeline.rerank(g["query"], retrieved, k=5, threshold=0.35)
        cat = g["category"]

        if g["relevant_chunks"]:
            by_cat[cat]["recall@100"].append(recall_at_k(g["relevant_chunks"], retrieved, 100))
            by_cat[cat]["recall@5"].append(recall_at_k(g["relevant_chunks"], reranked, 5))
            by_cat[cat]["ndcg@5"].append(ndcg_at_k(g["relevant_chunks"], reranked, 5))
        else:
            by_cat[cat]["abstained"].append(1.0 if abstained(reranked) else 0.0)

        # The 2x2 (§3.1)
        answer = pipeline.generate(g["query"], reranked)
        in_ctx = bool(set(reranked) & set(g["relevant_chunks"])) if g["relevant_chunks"] else False
        correct = pipeline.judge_correct(answer, g["gold_answer"])
        twobytwo[(in_ctx, correct)] += 1

    for cat, metrics in sorted(by_cat.items()):
        parts = [f"{m}={statistics.mean(v):.2f}" for m, v in metrics.items() if v]
        print(f"{cat:20s} " + "  ".join(parts))
    print("\n2x2:", dict(twobytwo))
    return by_cat, twobytwo
```

Run it against three configurations from Day 14: dense-only, hybrid, and
hybrid+rerank+threshold.

Fill in and commit this table:

| Config | recall@100 | recall@5 | nDCG@5 | exact-id recall | abstention on unanswerable |
| --- | --- | --- | --- | --- | --- |
| Dense only | | | | | |
| Hybrid | | | | | |
| Hybrid + rerank | | | | | |

Then compute the 2×2 for the best configuration and answer, in
`lab-notebook.md`: **where is your binding constraint, and what would you do
next?**

### Part C — Citation correctness (25 min)

Have the pipeline emit `[chunk_id]` citations. Then:

1. **Validate IDs.** Every emitted chunk ID must be in the retrieved set. Measure
   `hallucinated_chunk_id_rate`. It should be zero once validated — if it isn't,
   you have a Day 11 referential-integrity bug in your citation path.
2. **Decompose claims.** Split each answer into atomic claims (a model call is
   fine).
3. **Check entailment.** For each claim, ask a model: "Does this passage support
   this claim? Answer SUPPORTED / CONTRADICTED / NOT_ADDRESSED."
4. Report `citation_coverage`, `citation_correctness`, and `contradiction_rate`.

**Predict `citation_correctness` before you measure it.** Most people guess
0.90+. Write your guess down first — the gap between the guess and the number is
the point of the exercise, and it's the reason §4.3 exists.

---

## 7. Quiz

**1.** Describe the diagnostic 2×2 and what each cell tells you to do.

**2.** Why is the "not in context but correct" cell a warning rather than good
news?

**3.** Which metric do you optimize at the retrieval stage, and which after
reranking? Why is optimizing the wrong one at the retrieval stage harmful?

**4.** Give the six gold-set strata and say which is most diagnostic and why.

**5.** Why is synthetic-from-documents gold data biased, and what does it make
look better than it is?

**6.** Distinguish grounding from accuracy. Give a case with perfect grounding and
a wrong answer.

**7.** Distinguish an unsupported claim from a contradicted claim. Which should
alert, and why?

**8.** Name the four citation metrics and the failure each catches.

**9.** Compute: you want to detect a 4-point regression around an 85% baseline.
How large must the gold set be? Show the working.

**10.** `answer_in_context_rate = 0.55`, accuracy given context = 0.92, accuracy
without = 0.08. Current end-to-end? Ceiling from perfect generation? From perfect
retrieval? What do you work on?

---

## 8. Review hooks

- *Was the answer in the retrieved context? Build that measurement first.*
- *End-to-end accuracy can't exceed the in-context rate. That's your ceiling.*
- *Retrieve wide and measure recall; rank narrow and measure precision.*
- *The unanswerable stratum is the one everyone omits and the most diagnostic.*
- *Grounding is faithfulness, not truth. You need both.*
- *A wrong citation is worse than no citation — it looks checkable.*
- *Aggregates hide the failing category. Always segment.*

---

## 9. Answer key

**1.** Rows: was the answer present in the retrieved context. Columns: was the
final answer correct. **In-context + correct** — working. **In-context + wrong**
— a *generation* failure; fix the prompt, context ordering, chunk count, or model.
**Not in context + wrong** — a *retrieval* failure; fix chunking, hybrid search,
reranking, or the corpus. **Not in context + correct** — the model answered
without support; investigate.

**2.** Because the answer came from the model's parametric memory rather than
your documents. It looks like success but means: any citation attached to it is
fiction; the behaviour will silently break when your corpus contains something
the model's training contradicts; and it inflates your measured accuracy in a way
that will not survive a model change. It also means your retrieval is worse than
your accuracy number implies.

**3.** **Retrieval stage: recall@50/@100** — you cannot rerank what you never
retrieved, so recall sets the ceiling for everything downstream. **After
reranking: precision@5 and nDCG@5** — each context slot now costs attention
dilution. Optimizing *precision* at the retrieval stage is harmful because it
tightens the candidate set, caps recall, and permanently ceilings the reranker —
you've thrown away the material the accurate stage was going to sort.

**4.** Common real queries (40%), exact identifiers (15%), multi-hop/synthesis
(15%), paraphrases (10%), temporal/superseded (10%), **unanswerable (10%)**. The
unanswerable stratum is most diagnostic because it's the only one that measures
whether the system will *decline*, and a threshold-less retriever scores
perfectly on all other strata while confidently answering questions the corpus
can't support — the most damaging production failure mode, and invisible without
this stratum.

**5.** The question is generated *from* the chunk, so it reuses the chunk's
vocabulary and phrasing. This makes lexical/sparse retrieval and any
term-overlap-based method look far better than they'll be on real user queries,
which use different words than the documents. It also under-represents multi-hop
questions and can't produce unanswerable ones at all.

**6.** **Grounding** = every claim is supported by the retrieved context
(faithfulness to sources). **Accuracy** = the answer is true. Perfect grounding
with a wrong answer: the retrieved chunk is a superseded policy stating a 14-day
refund window; the model faithfully reports 14 days; the current policy is 30
days. Grounding = 1.0, accuracy = 0. This is why freshness (Day 14 §3.6) is a
correctness property.

**7.** An **unsupported** claim is one the context simply doesn't address — the
model added it, usually from parametric memory or over-generalization. A
**contradicted** claim is one the context explicitly denies. **Contradiction
should alert**, because it indicates either an instruction-following failure or —
more often — that mutually inconsistent chunks were retrieved and the model chose
arbitrarily, which is a retrieval/freshness defect with a much higher severity
than a stray extra assertion.

**8.** **Coverage** — fraction of claims carrying a citation (catches uncited
assertions). **Correctness** — does the cited passage actually support the claim
(catches decorative citations). **Precision** — is it the most relevant source
rather than a vaguely related one (catches sloppy attribution). **Granularity** —
does it point at a passage or a whole document (catches citations that are
unverifiable in practice).

**9.** `SE = sqrt(0.85 × 0.15 / n) = sqrt(0.1275/n) = 0.357/sqrt(n)`. For a
4-point change at ~2 SE: `0.04 = 2 × 0.357/sqrt(n)` → `sqrt(n) = 17.85` →
**n ≈ 319**. (Roughly 320 queries.)

**10.** Current: `0.55 × 0.92 + 0.45 × 0.08 = 0.506 + 0.036 = 0.542`.
Perfect generation: `0.55 × 1.00 + 0.45 × 0.08 = 0.586` — **+4.4 points**.
Perfect retrieval: `1.00 × 0.92 = 0.92` — **+37.8 points**. **Work on
retrieval**, and specifically the interventions with the best ratio: hybrid
search and a reranker (Day 14), which typically move recall a long way in days,
not months.

---

## 10. Going deeper

- **"RAGAS: Automated Evaluation of Retrieval Augmented Generation"** — Es,
  James, Espinosa-Anke, Schockaert (arXiv 2309.15217). Faithfulness, answer
  relevance, and context relevance as computable metrics.
- **"ARES: An Automated Evaluation Framework for RAG Systems"** — Saad-Falcon,
  Khattab, Potts, Zaharia (arXiv 2311.09476). Note especially its use of
  prediction-powered inference to get confidence intervals from few human labels.
- **"Evaluating Verifiability in Generative Search Engines"** — Liu, Zhang, Liang
  (arXiv 2304.09848). Measures citation correctness in deployed systems, and the
  numbers are sobering — read it before you predict yours.
- **"Attributed Question Answering"** — Bohnet et al. (arXiv 2212.08037). The
  AIS (Attributable to Identified Sources) framework.
- **"Introduction to Information Retrieval"** — Manning, Raghavan, Schütze, ch. 8.
  The classical definitions of recall, precision, MAP, and nDCG, done properly.

---

**Checkpoint:** take **[Checkpoint 3](checkpoint-3.md)** now.

**Next:** [Day 16 — Evals](day-16-evals.md). Retrieval evals were the specific
case. Tomorrow: the general discipline.
