# Day 6 — Prompt Caching vs. Semantic Caching: Tradeoffs

**Time:** ~3 h · **Prereqs:** Days 2–5 · **Lab needs:** Ollama + a hosted API key

---

## 0. Warm-up review

1. Which phase does prefix caching accelerate? (Day 3)
2. What mechanism in paged attention makes prefix sharing possible? (Day 4)
3. Why does putting a GUID at the top of a system prompt cost real money? (Day 2)
4. State the harness quality ladder's stage-5 property. (Day 1)

---

## 1. The one-paragraph version

These two things share a word and nothing else. **Prompt caching** (prefix
caching, KV caching) reuses the *computation* for a byte-identical prompt prefix.
It is exact, correctness-preserving, and essentially free — the model still runs;
you just skip re-deriving what you already derived. **Semantic caching** reuses a
previous *answer* for a question judged similar by embedding distance. It skips
the model entirely. It is a bet that similar-looking questions deserve identical
answers, and when that bet is wrong, it returns a confidently wrong answer with
no error, no trace, and no way for the user to tell. One is an optimization. The
other is a product decision with a failure mode, and it should be treated with
the same care as a cache in front of a financial ledger.

---

## 2. Why this bites you

**The prompt-caching wound** is self-inflicted and silent. Someone adds a
timestamp, a request ID, a user's display name, or an A/B flag near the top of
the system prompt. Every cache lookup misses. Costs rise 5–10× and TTFT doubles.
No error fires. No test fails. The prompt still *reads* correctly. The only
symptom is on the bill, six weeks later, and by then twelve other things have
changed. (You reproduced this in the Day 2 lab in thirty seconds. Most teams take
months.)

**The semantic-caching wound** is worse because it's a correctness bug that looks
like a performance feature. Real patterns:

- "What's my account balance?" — cached. Every user now gets the first user's
  balance. This is a **data breach**, delivered by a cache (Day 20).
- "Can I return this item?" vs "Can I return this item after 30 days?" — cosine
  similarity 0.94. Cache hit. The answer is the opposite of correct.
- "Is the API down?" — cached at 09:00, still served at 14:00 after recovery.
- "Should I take this medication with alcohol?" vs "…without alcohol?" — near-
  identical embeddings, opposite answers. Negation is famously weak in embedding
  space.

Every one of these ships as "we added caching, latency improved 60%." The
regression appears in support tickets, not dashboards.

---

## 3. Core concepts

### 3.1 The two mechanisms side by side

| | Prompt / prefix caching | Semantic caching |
| --- | --- | --- |
| **What's reused** | KV cache for a token prefix | The final generated answer |
| **Match condition** | Byte-identical prefix from token 0 | Embedding similarity above a threshold |
| **Model still runs?** | Yes, on the uncached suffix | **No** |
| **Correctness risk** | None (same computation) | **High** — the core tradeoff |
| **Saves** | Prefill compute, input token price, TTFT | Everything: full latency and full cost |
| **Typical saving** | 50–90% of input cost; large TTFT drop | ~100% on a hit |
| **Hit rate driver** | Prompt *structure* | Query *distribution* |
| **Failure mode** | Silent cost increase | **Silent wrong answer** |
| **Personalization safe?** | Yes (shared part is shared by design) | Only with correct key scoping |
| **Freshness risk** | None | High — answers age |

The row to tattoo somewhere: prompt caching's failure mode is a bigger bill;
semantic caching's failure mode is a wrong answer to a user who has no way to
know. These require completely different levels of scrutiny before shipping.

### 3.2 Prompt caching: how to actually get hits

The mechanism (Day 4 §3.4): identical leading tokens → shared KV blocks →
skipped prefill. Rules that follow directly from "identical, from token zero":

1. **Order by volatility: most stable first.**
   ```
   [system] [policy] [tool schemas] [few-shot] │ [retrieved] [history] [user]
                        stable, cacheable       │  varies per request
   ```
   Anything dynamic placed before the boundary invalidates everything after it.
   The Day 2 context layout was already designed for this — that's not a
   coincidence, it's the point.

2. **Never interpolate volatile values into the prefix.** Timestamps, request
   IDs, GUIDs, user names, random exemplar selection, experiment flags. If the
   model needs the current time, put it in the *dynamic* region or give it a tool.

3. **Watch for accidental volatility.** These are the ones that get people:
   - Serializing a dictionary whose key order isn't stable (C# `Dictionary`
     enumeration order is not guaranteed — use an ordered structure or sort).
   - JSON serializers that vary whitespace or property order across versions.
   - A tool list assembled from a `HashSet`.
   - Locale-dependent number or date formatting.
   - Trailing whitespace differences from a template engine.
   All of these produce a prefix that is *semantically* identical and *byte*
   different, which is the only kind that matters.

4. **Compact and rewrite infrequently.** Rewriting history in the middle of the
   context invalidates every cached block after the rewrite point (Day 2 §3.5).
   Compact at natural boundaries, not every turn.

5. **Understand your provider's cache lifetime.** Hosted prefix caches typically
   expire in minutes and may require explicit markers. Low-traffic endpoints can
   have a near-zero hit rate purely because entries expire between requests —
   which means **prompt caching pays off in proportion to your traffic density**,
   and a per-endpoint hit-rate metric is the only way to know.

6. **Measure the hit rate. Always.** Log a stable hash of your static prefix
   (Day 2 lab) and the provider's reported cache-read/cache-write token counts.
   A dashboard panel for cache hit rate is worth more than any amount of prompt
   review. This is a **regression test you can run in CI**: assert that the prefix
   hash is constant across two synthetic requests with different inputs.

### 3.3 Semantic caching: when the bet is defensible

Semantic caching is not always wrong. It's right when **all** of these hold:

- **The answer doesn't depend on who's asking.** No user data, no
  personalization, no permissions. ("What are your business hours?" ✅ — "What
  are my open tickets?" ❌.)
- **The answer doesn't depend on when.** Or you can bound staleness with a TTL
  short enough that a stale answer is harmless.
- **Query paraphrases genuinely map to identical answers.** True for FAQ; false
  for anything with parameters, quantities, dates, or negations.
- **A wrong hit is recoverable.** The user can tell, and can retry.
- **You have volume.** Semantic caching only pays with a heavy head of repeated
  intent. A long-tail workload gets a 3% hit rate and all of the risk.

If any one of those fails, the honest answer is: don't. Use prompt caching, which
gets you much of the cost benefit with none of the correctness risk.

### 3.4 If you do build one: the required parts

A semantic cache without these is a liability, not a feature.

**Namespaced keys.** The cache key is never just the query embedding. It is:
```
key = (tenant_id, user_permission_scope, locale, feature_version,
       prompt_template_version, model_id, retrieval_corpus_version, embedding)
```
Omitting `tenant_id` is the Day 20 incident. Omitting `prompt_template_version`
means yesterday's prompt's answers survive today's deploy. Omitting `model_id`
means your cheap model's answers get served as your expensive model's. **Every
one of these has been a real outage somewhere.**

**A conservative threshold, tuned on labelled data.** Not 0.85 because a blog
post said so. Build a set of query pairs labelled "same answer / different
answer," and choose the threshold from the precision/recall curve. Optimize for
**precision** — a false hit is a wrong answer; a false miss is just a normal
request. A useful posture: pick the threshold where false-hit rate is under 0.5%,
and accept whatever hit rate that gives you.

**Negation and quantity guards.** Embeddings are notoriously weak at negation
("with" vs "without", "can" vs "cannot") and at numbers ("30 days" vs "60 days").
A cheap, effective guard: reject a cache hit if the two queries differ in any
extracted number, date, entity, or negation token, regardless of similarity.
This is a lexical check bolted onto a semantic system, and it catches the
majority of the dangerous hits.

**TTL, and invalidation hooks.** Every entry has an expiry. The knowledge base
update pipeline flushes affected entries. If you can't invalidate, your TTL is
your correctness bound — set it accordingly.

**Sampling and observability.** Serve from cache but re-run the model on a
random 1–5% of hits and compare. That's your live false-hit rate. Without it,
you're flying blind on your own error rate; with it, cache quality becomes a
number on a dashboard (Day 17). Also: **every cached response must be marked as
such in the trace**, or your evals will silently measure the cache instead of the
model.

**A user-visible escape hatch.** "Regenerate" / "this doesn't answer my
question" — which must bypass the cache, and which is also your cheapest source
of false-hit labels.

### 3.5 The third option nobody considers: exact-match caching

Between the two sits **exact-match response caching** — normalize the query
(lowercase, trim, collapse whitespace), hash it with the full key namespace from
§3.4, and cache the response.

- Zero false-hit risk from similarity (it's exact).
- Still needs TTL, tenant scoping, and versioning.
- Hit rate is lower than semantic but far higher than zero — real traffic has a
  surprisingly heavy head of literally identical queries, especially from UI
  buttons, suggested prompts, and retries.

For most teams, **exact-match caching plus prompt caching captures most of the
available savings at a fraction of the risk of semantic caching.** It is the
boring correct answer and it is chronically skipped in favour of the interesting
risky one.

### 3.6 The caching decision tree

```
Is the response identical for every user, permission scope, and tenant?
├─ NO  → response caching is a data-leak risk. Namespace strictly, or don't.
│        Use prompt caching only.
└─ YES → Does the answer change over time?
    ├─ Frequently → prompt caching only, or a very short TTL
    └─ Rarely → Is the query distribution head-heavy?
        ├─ NO  → prompt caching only (semantic won't hit enough to matter)
        └─ YES → exact-match caching first. Measure the hit rate.
                 Still not enough? Semantic caching, WITH: labelled threshold,
                 negation/number guards, TTL, invalidation, shadow sampling,
                 and a regenerate button.
```

Notice that prompt caching appears in every branch. **It is never the wrong
answer.** That asymmetry is the lesson.

---

## 4. Napkin math

### 4.1 Prompt caching value

Feature: 500,000 requests/day. Prompt = 6,000 static prefix + 2,000 dynamic.
Input $3/M; cached input $0.30/M.

```
No caching:  500k × 8,000 × $3/M                        = $12,000/day
With caching: 500k × 6,000 × $0.30/M  = $  900
            + 500k × 2,000 × $3.00/M  = $3,000
                                        ────────
                                          $3,900/day
```

**$8,100/day saved = $2.96M/year**, with zero quality risk, from ordering the
prompt correctly. Plus a TTFT improvement of roughly 75% of the prefill, since
6,000 of 8,000 tokens are skipped.

### 4.2 Semantic caching value — and the risk priced in

Same feature. Suppose a 25% semantic hit rate at $0.0078/request (the cached-
prefix cost from above, plus output).

```
Savings: 500k × 0.25 × $0.0078 ≈ $975/day ≈ $356k/year
```

Now price the risk. Assume a 2% false-hit rate among hits — conservative for a
naively-tuned threshold:

```
wrong answers/day = 500k × 0.25 × 0.02 = 2,500
```

**2,500 confidently wrong answers per day**, invisible to every dashboard you
have. Compare to §4.1: prompt caching saved 8× more money with none of that.

If your product's cost of a wrong answer is anything above trivial — a support
escalation at $8, a lost transaction, a compliance report — the semantic cache is
net negative at that false-hit rate. Do this arithmetic *before* building it, and
notice that the number that dominates is one most teams never estimate.

### 4.3 When semantic caching genuinely wins

Public-facing FAQ bot. 2M requests/day, 70% concentrated in ~200 distinct
intents, no personalization, TTL of 24h acceptable, cost per uncached request
$0.004.

```
Hit rate ~65% → savings = 2M × 0.65 × $0.004 = $5,200/day ≈ $1.9M/year
False hits at a properly tuned 0.3% of hits = 3,900/day, on questions where the
answer is "here's our return policy" and the user can rephrase.
```

Here it's clearly right. The difference from §4.2 isn't the technology — it's the
**query distribution and the cost of being wrong**. Those are the two variables;
everything else is implementation.

---

## 5. Failure modes

| Failure | Cache type | Cause | Detection |
| --- | --- | --- | --- |
| Costs 5× expected | Prompt | Volatile value in prefix | Prefix-hash constancy test in CI |
| Cache hit rate ~0% on a low-traffic endpoint | Prompt | Entries expire between requests | Hit-rate metric per endpoint |
| Hit rate drops after a refactor | Prompt | Serializer changed byte output | Prefix-hash regression test |
| User A sees user B's data | Semantic | Key missing `tenant_id`/user scope | Key-namespace audit; shadow sampling |
| Answer contradicts current docs | Semantic | No invalidation on corpus update | TTL + invalidation hook |
| "Can I…" and "Can I not…" get the same answer | Semantic | Negation blindness in embeddings | Lexical negation guard |
| Old prompt's behaviour persists post-deploy | Semantic | Key missing template version | Version in key; flush on deploy |
| Evals mysteriously stop catching regressions | Either | Eval traffic served from cache | Mark cached responses in traces; bypass cache in eval runs |

That last row is a genuinely nasty one and it shows up on Day 16: if your eval
harness hits the cache, your eval measures the cache. Every eval run must bypass
response caches, and every trace must record `cache_hit: true|false`.

---

## 6. Lab (80 min)

### Part A — Prove the prompt cache (30 min)

Using this repo's `Providers/AnthropicProvider.cs` (or whichever hosted provider
you have keyed), issue two requests with a large identical system prompt and
different user turns, and read the usage fields the API returns.

1. Build a ~2,000-token static system prompt (paste in a chunk of `README.md`
   plus the Axiom persona from `Axiom_Modelfile`).
2. Send request 1. Record reported input tokens, cache-write tokens, cache-read
   tokens, and measured TTFT.
3. Send request 2 with a *different* user turn. Record the same four numbers.
4. Send request 3 after prefixing the system prompt with
   `$"Session start: {DateTime.UtcNow:O}\n"`. Record again.

Build the table:

| Request | Input tok | Cache write | Cache read | TTFT | $ |
| --- | --- | --- | --- | --- | --- |
| 1 (cold) | | | | | |
| 2 (warm) | | | | | |
| 3 (timestamp) | | | | | |

**Predict before running:** the ratio of request 2's TTFT to request 1's, and
whether request 3 looks like request 1 or request 2. Then extrapolate request 3's
cost to 500k requests/day and write the annual figure in your notebook. Feel it.

*(If your provider requires explicit cache markers, set them — and note that
"caching didn't work" is usually "caching wasn't enabled", which is itself a
lesson worth having.)*

### Part B — Build the CI guard (20 min)

The bug in request 3 should be impossible to merge. Write the test.

```csharp
// Tests/PrefixStabilityTests.cs  (adapt to whatever test runner you add)
[Fact]
public void StaticPrefix_IsByteIdentical_AcrossDifferentRequests()
{
    var budget = new ContextBudget();               // from the Day 2 lab
    var a = budget.StaticPrefix(user: "alice", tenant: "t1");
    var b = budget.StaticPrefix(user: "bob",   tenant: "t2");
    Assert.Equal(budget.PrefixHash(a), budget.PrefixHash(b));
}

[Fact]
public void StaticPrefix_IsStable_AcrossProcessRuns()
{
    // Guards against dictionary/hashset ordering and locale formatting.
    var hashes = Enumerable.Range(0, 20)
        .Select(_ => new ContextBudget().PrefixHash(
                     new ContextBudget().StaticPrefix(user: "x", tenant: "t")))
        .Distinct().ToList();
    Assert.Single(hashes);
}
```

Wire it into the build. **This twenty-line test is worth more than every prompt
review you will ever do**, because it converts a silent six-figure cost bug into
a red build.

### Part C — Measure semantic cache danger (30 min)

Build the false-hit measurement most teams skip.

```python
# lab-output/day06_semantic_risk.py
import httpx, itertools, json, numpy as np

def embed(text: str) -> np.ndarray:
    r = httpx.post("http://localhost:11434/api/embeddings",
                   json={"model": "nomic-embed-text", "prompt": text}, timeout=120)
    v = np.array(r.json()["embedding"], dtype=np.float32)
    return v / np.linalg.norm(v)

# (query_a, query_b, same_answer?)  — write 30 of these for YOUR domain.
PAIRS = [
    ("Can I return this item?", "Can I return this item after 30 days?", False),
    ("Can I return this item?", "Am I able to return this product?",      True),
    ("Is the API down?",        "Is the API currently unavailable?",      True),
    ("Take with alcohol?",      "Take without alcohol?",                  False),
    ("What is my balance?",     "What is the balance?",                   False),
    ("Refund after 30 days",    "Refund after 60 days",                   False),
    ("How do I reset my password?", "I forgot my password, what now?",    True),
    ("Delete my account",       "Deactivate my account",                  False),
]

sims = [(a, b, same, float(embed(a) @ embed(b))) for a, b, same in PAIRS]
for a, b, same, s in sorted(sims, key=lambda r: -r[3]):
    print(f"{s:.3f} same={str(same):5s} | {a!r} vs {b!r}")

# The threshold question: is there ANY cut that separates them?
for thr in [0.80, 0.85, 0.88, 0.90, 0.92, 0.95]:
    hits       = [(same, s) for _, _, same, s in sims if s >= thr]
    false_hits = sum(1 for same, _ in hits if not same)
    print(f"thr={thr}: hits={len(hits)}/{len(sims)}  FALSE HITS={false_hits}")
```

Write 30 pairs from a domain you care about — half true paraphrases, half
dangerous near-misses (differing in a number, a date, a negation, or a
possessive). Then answer in `lab-notebook.md`:

1. Is there a threshold with zero false hits? What's the hit rate there?
2. Which pair types are most dangerous — negations, numbers, or possessives?
3. Would a lexical guard (reject if extracted numbers/negations differ) fix them?
   Implement it and re-run.
4. **Final question:** given your numbers, would you ship a semantic cache for
   this domain? Write the one-paragraph justification you'd give a PM, including
   the estimated false-hit count per day.

---

## 7. Quiz

**1.** In one sentence each, state what prompt caching reuses and what semantic
caching reuses.

**2.** Which one can return a wrong answer? Explain the mechanism.

**3.** Give three "accidental volatility" sources that break a prefix while
leaving it semantically identical.

**4.** Compute: 800k requests/day, 5,000-token static prefix, 1,500-token dynamic.
Input $3/M, cached $0.30/M. Daily cost with and without prefix caching, and the
annual delta.

**5.** List five components that must appear in a semantic cache key, and name a
concrete failure for each omission.

**6.** Why should a semantic cache threshold be tuned for precision rather than
recall?

**7.** Your semantic cache has a 30% hit rate and a 3% false-hit rate among hits,
on 1M requests/day. How many wrong answers per day? If each costs $6 in support
load, is a $0.003/request saving worth it? Show the arithmetic.

**8.** What is a lexical negation/quantity guard, and which failure class does it
address that a higher similarity threshold does not?

**9.** Why must eval runs bypass response caches, and what must every trace record
so you can tell?

**10.** Under what circumstances is exact-match response caching the right answer
over both alternatives?

---

## 8. Review hooks

- *Prompt caching reuses computation. Semantic caching reuses conclusions.*
- *Prompt caching's failure mode is a bill. Semantic caching's is a wrong answer.*
- *Identical, from token zero. Semantically identical is not identical.*
- *The cache key is never just the embedding.*
- *Tune for precision. A false miss is a normal request; a false hit is a lie.*
- *Prompt caching is never the wrong answer.*
- *Evals must bypass the cache, or you're evaluating the cache.*

---

## 9. Answer key

**1.** Prompt caching reuses the KV cache — the computed internal state — for a
byte-identical token prefix. Semantic caching reuses a previously generated
answer for a query judged similar by embedding distance.

**2.** Semantic caching. It substitutes an answer generated for a *different*
question, on the assumption that embedding proximity implies answer equivalence.
That assumption fails on negations, quantities, dates, possessives, and anything
user- or time-dependent — and the substitution is invisible to the user.

**3.** Any three: unordered dictionary/hashset serialization; JSON serializer
whitespace or property-order changes; locale-dependent date/number formatting;
randomly sampled few-shot exemplars; trailing whitespace from template engines;
a tool list built from an unordered collection; injected build/version metadata.

**4.** Without: 800k × 6,500 × $3/M = **$15,600/day**. With: 800k × 5,000 ×
$0.30/M = $1,200, plus 800k × 1,500 × $3/M = $3,600 → **$4,800/day**. Delta
= $10,800/day = **$3.94M/year**.

**5.** Five of: `tenant_id` (omission → cross-tenant data leak); user/permission
scope (→ user sees data they can't access); `model_id` (→ cheap model's answers
served as the premium model's); `prompt_template_version` (→ pre-deploy behaviour
persists after deploy); `retrieval_corpus_version` (→ answers reflect deleted or
superseded documents); locale (→ wrong-language or wrong-jurisdiction answer);
feature/tool-set version (→ answers referencing capabilities that no longer exist).

**6.** Because the two errors are not symmetric. A false miss costs one ordinary
model call — the normal price of doing business. A false hit delivers an
incorrect answer to a user with no error signal, no trace, and no recovery path.
Optimizing recall trades a bounded, cheap error for an unbounded, silent one.

**7.** Wrong answers = 1,000,000 × 0.30 × 0.03 = **9,000/day**. Support cost =
9,000 × $6 = **$54,000/day**. Saving = 1,000,000 × 0.30 × $0.003 = **$900/day**.
**Net −$53,100/day.** Catastrophically not worth it — and note that the saving is
the number that appears on a dashboard while the cost is the number that doesn't.

**8.** A deterministic pre-check that rejects a cache hit whenever the two queries
differ in any extracted number, date, entity, or negation token — regardless of
cosine similarity. It addresses the class where embeddings are *legitimately*
close because the sentences are nearly identical in surface form, and the one
differing token inverts the meaning. Raising the threshold doesn't help: those
pairs sit at the very top of the similarity distribution, above true paraphrases.

**9.** Because a cached response measures the cache's stored answer, not the
current model, prompt, or retrieval pipeline — so an eval run against a warm cache
can report a passing score for a system that is actually broken, and can hide a
regression entirely. Every trace must record `cache_hit` (and which cache tier),
so eval results can be filtered and cache-quality tracked separately (Days 16–17).

**10.** When the answer is genuinely user-independent and slow-changing, the query
distribution has a heavy head of *literally identical* strings (UI suggestions,
buttons, retries), and you want response-level savings without accepting
similarity-matching risk. It gives a meaningful hit rate at zero false-hit risk
from matching, needing only TTL, versioning, and tenant scoping — which every
response cache needs anyway.

---

## 10. Going deeper

- **Your provider's prompt-caching documentation.** Read the sections on cache
  lifetime, minimum cacheable length, and explicit markers. Implementation
  details here change often; the prefix-stability principle does not.
- **"Efficiently Programming Large Language Models using SGLang"** — Zheng et al.
  (arXiv 2312.07104). RadixAttention: automatic prefix sharing via a radix tree
  over KV blocks. The best explanation of *automatic* prefix caching mechanics.
- **"GPTCache"** — open-source semantic cache. Worth reading the source for the
  key-scoping and eviction design, and worth noting what it leaves to you.
- **"Caching" in *Designing Data-Intensive Applications*** — Kleppmann. The
  invalidation and staleness reasoning is not LLM-specific, and this material is
  where semantic caching goes wrong most often.

---

**Next:** [Day 7 — Quantization](day-07-quantization.md). You've made the prompt
cheaper. Now make the weights smaller, and find out what it costs.
