# Day 1 — Harness Engineering, Not Just Prompt Engineering

**Time:** ~2.5 h · **Prereqs:** none · **Lab needs:** the Warden repo, a text editor

---

## 0. Warm-up review

First day — nothing to review yet. Instead, write down your current answer to
this, and keep it. You'll re-read it on Day 22:

> "When an LLM feature gives a wrong answer in production, what is the most
> likely cause?"

Be specific. Don't write "hallucination" — that's a description of the symptom,
not a cause.

---

## 1. The one-paragraph version

A prompt is a string. A **harness** is the program that decides which string,
built from what, sent to which model, with what tools attached, validated how,
retried under what conditions, and terminated when. In production systems, the
harness accounts for the large majority of both quality and failure. Prompt
engineering is real, but it is a small, late-stage optimization applied to one
component of a much larger machine. Teams that spend a year "improving the
prompt" are usually looking at the wrong layer.

---

## 2. Why this bites you

Here is the shape of the mistake, and it is almost universal.

A team ships an LLM feature. It's a function: take user input, format it into a
prompt template, call the API, return the text. Quality is 70%. So they improve
the prompt. Quality goes to 76%. They improve it again. 78%. They add
few-shot examples. 81%. They try a bigger model. 83%. Then it plateaus, and
every further prompt change makes some cases better and others worse. The team
concludes "LLMs just aren't reliable enough for this."

What actually happened: the remaining 17% was never a prompt problem. It was
some mix of — the retrieved context didn't contain the answer; the model
returned valid JSON with a semantically wrong field; the tool call had a
plausible but nonexistent ID in it; the user's question needed two steps and the
harness only allowed one; the request timed out at 30s and returned a partial
generation that was silently treated as complete. **None of those are fixable by
changing the words in the prompt.** They are fixable by changing the program.

The prompt is one input to one call. The harness is the control flow, the state,
the contracts, the validation, and the failure handling around every call. When
you can only edit the prompt, every problem looks like a wording problem.

---

## 3. Core concepts

### 3.1 What the harness actually contains

Draw this once and you'll never unsee it. A single "LLM feature" in production
is at minimum:

```
  user input
      │
      ▼
 ┌─────────────────┐
 │ INPUT CONTRACT  │  validate, normalize, classify intent, reject early,
 │                 │  attach identity + tenant + permissions
 └────────┬────────┘
          ▼
 ┌─────────────────┐
 │ CONTEXT ASSEMBLY│  system prompt + policy + retrieved docs + history
 │                 │  + tool schemas + few-shot, under a token budget
 └────────┬────────┘   (Day 2)
          ▼
 ┌─────────────────┐
 │ ROUTING         │  which model, which tier, which region, what params
 └────────┬────────┘   (Day 13)
          ▼
 ┌─────────────────┐
 │ THE CALL        │  ← "the prompt" lives in here. This is the small part.
 └────────┬────────┘
          ▼
 ┌─────────────────┐
 │ OUTPUT CONTRACT │  parse, schema-validate, semantic-validate,
 │                 │  repair loop, fallback chain              (Day 10)
 └────────┬────────┘
          ▼
 ┌─────────────────┐
 │ EFFECTS         │  tool calls with argument validation, idempotency keys,
 │                 │  permission checks at the boundary        (Days 11, 19)
 └────────┬────────┘
          ▼
 ┌─────────────────┐
 │ CONTROL LOOP    │  continue? budgets? termination? escalate to human?
 └────────┬────────┘   (Day 12)
          ▼
 ┌─────────────────┐
 │ TELEMETRY       │  trace, spans, tokens, cost attribution, eval sampling
 └─────────────────┘   (Days 17, 18)
```

Count the boxes. The prompt is inside one of nine. If your codebase has a
`prompts/` directory and no equivalent of the other eight, you don't have a
harness — you have a template renderer, and you will hit the 83% ceiling.

### 3.2 The determinism sandwich

The single most useful design principle in this course:

> **Deterministic code on both sides of every model call.**

The model is a nondeterministic component embedded in a deterministic program.
Your job is to make the nondeterminism *bounded* — to shrink the surface where
"anything could happen" down to a place where anything happening is survivable.

- **Before** the call, deterministic code decides what the model is allowed to
  see. That's the only lever you have over what it can know.
- **After** the call, deterministic code decides what the model is allowed to
  *do*. That's the only lever you have over what it can break.

A model saying `delete_customer(id="all")` is not a failure. A system executing
it is. The difference is entirely on the deterministic side.

Corollary: **never let the model be the last check on anything that matters.**
"I'll add a line to the prompt saying don't do X" is not a control, it's a
preference. Controls are code that returns an error.

### 3.3 The harness quality ladder

Systems progress through recognizable stages. Find where you are.

| Stage | Shape | Typical ceiling | What breaks |
| --- | --- | --- | --- |
| 0 | String → API → string | ~70% | Everything, invisibly |
| 1 | + template, few-shot, tuned wording | ~80% | Parsing, edge inputs |
| 2 | + output schema and parse-retry | ~85% | Semantically valid but wrong output |
| 3 | + retrieval, tools, validation of *arguments* | ~90% | Stale/missing context, loops |
| 4 | + budgets, termination, routing, fallback, degraded mode | ~93% | Silent quality drift |
| 5 | + evals, tracing, cost attribution, regression gates | ceiling moves with the model | You now find out *first* |

Stage 5 isn't "more accurate" in a single-shot sense. Its distinguishing
property is that **you learn about failures from your dashboard, not from your
users.** That's the actual product of harness engineering.

### 3.4 Prompt engineering's real, narrow job

None of this means prompts don't matter. It means their job is specific:

- **Specify the contract** — role, output format, refusal conditions, tone.
- **Disambiguate** — where the task has a genuinely ambiguous reading.
- **Demonstrate** — few-shot examples for format and edge-case handling, which
  outperform prose descriptions for anything structural.
- **Constrain** — narrow the space of acceptable outputs so validation is cheap.

What prompts cannot do: supply information that isn't in the context, enforce
anything, guarantee format, or bound cost. Those are harness jobs. When you find
yourself writing "IMPORTANT: You MUST ALWAYS…" in caps for the fourth time,
that's a signal you're trying to solve a harness problem with words. Stop and
ask what code would make the failure impossible instead of merely discouraged.

### 3.5 The harness is where the money is, too

A prompt change alters quality. A harness change alters quality, latency, cost,
and blast radius simultaneously. Examples you'll build later in the course:

- Reordering the prompt so the stable parts come first: **no quality change,
  up to ~90% cost reduction on repeated calls** via prefix caching (Day 6).
- Routing 60% of traffic to a small model with a confidence-gated escalation:
  **large cost reduction, quality within noise** (Day 13).
- Adding a tool-argument validator with a repair loop: **turns a 4% hard-failure
  rate into a 0.3% one**, no model change (Days 10–11).

Not one of those is a prompt edit. All of them are twenty to two hundred lines
of ordinary, boring, testable code.

---

## 4. Napkin math: where does your quality budget go?

A useful exercise for any feature. Suppose end-to-end success is 83% and you
want 95%. Decompose the failure into independent stages:

```
P(success) = P(right context) × P(right generation) × P(valid output) × P(right effect)
```

Say you measure:

| Stage | Current | Cost to fix |
| --- | --- | --- |
| Right context retrieved | 0.90 | rerank + hybrid search: 2 weeks |
| Model generates correctly given context | 0.96 | better model: +3× per-token cost |
| Output parses and validates | 0.97 | schema + repair loop: 3 days |
| Effect executed correctly | 0.99 | arg validation: 2 days |

0.90 × 0.96 × 0.97 × 0.99 = **0.83**. Matches.

Now: what does perfect prompt engineering buy? It moves the *generation* term,
best case 0.96 → 0.98. New total: **0.847**. You spent a month for 1.7 points.

Fix retrieval to 0.97 instead: total = **0.894**. Fix retrieval *and* add the
schema repair loop (0.97 → 0.995): **0.917**. Same month, four times the gain,
and the changes are testable and permanent — they don't regress when the model
version changes underneath you.

**The lesson: multiplicative chains are dominated by their worst term. Find the
worst term before you optimize anything.** Almost nobody does this, which is why
almost everybody optimizes prompts.

---

## 5. Failure modes

| Failure | Looks like | Actual layer | Prompt can fix? |
| --- | --- | --- | --- |
| Model invents a customer ID | Hallucination | No argument validation | No |
| Answer contradicts the docs | Hallucination | Retrieval missed the doc | No |
| JSON parse error 3% of the time | "Flaky model" | No repair loop / no constrained decoding | Partially |
| Agent runs 40 tool calls, burns $12 | "Runaway agent" | No loop or tool budget | No |
| Quality dropped last Tuesday | "Model got worse" | No regression eval; something upstream changed | No |
| Slow for one big customer | "API is slow" | Prefill cost scales with their context | No |
| Tenant B saw tenant A's data | Security incident | Cache key omitted tenant ID | No |
| Answer is right but cites nothing | "Needs better prompting" | No attribution pipeline | Partially |

Read that "Prompt can fix?" column again. That's the argument for this course.

---

## 6. Lab (60 min) — Map the harness you already have

You're going to audit this repo against the nine-box diagram. This is not busy
work; it's the baseline you'll improve for the next 21 days.

### Step 1 — Trace one call end to end (20 min)

Open `Providers/AnthropicProvider.cs` and follow `ChatAsync` from the first line
to the return. Write down, in order, every distinct thing that happens to data.

Then answer, in writing, with file and line references:

1. Where is the input validated? (Look at what happens to `userPrompt`.)
2. Where is the output validated? What is the type of the return value, and
   what does the caller do if the model returned something nonsensical?
3. What happens on HTTP 429 or 529? Where is the retry? Where is the backoff?
4. What is the maximum amount of money one user action can spend? Trace it.
   Is there any code path that bounds it?
5. `Timeout = TimeSpan.FromMinutes(10)` — what does the user see at minute 9?
6. If the stream is cut off halfway, how does the caller distinguish a partial
   answer from a complete one?

### Step 2 — Score the harness (15 min)

Build this table for the repo. Be harsh; the point is a real baseline.

| Box | Present? | Where (file:line) | Notes |
| --- | --- | --- | --- |
| Input contract | | | |
| Context assembly | | | |
| Routing | | | |
| The call | ✅ | `Providers/*.cs` | |
| Output contract | | | |
| Effects / tool boundary | | | |
| Control loop | | | |
| Telemetry | | | |

Assign a stage 0–5 from §3.3. Write one sentence justifying it.

### Step 3 — Pick the worst term (15 min)

Using §4's method, list the three failure modes you believe are most common in
this app today. For each, note which box would fix it. Rank by
(expected frequency × cost of failure) ÷ (engineering days).

### Step 4 — Write it down (10 min)

Create `lab-notebook.md` at the repo root (gitignored). Start it with:

```markdown
# LLM Production Engineering — Lab Notebook

## Day 1 — Harness audit
**Baseline stage:** N
**Worst term:** ...
**Prediction I'll check on Day 22:** ...
```

The prediction matters. On Day 22 you'll grade yourself against it.

---

## 7. Quiz

Closed book. Answers in §9.

**1.** Define "harness" in one sentence, without using the words "prompt" or
"wrapper".

**2.** A team's LLM feature has these measured stage success rates: retrieval
0.85, generation 0.95, output validation 0.99, effect execution 0.98. What is
end-to-end success? Which single fix gives the biggest gain, and roughly how
much?

**3.** True or false, and defend it: "Adding `Never make up a product ID.` to the
system prompt is a control that prevents hallucinated product IDs."

**4.** State the determinism sandwich principle and explain what each side of it
protects against.

**5.** Your agent occasionally makes 30+ tool calls and costs $8 for one user
request. A colleague proposes adding "Be efficient and use as few tool calls as
possible" to the prompt. Give two reasons this is inadequate and name the actual
fix.

**6.** Which of these are harness changes rather than prompt changes? (Select all)
 (a) Moving the retrieved documents after the system prompt instead of before
 (b) Adding three few-shot examples
 (c) Adding a JSON Schema validation step with one retry
 (d) Switching from one model to a cheaper one when input is under 200 tokens
 (e) Rewording an instruction from passive to imperative voice

**7.** A feature is at stage 4 on the ladder in §3.3. Its users report that
answers "feel worse than last month" but every dashboard is green. What is
missing, and which stage does adding it move them to?

**8.** Why does the multiplicative decomposition in §4 argue *against* spending
your first month on prompt optimization? Answer with the math, not the vibe.

**9.** Name three things a prompt fundamentally cannot do, no matter how well
written.

**10.** Short answer: your PM says "we need to improve accuracy from 83% to 95%."
What is the first thing you do, before writing any code or editing any prompt?

---

## 8. Review hooks

Say these out loud. They're the spaced-repetition items you'll see again on
days 2, 4, and 8.

- *The prompt is one input to one call. The harness is everything else.*
- *Deterministic code on both sides of every model call.*
- *A model saying it is not a failure; a system doing it is.*
- *Multiplicative chains are dominated by their worst term.*
- *If your only tool is the prompt, every problem looks like a wording problem.*
- *Stage 5's product isn't accuracy, it's finding out first.*

---

## 9. Answer key

**1.** The program that decides what the model sees, what it's allowed to do
with the result, and when to stop — including validation, retries, budgets,
routing, and telemetry. (Any answer capturing "surrounding control flow +
contracts + failure handling" is correct.)

**2.** 0.85 × 0.95 × 0.99 × 0.98 = **0.783**. Retrieval is the worst term. Taking
it 0.85 → 0.95 gives 0.875 — a gain of ~9.2 points, larger than perfecting every
other stage combined (perfecting all three others gives 0.85 → 0.85, i.e. 0.85
max, a gain of 6.7).

**3.** **False.** It's a preference expressed in natural language to a
probabilistic system; it reduces frequency but cannot prevent. A control is code
that checks the emitted ID against the catalog and rejects the call. The tell:
you cannot write a unit test that proves the prompt line works, but you can
trivially write one for the validator.

**4.** Deterministic code before the call controls what the model can *know*
(bounding misinformation, leakage, and irrelevance). Deterministic code after
the call controls what the model can *do* (bounding blast radius of a wrong
output). Together they shrink nondeterminism to a survivable window.

**5.** (i) It's unenforceable — no mechanism stops call 31. (ii) It's
unmeasurable — you can't test it, and it silently degrades when the model
changes. Also acceptable: it competes with other instructions and may reduce
quality by making the model stop early on legitimately hard tasks. **Actual fix:**
hard loop-count and cumulative-token/cost budgets in the control loop, with an
explicit "budget exhausted" terminal state and a degraded-mode response. (Day 12.)

**6.** (a), (c), and (d). (a) is a harness change *disguised* as a prompt change
— it alters cache-prefix stability and therefore cost (Day 6). (b) and (e) are
prompt changes.

**7.** Missing: evals, regression gates, and drift detection — the stage 5
additions. Green dashboards measuring latency and error rate say nothing about
answer quality. Adding a golden set plus scheduled regression runs moves them to
stage 5. (Days 16–17.)

**8.** Prompt work only moves the generation term, which is typically already
the highest term in the product (0.95–0.98). Multiplying a near-1.0 factor by a
slightly higher near-1.0 factor yields a small absolute gain, while the lowest
term (often retrieval at 0.85–0.90) has far more headroom, and each point there
propagates through the whole product.

**9.** Any three of: supply information not present in the context; enforce a
constraint (only bound it probabilistically); guarantee output format; bound
cost or latency; prevent a side effect; verify its own claims; provide isolation
between tenants.

**10.** Decompose the 83% into per-stage success rates by measuring each stage
independently — i.e. build the measurement before the fix. You cannot pick a
target without knowing which term is worst, and "83%" is a product of at least
four numbers you currently don't have.

---

## 10. Going deeper

- **"Building Effective Agents"** — Anthropic engineering blog, 2024. The
  clearest published statement of the workflow-vs-agent distinction and of
  preferring simple composable patterns over autonomy. (Vendor blog; the
  taxonomy is durable, specific API advice ages fast.)
- **"The Bitter Lesson"** — Rich Sutton, 2019. Read it as a caution about
  over-engineering the *model-facing* parts while under-engineering the systems
  parts. It is frequently misused to argue against harnesses; that's a misread.
- **Release It!, 2nd ed.** — Michael Nygard. Not about LLMs at all. Circuit
  breakers, bulkheads, timeouts, and steady-state — the entire vocabulary of
  days 12, 13, and 22 comes from here, and it predates all of this by 15 years.
- **"Site Reliability Engineering"** — Beyer et al., ch. 4 (SLOs). The framing
  you'll need on Day 21 to argue about tradeoffs with a number rather than a
  feeling.

---

**Next:** [Day 2 — Context Engineering](day-02-context-engineering.md), where we
open the largest box in the diagram.
