# Day 17 — LLM Observability: Traces, Spans, Tokens, Latency, Errors, Drift

**Time:** ~3 h · **Prereqs:** Days 3, 5, 10–13, 16 · **Lab needs:** .NET, Python

---

## 0. Warm-up review

1. Which two latency metrics must never be collapsed into one, and why? (Day 3)
2. Why is GPU utilization a poor serving metric? What replaces it? (Day 5)
3. What does `degraded_responses_total` catch? (Day 13)
4. Name three leading indicators of quality drift from earlier days. (Days 10–15)

---

## 1. The one-paragraph version

An LLM request is not one operation — it's a tree of retrievals, model calls,
validations, repairs, tool invocations, and fallbacks, any of which can be the
reason the whole thing was slow, expensive, or wrong. **Observability is the
discipline of making that tree inspectable after the fact**, and it has a specific
shape here: distributed **traces** with typed **spans**, carrying token counts,
cost, model identity, cache status, and validation outcomes, plus a link to the
actual prompt and response for the cases you need to read. The unique demand of
LLM systems is that **correctness is not observable from status codes** — a
request can be 200 OK, fast, and cheap, and still be a wrong answer. So the
telemetry has to carry quality signals, not just health signals, and it has to be
designed in from the first call, because retrofitting trace context through fifty
call sites is a project rather than a patch.

---

## 2. Why this bites you

**The unanswerable question.** A customer says "yesterday afternoon your assistant
told my team the wrong retention policy." You have: request logs with timestamps
and status codes. You do not have: which documents were retrieved, which model
answered, whether it was a degraded fallback, whether the response came from a
cache, what the prompt actually contained, or whether the retrieval index had been
reindexed that morning. **You cannot answer the question at all.** Not "it takes a
while" — you cannot answer it, ever, because the information was never recorded.

**The 40-second p99 nobody could explain.** Traces exist but only at the HTTP
level. The span says `POST /chat: 41s`. Inside it: a retrieval that took 300ms, a
model call that took 4s, a schema validation failure, two repairs, a fallback to a
second provider after a 20s timeout, and a second model call. Without child spans
you see one 41-second box and guess for two weeks.

**The invisible 60% cost.** The bill doubled. Token counts are logged per request
in aggregate. Nobody logged that repair attempts (Day 10) are separate model calls
attributed to the same request, so "tokens per request" looked flat while the
actual number of calls per request went from 1.02 to 1.9.

---

## 3. Core concepts

### 3.1 The trace tree

One user action = one trace. Everything inside is a span.

```
trace: chat_request                          [8.4s] user=u_18823 tenant=t_04
├─ span: input_validation                    [2ms]
├─ span: context_assembly                    [340ms]
│  ├─ span: retrieval.dense                  [180ms] k=100 corpus_v=2024-08-01
│  ├─ span: retrieval.sparse                 [40ms]  k=100
│  ├─ span: retrieval.rerank                 [110ms] in=100 out=5 min_score=0.41
│  └─ span: budget_check                     [1ms]   tokens=6231 prefix_hash=a91f…
├─ span: llm.call                            [3.1s]  model=X tier=large
│  │                                          in=6231 out=412 cached_in=4500
│  │                                          ttft=680ms cache=hit stop=end_turn
├─ span: output.schema_validate              [3ms]   result=fail errors=1
├─ span: llm.call (repair 1)                 [2.8s]  in=6902 out=388 stop=end_turn
├─ span: output.schema_validate              [3ms]   result=ok
├─ span: output.semantic_validate            [40ms]  checks=5 result=ok
├─ span: tool.search_orders                  [220ms] idem=miss effect=read_only
└─ span: eval.sample                         [—]     sampled=true
```

Read that trace and you can answer, without opening a single log file: why it
took 8.4s (two model calls, one caused by a validation failure), what it cost
(token counts on both), whether the cache helped (4,500 of 6,231 input tokens
cached), which documents informed it (retrieval spans with corpus version), and
whether it was degraded (model and tier attributes).

**That's the standard.** If your telemetry can't answer those five questions for
an arbitrary request from last week, it isn't finished.

### 3.2 The span attributes that matter

OpenTelemetry has GenAI semantic conventions (`gen_ai.*`) — use them, because
tooling understands them, and add your own for the things they don't cover.

**On every LLM call span:**

| Attribute | Why |
| --- | --- |
| `gen_ai.request.model` / `response.model` | These can differ. The response one is truth. |
| `gen_ai.usage.input_tokens` / `output_tokens` | Cost and the Day 3 phase split |
| `cache_read_tokens` / `cache_write_tokens` | Day 6's cache-hit rate |
| `prompt_prefix_hash` | Day 2/6 — detects prefix drift instantly |
| `ttft_ms` | Prefill (Day 3) |
| `gen_ai.response.finish_reason` | **Truncation detection** (Day 10 §4.3) |
| `temperature`, `top_p`, `max_tokens` | Reproducibility |
| `route.tier`, `route.reason`, `route.degraded` | Day 13 |
| `attempt` (0 = original, ≥1 = repair) | Day 10 |
| `prompt_template_version` | Which prompt produced this |
| `cost_usd` | Computed at emit time, not later (Day 18) |

**On retrieval spans:** query, k in/out, retriever type, top score, min score
after threshold, chunk IDs, corpus version, whether a permission filter applied.

**On tool spans:** tool name, effect class, validation layer that rejected (if
any), idempotency hit/miss, duration, retry count.

**On every trace root:** `tenant_id`, `user_id` (pseudonymous), `feature`,
`workflow`, `session_id`, `trace_id`, and the client's SDK/version.

That trace-root set is what makes Day 18 possible. Add it now; adding it later
means re-instrumenting everything.

### 3.3 What to record about the content itself

This is the part with a privacy tension, and it needs a deliberate policy rather
than a default.

| Level | Records | Use |
| --- | --- | --- |
| **Metadata only** | Token counts, hashes, lengths | Always on, all requests |
| **Redacted content** | Prompt/response with PII redacted | Sampled, most systems |
| **Full content** | Everything | Sampled, with consent/contract, short retention |

Practical policy that works for most teams:

- **Always:** metadata, on 100% of requests. It's small and it's what most
  debugging needs.
- **Sample:** full (or redacted) content on 1–5%, plus **100% of anything that
  errored, was repaired, hit a budget, or was degraded**. Failures are rare and
  are exactly what you need to read.
- **Retain content briefly** (7–30 days) and metadata long (13 months, so you can
  compare year over year).
- **Never log secrets or raw API keys** — obvious, and yet: prompts sometimes
  contain credentials that a user pasted, so redaction runs on the *content*, not
  just on your own fields.
- **Tenant-scope the trace store.** Support engineers should not be able to read
  arbitrary tenants' prompts (Days 19–20).

The "100% of failures" rule is the important one. Sampling uniformly at 2% means
that when a rare bug fires ten times, you have 0.2 traces of it.

### 3.4 The four metric families

**Health** (standard, necessary, insufficient): request rate, error rate by
class, latency percentiles, saturation.

**Performance** (LLM-specific, Days 3–5):
`ttft_ms` and `itl_ms` percentiles — never merged; queue depth and wait time;
batch occupancy; KV utilization; goodput; preemption rate; tokens/sec in and out.

**Quality** (the ones nobody has, from every prior day):

| Metric | Day |
| --- | --- |
| `structured_output_repair_rate` | 10 |
| `schema_validation_failure_rate{layer}` | 10 |
| `tool_validation_failure_rate{tool,layer}` | 11 |
| `hallucinated_id_rate` | 11 |
| `budget_exhaustion_rate{budget}` | 12 |
| `degraded_response_rate{reason}` | 13 |
| `answer_in_context_rate` | 15 |
| `citation_correctness` (sampled) | 15 |
| `judge_score{dimension}` (sampled) | 16 |
| `abstention_rate` | 15 |

**Cost** (Day 18): tokens and dollars by model, feature, workflow, tenant, and
whether cached.

**The distinguishing property of an LLM observability stack is the quality
family.** Health and performance metrics are the same as any service. Quality
metrics are the ones that catch a system that is 200-OK, fast, cheap, and wrong —
which is the characteristic LLM failure.

### 3.5 Drift: the failure with no deploy

Everything else in this course assumes something changed. Drift is what happens
when nothing did.

| Drift type | Cause | Detect with |
| --- | --- | --- |
| **Input drift** | Users ask different things | Query embedding distribution; length distribution; new-intent rate |
| **Corpus drift** | Documents added/changed/deleted | Corpus size, age distribution, `stale_chunk_retrieval_rate` |
| **Model drift** | Provider silently updated a model version | `gen_ai.response.model` changes; canary eval scores |
| **Quality drift** | Any of the above, or accumulation | Scheduled evals; judge scores on sampled traffic |
| **Cost drift** | Prompt growth, repair rate, cache-hit decay | Tokens per request over time; cache-hit rate |
| **Behavioural drift** | Users adapt to the system | Session length, regeneration rate, abandonment |

**Model drift deserves specific paranoia.** A provider updating a model behind a
stable alias will change your outputs with no notification and no deploy on your
side. Defenses: pin explicit versioned model IDs where the provider offers them;
record `gen_ai.response.model` on every call and **alert on any change**; run a
small canary eval hourly against production configuration.

**The canary eval is worth its own note:** 20–30 cases with deterministic scoring,
run hourly, costs a couple of dollars a day, and it's the fastest possible
detection of "the model underneath us changed." Combined with the pinned-version
alert, you find out in an hour instead of from a customer.

### 3.6 Correlating quality complaints with telemetry

The workflow that pays for everything above:

```
1. Complaint arrives with a rough time and a user.
2. Find the trace: filter by tenant + user + time window.
3. Read the trace tree: which model, degraded?, cached?, repaired?, which chunks?
4. Check the retrieval span: was the right chunk retrieved at all?  (Day 15's 2x2)
   → NOT retrieved  → retrieval problem; check corpus version, permission filter
   → retrieved      → generation problem; read the prompt and response
5. Check for a systemic cause: was there a degradation window? a model change?
   a corpus reindex? Compare against the drift dashboards.
6. Add the case to the eval suite.  (Day 16 — mandatory)
```

Step 6 closes the loop between this day and the last. **A debugging session that
doesn't end in a regression case is an incomplete debugging session.**

### 3.7 Sampling, cardinality, and cost of the telemetry itself

Observability has a bill too, and it's easy to make it absurd.

- **Head sampling** (decide at the start) is simple; **tail sampling** (decide
  after seeing the outcome) is far better here — it lets you keep 100% of slow,
  errored, repaired, or degraded traces and 1% of the boring ones.
- **Cardinality kills.** Never put `user_id`, `trace_id`, `query`, or `chunk_id`
  in a *metric* label — that's a trace attribute. A metric labelled by user ID
  will produce millions of time series and a very memorable invoice.
- **Compute cost at emit time.** Prices change; store the dollar figure with the
  price version, so historical analysis stays correct (Day 18).
- **Budget it.** A reasonable target is telemetry costing 1–3% of inference spend.
  Above ~5%, sample harder.

---

## 4. Napkin math

### 4.1 What a trace is worth

Team of 6 engineers. Without span-level tracing, an average quality complaint
takes ~4 hours to investigate and roughly 40% are never explained. With it, ~20
minutes and ~5% unexplained.

```
30 complaints/month × 3.67 hours saved × $90/hr ≈ $9,900/month
                                                ≈ $119k/year
```

Plus the 35% that go from "never explained" to explained, which is where the
systemic bugs are — the ones costing far more than the investigation time.

### 4.2 Telemetry cost

2M requests/day. Metadata span set ≈ 2 KB/trace; 3% sampled with content ≈ 20 KB.

```
metadata: 2M × 2 KB               = 4 GB/day
content:  60k × 20 KB             = 1.2 GB/day
                                    ─────────
                                    5.2 GB/day ≈ 158 GB/month
```

At typical observability pricing this is a few hundred to a couple thousand
dollars a month, against an inference spend that at 2M requests/day is likely six
figures monthly. **Well inside the 1–3% budget.** The failure mode isn't cost,
it's cardinality: the same volume with `user_id` as a metric label can cost more
than the inference.

### 4.3 Canary eval economics

25 cases, hourly, ~2,000 in / 300 out at $3/M in and $15/M out:

```
per run:  25 × (2,000 × $3/M + 300 × $15/M) = 25 × ($0.006 + $0.0045) = $0.26
per day:  × 24                                                        = $6.30
per year:                                                             = $2,300
```

**$2,300/year to detect a silent model change within an hour.** Compare to Day 13
§4.4's four-hour silent degradation producing 7,200 bad answers, or a model
version change that goes unnoticed for a month.

---

## 5. Failure modes

| Failure | Missing telemetry | Fix |
| --- | --- | --- |
| Can't explain a complaint from last week | Trace content not retained | Sample content; keep 100% of failures |
| One 41s span, no detail | No child spans | Instrument each pipeline stage |
| Cost doubled invisibly | Repairs not counted as separate calls | `attempt` attribute; calls-per-request metric |
| Silent model version change | `response.model` not recorded | Record + alert on change; pin versions; canary |
| Cache regression unnoticed | No `prompt_prefix_hash` / cache token counts | Add both; alert on hit-rate drop |
| Truncation reported as a format error | `finish_reason` not recorded | Record it; branch on `length` (Day 10) |
| Quality drop with green dashboards | No quality metric family | Add §3.4's quality metrics |
| Observability bill exceeds inference bill | High-cardinality metric labels | IDs go on traces, never on metric labels |
| Support can read any tenant's prompts | Trace store not tenant-scoped | Scope access (Days 19–20) |
| Rare bug fires 10×, no traces captured | Uniform head sampling | Tail sampling: 100% of failures |

---

## 6. Lab (85 min)

### Part A — Instrument this repo end to end (45 min)

The Warden currently has no telemetry. Add it.

Create `Telemetry/WardenTelemetry.cs`:

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RevenantWorkspaceWarden.Telemetry;

internal static class WardenTelemetry
{
    internal const string SourceName = "RevenantWorkspaceWarden";
    internal static readonly ActivitySource Source = new(SourceName);
    private static readonly Meter Meter = new(SourceName);

    // Quality metrics — the family nobody has.
    internal static readonly Counter<long> RepairAttempts =
        Meter.CreateCounter<long>("llm.structured_output.attempts");
    internal static readonly Counter<long> DegradedResponses =
        Meter.CreateCounter<long>("llm.degraded_responses");
    internal static readonly Counter<long> ToolValidationFailures =
        Meter.CreateCounter<long>("llm.tool.validation_failures");
    internal static readonly Histogram<double> Ttft =
        Meter.CreateHistogram<double>("llm.ttft", unit: "ms");
    internal static readonly Histogram<double> CostUsd =
        Meter.CreateHistogram<double>("llm.cost", unit: "usd");

    internal static Activity? StartLlmCall(string provider, string model, int attempt)
    {
        var a = Source.StartActivity("llm.call", ActivityKind.Client);
        a?.SetTag("gen_ai.system", provider);
        a?.SetTag("gen_ai.request.model", model);
        a?.SetTag("attempt", attempt);
        return a;
    }
}
```

Then wire it through:

1. **`Providers/AnthropicProvider.cs` and `OllamaProvider.cs`** — wrap `ChatAsync`
   in an `llm.call` span. Record: request model, **response model**, input/output
   tokens, cache read/write tokens, TTFT (timestamp the first streamed chunk),
   `finish_reason`, temperature, and computed `cost_usd`.
2. **`ChatSessionManager.cs`** — start the root `chat_request` activity, with
   `tenant_id`, `user_id`, `feature`, `session_id` as tags.
3. **The Day 2 `ContextBudget`** — emit `prompt_prefix_hash` and per-region token
   counts on a `context_assembly` span.
4. **The Day 10 pipeline** — emit `RepairAttempts` with the failure layer.
5. **The Day 13 router** — emit `DegradedResponses{reason}` and route tags.

Export to console first (`AddConsoleExporter`), then to whatever backend you
prefer via OTLP.

**Acceptance test:** make one request that triggers a repair and a fallback. Then
answer, from the trace alone, the five questions in §3.1. If you can't answer all
five, keep instrumenting.

### Part B — Detect a silent model change (20 min)

```python
# lab-output/day17_canary.py
# 25 deterministic-scored cases, run against the production config.
# Emit: overall score, per-category scores, and the response model ID.
# Alert if: score drops > noise floor (Day 16), OR response model != expected.
```

Run it twice to establish the noise floor. Then simulate drift: point it at a
different model tag and confirm both alerts fire. Write down how quickly you'd
detect a real change at your chosen cadence.

### Part C — Build the debug workflow (20 min)

Write the runbook, in `docs/runbooks/quality-complaint.md`, for §3.6. It should be
specific enough that someone on call at 2am can follow it: the exact trace query,
which span attributes to read in what order, the decision tree at step 4, and
where to file the regression case.

Then **test it** by injecting a deliberate failure (break the retrieval permission
filter, or force a degraded route), filing a fake complaint, and having someone
else follow the runbook. Whatever they couldn't determine is missing telemetry —
add it, and note it in `lab-notebook.md`.

---

## 7. Quiz

**1.** Give the five questions a good trace should answer about any past request.

**2.** Name six attributes that must be on every LLM call span, with the reason
for each.

**3.** Why is `gen_ai.response.model` more important than `request.model`?

**4.** What is tail sampling and why is it better than head sampling here?

**5.** Name the four metric families. Which one is specific to LLM systems, and
what failure does it catch that the others cannot?

**6.** Name four kinds of drift and how you detect each.

**7.** Why must `user_id` never be a metric label? What's the correct place for it?

**8.** A canary eval of 25 cases running hourly costs ~$6/day. Argue for it in one
paragraph, with a concrete counterfactual.

**9.** Your p99 is 41s and you have one HTTP-level span. List the six things
child spans would let you distinguish.

**10.** State the rule connecting a debugging session to the eval suite, and
explain why it exists.

---

## 8. Review hooks

- *One user action, one trace, typed spans all the way down.*
- *Correctness is not observable from status codes.*
- *The quality metric family is what makes it LLM observability.*
- *Record the response model and alert on any change.*
- *Sample the boring; keep 100% of failures.*
- *IDs go on traces. Never on metric labels.*
- *A debugging session that doesn't end in a regression case isn't finished.*

---

## 9. Answer key

**1.** Why was it slow (which span dominated)? What did it cost (tokens per call,
including repairs)? Which model actually served it, and was it degraded? What
context informed it (which chunks, which corpus version)? Was it served from a
cache or a repair path? (Also acceptable: what did the user actually see.)

**2.** Six of: `gen_ai.response.model` (what actually ran); input/output token
counts (cost and phase analysis); `cache_read/write_tokens` (cache-hit rate);
`prompt_prefix_hash` (prefix drift); `ttft_ms` (prefill vs. decode split);
`finish_reason` (truncation detection); `attempt` (repair accounting); route tier
and degraded flag; `prompt_template_version`; `cost_usd`.

**3.** Because they can differ — a provider may serve a different version behind a
stable alias, and a router may have fallen back to a different model than
requested. The response value is what actually produced the output, so it's the
one that explains behaviour, and it's the one that reveals a silent model change.

**4.** Tail sampling decides whether to keep a trace *after* the request completes
and its outcome is known. It's better here because the traces worth keeping are
the rare ones — slow, errored, repaired, degraded, budget-exhausted — and uniform
head sampling at 2% means a bug that fires ten times leaves you 0.2 traces. Tail
sampling keeps 100% of failures and 1% of successes.

**5.** Health, performance, **quality**, and cost. Quality is LLM-specific, and it
catches the characteristic failure: a request that is 200 OK, fast, and cheap, and
whose answer is wrong. Health metrics report success; performance metrics report
speed; neither can distinguish a correct answer from a confident fabrication.

**6.** Any four: **input drift** — query embedding/length distributions, new-intent
rate. **Corpus drift** — corpus size, document age distribution, stale-chunk
retrieval rate. **Model drift** — `gen_ai.response.model` changes plus canary eval
scores. **Quality drift** — scheduled evals and sampled judge scores. **Cost
drift** — tokens per request and cache-hit rate over time. **Behavioural drift** —
regeneration rate, session length, abandonment.

**7.** Because each distinct label value creates a separate time series;
`user_id` produces one per user, which is millions of series — an explosion in
storage, query time, and cost that can exceed the inference bill. The correct
place is as a **trace/span attribute**, where it's stored per-event and indexed
for search rather than aggregated into a time series.

**8.** $2,300/year buys detection of a silent model version change — or any
quality regression exceeding the noise floor — within one hour, against a
counterfactual where a provider updates a model behind a stable alias and you find
out weeks later from a customer, having in the meantime served an unknown number
of degraded answers with no ability to reconstruct which requests were affected.
Compare to a single four-hour silent-degradation incident producing thousands of
bad answers (Day 13 §4.4): the canary pays for itself several times over on the
first incident it catches.

**9.** Retrieval time; the model call itself; whether validation failed; how many
repair attempts occurred; whether a provider timeout triggered a fallback and how
long that timeout was; and queueing/wait time before the call started. (Also
acceptable: tool call durations, and whether a cache lookup happened.) Without
child spans all six are indistinguishable inside a single 41-second box.

**10.** **No debugging session closes without adding a regression case to the eval
suite** (Day 16 §3.2). It exists because a fix made in a prompt, in context
assembly, or as a model-specific workaround is routinely undone by a model
upgrade or an adjacent change — and without a permanent case, the same bug returns
undetected and is debugged from scratch, at full cost, by someone who wasn't
there the first time.

---

## 10. Going deeper

- **OpenTelemetry GenAI semantic conventions** — the `gen_ai.*` attribute set.
  Read them before inventing your own names; the tooling ecosystem assumes them.
- **"Observability Engineering"** — Majors, Fong-Jones, Miranda. Chapters on
  high-cardinality data and the difference between monitoring and observability.
  The distinction matters more for LLM systems than for most.
- **"Distributed Systems Observability"** — Cindy Sridharan. Short, free, and the
  clearest statement of why logs, metrics, and traces answer different questions.
- **"Site Reliability Engineering"** — Beyer et al., ch. 6 ("Monitoring
  Distributed Systems"). The four golden signals, and why they're necessary and
  insufficient here.
- Your provider's response headers and usage objects — read what they actually
  return. Cache token counts and finish reasons are often available and
  routinely discarded.

---

**Next:** [Day 18 — Cost Attribution](day-18-cost-attribution.md). You have the
tokens on every span. Now answer "which feature caused the bill?"
