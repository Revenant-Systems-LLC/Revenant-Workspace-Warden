# Day 14 — RAG Architecture: Chunking, Embeddings, Hybrid Search, Reranking, Freshness

**Time:** ~3.5 h · **Prereqs:** Days 2, 6, 9 · **Lab needs:** Ollama + embeddings, Python

---

## 0. Warm-up review

1. Which adaptation lever handles facts, and which handles behaviour? (Day 9)
2. Why can RAG enforce per-user access control when fine-tuning cannot? (Day 9)
3. What shape does the accuracy-vs-k curve have, and why? (Day 2)
4. Why must the eval bypass response caches? (Day 6)

---

## 1. The one-paragraph version

RAG is a pipeline with six stages — ingest, chunk, embed, retrieve, rerank,
generate — and **the generation stage is almost never where the quality problem
is.** Most RAG failures are retrieval failures wearing a hallucination costume:
the right passage was never in the context, so the model filled the gap. The
architecture decisions that matter are chunking (what a retrievable unit is),
hybrid search (semantic recall plus lexical precision, because embeddings cannot
match exact identifiers), reranking (turning high recall into high precision),
and freshness (which is a *correctness* property, not an ops chore). If you build
only "embed everything, cosine top-10, stuff into the prompt," you have built the
version that demos beautifully and degrades as your corpus grows — which,
per Day 2, is exactly when it matters.

---

## 2. Why this bites you

The naive pipeline works impressively on 500 documents and fails on 50,000. Three
mechanisms, all predictable:

**Distractor accumulation.** With a fixed k=10, a 100× larger corpus means 100×
more near-miss passages competing for those ten slots. Precision collapses even
though recall@1000 is unchanged, and per Day 2, distractors actively degrade the
answer.

**Exact-match blindness.** A user searches for error code `E_INVALID_SIG_0x8007`
or SKU `RWW-8837-B`. Embeddings encode *meaning*, and these strings have almost
none — they land near every other alphanumeric identifier in the space. Pure
vector search misses them routinely. This is not a tuning problem; it is a
property of dense retrieval, and it is why hybrid search exists.

**Silent staleness.** A policy changed in March. Both the old and new documents
are in the index. Both retrieve. The model has no principled way to choose, so it
picks arbitrarily or blends them — and sounds equally confident either way.

The team's diagnosis is almost always "the model hallucinates." The instrument
that would correct them is a retrieval eval (Day 15) — which is why these two days
are adjacent.

---

## 3. Core concepts

### 3.1 The pipeline, and where things go wrong

```
INGEST     parse, clean, extract metadata, deduplicate
   ↓                                      ← failures: bad PDF extraction, lost
CHUNK      split into retrievable units      structure, tables destroyed
   ↓                                      ← failures: split mid-concept, no context
EMBED      chunk → vector; also build a lexical index
   ↓                                      ← failures: wrong model, no domain fit
RETRIEVE   query → candidates (dense + sparse)
   ↓                                      ← failures: low recall, exact-match miss
RERANK     candidates → ordered by true relevance
   ↓                                      ← failures: none, if you have one; big if not
ASSEMBLE   select top-k under a budget, label provenance   (Day 2)
   ↓                                      ← failures: too many chunks, no dates
GENERATE   answer, grounded, with citations
                                          ← failures: the ones everyone blames
```

**Track a failure to its stage before fixing anything.** The Day 15 diagnostic —
was the answer in the retrieved context? — separates "retrieval missed it" from
"retrieval found it and generation ignored it," and those have entirely different
fixes.

### 3.2 Chunking: the highest-leverage decision

A chunk is the atomic unit of retrieval. Get this wrong and nothing downstream
recovers.

**The tension:** small chunks are precise but lose context ("It costs $49" — what
does?); large chunks carry context but dilute the embedding, because a 2,000-token
chunk's vector is an average over many topics and is close to nothing in
particular.

**Strategies, worst to best:**

| Strategy | How | Problem |
| --- | --- | --- |
| **Fixed size** | Every 512 tokens | Splits mid-sentence, mid-table, mid-function |
| **Fixed + overlap** | 512 with 64-token overlap | Better boundaries; duplicated content |
| **Recursive/structural** | Split on ¶ → sentence, respecting markdown/code structure | Good default |
| **Document-aware** | Split on real boundaries: headings, sections, function definitions | Much better; needs per-format work |
| **Semantic** | Split where embedding similarity between adjacent sentences drops | Expensive; sometimes worth it |
| **Contextual / late** | Chunk normally, then prepend document- and section-level context to each chunk *before embedding* | Best quality; extra ingest cost |

**Contextual chunking deserves attention** because it directly addresses the
central weakness. Before embedding a chunk, prepend a short generated or templated
summary situating it:

```
[Document: 2024 Refund Policy | Section: Damaged Goods | Effective: 2024-03-01]
Refunds for damaged goods must be requested within 30 days of delivery...
```

Now the chunk's embedding encodes *what it's about*, not just its words, and
"what's the damaged-goods refund window?" matches it strongly. Anthropic's
published version of this reported large reductions in retrieval failure rate;
even a cheap templated version (metadata prefix, no LLM call) captures a lot of
the benefit.

**Practical defaults to start from, then measure:** 300–800 tokens per chunk,
10–20% overlap, split on structural boundaries, prepend document title and
section path, store the parent document ID for expansion.

**Parent-document retrieval** is a strong pattern worth knowing: embed and search
over *small* chunks for precision, but pass the *parent* section to the model for
context. You get precise matching and sufficient context, which is otherwise a
genuine tradeoff.

### 3.3 Embeddings: what actually matters

- **Domain fit beats leaderboard rank.** A model that tops MTEB on general text
  may underperform on your legal or medical or code corpus. Evaluate on your data
  (Day 15). This is the same lesson as Day 7's benchmark trap.
- **Asymmetry.** Queries and documents have different shapes — a short question
  vs. a long passage. Many models expect instruction prefixes
  (`"query: "` / `"passage: "`). **Using the wrong prefix, or omitting it, quietly
  degrades retrieval by a lot**, and it's invisible unless you measure.
- **Dimensionality trades storage for quality**, usually with diminishing returns.
  Some models support Matryoshka truncation, letting you cut dimensions with
  graceful degradation.
- **Normalize consistently**, and use the same distance metric everywhere. Mixing
  cosine and dot product across your index and query path is a real bug that
  produces plausible-looking bad results.
- **Changing the embedding model means re-embedding the entire corpus.** Budget
  for it. Version your index by embedding model — and note that this is also a
  cache-key component (Day 6 §3.4).

### 3.4 Hybrid search: why dense alone is insufficient

Dense (vector) retrieval finds semantic matches. Sparse (BM25/keyword) finds
lexical matches. They fail on opposite things:

| Query | Dense | Sparse |
| --- | --- | --- |
| "how do I get my money back" | ✅ finds "refund policy" | ❌ no term overlap |
| "error E_INVALID_SIG_0x8007" | ❌ near every other code | ✅ exact match |
| "RWW-8837-B" | ❌ meaningless in embedding space | ✅ exact match |
| "what if the item arrives broken" | ✅ finds "damaged goods" | ⚠️ partial |
| "Smith v. Anderson 2019" | ❌ | ✅ |

**Any corpus with identifiers, codes, product names, error strings, or proper
nouns needs sparse retrieval.** That's most corpora, and it's why "we'll just use
a vector database" is an incomplete architecture.

**Combining them — Reciprocal Rank Fusion** is the robust default because it needs
no score normalization (dense and sparse scores are not comparable):

```python
def rrf(rank_lists, k=60):
    """rank_lists: list of ranked doc-id lists, best first."""
    scores = {}
    for ranks in rank_lists:
        for i, doc_id in enumerate(ranks):
            scores[doc_id] = scores.get(doc_id, 0) + 1.0 / (k + i + 1)
    return sorted(scores, key=scores.get, reverse=True)
```

RRF only uses *rank position*, which makes it resilient to the two retrievers
having wildly different score scales. Weighted score fusion can beat it when
tuned, but requires normalization and re-tuning whenever either retriever changes.
Start with RRF.

**A third leg worth knowing:** ColBERT-style late interaction stores a vector per
token and scores by max-similarity across token pairs. Much better at exact-ish
matching than single-vector dense retrieval, at higher storage cost. Consider it
when hybrid isn't enough.

### 3.5 Reranking: turning recall into precision

**The insight:** retrieval must be fast over millions of documents, so it uses
cheap approximations (a single vector per chunk, no query-document interaction).
Reranking runs an expensive, accurate model over the ~50–100 candidates retrieval
returned.

A **cross-encoder** reranker takes `(query, passage)` *together* and scores
relevance. Because it sees both at once, it can judge things a dot product can't:
does this passage actually *answer* the question, or merely discuss the topic?

```
Retrieve top-100 (fast, high recall, low precision)
        ↓
Rerank to top-5 (slow, high precision)
        ↓
Generate
```

Why this matters so much: per Day 2, **five good chunks beat fifty mediocre
ones.** Reranking is the mechanism that gets you the five. It is typically the
single largest quality improvement available to a naive RAG system, usually adds
50–200ms, and requires no change to your index.

**Rerankers you'll encounter:** cross-encoder models (Cohere Rerank, BGE-reranker,
mxbai-rerank) and LLM-as-reranker (more expensive, more flexible, can incorporate
instructions like "prefer documents from the last 90 days").

**Set a relevance threshold, not just a top-k.** If the best candidate scores 0.2,
returning it anyway hands the model an irrelevant passage to confabulate over.
Returning *nothing* — and having the generation stage say "I don't have
information about that" — is a better product. **This is one of the few places
where the correct engineering move is to make the system refuse more often.**

### 3.6 Freshness — a correctness property

**Every chunk carries dates.** Ingested, last modified, effective-from,
effective-until. Then:

- **Filter** by validity where the domain has it (don't retrieve a policy that
  expired).
- **Boost** recency in ranking, with a decay tuned to how fast your domain moves.
- **Surface dates to the model** in the chunk header, and instruct it: prefer the
  most recent when sources conflict, and say when it's using something old.
- **Detect conflicts explicitly.** If two retrieved chunks cover the same topic
  with different dates, that's a signal — either drop the older one or tell the
  model which supersedes which.

**Deletion and update are correctness operations, not maintenance.** If a document
is deleted from the source and not from the index, you will confidently cite
something that no longer exists. Design the sync path first:

| Approach | Latency | Complexity |
| --- | --- | --- |
| Full reindex on a schedule | Hours–days | Low |
| Incremental via change feed | Minutes | Medium |
| Event-driven on write | Seconds | Higher — needs a reliable event path |
| Tombstones + filter at query time | Instant for deletes | Low; index grows |

Tombstoning is underrated: mark deleted, filter at query time, and reclaim during
a scheduled compaction. Deletes become instantly correct without a reindex.

### 3.7 Query processing — the cheap wins before retrieval

- **Query expansion / rewriting.** Turn "what about the other one?" into a
  standalone query using conversation context. Essential for multi-turn — without
  it, turn-3 retrieval is nonsense.
- **Multi-query.** Generate 3–5 phrasings, retrieve for each, fuse with RRF.
  Improves recall meaningfully for a small cost.
- **HyDE** (Hypothetical Document Embeddings). Have the model write a *fake*
  answer, embed that, and retrieve with it — because a hypothetical answer sits
  closer to real answers in embedding space than a question does. Effective when
  queries and documents are stylistically very different.
- **Decomposition.** "Compare our refund policy to our warranty policy" is two
  retrievals, not one. A single query for a comparative question retrieves the
  average of two topics, which is neither.
- **Metadata extraction.** Pull filters out of the query ("in 2023", "for
  enterprise customers") and apply them as hard filters rather than hoping the
  embedding captures them. Embeddings are bad at dates and negation, exactly as
  they were bad at them on Day 6.

### 3.8 The permission boundary (preview of Days 19–20)

**Filter by permission at retrieval, never after.** Two rules:

1. The permission filter is applied *in the query* to the index, so unauthorized
   chunks are never candidates. Post-filtering the model's output is not a
   control — the content already entered the context and can leak through
   paraphrase.
2. Permissions are evaluated from the authenticated session, never from anything
   the model supplies (Day 11 §3.3).

And a subtle one: **permission-filtered retrieval changes the cache key.** Two
users asking the identical question with different access levels must not share a
cached answer (Day 6 §3.4, Day 20).

---

## 4. Napkin math

### 4.1 Why reranking pays

Corpus 200k chunks. Dense-only, k=10: measured recall@10 = 0.71.
Hybrid retrieval k=100: recall@100 = 0.94. Rerank to top-5: recall@5 = 0.89.

```
                            recall   chunks in context   distractor ratio
dense top-10                 0.71           10                 ~0.85
hybrid top-100 → rerank 5    0.89            5                 ~0.30
```

Recall up 18 points, context *halved*, distractor ratio down by more than half.
Per Day 2, all three of those independently improve the answer. Cost: one rerank
call, ~100ms, no index change.

If end-to-end accuracy tracks retrieval recall at roughly 0.9 conversion, that's
`0.18 × 0.9 ≈ 16` points of end-to-end accuracy — far more than any prompt change
will ever give you (Day 1 §4).

### 4.2 Chunking cost/benefit

50,000 documents, ~4,000 tokens each = 200M tokens.

```
Naive chunking:          free
Contextual chunking:     200M tokens through a small model to generate
                         per-chunk context ≈ $0.10/M in + $0.40/M out
                         ≈ $20 in + ~$60 out  ≈ $80 one-time
```

**$80, once**, for what is often a double-digit percentage reduction in retrieval
failures. Reindexing on a change is the recurring cost, and it's small. There are
very few interventions in this course with a better ratio.

### 4.3 The staleness bill

Support assistant, 30,000 queries/day. 8% of the corpus is stale (superseded but
still indexed). Suppose stale chunks are retrieved for 5% of queries and produce a
wrong answer 60% of the time:

```
wrong answers/day = 30,000 × 0.05 × 0.60 = 900
```

**900 confidently wrong answers per day from an ops gap.** Fixed by date metadata,
validity filtering, and a tombstone path — a day of work.

---

## 5. Failure modes

| Symptom | Stage | Fix |
| --- | --- | --- |
| Can't find exact error codes / SKUs | Retrieve — dense only | Add BM25; fuse with RRF |
| Quality falls as corpus grows | Rerank — missing | Add a cross-encoder reranker + threshold |
| Answers cite outdated policy | Freshness | Date metadata, validity filter, recency boost |
| Cites a deleted document | Freshness | Tombstones + query-time filter |
| Chunk says "it costs $49", no referent | Chunk — no context | Contextual chunking or parent-document retrieval |
| Multi-turn retrieval is nonsense | Query — no rewriting | Query rewriting from conversation state |
| Comparative questions answered poorly | Query — no decomposition | Decompose into multiple retrievals |
| Tables become gibberish | Ingest — parsing | Format-aware extraction; keep tables intact |
| Retrieval works in dev, not prod | Embedding prefix mismatch | Verify query/passage prefixes on both paths |
| User sees a document they can't access | Permission — post-filtered | Filter in the query, from session identity |
| Great recall, bad answers | Assemble — too many chunks | Rerank, cut k, per Day 2's dilution curve |
| Nothing relevant exists, answer invented anyway | Rerank — no threshold | Score threshold + honest "I don't know" |

---

## 6. Lab (95 min)

Build a real hybrid+rerank pipeline over this repo's own documentation. It's a
small corpus with exactly the characteristics that break naive RAG: identifiers
(`ORD-`, file paths, class names), code, and prose mixed together.

### Part A — Corpus and two chunkers (25 min)

```python
# lab-output/day14_rag.py
import re, glob, json, httpx, numpy as np
from rank_bm25 import BM25Okapi

def load_corpus():
    docs = []
    for path in glob.glob("**/*.md", recursive=True) + glob.glob("**/*.cs", recursive=True):
        try: docs.append({"path": path, "text": open(path, encoding="utf-8").read()})
        except Exception: pass
    return docs

def chunk_fixed(doc, size=500, overlap=50):
    words, out = doc["text"].split(), []
    for i in range(0, len(words), size - overlap):
        out.append({"path": doc["path"], "text": " ".join(words[i:i+size]),
                    "heading": None})
    return out

def chunk_structural(doc, max_words=400):
    """Split on markdown headings / C# member boundaries; keep the heading path."""
    parts, heading = [], None
    buf = []
    for line in doc["text"].splitlines():
        if re.match(r"^#{1,4}\s", line) or re.match(r"^\s*(internal|public|private)\s.*\{?$", line):
            if buf:
                parts.append({"path": doc["path"], "heading": heading,
                              "text": "\n".join(buf)})
                buf = []
            heading = line.strip()
        buf.append(line)
        if len(" ".join(buf).split()) > max_words:
            parts.append({"path": doc["path"], "heading": heading, "text": "\n".join(buf)})
            buf = []
    if buf: parts.append({"path": doc["path"], "heading": heading, "text": "\n".join(buf)})
    return parts

def contextualize(chunk):
    """Cheap templated contextual chunking — no LLM call needed to see the effect."""
    head = f"[File: {chunk['path']}"
    if chunk["heading"]: head += f" | Section: {chunk['heading']}"
    return head + "]\n" + chunk["text"]

def embed(texts, prefix="passage: "):
    out = []
    for t in texts:
        r = httpx.post("http://localhost:11434/api/embeddings",
                       json={"model": "nomic-embed-text", "prompt": prefix + t},
                       timeout=300)
        v = np.array(r.json()["embedding"], dtype=np.float32)
        out.append(v / np.linalg.norm(v))
    return np.vstack(out)
```

Build four indexes: {fixed, structural} × {plain, contextualized}.

### Part B — Hybrid retrieval and RRF (25 min)

```python
def dense_search(qvec, mat, ids, k=50):
    scores = mat @ qvec
    top = np.argsort(-scores)[:k]
    return [ids[i] for i in top]

def sparse_search(query, bm25, ids, k=50):
    scores = bm25.get_scores(query.lower().split())
    return [ids[i] for i in np.argsort(-scores)[:k]]

def rrf(rank_lists, k=60):
    scores = {}
    for ranks in rank_lists:
        for i, doc in enumerate(ranks):
            scores[doc] = scores.get(doc, 0) + 1.0 / (k + i + 1)
    return sorted(scores, key=scores.get, reverse=True)
```

Write **25 test queries with known correct chunks**, deliberately including:
- 5 semantic queries ("how does the app handle a failed API key?")
- 5 exact-identifier queries (`OLLAMA_NUM_PARALLEL`, `ILLMProvider`, `ORD-12345`)
- 5 multi-hop ("which providers use SecretsManager and what do they do on
  failure?")
- 5 queries whose answer is genuinely absent (must return nothing)
- 5 paraphrases of the semantic ones

Measure **recall@10** for: dense-only, sparse-only, hybrid+RRF. Fill this in:

| Query type | Dense | Sparse | Hybrid |
| --- | --- | --- | --- |
| Semantic | | | |
| Exact identifier | | | |
| Multi-hop | | | |
| Absent (should return ∅) | | | |

**Predict the exact-identifier row before running.** This is the table that
proves §3.4 on your own data, and it's the one to show anyone who says "we'll
just use a vector DB."

### Part C — Rerank and threshold (25 min)

Add an LLM reranker (a small local model scoring `(query, chunk)` relevance 0–10),
apply it to the hybrid top-50, and cut to top-5.

Measure: recall@5 after rerank vs. recall@5 without, and — the important one —
what happens to the **absent** queries. Find the score threshold below which you
return nothing, and report the false-answer rate on absent queries with and
without it.

### Part D — Freshness (20 min)

Add `effective_from` and `superseded_by` metadata to chunks. Create a deliberate
conflict: two versions of the same policy with different dates. Then measure:

1. Without dates in the chunk header — which version does the model use across 10
   trials?
2. With dates and an instruction to prefer the most recent — same 10 trials.
3. With the superseded version filtered at retrieval — same 10 trials.

Record all three. Write down which mechanism is a *control* and which are merely
*preferences*, using the Day 1 §3.2 vocabulary.

---

## 7. Quiz

**1.** Name the six RAG pipeline stages and one characteristic failure of each.

**2.** Why does dense retrieval fail on `E_INVALID_SIG_0x8007`? What's the fix,
and why is it structural rather than a tuning problem?

**3.** Explain RRF and why it's preferred over weighted score fusion as a default.

**4.** What is a cross-encoder reranker, and what can it judge that a dot product
cannot?

**5.** Why should a reranker have a score threshold and not just a top-k?

**6.** Explain contextual chunking and the retrieval failure it addresses.

**7.** What is parent-document retrieval, and which tradeoff does it dissolve?

**8.** Give four freshness mechanisms. Which is a control and which are
preferences?

**9.** Why must permission filtering happen at retrieval rather than after
generation? Give the failure mode of post-filtering.

**10.** Recall@10 improves 0.68 → 0.91 and you halve the chunks in context.
Name three separate mechanisms by which the answer improves.

---

## 8. Review hooks

- *Most RAG failures are retrieval failures wearing a hallucination costume.*
- *Dense finds meaning; sparse finds strings. You need both.*
- *Retrieve wide, rerank narrow. Five good chunks beat fifty mediocre ones.*
- *A reranker without a threshold hands the model something to confabulate over.*
- *Freshness is a correctness property. Dates on every chunk.*
- *Filter permissions in the query, never after generation.*
- *Trace the failure to a stage before fixing anything.*

---

## 9. Answer key

**1.** **Ingest** — bad PDF/table extraction destroying structure. **Chunk** —
splitting mid-concept so a chunk has no referent. **Embed** — wrong query/passage
prefix, or a model that doesn't fit the domain. **Retrieve** — low recall, or
exact-identifier misses from dense-only search. **Rerank** — absent entirely, so
high recall never becomes high precision. **Generate** — ignoring provided
context, or fabricating when nothing relevant was retrieved.

**2.** Embeddings encode semantic meaning, and an opaque identifier carries almost
none — every alphanumeric code lands in a similar, undifferentiated region of the
space, so the correct one isn't distinguishable from the others. **Fix:** sparse
(BM25) retrieval fused with dense. **Structural, not tuning**, because no
threshold, dimensionality, or embedding model change makes a meaningless string
semantically distinctive; the information needed is lexical, and only a lexical
index has it.

**3.** Reciprocal Rank Fusion scores each document as `Σ 1/(k + rank_i)` across
retrievers, using only its rank position in each list. Preferred by default
because dense and sparse scores are on incomparable scales — cosine similarities
and BM25 scores have no shared units — so weighted fusion requires normalization
and re-tuning whenever either retriever changes, while RRF is scale-free and
robust out of the box.

**4.** A model that takes the query and passage *together* as a single input and
outputs a relevance score, allowing full attention between them. It can judge
whether the passage actually *answers* the question rather than merely sharing its
topic — plus negation, specificity, and conditional relevance ("applies only to
enterprise customers"). A dot product compares two independently-computed vectors
and cannot model any interaction between them.

**5.** Because top-k always returns k results, including when nothing relevant
exists. Handing the model an irrelevant passage invites confabulation over it and
produces a confident wrong answer, whereas returning nothing lets the generation
stage honestly say it lacks the information. A threshold converts "always answer
something" into "answer when there's a basis," which is the correct product
behaviour.

**6.** Prepending document- and section-level context (title, heading path,
effective date, a one-line summary) to each chunk *before embedding*. It addresses
the failure where a chunk is semantically orphaned — "Refunds must be requested
within 30 days" has no indication of *what* it refers to, so it doesn't match
"damaged goods refund window." With context prepended, the embedding encodes what
the chunk is about, not just its literal words.

**7.** Embedding and searching over small chunks (precise matching) while passing
the *parent* section or document to the model (sufficient context). It dissolves
the small-chunks-are-precise vs. large-chunks-have-context tradeoff, because
matching and context no longer have to use the same unit.

**8.** Date metadata on every chunk; validity filtering at retrieval
(effective_from/until); recency boosting in ranking; surfacing dates to the model
with an instruction to prefer recent; tombstones for deletion. **Controls:**
validity filtering and tombstone filtering — the stale content cannot enter the
context. **Preferences:** recency boosting and the model instruction — they change
likelihoods but the stale content is still present and can still be used (Day 1
§3.2).

**9.** Because once unauthorized content is in the context it has already been
read by the model and can leak through paraphrase, summary, inference, or partial
quotation — even if the final text is filtered, the *answer* was shaped by data
the user may not see. Post-filtering also can't catch indirect disclosure ("based
on the documents, your competitor's contract expires in Q3"). Filtering at
retrieval means the content was never a candidate, which is a control rather than
a mitigation.

**10.** (i) **Higher recall** — the answer is now present in the context far more
often, so the model has the material to be correct. (ii) **Lower distractor
ratio** — fewer near-miss passages competing for attention, so less dilution and
less chance of the model latching onto a plausible wrong passage (Day 2 §3.2).
(iii) **Less contradiction** — with five chunks instead of ten there is less
opportunity for two retrieved passages to disagree, which is the condition that
produces confidently wrong output. (Also acceptable: shorter context reduces
prefill latency and cost, and frees window for the answer.)

---

## 10. Going deeper

- **"Retrieval-Augmented Generation for Knowledge-Intensive NLP Tasks"** — Lewis,
  Perez, Piktus et al. (arXiv 2005.11401). The original.
- **"Reciprocal Rank Fusion Outperforms Condorcet and Individual Rank Learning
  Methods"** — Cormack, Clarke, Buettcher (SIGIR 2009). Two pages; the whole
  argument for §3.4.
- **"ColBERT: Efficient and Effective Passage Search via Contextualized Late
  Interaction over BERT"** — Khattab & Zaharia (arXiv 2004.12832), and
  **ColBERTv2** — Santhanam et al. (arXiv 2112.01488).
- **"Precise Zero-Shot Dense Retrieval without Relevance Labels" (HyDE)** — Gao,
  Ma, Lin, Callan (arXiv 2212.10496).
- **"Contextual Retrieval"** — Anthropic engineering blog, 2024. The contextual
  chunking technique and reported failure-rate reductions. (Vendor blog; the
  technique is model-agnostic.)
- **"Self-RAG: Learning to Retrieve, Generate, and Critique through
  Self-Reflection"** — Asai, Wu, Wang, Sil, Hajishirzi (arXiv 2310.11511).
  Retrieval decided adaptively rather than always-on.

---

**Next:** [Day 15 — Retrieval Evals](day-15-retrieval-evals.md). You built it.
Now find out whether it works — and learn to tell a retrieval bug from a
generation bug.
