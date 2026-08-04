# Day 10 — Structured Output: Failures, Schema Validation, Repair Loops, Fallback Chains

**Time:** ~3 h · **Prereqs:** Days 1, 7, 9 · **Lab needs:** Ollama, Python, .NET

---

## 0. Warm-up review

1. Which lever fixes "output format inconsistent"? (Day 9)
2. Which capability degrades early under quantization and is directly relevant
   today? (Day 7)
3. State the determinism sandwich. (Day 1)
4. Why must eval runs bypass response caches? (Day 6)

---

## 1. The one-paragraph version

The moment an LLM's output feeds a program instead of a human, "mostly right" is
a defect. Structured output has **three independent failure layers** — it may not
parse (syntax), it may parse but violate the schema (shape), or it may satisfy
the schema and be semantically wrong (meaning) — and only the first two are
caught by a JSON parser and a validator. The production answer is a layered
defense: **constrain generation** so invalid syntax is impossible, **validate
against a schema** because constrained generation still permits wrong shapes,
**validate semantically** because the schema can't express "this ID exists," then
a **bounded repair loop**, then a **fallback chain** with a well-defined terminal
failure. The critical design rule is that the chain must always terminate in an
honest error rather than a plausible fabrication — and most systems get that
backwards.

---

## 2. Why this bites you

Three real shapes of this bug:

**The 3% that isn't.** JSON parsing fails 3% of the time. The team adds
`try/catch` and returns `null`. Downstream, `null` is treated as "no results
found." Users see "no matching records" for 3% of queries that had matches.
Nobody ever files a bug, because a system saying "no results" doesn't look
broken. This runs for a year.

**The valid-but-wrong.** The model returns
`{"action": "refund", "amount": 4999, "currency": "USD"}`. Schema-valid. The
actual amount was **$49.99** and the field is in cents in one internal service
and dollars in another. The schema said `number`. It was a number. **A schema
validates shape, never meaning** — and this is the failure class that survives
every layer people usually build.

**The infinite repair.** A repair loop that re-asks on failure, with no attempt
cap. A malformed request triggers a repair, which fails, which triggers a repair.
One user request costs $14 and 90 seconds. Discovered on the invoice.

Notice the pattern: each is a *harness* failure (Day 1), and each is prevented
by ten to fifty lines of ordinary code.

### 2.1 The three layers, precisely

| Layer | Failure | Detected by | Example |
| --- | --- | --- | --- |
| **Syntax** | Not parseable | JSON parser | Trailing comma, markdown fence, truncation |
| **Shape** | Parses, violates schema | Schema validator | Missing required field, wrong type, unknown enum |
| **Meaning** | Valid, semantically wrong | **Your business logic only** | Nonexistent ID, wrong units, impossible date, contradicts input |

**Nearly all published guidance stops at layer 2.** Layer 3 is where the money
goes, and it requires domain code — there is no library for it.

---

## 3. Core concepts

### 3.1 Constrained decoding: make invalid syntax impossible

The strongest tool available, and underused. Rather than hoping for valid JSON,
constrain the sampler: at each decoding step, compute which tokens could still
lead to a valid document under the grammar, and mask everything else to
probability zero.

Implementations: JSON mode and structured-output modes in hosted APIs; GBNF
grammars in llama.cpp/Ollama; Outlines, Guidance, LM Format Enforcer, and
XGrammar for self-hosted stacks.

**What it guarantees:** the output parses, and matches the schema's *structural*
constraints — required keys, types, enum membership, nesting.

**What it does not guarantee:**
- **Semantic correctness.** It will happily emit a well-formed nonexistent ID.
- **Termination.** A constrained model can still fill a `string` field with 4,000
  tokens of drivel or get stuck in a repetitive valid state.
- **Quality.** There is evidence that heavy constraint can *reduce* reasoning
  quality on some tasks — the model's preferred token is masked and it takes a
  worse path. The mitigation is a two-call pattern: let the model reason freely,
  then extract to schema in a second constrained call. Costs a call; often worth
  it for anything requiring thought.

**Rule:** if your stack supports constrained decoding, use it. It eliminates
layer 1 almost entirely, which is most of your incident volume, for near-zero
cost. Then keep the validator anyway, because layer 2 constraints in most
implementations don't cover everything (`minItems`, cross-field dependencies,
conditional requirements), and because you'll want the same validation on
fallback paths that lack constraint support.

### 3.2 Schema design for LLM consumption

Schemas designed for humans and schemas designed for models differ. Rules:

1. **Flat beats nested.** Deep nesting is where bracket-matching fails. Three
   levels is a lot.
2. **Enums beat free strings.** `"status": "open"|"closed"|"pending"` is
   checkable and constrainable; `"status": string` is not.
3. **Required beats optional.** Optional fields invite inconsistent omission.
   Prefer a required field with an explicit `null` or `"unknown"` member.
4. **Name fields the way the domain talks.** `refund_amount_cents` beats
   `amt` — the name is part of the prompt, and it carries the unit.
5. **Put the unit in the name or in an enum.** The §2 bug is fully prevented by
   `{"amount": number, "unit": "cents"|"dollars"}` — or better, by requiring
   cents and saying so in the field name.
6. **Add a `confidence` or `sufficient_information` field.** Give the model a
   licit way to say "I don't know" instead of forcing it to fabricate a value to
   satisfy a required field. This one change measurably reduces hallucination in
   extraction tasks.
7. **Avoid free-form `additionalProperties`.** Unbounded keys are unvalidatable.
8. **Bound every array and string.** `maxItems`, `maxLength`. Otherwise the model
   can generate until it hits the context limit.

Point 6 deserves emphasis. A required `"customer_id": string` field with no
escape hatch **forces** fabrication when the document doesn't contain one — you
have engineered a hallucination into the contract. Always provide the null path.

### 3.3 Semantic validation — layer 3

This is your code, and it's where the real defects are caught:

```csharp
// Sketch — belongs between parse and effect (Day 1's OUTPUT CONTRACT box)
internal sealed record ValidationResult(bool Ok, IReadOnlyList<string> Errors);

internal static ValidationResult ValidateRefund(RefundCommand cmd, Order order)
{
    var errors = new List<string>();

    // Referential integrity — does the thing exist?
    if (!OrderRepo.Exists(cmd.OrderId))
        errors.Add($"order_id '{cmd.OrderId}' does not exist");

    // Range and plausibility — is the value sane?
    if (cmd.AmountCents <= 0 || cmd.AmountCents > order.TotalCents)
        errors.Add($"amount_cents {cmd.AmountCents} outside 1..{order.TotalCents}");

    // Cross-field consistency
    if (cmd.Reason == RefundReason.Damaged && cmd.EvidenceUrls.Count == 0)
        errors.Add("reason 'damaged' requires at least one evidence_url");

    // Grounding — is every claim traceable to the input? (Day 15)
    foreach (var q in cmd.QuotedLines)
        if (!order.Notes.Contains(q, StringComparison.Ordinal))
            errors.Add($"quoted line not present in source: {Truncate(q)}");

    // Policy — is this allowed, for THIS caller? (Day 19)
    if (cmd.AmountCents > Policy.AutoApproveLimitFor(order.TenantId))
        errors.Add("amount exceeds auto-approval limit; requires human review");

    return new ValidationResult(errors.Count == 0, errors);
}
```

Five distinct check families, and every one of them catches errors that pass
JSON Schema:

| Check | Catches |
| --- | --- |
| **Referential integrity** | Hallucinated IDs, nonexistent entities |
| **Range/plausibility** | Wrong units, off-by-100, impossible dates |
| **Cross-field consistency** | Contradictory field combinations |
| **Grounding** | Quotes and claims not present in the source |
| **Policy/authorization** | Actions the caller isn't permitted to take |

### 3.4 The repair loop — bounded, informative, escalating

When validation fails, ask again — but properly.

```python
MAX_REPAIRS = 2      # not 5, and definitely not unbounded

def generate_validated(prompt, schema, validate_semantic, model=PRIMARY):
    messages = [{"role": "user", "content": prompt}]
    for attempt in range(MAX_REPAIRS + 1):
        raw = call(model, messages, response_format=schema)   # constrained
        parsed, err = try_parse(raw)
        if err:
            errors = [f"Response was not valid JSON: {err}"]
        else:
            errors = schema_errors(parsed, schema) + validate_semantic(parsed)
            if not errors:
                emit_metric("structured_output.ok", attempt=attempt)
                return parsed

        emit_metric("structured_output.repair", attempt=attempt, errors=errors)
        if attempt == MAX_REPAIRS:
            break
        messages += [
            {"role": "assistant", "content": raw},
            {"role": "user", "content":
                "Your previous response was rejected for these specific reasons:\n"
                + "\n".join(f"- {e}" for e in errors)
                + "\nReturn a corrected response. Change only what the errors "
                  "require; keep everything else identical."},
        ]
    raise StructuredOutputFailure(errors, attempts=MAX_REPAIRS + 1)
```

Design points that matter:

- **Specific errors, not "try again."** `"amount_cents 4999 exceeds order total
  1299"` gets fixed; "invalid output" gets a random re-roll.
- **Include the failed attempt.** The model needs to see what it did.
- **"Change only what the errors require"** — otherwise repairs drift and
  introduce new errors in previously-correct fields.
- **Cap at 2 repairs.** Empirically, if attempts 1 and 2 both fail, attempt 3
  rarely succeeds — the failure is usually structural (bad prompt, impossible
  schema, missing information), not stochastic. Three attempts is 3× cost and 3×
  latency; know what you're buying.
- **Emit a metric on every repair.** Repair rate is one of your best leading
  indicators of quality drift (Day 17). A repair rate that climbs from 2% to 9%
  after a model version change is a regression you found before your users did.
- **Repairs are not free and not invisible.** They must appear in cost
  attribution (Day 18) and in latency budgets.

### 3.5 The fallback chain

When repairs are exhausted, escalate along a chain — each step more expensive or
more constrained than the last:

```
1. Constrained generation, primary model            (cheap, usual path)
2. Repair loop, ×2, same model                      (adds latency + cost)
3. Retry with a stronger model                      (more expensive, higher success)
4. Simplify: split into per-field extraction calls  (slower; each call trivially
                                                     easy, so success is high)
5. Deterministic extraction: regex/heuristic for
   the fields you can get that way                  (partial result)
6. Return an explicit, typed failure                (honest terminal state)
```

**Step 6 is mandatory and is the step teams omit.** The chain must end in a
failure the caller can see and handle. What it must *never* do is return a
fabricated or default-filled object that looks like success. `amount = 0` because
extraction failed will be executed as a $0 refund; `null` will be rendered as
"no data"; an empty list will be shown as "no results."

**The rule:** the terminal state of every structured-output pipeline is a typed
error, never a value. If your function's return type can't express failure, the
type is wrong.

A useful intermediate concept is **partial success**: return the fields you
validated plus an explicit list of the ones you couldn't, and let the caller
decide. Far better than all-or-nothing when the consumer is a UI that can prompt
the user for the missing piece.

### 3.6 Where structured output interacts with the rest of the course

- **Quantization** (Day 7) degrades format adherence early. A jump in repair rate
  is often the first visible symptom of a precision change.
- **Long contexts** (Day 2) raise truncation risk; if output space isn't
  reserved, JSON gets cut mid-document and reads as a syntax failure.
- **Caching** (Day 6): never cache an unvalidated response, and mark cached
  responses so repair-rate metrics aren't diluted by cache hits.
- **Tool calling** (Day 11) is structured output with side effects — every rule
  here applies, plus idempotency.
- **Evals** (Day 16): repair rate and validation-failure rate belong in your
  regression suite as first-class metrics, not just answer quality.

---

## 4. Napkin math

### 4.1 What a repair loop costs

1M requests/day, base cost $0.004/request, 5% initial validation failure,
70% of failures fixed by repair 1, 60% of the remainder by repair 2.

```
attempt 1: 1,000,000 × $0.004                       = $4,000
repair 1:     50,000 × $0.005 (longer input)        =   $250
repair 2:     15,000 × $0.006                       =    $90
fallback:      6,000 × $0.020 (stronger model)      =   $120
                                                      ───────
                                                       $4,460
```

**11% cost overhead for a hard-failure rate of 6,000 → whatever survives step 4.**
That is a very good trade — but only if you *measure* it. A team with an
unbounded repair loop and a 15% failure rate on a bad day is in a very different
place, which is exactly why the cap exists.

### 4.2 What no validation costs

Same volume, no semantic validation, 0.4% of outputs semantically wrong but
schema-valid (hallucinated IDs, wrong units):

```
4,000 wrong actions/day executed against real systems.
```

If each takes 10 minutes of human remediation at $50/hr, that's **$33,000/day**
in cleanup. If any of them are financial, it's worse. Semantic validation is
maybe 200 lines of code.

### 4.3 The truncation trap

Model window 8k, prompt 7.2k, and you allow up to 800 output tokens. Your JSON
response needs 950. It truncates at 800 with an unclosed brace. Your parser
reports a syntax error, so you enter the repair loop — which sends a *longer*
prompt (original + failed attempt + error), leaving even less output room. **Each
repair makes the underlying condition worse.** The loop burns three attempts and
fails, and the logged cause says "malformed JSON," pointing everyone at the wrong
problem for a week.

**The fix is a check, not a retry:** inspect the finish/stop reason. If it's
`length`, this is not a format failure — do not repair. Reduce input, raise the
output allowance, or split the task. Day 2's "reserve output space" rule and
Day 10's repair loop meet exactly here.

---

## 5. Failure modes

| Failure | Layer | Detection | Fix |
| --- | --- | --- | --- |
| Markdown fence around JSON | Syntax | Parse error | Constrained decoding; strip fences defensively |
| Truncated mid-object | Syntax | **Stop reason = length** | Reserve output space; don't repair |
| Missing required field | Shape | Schema validator | Constrained decoding |
| Enum value not in enum | Shape | Schema validator | Constrained decoding |
| Hallucinated entity ID | **Meaning** | Referential check | Semantic validation |
| Wrong units (cents/dollars) | **Meaning** | Range + unit-in-name | Schema naming + range check |
| Quote not in source document | **Meaning** | Grounding check | Substring/grounding validation (Day 15) |
| Repair loop never terminates | Harness | Cost spike | Hard attempt cap |
| Failure returns `null`, read as "empty" | Harness | Silent | Typed error, never a value |
| Repair fixes one field, breaks another | Harness | Repair rate high, success low | "Change only what the errors require" |
| Repair rate jumped after a deploy | Any | Repair-rate metric | Bisect: model version, prompt, quantization |

---

## 6. Lab (85 min)

### Part A — Measure your three layers (30 min)

```python
# lab-output/day10_layers.py
import httpx, json, re, jsonschema

SCHEMA = {
  "type": "object",
  "required": ["order_id", "amount_cents", "reason", "confidence"],
  "additionalProperties": False,
  "properties": {
    "order_id":     {"type": "string", "pattern": "^ORD-[0-9]{5}$"},
    "amount_cents": {"type": "integer", "minimum": 1, "maximum": 1000000},
    "reason":       {"enum": ["damaged", "late", "wrong_item", "other"]},
    "confidence":   {"type": "number", "minimum": 0, "maximum": 1},
    "evidence":     {"type": "array", "items": {"type": "string"}, "maxItems": 5},
  },
}

KNOWN_ORDERS = {"ORD-12345": 129900, "ORD-88372": 4550}

TICKETS = [
  "Order ORD-12345 arrived smashed. Customer wants $49.99 back.",
  "The package for order ORD-88372 was 3 weeks late, refund in full please.",
  "Customer is upset about their order but didn't give a number. Wants a refund.",
  "Order ORD-99999 — wrong colour shipped, refund $20.",
]

def call(ticket, constrained: bool):
    body = {"model": "llama3.1:8b", "stream": False,
            "options": {"temperature": 0, "num_predict": 300},
            "prompt": f"Extract a refund command as JSON matching this schema:\n"
                      f"{json.dumps(SCHEMA)}\n\nTicket: {ticket}\nJSON:"}
    if constrained:
        body["format"] = SCHEMA          # Ollama structured-output mode
    return httpx.post("http://localhost:11434/api/generate",
                      json=body, timeout=300).json()["response"]

def classify(raw, ticket):
    try:
        obj = json.loads(re.sub(r"^```(?:json)?|```$", "", raw.strip(), flags=re.M))
    except Exception:
        return "L1_syntax", None
    try:
        jsonschema.validate(obj, SCHEMA)
    except jsonschema.ValidationError:
        return "L2_shape", obj
    # Layer 3 — semantic
    if obj["order_id"] not in KNOWN_ORDERS:
        return "L3_bad_id", obj
    if obj["amount_cents"] > KNOWN_ORDERS[obj["order_id"]]:
        return "L3_amount_exceeds_order", obj
    if obj["amount_cents"] in (4999 // 100, 49):   # dollars-not-cents smell
        return "L3_unit_suspect", obj
    return "OK", obj

for constrained in (False, True):
    counts = {}
    for t in TICKETS * 8:
        k, _ = classify(call(t, constrained), t)
        counts[k] = counts.get(k, 0) + 1
    print(f"constrained={constrained}: {counts}")
```

**Predict first:** what does constrained decoding do to each layer's count?

**The expected result — and the point of the lab:** L1 and L2 go to (near) zero.
**L3 does not move at all.** Ticket 3 has no order number, so the model must
invent one to satisfy a required field; ticket 4 references a nonexistent order.
Constrained decoding makes both *beautifully formatted* and *just as wrong*.

Then add `"sufficient_information": {"type":"boolean"}` as required (§3.2 rule 6)
and re-run. Measure how much the fabrication rate on ticket 3 drops.

### Part B — Build the chain in this repo (35 min)

Add a real structured-output pipeline to the Warden. Create
`StructuredOutput.cs`:

```csharp
namespace RevenantWorkspaceWarden;

internal abstract record OutcomeOf<T>
{
    internal sealed record Ok(T Value, int Attempts) : OutcomeOf<T>;
    internal sealed record Partial(T Value, IReadOnlyList<string> MissingFields)
        : OutcomeOf<T>;
    internal sealed record Failed(IReadOnlyList<string> Errors, int Attempts)
        : OutcomeOf<T>;
    // NOTE: there is deliberately no `Ok(default)` path. Failure is a type,
    // never a value. See Day 10 §3.5.
}

internal sealed class StructuredOutputPipeline<T>
{
    private const int MaxRepairs = 2;

    // TODO(you):
    //  1. GenerateAsync(prompt, ILLMProvider primary, ILLMProvider stronger)
    //  2. Parse → schema-validate → semantic-validate
    //  3. Repair loop with SPECIFIC errors fed back, capped at MaxRepairs
    //  4. Escalate to `stronger` on exhaustion
    //  5. Return Failed(...) — never a default-filled T
    //  6. Emit: attempts, layer of each failure, stop reason, tokens, cost
}
```

Requirements to satisfy:
- `MaxRepairs` is a constant, not a loop-until-success.
- The stop reason is checked; `length` short-circuits to a distinct error and
  does **not** trigger a repair (§4.3).
- Every attempt emits a structured log line with the failure layer.
- The success path returns the attempt count so you can chart repair rate.

Write three tests: a clean success, a shape failure repaired on attempt 2, and an
exhausted chain returning `Failed`. Assert that no code path can return a
default-constructed `T`.

### Part C — The repair-rate dashboard line (20 min)

Add the metric you'll need on Day 17:

```
structured_output_attempts_total{feature, model, outcome="ok|repaired|failed", layer}
```

Then answer in `lab-notebook.md`:
1. What repair rate would you set an alert on, and why that number?
2. If repair rate doubles overnight, what are your three top suspects, in order?
3. Why is repair rate a better *leading* indicator of quality drift than user
   complaints?

---

## 7. Quiz

**1.** Name the three failure layers, with an example of each, and say which one
no library can check for you.

**2.** What does constrained decoding guarantee, and what does it not?

**3.** Why can constrained decoding reduce answer quality on reasoning tasks, and
what's the standard mitigation?

**4.** Give four schema design rules for LLM-consumed schemas, with the failure
each prevents.

**5.** Why does a required field with no null path *cause* hallucination? Give the
fix.

**6.** Name the five families of semantic validation and what each catches.

**7.** Your repair loop is capped at 5 and 40% of attempt-3 repairs still fail.
What does this tell you, and what should the cap be?

**8.** Explain the truncation trap: why does repairing a length-truncated response
make things worse, and what should the code check instead?

**9.** A structured-output pipeline returns `null` on total failure. Give three
distinct ways this causes a user-visible bug, and state the rule that prevents
all three.

**10.** Compute: 2M requests/day, base $0.003, 8% initial failure, 65% fixed by
repair 1, 50% of the rest by repair 2, remainder escalates to a model at
$0.015/request. Total daily cost and percentage overhead.

---

## 8. Review hooks

- *Three layers: syntax, shape, meaning. Only the first two have libraries.*
- *Constrained decoding makes wrong answers beautifully formatted.*
- *A required field with no null path engineers a hallucination into the contract.*
- *Repair with specific errors, capped at 2, changing only what's required.*
- *The terminal state is a typed error, never a value.*
- *Check the stop reason. Truncation is not a format failure.*
- *Repair rate is your best leading indicator of quality drift.*

---

## 9. Answer key

**1.** **Syntax** — unparseable (markdown fence, trailing comma, truncation);
**shape** — parses but violates the schema (missing required field, bad enum);
**meaning** — schema-valid but wrong (hallucinated ID, dollars where cents were
expected, a quote not present in the source). **Meaning** has no library — it
requires domain-specific code.

**2.** **Guarantees:** the output parses and satisfies the schema's structural
constraints — required keys, types, enum membership, nesting. **Does not
guarantee:** semantic correctness (a well-formed nonexistent ID is fine by it),
termination or bounded length, answer quality, or constraints many
implementations don't enforce (`minItems`, cross-field conditionals).

**3.** Masking tokens forces the model off its highest-probability path; if that
path was the beginning of correct reasoning, the constrained alternative can be a
worse trajectory. **Mitigation:** a two-call pattern — one unconstrained call to
reason freely, then a second constrained call that extracts the structured result
from that reasoning.

**4.** Any four: flat over nested (prevents bracket-matching failures at depth);
enums over free strings (makes values checkable and constrainable); required with
an explicit null/unknown member over optional (prevents inconsistent omission and
forced fabrication); units in field names (prevents the cents/dollars class);
bounded arrays and strings (prevents runaway generation); no
`additionalProperties` (keeps the object validatable); domain-language field names
(the schema is part of the prompt).

**5.** If the schema requires `customer_id` and the source contains none, the
model's only schema-satisfying options are to fabricate a plausible value or to
fail generation — and constrained decoding removes the second option. You have
made hallucination the compliant behaviour. **Fix:** allow `null`, or add a
required `sufficient_information: boolean` (or `confidence`) field so "I don't
know" is a valid, expressible answer.

**6.** **Referential integrity** (hallucinated IDs / nonexistent entities);
**range and plausibility** (wrong units, impossible values or dates);
**cross-field consistency** (contradictory combinations); **grounding** (claims
or quotes not present in the source input); **policy/authorization** (actions the
caller isn't permitted to take).

**7.** It tells you the failures are **structural, not stochastic** — an
impossible schema, a prompt that omits needed information, missing source data,
or a truncation problem. Re-rolling won't fix any of those. The cap should be
**2**, with the residual escalating to a different strategy (stronger model,
decomposed per-field extraction) rather than another identical attempt.

**8.** The response was cut off by the output limit, not malformed by the model.
The repair sends the original prompt *plus* the failed attempt *plus* the error
message, which is strictly longer — leaving even less output room, so it truncates
again, sooner. Each attempt worsens the cause. **The code should check the
finish/stop reason**: if it's `length`, treat it as a distinct
`OutputTruncated` failure and respond by reducing input, raising the output
allowance, or splitting the task — never by repairing.

**9.** Three of: `null` rendered by the UI as "no results found," so users
believe their data doesn't exist; `null` coerced to a default (`0`, `""`, `[]`)
and then acted on — e.g. a $0 refund executed; `null` silently skipped in a loop,
so a batch job reports success having processed nothing; the failure never
appears in metrics because it isn't an error type. **Rule:** the terminal state
of the pipeline is a typed error, never a value — if the return type can't
express failure, the type is wrong.

**10.**
```
attempt 1: 2,000,000 × $0.003            = $6,000
failures:  160,000
repair 1:    160,000 × $0.004            =   $640      (65% fixed → 56,000 remain)
repair 2:     56,000 × $0.005            =   $280      (50% fixed → 28,000 remain)
escalate:     28,000 × $0.015            =   $420
                                           ────────
                                            $7,340
```
Overhead = $1,340 / $6,000 = **22.3%**. (An initial 8% failure rate is high — the
first thing to investigate is whether constrained decoding is enabled at all.)

---

## 10. Going deeper

- **"Efficient Guided Generation for Large Language Models"** — Willard & Louf
  (arXiv 2307.09702). The finite-state-machine approach behind Outlines; the
  clearest explanation of how constrained decoding actually works.
- **"XGrammar: Flexible and Efficient Structured Generation Engine"** — Dong et
  al. (arXiv 2411.15100). Modern high-performance grammar-constrained decoding.
- **"Let Me Speak Freely? A Study on the Impact of Format Restrictions on
  Performance of LLMs"** — Tam et al. (arXiv 2408.02442). The evidence for §3.1's
  quality caveat and the two-call mitigation.
- **JSON Schema specification**, particularly `oneOf`, `dependentRequired`, and
  `const`. Most people use ~10% of the expressiveness available.
- **Your provider's structured-output documentation** — specifically which schema
  keywords are actually enforced by their constrained decoder versus silently
  ignored. This differs by provider and is the source of much confusion.

---

**Checkpoint:** take **[Checkpoint 2](checkpoint-2.md)** now.

**Next:** [Day 11 — Function Calling Reliability](day-11-function-calling.md).
Structured output that also *does* something.
