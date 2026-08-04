# Checkpoint 2 — Days 6–10

**Covers:** caching, quantization, speculative decoding, adaptation levers,
structured output.

**Rules:** 60 minutes, closed book. Sections A–C are cumulative — some questions
reach back into Days 1–5 deliberately, because production problems don't respect
lesson boundaries.

**Passing:** 70/100.

---

## Section A — Recall (20 points, 2 each)

**A1.** What does prompt caching reuse? What does semantic caching reuse?

**A2.** Write the speculative decoding expected-tokens formula.

**A3.** Order the quantization damage profile — where does aggregate benchmark
accuracy sit?

**A4.** Name the three structured-output failure layers.

**A5.** Which of the three speed levers is output-preserving, and why?

**A6.** State the facts-vs-behaviour rule for choosing an adaptation lever.

**A7.** What is the terminal state of a structured-output pipeline?

**A8.** Give two things a semantic cache key must include besides the embedding.

**A9.** What does "W4A16" mean, and which inference phase does it help?

**A10.** Why does a required schema field with no null path cause hallucination?

---

## Section B — Calculation (30 points)

**B1. (10 pts)** 1.2M requests/day. Prompt = 4,500-token static prefix +
2,000-token dynamic. Input $3/M, cached input $0.30/M, output $15/M, average
output 200 tokens.

 (a) Daily cost with no prefix caching.
 (b) Daily cost with prefix caching working.
 (c) A deploy adds `"build: {commit_sha}"` to line 1 of the system prompt.
     Daily cost now, and the annual cost of that line.
 (d) Which single metric would have caught (c) within a day?

**B2. (10 pts)** Speculative decoding, measured α = 0.75, draft cost ratio
c = 0.08.

 (a) Expected tokens per step at γ = 3, 5, and 7.
 (b) Speedup at each.
 (c) Which γ is optimal, and why isn't larger always better?
 (d) Your production service runs at batch 96. Revise your recommendation and
     justify it in one sentence.

**B3. (10 pts)** A 40B model. You quantize to 4-bit with group size 64, FP16
scale and FP16 zero-point.

 (a) Actual bits per weight.
 (b) Actual size in GB.
 (c) By how much does the naive "4-bit = 20 GB" estimate understate it?
 (d) On hardware with 2.4 TB/s bandwidth, the decode floor before and after.

---

## Section C — Diagnosis (30 points, 10 each)

State the most likely cause, the metric that confirms it, and the fix.

**C1.** A team quantized their model to INT4 last month. MMLU moved 0.4 points.
Since then: structured-output repair rate rose from 1.8% to 6.1%; support tickets
about "it can't find things in long documents" tripled; the Spanish endpoint's
user satisfaction dropped. Latency improved 15% — less than the 60% they
projected.

**C2.** A semantic cache was added to a customer-service assistant. Latency p50
dropped 55% and costs fell 30%. Two weeks later, three customers report being
shown another customer's order history. The engineer who built it says the
similarity threshold is 0.92 and "that's very conservative."

**C3.** A support-article assistant was built by fine-tuning a base model on
14,000 help articles. It scores 91% on the team's eval. In production: it cites
no sources, confidently describes a refund policy that changed in March, and the
compliance team has just asked how to remove a specific customer's data from it.

---

## Section D — Design (20 points)

**D1. (20 pts)** You own an invoice-processing feature. It reads a PDF's extracted
text (2,000–40,000 tokens) and must emit a structured record: vendor ID, line
items with amounts in cents, currency, due date, and PO reference. Downstream, an
approved record triggers an actual payment.

Volume: 90,000 documents/day. Current state: a single unconstrained call to a
70B model, `try/catch` around `JSON.parse`, `null` on failure. Measured: 4.2% parse
failures, unknown semantic error rate, $0.021/document.

Produce:
 (a) The full output-contract design: which layers, in what order, and one
     concrete check per semantic-validation family from Day 10 §3.3.
 (b) Three specific schema design changes and the failure each prevents.
 (c) The repair and fallback chain, with the attempt cap and the terminal state.
 (d) Whether you'd quantize this workload. Justify with reference to Day 7 §3.6
     and Day 3's phase analysis.
 (e) One thing in this design that must be true *before* anyone is allowed to
     turn on a semantic cache for it.

---

## Answer key

### Section A

**A1.** Prompt caching reuses the KV cache (computation) for a byte-identical
token prefix. Semantic caching reuses a previously generated answer for a query
judged similar by embedding distance.
**A2.** `E[tokens] = (1 − α^(γ+1)) / (1 − α)`
**A3.** Long-context recall → rare tokens/entities → format adherence →
multi-step arithmetic → low-resource languages → probability calibration →
aggregate benchmark accuracy. Aggregate accuracy is **last**.
**A4.** Syntax (unparseable), shape (parses but violates schema), meaning
(schema-valid but wrong).
**A5.** Speculative decoding. Its accept/reject rule (`min(1, p/q)` with residual
sampling on rejection) yields exactly the target model's output distribution.
**A6.** Facts belong in retrievable storage (RAG/ICL); behaviour belongs in
weights (fine-tuning). Confusing them is the expensive mistake.
**A7.** A typed error — never a value, never a default-filled object, never
`null`.
**A8.** Any two: `tenant_id`, user permission scope, `model_id`,
`prompt_template_version`, retrieval corpus version, locale.
**A9.** 4-bit weights, 16-bit activations. It helps **decode** (and memory
footprint), not prefill.
**A10.** With no source value available and no null path, the only
schema-satisfying output is a fabricated one — and constrained decoding removes
the option of failing. Hallucination becomes the compliant behaviour.

### Section B

**B1.**
 (a) Input: 1.2M × 6,500 = 7.8e9 → 7,800 × $3 = $23,400. Output: 1.2M × 200 =
 2.4e8 → 240 × $15 = $3,600. **Total $27,000/day.**
 (b) Cached: 1.2M × 4,500 = 5.4e9 → 5,400 × $0.30 = $1,620. Uncached: 1.2M ×
 2,000 = 2.4e9 → 2,400 × $3 = $7,200. Plus $3,600 output. **Total $12,420/day.**
 (c) Prefix is broken → back to (a): **$27,000/day**. The line costs
 $14,580/day = **$5.32M/year.**
 (d) **Cache hit rate** (or cache-read vs. cache-write token counts) per
 endpoint. A prefix-hash constancy test in CI would have prevented it entirely.

**B2.**
 (a) γ=3: `(1−0.75⁴)/0.25 = (1−0.3164)/0.25 = 2.73`.
 γ=5: `(1−0.75⁶)/0.25 = (1−0.1780)/0.25 = 3.29`.
 γ=7: `(1−0.75⁸)/0.25 = (1−0.1001)/0.25 = 3.60`.
 (b) γ=3: `2.73/(1+0.24) = 2.20×`. γ=5: `3.29/(1+0.40) = 2.35×`.
 γ=7: `3.60/(1+0.56) = 2.31×`.
 (c) **γ=5.** `E[tokens]` saturates toward `1/(1−α) = 4` while the draft cost
 `γ·c` grows linearly without bound, so past the optimum you pay for drafts that
 will almost certainly be rejected.
 (d) At batch 96 the kernel is compute-bound, so speculation's extra verification
 FLOPs compete with real work and rejected drafts are pure waste — **the expected
 speedup collapses toward 1× or below, so don't enable it; use quantization or
 more batching instead.**

**B3.**
 (a) `4 + (16+16)/64 = 4.5 bits/weight`.
 (b) `40e9 × 4.5 / 8 = 22.5 GB`.
 (c) **2.5 GB / 12.5% understated.**
 (d) FP16: `80 GB / 2.4 TB/s = 33.3 ms` → **~30 tok/s**. 4-bit: `22.5 / 2400 =
 9.4 ms` → **~107 tok/s** (~3.6×, not the naive 4× — the metadata costs you).

### Section C

**C1.** **Cause:** the quantization damaged exactly the capabilities in Day 7's
damage profile — format adherence, long-context recall, and non-English — none
of which MMLU measures. Separately, the latency shortfall means the workload is
**prefill-dominated** (long PDFs/documents), so weight-only quantization was
never going to deliver the projected decode speedup.
**Metric:** per-category eval scores (format, long-context, per-language) against
the FP16 baseline; plus the TTFT/decode split to confirm the prefill share.
**Fix:** revert or move to INT8/FP8; build the per-category eval before retrying;
if latency is the real goal, attack prefill (context engineering, prefix caching,
chunked prefill), not decode.

**C2.** **Cause:** the cache key omits `tenant_id` / user scope, so a
personalized answer generated for one customer is being served to another whose
query embeds similarly. The threshold is irrelevant — 0.92 or 0.999, the bug is
that user-specific responses are cacheable at all across users.
**Metric:** an audit of the cache key composition; then shadow sampling (re-run
the model on a sample of hits and compare) to quantify the blast radius.
**Fix:** immediately disable the cache for any response that depends on user
data; namespace the key with tenant and permission scope; add the Day 20
isolation tests. This is a data-breach incident, not a tuning issue — treat it
as one, including disclosure obligations.

**C3.** **Cause:** wrong adaptation lever — they fine-tuned to teach **facts**.
The eval scores 91% because it was drawn from the same articles the model trained
on, measuring memorization of the training distribution rather than production
performance.
**Metric:** attribution/citation rate and factual freshness against the current
article set — neither of which the current eval measures. (Accepted alternative:
eval on held-out, post-training-cutoff articles.)
**Fix:** rebuild as RAG over the article corpus, so facts are current, citable,
permission-filterable, and deletable. Keep a light fine-tune only if a
*behavioural* gap (tone, answer structure) remains after that. The compliance
question has no answer in the fine-tuned design, which by itself decides it.

### Section D

**D1.** Grade on completeness and reasoning.

 (a) **Order:** constrained decoding → parse → schema validation → semantic
 validation → repair → fallback → terminal. One check per family:
 *referential* — `vendor_id` exists in the vendor master; *range/plausibility* —
 each `line_item.amount_cents` is a positive integer and the sum equals the stated
 total; *cross-field* — `currency` is consistent across all line items and matches
 the vendor's registered currency; *grounding* — the `po_reference` string appears
 verbatim in the extracted PDF text; *policy* — total above the auto-approval
 threshold routes to human review rather than payment.

 (b) Three of: amounts named `amount_cents` with `type: integer` (kills the
 dollars/cents class); `currency` as an enum, not a string (kills invented codes);
 a required `sufficient_information: boolean` or per-field `confidence` (kills
 forced fabrication when the PDF is unreadable); `maxItems` on line items (kills
 runaway generation); flat structure with line items as a single array rather
 than nested groups (kills deep-bracket failures).

 (c) Constrained generation → up to **2** repairs with specific error text and
 "change only what the errors require" → escalate to per-field extraction calls
 (each trivially easy) → deterministic regex extraction for what's recoverable →
 **terminal: a typed `Failed` result routed to a human review queue.** Never a
 default-filled record — the downstream effect is a real payment. A stop reason of
 `length` short-circuits to `OutputTruncated` and does not repair.

 (d) **No — or not yet.** Inputs of 2,000–40,000 tokens with a small structured
 output make this strongly prefill-dominated, so weight-only quantization buys
 little latency (Day 3). Meanwhile Day 7 §3.6 says format adherence, long-context
 recall, and rare-token precision degrade first — which is precisely this
 workload's entire surface (schema compliance, finding fields deep in a long
 document, exact vendor IDs). Wrong risk, wrong reward. If cost is the goal,
 route short/simple invoices to a smaller model (Day 13) and attack prefill.

 (e) The pipeline must **mark cached responses in the trace and exclude them from
 eval runs**, and the cache key must be namespaced by vendor/tenant and by
 document hash. But the stronger answer: for a feature that triggers payments,
 responses are document-specific and effectively never repeat — a semantic cache
 has no legitimate hit source here and should not be turned on at all. Recognizing
 that the question is a trap scores full marks.

---

## Scoring

| Score | Read this as |
| --- | --- |
| 90–100 | Strong. Continue to Day 11. |
| 70–89 | Solid. Re-skim the weakest day's §3. |
| 60–69 | Redo the weakest day's lab before Day 11. |
| < 60 | Redo Days 7 and 10 in full. Days 11–13 build directly on the output-contract material. |

Log the score in `lab-notebook.md`.
