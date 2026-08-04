# Day 2 — Context Engineering, Not Just Long Prompts

**Time:** ~3 h · **Prereqs:** Day 1 · **Lab needs:** Ollama, Python venv

---

## 0. Warm-up review

From memory, no scrolling:

1. State the determinism sandwich principle. (Day 1)
2. A chain has stage success rates 0.88, 0.99, 0.97, 0.99. What's end-to-end,
   and where do you spend your next two weeks? (Day 1)
3. Name two things a prompt fundamentally cannot do. (Day 1)

---

## 1. The one-paragraph version

The context window is not a place to put things. It is a **scarce, contended
resource with a nonlinear cost curve and a nonuniform attention profile** — and
context engineering is the discipline of deciding, per request, what earns a
seat. Every token you add costs money, adds prefill latency, and — critically —
*dilutes* the model's attention over the tokens that actually mattered. "The
model has a 200k window" is a statement about capacity, not about a good idea.
The right question is never "will it fit" but "what is the minimum sufficient
context, and how do I assemble it reliably."

---

## 2. Why this bites you

Two failure stories, both extremely common.

**The stuffer.** A team has a 200k-token window and 40k tokens of company
documentation. So they put all 40k in every request. It works! Quality is good.
Then: the docs grow to 90k, latency triples, cost per request goes to $0.30, and
quality *drops* — because the model now has 90k tokens of mostly-irrelevant
material to sift, and the specific paragraph that answers the question is
competing with 200 similar-looking paragraphs. The team's conclusion is "we need
a bigger window." The actual need was retrieval and relevance.

**The accumulator.** A chat agent appends every turn to the history. By turn 30
the context contains the user's original goal, 12 tool outputs (several
containing 8,000-token API dumps), three abandoned plans, two corrections, and a
contradiction the user made at turn 9 and reversed at turn 14. The model starts
acting on the reversed instruction. Nobody wrote a bug; the harness simply had
no policy for what leaves the window. **Context is state, and state without a
lifecycle policy becomes garbage.**

Both teams thought they had a model problem. Both had a *curation* problem.

---

## 3. Core concepts

### 3.1 The four costs of a token in context

Every token you place in the window is charged four times. Most people count one.

| Cost | Scales as | Notes |
| --- | --- | --- |
| **Money** | linear in input tokens | Often 3–5× cheaper than output tokens, and up to ~10× cheaper again if cached (Day 6) |
| **Prefill latency** | ~linear, with a quadratic attention term that bites at long context | This is your TTFT (Day 3) |
| **KV cache memory** | linear in tokens × concurrency | This is what actually kills your server (Day 4) |
| **Attention dilution** | nonlinear, empirical | The real one. More below. |

The first three are budget lines. The fourth is a quality line, and it's the one
that surprises people, because it means **adding correct, relevant information
can make the answer worse.**

### 3.2 Attention dilution and position effects

Empirically, across many models and many tasks:

- **Position matters.** Information at the very beginning and very end of a long
  context is retrieved more reliably than information in the middle. The
  canonical study is Liu et al., *"Lost in the Middle"* (2023) — accuracy on a
  retrieval-in-context task traced a U-shape as the target document moved through
  the window. This has improved with newer models but has not vanished.
- **Distractors hurt more than absence.** A context containing 20 near-miss
  passages plus the right one often scores *worse* than a context containing only
  the right one, and sometimes worse than a context containing none (where the
  model may correctly refuse).
- **Contradiction is worst.** Two context chunks that disagree — a current doc
  and a stale one — produce confidently wrong output at a high rate. The model
  has no principled way to adjudicate freshness unless you tell it.

Practical rules that follow:

1. **Put the task instruction last, or repeat it last.** Recency is reliable.
2. **Put stable, cacheable material first** (system prompt, policy, tool
   schemas) — this also happens to be exactly what prefix caching needs (Day 6).
   Position discipline and cost discipline point the same direction.
3. **Rank retrieved chunks and cut aggressively.** Five good chunks beat fifty
   mediocre ones. This is why reranking exists (Day 14).
4. **Label every chunk with provenance and date.** Give the model the means to
   prefer fresh over stale, and to cite (Day 15).

### 3.3 The context budget

Treat the window like a memory allocator with fixed regions. For a request with
a 32k working budget:

```
┌────────────────────────────────────────────────┐
│ SYSTEM / POLICY            ~1.5k   [static]    │  ← cacheable, never varies
├────────────────────────────────────────────────┤
│ TOOL SCHEMAS               ~2k     [static]    │  ← cacheable, varies by feature
├────────────────────────────────────────────────┤
│ FEW-SHOT EXEMPLARS         ~2k     [static]    │  ← cacheable
├────────────────────────────────────────────────┤   ═══ cache prefix boundary ═══
│ RETRIEVED CONTEXT       ≤ 12k      [dynamic]   │  ← ranked, truncated, cited
├────────────────────────────────────────────────┤
│ CONVERSATION HISTORY    ≤  8k      [dynamic]   │  ← compacted, see 3.4
├────────────────────────────────────────────────┤
│ CURRENT USER TURN       ≤  2k      [dynamic]   │
├────────────────────────────────────────────────┤
│ TASK RESTATEMENT        ~0.2k      [static]    │  ← recency slot
└────────────────────────────────────────────────┘
  reserved for output: 4k+   ← the window is shared; forgetting this truncates you
```

Three things to internalize about this layout:

- **The static block goes first and is byte-identical across requests.** That's
  what makes it cacheable. Putting a timestamp or a user's name in the system
  prompt destroys the cache for everyone (Day 6). This is the single most common
  self-inflicted cost wound in the industry.
- **Each dynamic region has a hard cap**, enforced in code, before the call.
  Not "usually about 12k" — a `Math.Min` that runs every time.
- **Output space is part of the budget.** If your window is 32k and you allow
  31k of input, a long answer gets truncated mid-sentence, and unless you check
  the stop reason, you'll return it to the user as if it were complete.

### 3.4 Conversation history: the lifecycle policy

Unbounded history is a bug. Pick a policy explicitly:

| Policy | How it works | Good for | Loses |
| --- | --- | --- | --- |
| **Full** | Keep everything | Short tasks (<10 turns) | Nothing, until it explodes |
| **Sliding window** | Last N turns | Chat where old turns are irrelevant | The original goal |
| **Window + pinned head** | First turn + last N | Goal-directed tasks | Mid-task decisions |
| **Summarize-and-compact** | Replace old turns with an LLM summary at a threshold | Long agent runs | Fidelity; summaries drift |
| **Structured state** | Extract facts into a typed object; render it deterministically | Anything long-running | Requires you to know the schema |
| **Externalized** | Write to files/DB; model reads on demand via tools | Very long tasks | Extra round trips |

The progression from top to bottom is roughly the progression from prototype to
production. **Structured state is the strongest option and the least used.**
Instead of keeping the *transcript* of how a fact was established, keep the fact:

```csharp
// Instead of 14 turns of transcript, carry this:
sealed record TaskState(
    string Goal,
    IReadOnlyList<string> ConfirmedFacts,
    IReadOnlyList<string> RuledOut,
    string? PendingQuestion,
    int StepsTaken);
```

Render it into the prompt in a fixed format. It's ~200 tokens instead of ~14,000,
it can't contradict itself, it's inspectable in a debugger, and — the part people
miss — **it's testable.** You can unit-test that a tool result updates state
correctly. You cannot unit-test "the model probably remembers turn 6."

### 3.5 Compaction, and its specific dangers

When you must summarize (long agent runs, open-ended chat), know what you're
risking:

- **Summaries lose identifiers.** "The user's order" replaces
  `order_id=8837261`. Then the agent calls a tool with a hallucinated ID. **Rule:
  extract entities and IDs into a structured block that is never summarized.**
- **Summaries lose negations.** "We tried the API approach" doesn't preserve
  "and it failed with a 403." The agent retries it. **Rule: keep a `RuledOut`
  list verbatim.**
- **Summaries compound.** Summarizing a summary three times produces confident
  fiction. **Rule: always summarize from the original tail, never from a prior
  summary, or cap the compaction generation count.**
- **Compaction is a cache invalidation event.** Rewriting the middle of the
  context invalidates every cached prefix after that point. Compact at natural
  boundaries and infrequently (Day 6).

### 3.6 Context assembly is a pipeline, not a template

The mature form of this is a small deterministic pipeline with typed stages:

```
gather()   → candidate items (retrieved docs, history, tool results, state)
score()    → relevance × recency × authority, per item
select()   → greedy fill under per-region token caps
order()    → static-first, task-last, provenance-labeled
render()   → deterministic string; identical inputs → identical bytes
verify()   → assert budgets, assert no PII crossing tenant boundary, assert
             cache prefix unchanged
```

That `verify()` step is worth its own paragraph. It is where you catch, *before*
the API call: budget overruns, a tenant's data appearing in another tenant's
context (Day 20), a secret that got interpolated from an env var, and accidental
cache-prefix drift. It is maybe 40 lines of assertions and it prevents several
categories of incident that are otherwise found by customers.

---

## 4. Napkin math

### 4.1 The cost of stuffing

Feature does 200,000 requests/day. Two designs:

| | Stuff-everything | Retrieve-then-rank |
| --- | --- | --- |
| Input tokens/request | 45,000 | 6,000 |
| Input cost @ $3/M | $0.135 | $0.018 |
| Daily | **$27,000** | **$3,600** |
| Annual | **$9.86M** | **$1.31M** |

An **$8.5M/year** difference from one architectural decision, before you consider
that the stuffed version is also slower and — per §3.2 — quite possibly less
accurate. This is why "we'll just use the big window" is a business decision, not
a technical shortcut.

### 4.2 The cost of a bad cache prefix

Same feature, retrieve-then-rank design. Static prefix is 5,500 tokens.

- **Prefix stable:** 5,500 tokens hit cache at ~10% of input price.
  200k × 5,500 × $0.30/M = **$330/day**.
- **Prefix contains `DateTime.Now` in the system prompt:** every request is a
  cache miss. 200k × 5,500 × $3.00/M = **$3,300/day**.

**$1.08M/year for one interpolated timestamp.** You will not find this by reading
the prompt; you find it by checking the cache-hit-rate metric (Day 17), which is
why that metric exists.

### 4.3 Attention dilution, quantified for your own system

You can't look this up — it's task-specific. Measure it (that's today's lab):
plot answer accuracy against number of retrieved chunks *k*. The curve is
typically a hump: rising to k≈3–8 as recall improves, then flat, then declining
as distractors accumulate. **The peak is your k.** Teams that never plot this
almost always run at 2–5× their optimal k.

---

## 5. Failure modes

| Failure | Root cause | Fix |
| --- | --- | --- |
| Quality drops as knowledge base grows | Fixed k, rising distractor ratio | Rerank + relevance threshold (Day 14) |
| Agent acts on an instruction the user retracted | Unbounded history, no state model | Structured state (§3.4) |
| Answer truncated mid-sentence | Output space not reserved | Reserve output budget; check stop reason |
| Cost 10× expected | Cache prefix broken by dynamic content | Prefix discipline + cache-hit metric |
| Model uses a stale policy | Contradictory chunks, no dates | Provenance labels + freshness rules (Day 14) |
| Agent invents an ID after turn 20 | Compaction dropped identifiers | Never summarize the entity block |
| Works in dev, slow in prod | Dev context 2k, prod context 40k | Test with production-shaped context |
| Tenant A's doc in tenant B's answer | No tenant assertion in assembly | `verify()` stage (Day 20) |

---

## 6. Lab (75 min) — Measure dilution, then build a budget

### Part A — The dilution curve (35 min)

Build a small needle-in-haystack test against your local model.

```python
# docs/curriculum/llm-production-engineering/lab-output/day02_dilution.py
import httpx, random, json

OLLAMA = "http://localhost:11434/api/generate"
MODEL  = "llama3.1:8b"

# 60 plausible-but-wrong distractor facts + 1 true needle
NEEDLE = "The Revenant Warden internal build server is named FORGE-7."
DISTRACTORS = [f"The Revenant Warden {n} server is named {c}-{i}."
               for i, (n, c) in enumerate(
                   [(w, c) for w in ["staging","backup","telemetry","archive","test"]
                           for c in ["ANVIL","HAMMER","KILN","QUARRY","BELLOWS","TONGS"]])]

def build(k: int, position: str, seed: int) -> str:
    rng = random.Random(seed)
    chunks = rng.sample(DISTRACTORS, min(k - 1, len(DISTRACTORS)))
    idx = {"start": 0, "middle": len(chunks)//2, "end": len(chunks)}[position]
    chunks.insert(idx, NEEDLE)
    body = "\n".join(f"[doc {i+1}] {c}" for i, c in enumerate(chunks))
    return (f"Answer only from the documents below.\n\n{body}\n\n"
            f"Question: What is the name of the internal build server?\n"
            f"Answer with just the name.")

def ask(prompt: str) -> str:
    r = httpx.post(OLLAMA, json={"model": MODEL, "prompt": prompt,
                                 "stream": False, "options": {"temperature": 0}},
                   timeout=180)
    return r.json()["response"].strip()

rows = []
for k in [1, 3, 6, 12, 25, 31]:
    for position in ["start", "middle", "end"]:
        hits = sum("FORGE-7" in ask(build(k, position, s)) for s in range(5))
        rows.append((k, position, hits / 5))
        print(f"k={k:3d} pos={position:6s} accuracy={hits/5:.0%}")

json.dump(rows, open("day02_dilution.json", "w"))
```

**Before you run it, write your prediction in `lab-notebook.md`:** at k=31, which
position wins, and by how much? Then run it and record the gap.

Extend it: change the question to one requiring *synthesis* of two needles. The
curve usually degrades much faster. That's the shape most real RAG tasks have.

### Part B — The budget enforcer (40 min)

Write a real context assembler for this repo. Create
`ContextBudget.cs` alongside `ChatSessionManager.cs`:

```csharp
namespace RevenantWorkspaceWarden;

internal sealed record ContextRegion(string Name, int MaxTokens, bool IsStatic);

internal sealed class ContextBudget
{
    // Rough heuristic: ~4 chars/token for English + code. Replace with a real
    // tokenizer before you trust it for anything (see note below).
    public static int Estimate(string s) => (s?.Length ?? 0) / 4;

    private readonly List<ContextRegion> _regions = new()
    {
        new("system",    1_500, IsStatic: true),
        new("tools",     2_000, IsStatic: true),
        new("fewshot",   2_000, IsStatic: true),
        new("retrieved", 12_000, IsStatic: false),
        new("history",    8_000, IsStatic: false),
        new("user",       2_000, IsStatic: false),
    };

    public int ReservedForOutput { get; init; } = 4_000;

    // TODO(you): implement
    //  - Fit(IDictionary<string,string> parts) -> throws or truncates per region
    //  - StaticPrefix(...) -> returns the concatenated static block
    //  - PrefixHash(...) -> stable hash of the static block, for cache telemetry
    //  - Verify(...) -> assert total + ReservedForOutput <= modelWindow
}
```

Implement `Fit`, `StaticPrefix`, `PrefixHash`, and `Verify`. Then:

1. Wire it into one provider so every call logs `prefix_hash`, `region_tokens`,
   and `total_tokens`.
2. Make ten calls and confirm `prefix_hash` is **identical** every time. If it
   isn't, find what's varying — that's a live cost bug and you just caught it.
3. Deliberately break it: interpolate `DateTime.Now` into the system prompt.
   Watch the hash change. This is the $1M/year bug from §4.2, reproduced in 30
   seconds.

**Note on tokenizers:** `length / 4` is fine for budgeting headroom and wrong for
anything tight. Code, JSON, non-English text, and long identifiers tokenize much
worse than 4 chars/token. Before Day 18 (cost attribution), replace it with the
real count from the provider's usage response, and use the estimate only as a
pre-flight guard.

---

## 7. Quiz

**1.** Name the four costs of a token in context. Which one is a quality cost
rather than a budget cost?

**2.** Your system prompt is 1,200 tokens and starts with
`"Current time: {now}. You are a helpful assistant..."`. What is wrong, what does
it cost, and what's the fix (keeping the timestamp available to the model)?

**3.** Sketch the accuracy-vs-k curve from §4.3. Explain the rising part and the
falling part with different mechanisms.

**4.** An agent at turn 25 calls `refund_order(order_id="ORD-12345")` but no such
order exists; the real one was `ORD-98217`, mentioned at turn 4. Which context
policy caused this, and what is the specific fix?

**5.** Model window is 128k. You assemble 126k of input. The user reports answers
"just stop". Diagnose, and give two fixes.

**6.** Why do position discipline (task-last) and cost discipline (static-first)
not conflict? Draw the layout that satisfies both.

**7.** Rank these history policies by suitability for a 200-step coding agent,
best to worst, with one-line justifications: sliding window; full history;
structured state; summarize-and-compact.

**8.** Calculate: 500k requests/day, input drops from 30k to 4k tokens via
retrieval, input price $3/M. Annual saving? What second-order effect on latency
would you also expect, and why?

**9.** What does the `verify()` stage catch that no amount of prompt review will?
Name three things.

**10.** True or false, defend: "Adding more relevant documents to the context
cannot make the answer worse."

---

## 8. Review hooks

- *The window is a resource with a nonlinear cost curve, not a place to put things.*
- *Adding correct information can make the answer worse.*
- *Static first, task last. Cost and attention agree.*
- *Context is state; state without a lifecycle policy becomes garbage.*
- *Never summarize the entity block.*
- *Reserve output space, and check the stop reason.*

---

## 9. Answer key

**1.** Money (input token price), prefill latency (TTFT), KV cache memory
(concurrency limit), attention dilution. **Dilution is the quality cost.**

**2.** The timestamp makes the prefix vary per request, so no prompt cache ever
hits — every request pays full input price for all 1,200 tokens, plus full
prefill. Fix: move the timestamp *out* of the static prefix into the dynamic
region near the user turn (or supply it via a tool the model can call). The
static block must be byte-identical across requests.

**3.** Rises because recall improves — larger k means higher probability the
answer is present at all. Falls because of attention dilution and distractor
interference — irrelevant near-miss passages compete with the correct one, and
contradictory content is adjudicated arbitrarily. Two different mechanisms;
peak is where marginal recall stops beating marginal noise.

**4.** Summarize-and-compact (or a sliding window) dropped the identifier when the
early turns were compressed; the model then reconstructed a plausible-looking ID.
Fix: maintain a never-summarized structured entity block carrying IDs verbatim,
and validate tool arguments against the real catalog before execution (Day 11).

**5.** Input consumed nearly the whole window, leaving almost no room for output,
so generation hits the limit and stops mid-answer — and the harness returns it as
if complete. Fixes: (i) reserve an explicit output budget and cap input at
`window − reserved`; (ii) check the finish/stop reason on every response and
surface truncation as an error or continuation, never as a normal answer.

**6.** They don't conflict because the *task restatement* is cheap (~200 tokens)
and lives in the dynamic tail, while the *bulk* static material (system, tools,
few-shot) lives in the head. Layout: `[system][tools][fewshot] | [retrieved]
[history][user][task restatement]`, with the cache prefix boundary after
few-shot.

**7.** Structured state (survives arbitrary length, testable, no drift) >
summarize-and-compact (works but loses fidelity and compounds) > window + pinned
head (loses mid-run decisions) > sliding window (loses the goal — catastrophic at
200 steps) > full history (won't fit).

**8.** 26,000 tokens saved × 500,000 × $3/M = $39,000/day ≈ **$14.2M/year**.
Latency also drops sharply because prefill is roughly linear in input length —
TTFT should fall by close to the same proportion (Day 3), often more if the long
version was pushing into a quadratic attention regime.

**9.** Any three: total budget overrun before the API bills you; cross-tenant data
in the assembled context; secrets/credentials interpolated from env; cache prefix
drift; missing provenance labels; duplicate chunks. All are structural properties
of the assembled string, invisible to prompt review because they depend on
runtime values.

**10.** **False.** Additional relevant-but-not-needed documents raise the
distractor ratio and dilute attention; near-duplicate or stale documents can
directly contradict the correct one. Relevance is necessary but not sufficient —
*sufficiency and non-contradiction* are what matter, which is why reranking and
freshness rules exist (Day 14).

---

## 10. Going deeper

- **"Lost in the Middle: How Language Models Use Long Contexts"** — Liu, Lin,
  Hewitt, Paranjape, Bevilacqua, Petroni, Liang (arXiv 2307.03172). The U-shaped
  position curve. Newer models do better; the effect is not gone.
- **"Contextual Retrieval"** — Anthropic engineering blog, 2024. Prepending
  chunk-specific context before embedding; directly relevant to Day 14. (Vendor
  blog, but the technique is model-agnostic.)
- **"Efficient Prompt Caching"** / provider prompt-caching docs — read your
  provider's, carefully, before Day 6. The prefix-stability rules are load-bearing.
- **"MemGPT: Towards LLMs as Operating Systems"** — Packer et al.
  (arXiv 2310.08560). Treats context as paged virtual memory. Worth reading for
  the framing even if you never adopt the system.

---

**Next:** [Day 3 — Prefill vs. Decode Latency](day-03-prefill-vs-decode.md).
You've been paying for tokens all lesson. Now find out what they actually cost
the hardware.
