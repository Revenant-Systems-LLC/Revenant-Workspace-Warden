# LLM Production Engineering — A 22-Day Course

**Audience:** an engineer who can already get a model to answer a question, and now
has to keep it answering correctly, quickly, cheaply, and safely for other people.

**Format:** 22 lessons. One subject per day. Each lesson is self-contained and
includes theory, napkin math, a hands-on lab, a quiz with an answer key, and a
spaced-repetition review of earlier days.

**Not covered:** how transformers work internally (attention math, training
dynamics, backprop). This course starts *after* the model exists and assumes you
treat it as a component with known cost and failure characteristics.

---

## How to run this course

### Pacing options

| Pace | Sessions/week | Calendar time | Who it's for |
| --- | --- | --- | --- |
| **Sprint** | 7–8 | ~3 weeks | Full-time study, no day job competing |
| **Standard** | 5 (weekdays) | ~4.5 weeks | 2–3 focused hours a day |
| **Sustainable** | 3 | ~7.5 weeks | Learning around a job |

A "day" is roughly **2.5–3.5 hours**: ~60–75 min reading and working the
examples, ~45–75 min lab, ~20 min quiz and review. Days 15, 16, 17 and 22 run
long — budget an extra hour.

Do not skip the labs. This subject is full of ideas that feel obvious when read
and turn out to be wrong when measured. The labs exist to make you measure.

### The daily loop

1. **Warm-up review (10 min).** Every lesson opens with questions drawn from
   day *N−1*, *N−3*, and *N−7*. Answer them from memory before scrolling. This
   is the single highest-value part of the course and the easiest to skip.
2. **Read the lesson.** Work the napkin math by hand. If a number surprises you,
   recompute it — surprise usually means you had a wrong mental model.
3. **Do the lab.** Write down the number you expect *before* you run it.
   The gap between prediction and measurement is the lesson.
4. **Take the quiz closed-book.** Grade honestly. Anything below 70% means
   re-read section 3 of that lesson before moving on.
5. **Log it.** Append to `lab-notebook.md` (create it locally; it's gitignored
   by default — see below).

### Checkpoints

Cumulative exams sit after days 5, 10, 15, and 21. They are harder than the
daily quizzes and deliberately mix subjects, because production problems arrive
mixed. Take them timed and closed-book.

| After day | File | Covers |
| --- | --- | --- |
| 5 | [`checkpoint-1.md`](checkpoint-1.md) | Days 1–5 — harness, context, serving mechanics |
| 10 | [`checkpoint-2.md`](checkpoint-2.md) | Days 6–10 — caching, compression, adaptation, structure |
| 15 | [`checkpoint-3.md`](checkpoint-3.md) | Days 11–15 — tools, agents, routing, RAG |
| 21 | [`checkpoint-4.md`](checkpoint-4.md) | Days 16–21 — evals, observability, cost, safety |

Day 22 is the capstone: a failure-mode catalog plus a design exercise that
touches every prior day.

---

## Prerequisites and setup

You need all of these working before Day 3.

**Local inference — Ollama.** The serving-mechanics labs (days 3–8) need a model
you can run and instrument yourself. Hosted APIs hide exactly the numbers those
days are about.

```powershell
# Windows, from anywhere
winget install Ollama.Ollama
ollama pull llama3.1:8b        # primary lab model
ollama pull llama3.2:1b        # draft model for Day 8 (speculative decoding)
ollama pull nomic-embed-text   # embeddings for Days 14-16
```

Verify Ollama is reachable at the address this repo's provider expects
(`http://localhost:11434`, see `Providers/OllamaProvider.cs:16`).

**A hosted API key.** Days 6, 13, 18 and 19 use a hosted provider for prompt
caching, routing, and billing telemetry. This repo already has
`Providers/AnthropicProvider.cs` and `Providers/OpenAICompatibleProvider.cs`
wired through `Providers/SecretsManager.cs` — use that path, and never put a key
in a lesson file or a lab script.

**Python 3.11+** for eval and retrieval labs (days 14–17):

```powershell
py -m venv .venv-course
.\.venv-course\Scripts\Activate.ps1
pip install httpx numpy scikit-learn rank_bm25 pydantic jsonschema pytest
```

**The .NET side.** Labs that touch this repo build with:

```powershell
dotnet build RevenantWorkspaceWarden.csproj
```

Course work happens on a branch, per `AGENT.md`. Do not commit lab scratch files,
measurement CSVs, or anything with a key in it. Add to `.gitignore`:

```
lab-notebook.md
docs/curriculum/**/lab-output/
*.measurements.csv
.venv-course/
```

---

## Syllabus

### Week 1 — The harness and the machine under the model

The theme: almost everything people call "prompt engineering" is actually
systems engineering, and the model's cost behaviour is not one number.

| Day | Subject | Core question |
| --- | --- | --- |
| [1](day-01-harness-engineering.md) | Harness engineering, not prompt engineering | What is the program *around* the model, and why does it determine quality more than the prompt does? |
| [2](day-02-context-engineering.md) | Context engineering, not long prompts | How do you decide what occupies the window, and what does the wrong answer cost? |
| [3](day-03-prefill-vs-decode.md) | Prefill vs. decode latency | Why do the two halves of a request obey different physics, and optimize in opposite directions? |
| [4](day-04-kv-cache.md) | KV cache: management, eviction, reuse, memory pressure | Why does your server run out of memory at a *concurrency* number rather than a request-size number? |
| [5](day-05-batching-paged-attention.md) | Continuous batching, paged attention, throughput | How do you get 10× the throughput out of hardware you already own? |

→ **[Checkpoint 1](checkpoint-1.md)**

### Week 2 — Making it cheaper without making it dumber

The theme: every efficiency lever trades away something. Know what, and know how
you'd detect the loss.

| Day | Subject | Core question |
| --- | --- | --- |
| [6](day-06-prompt-vs-semantic-caching.md) | Prompt caching vs. semantic caching | One is a correctness-preserving optimization; one is a bet on your users. Which is which, and when does the bet lose? |
| [7](day-07-quantization.md) | INT8, INT4, FP8, AWQ, GPTQ | Where does quality actually go when you shrink the weights, and how would you know? |
| [8](day-08-speculative-decoding.md) | Speculative decoding vs. quantization vs. distillation | Three ways to go faster with three different risk profiles — which is lossless? |
| [9](day-09-adaptation-levers.md) | Fine-tuning vs. in-context learning vs. RAG vs. distillation | When is each one the *wrong* tool, and what's the tell? |
| [10](day-10-structured-output.md) | Structured output, schema validation, repair loops, fallback chains | The model returned JSON. Is it the JSON you asked for, and what happens when it isn't? |

→ **[Checkpoint 2](checkpoint-2.md)**

### Week 3 — Correctness at the interface

The theme: the model touching the outside world is where "mostly right" becomes
"deleted the wrong record".

| Day | Subject | Core question |
| --- | --- | --- |
| [11](day-11-function-calling.md) | Function calling reliability, tool contracts, argument validation, idempotency | How do you design a tool so a confused model can't do lasting damage with it? |
| [12](day-12-agent-guardrails.md) | Agent guardrails, loop budgets, tool budgets, termination | How does an agent *stop*, and what does it do when it can't finish? |
| [13](day-13-routing-and-fallback.md) | Model routing, graceful fallback, degraded-mode UX | When the good model is down, what does the user see? |
| [14](day-14-rag-architecture.md) | RAG: chunking, embeddings, hybrid search, reranking, freshness | Why is "just add a vector DB" the most common way to ship a confidently wrong product? |
| [15](day-15-retrieval-evals.md) | Retrieval evals: recall, precision, grounding, attribution, citations | How do you tell a retrieval bug from a generation bug? |

→ **[Checkpoint 3](checkpoint-3.md)**

### Week 4 — Knowing whether it works, and keeping it honest

The theme: you cannot manage what you cannot see, and LLM systems degrade
silently by default.

| Day | Subject | Core question |
| --- | --- | --- |
| [16](day-16-evals.md) | Golden sets, regression tests, adversarial tests, LLM-as-judge, human evals | How do you build a test suite for a system with no deterministic output? |
| [17](day-17-observability.md) | Traces, spans, tokens, latency, errors, drift | What do you instrument on day one so that day ninety is debuggable? |
| [18](day-18-cost-attribution.md) | Cost per feature, workflow, tenant, and user journey | Your bill is $40k. Which feature caused it? |
| [19](day-19-safety-engineering.md) | Prompt injection defense, data leakage prevention, permission boundaries | Why is "the model shouldn't do that" not a security control? |
| [20](day-20-multi-tenant-isolation.md) | Multi-tenant isolation, cache safety, cross-user contamination | How does tenant A's data end up in tenant B's answer, and which of your caches did it? |
| [21](day-21-full-stack-tradeoffs.md) | Latency, quality, cost, reliability across the full stack | Given a fixed budget, where does the next dollar go? |

→ **[Checkpoint 4](checkpoint-4.md)**

| Day | Subject | Core question |
| --- | --- | --- |
| [22](day-22-production-failure-modes.md) | Production failure modes + capstone | Hallucinated tool calls, malformed JSON, stale retrieval, runaway agents, silent eval regression — recognize, instrument, contain. |

**Supporting material:** [`glossary.md`](glossary.md) — every term the course
uses, defined once, with the day it's introduced.

---

## What "done" looks like

By Day 22 you should be able to walk into a room and:

- Read a latency graph and say whether the problem is prefill, decode, queueing,
  or the network — without guessing.
- Compute KV cache memory for a given model and concurrency on the back of an
  envelope, and predict the concurrency at which the server dies.
- Explain to a non-engineer why the answer got worse after a "cost optimization",
  and produce the eval that proves it.
- Design a tool contract that a confused model cannot use to cause an
  irreversible side effect.
- Point at any dollar on the bill and name the feature, tenant, and user journey
  that produced it.
- Say, out loud and specifically, how your system leaks data between tenants —
  because every system has an answer, and "it doesn't" is never it.

---

## A note on citations

Each lesson ends with a "Going deeper" section listing papers and docs by
**title and author**, and where I'm confident, an arXiv ID. Verify IDs before
citing them anywhere that matters — titles and authors are the reliable part.
Where a claim comes from a vendor blog post or a fast-moving implementation
detail, the lesson says so, because those go stale fastest.

Numbers in this course (bandwidths, cache sizes, price points) are illustrative
of the reasoning, not a current price sheet. The *method* of computing them is
the durable part. Re-derive with today's hardware and today's prices.
