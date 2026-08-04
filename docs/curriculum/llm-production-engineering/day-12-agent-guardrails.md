# Day 12 — Agent Guardrails: Loop Budgets, Tool Budgets, Termination Conditions

**Time:** ~3 h · **Prereqs:** Days 1, 2, 11 · **Lab needs:** .NET or Python

---

## 0. Warm-up review

1. List the six ordered layers of tool-argument validation. (Day 11)
2. Why must caller identity never come from a model argument? (Day 11)
3. What is the terminal state of a structured-output pipeline? (Day 10)
4. Which history policy is strongest for long-running tasks, and why? (Day 2)

---

## 1. The one-paragraph version

An agent is a loop: think, act, observe, repeat. Loops need exit conditions, and
**a language model is not a reliable exit condition.** Left to itself an agent
will retry a failing tool twenty times, oscillate between two plans, "verify" its
work forever, or declare success without doing anything. Guardrails are the
deterministic bounds that make the loop safe: **budgets** (iterations, tokens,
wall-clock, cost, per-tool calls) that are checked in code and cannot be argued
with; **progress detection** that notices when the loop is spinning rather than
advancing; and **explicit terminal states** — success, budget-exhausted,
blocked, escalated — every one of which must be handled by the caller. The
governing rule: **an agent must be able to fail, and failing must be a
first-class outcome rather than the absence of success.**

---

## 2. Why this bites you

**The $340 question.** A user asks an agent to "clean up the old branches in this
repo." A tool call fails with a permissions error. The agent retries. Fails.
Tries a slightly different command. Fails. Reasons about it at length. Tries
again. After 90 minutes and 1,100 tool calls it is still going, having burned
$340 in tokens on one question. There was no iteration cap.

**The confident nothing.** An agent is asked to "update the deployment docs to
reflect the new region." It reads three files, makes no edits, and returns "I've
updated the documentation to include the new region." The harness sees a normal
completion. Nobody checks whether a write actually happened. The completion
criterion was *the model saying it was done.*

**The oscillator.** Agent tries approach A, hits a wall, switches to B, hits a
wall, and — because the failure of A has by now scrolled out of the context
window (Day 2) — returns to A. Forever. Each cycle looks locally reasonable.
There is no global progress detector.

**The silent budget breach.** An agent hits its 20-iteration cap and returns
whatever partial state it had, formatted as a normal answer. Downstream, a
partially-migrated database is treated as migrated. The budget existed; the
**terminal state was mishandled**, which is worse than having no budget, because
now you believe you're protected.

Every one of these is a control-loop defect. None is fixable by prompting.

---

## 3. Core concepts

### 3.1 The five budgets

Every agent run needs all five. They fail in different ways, and any one alone
leaves a gap.

| Budget | Bounds | Typical | Catches |
| --- | --- | --- | --- |
| **Iterations** | Think-act-observe cycles | 10–30 | Infinite loops, oscillation |
| **Wall-clock** | Elapsed time | 60–600 s | Slow external tools, hangs |
| **Tokens** | Cumulative in + out | 100k–1M | Context bloat, verbose reasoning |
| **Cost** | Dollars | $0.10–$5.00 | Everything, in the unit that matters |
| **Per-tool calls** | Calls to a specific tool | 3–10 each | Retry storms on one failing tool |

Why all five: an agent can burn $50 in 8 iterations (huge contexts), or run 200
cheap iterations in 30 seconds, or sit for 10 minutes on one hung tool. Each
budget catches a shape the others miss.

**Per-tool budgets are the underrated one.** A global iteration cap of 25 doesn't
stop an agent from calling the same broken tool 25 times. Per-tool caps convert
that into a *specific, informative* failure — "search_orders failed 5 times, last
error: connection refused" — which is both cheaper and far more debuggable.

Two implementation rules:

- **Budgets live in the harness, not the prompt.** "Try to be efficient" is not a
  budget (Day 1). A budget is code that returns a terminal state.
- **Budgets are per-run, tracked in a run context, and checked before *every*
  tool call and every model call.** Not sampled, not approximate.

```csharp
internal sealed class RunBudget
{
    public int MaxIterations { get; init; } = 20;
    public TimeSpan MaxWallClock { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxTokens { get; init; } = 400_000;
    public decimal MaxCostUsd { get; init; } = 2.00m;
    public IReadOnlyDictionary<string, int> MaxCallsPerTool { get; init; }
        = new Dictionary<string, int> { ["*"] = 8 };

    // Returns null if OK, or the terminal reason if a budget is exhausted.
    public TerminalReason? Check(RunState s) =>
        s.Iteration    >= MaxIterations              ? TerminalReason.IterationBudget :
        s.Elapsed      >= MaxWallClock               ? TerminalReason.TimeBudget      :
        s.TotalTokens  >= MaxTokens                  ? TerminalReason.TokenBudget     :
        s.CostUsd      >= MaxCostUsd                 ? TerminalReason.CostBudget      :
        s.ExceededToolLimit(MaxCallsPerTool, out _)  ? TerminalReason.ToolBudget      :
        null;
}
```

### 3.2 Termination conditions

An agent should stop for one of these reasons, and the set must be **exhaustive
and explicit**:

| Terminal state | Meaning | Caller must |
| --- | --- | --- |
| `Completed` | Success criterion **verified by code** | Use the result |
| `CompletedPartial` | Some sub-goals verified, some not | Use verified parts; surface the rest |
| `BudgetExhausted` | Hit a budget mid-work | Surface honestly; offer continuation |
| `Blocked` | Needs information or permission the agent lacks | Ask the user |
| `NoProgress` | Loop detected, not advancing | Surface; do not retry identically |
| `Failed` | Unrecoverable error | Surface the error |
| `Escalated` | Requires human approval (Day 11) | Route to a human |

**The critical design point is `Completed`.** The model saying "I'm done" is a
*proposal*, not a terminal state. Whenever possible, completion is verified by
deterministic code:

```csharp
// Weak — the model is the judge of its own work
if (response.Contains("task complete")) return Completed;

// Strong — code verifies the claimed effect
var verdict = await _completion.VerifyAsync(goal, runState);
return verdict switch
{
    { AllSatisfied: true }  => Completed,
    { AnySatisfied: true }  => CompletedPartial(verdict.Unsatisfied),
    _                       => NoProgress,
};
```

What "verified by code" looks like in practice, by task type:

| Task | Verifiable completion criterion |
| --- | --- |
| Edit files | The files changed on disk; the diff is non-empty |
| Fix a bug | The failing test now passes and the others still do |
| Answer with citations | Every claim maps to a retrieved chunk (Day 15) |
| Fill a form | Every required field is populated and validates |
| Migrate records | Source count == destination count; checksums match |

Where completion genuinely can't be verified by code, use a separate
**checker model call** with a narrow, structured question ("Does this output
satisfy each of these criteria? Answer per criterion.") rather than letting the
working agent grade itself. A model evaluating its own work in the same context
that produced it is the least reliable configuration available (Day 16).

### 3.3 Progress detection

Budgets stop runaways eventually. Progress detection stops them *early*, and
gives a much better error.

**Signals that the loop is spinning:**

1. **Repeated identical tool calls.** Same tool, same canonical arguments, twice
   in a run — almost always a loop. (You already compute this hash for
   idempotency, Day 11 §3.4. Reuse it.)
2. **Cycling state.** A hash of the agent's working state repeats a value it held
   two or more steps ago.
3. **No state delta.** N consecutive iterations with no change to files, records,
   confirmed facts, or any tracked artifact.
4. **Repeated identical errors.** The same error code from the same tool three
   times.
5. **Rising token spend with a flat artifact count.** The strongest aggregate
   signal, and the easiest to chart.

```csharp
internal sealed class ProgressMonitor
{
    private readonly Dictionary<string, int> _callHashes = new();
    private readonly List<string> _stateHashes = new();
    private int _idleIterations;

    public NoProgressReason? Observe(ToolCall call, RunState after)
    {
        var callHash = IdempotencyKey.For(after.RunId, call);   // Day 11
        if (++_callHashes.GetValueOrDefault(callHash) is var n && n >= 2)
            return NoProgressReason.RepeatedIdenticalCall;

        var stateHash = after.ArtifactHash();
        if (_stateHashes.Contains(stateHash))
            return NoProgressReason.CyclicState;
        _stateHashes.Add(stateHash);

        _idleIterations = after.ArtifactsChanged ? 0 : _idleIterations + 1;
        return _idleIterations >= 3 ? NoProgressReason.NoStateChange : null;
    }
}
```

**When no progress is detected, do not simply stop.** Escalate along a ladder:

```
1. Inject the observation into context:
   "You have called search_orders with identical arguments 2 times and it has
    returned the same error each time. That approach is not working. Either try a
    materially different approach or report that you are blocked."
2. Still no progress after 2 more iterations → restrict the tool set (remove the
   failing tool) and re-prompt.
3. Still nothing → terminate as NoProgress with the full diagnostic trail.
```

Step 1 alone recovers a surprising fraction of runs. Agents get stuck because
they can't see their own pattern — the loop is visible in the harness's data
structures, not in the token stream. **Showing the agent its own trajectory is
one of the highest-leverage interventions in agent engineering.**

### 3.4 Degraded and partial outcomes

What the user sees when the agent doesn't finish is a product decision, and the
default (nothing, or a lie) is the worst option.

**Bad:** return partial work formatted as complete.
**Bad:** return "An error occurred."
**Good:**

> I updated 7 of the 12 configuration files. I stopped because I hit the time
> limit for a single run while working on `services/billing/config.yaml`, which
> has an unusual structure I couldn't parse.
>
> **Done:** api, worker, scheduler, gateway, auth, cache, search
> **Not done:** billing, reporting, analytics, legacy, sandbox
>
> [Continue from here] [Show me what changed] [Cancel and revert]

That requires three things from the harness: per-sub-goal tracking (not a single
boolean), a serializable run state that can be resumed, and terminal states the
UI knows how to render. Design them in from the start; retrofitting partial-
outcome UX onto a boolean-completion agent is a rewrite.

### 3.5 Budget policy: how to actually choose the numbers

Don't guess. Derive:

1. **Instrument first.** Run without hard caps in a controlled environment and
   record the distribution of iterations, tokens, and cost per run.
2. **Set the cap at roughly p99 of *successful* runs**, not the mean of all runs.
   Failed runs are exactly the ones that inflate the tail, so including them
   defeats the purpose.
3. **Check what's above the cap.** If 4% of runs exceed it and most of those were
   going to fail anyway, the cap is right. If half were going to succeed, raise
   it or decompose the task.
4. **Vary by task class.** "Answer a question" and "refactor a module" do not
   share a budget. One number for all agents is a smell.
5. **Make the cap a per-run parameter with a system maximum.** Trusted callers
   and interactive users can raise it; nobody can exceed the ceiling.
6. **Alert on the exhaustion *rate*, not on individual exhaustions.** A budget
   that fires 0% of the time is too loose to be protecting anything; one that
   fires 20% of the time is a product problem, not a safety net.

### 3.6 Composition and blast radius

Agents that spawn agents multiply everything.

- **Budgets must be hierarchical.** A child's consumption counts against the
  parent's remaining budget, or a 20-iteration parent spawning 20-iteration
  children is a 400-iteration run wearing a disguise.
- **Cap depth explicitly.** Depth ≤ 2 in most production systems. Unlimited
  recursion is not a feature.
- **Cap fan-out.** N children per parent, enforced.
- **Propagate cancellation.** Cancelling a parent must cancel every descendant.
  In .NET, one linked `CancellationTokenSource` per run, passed everywhere —
  note that `ILLMProvider.ChatAsync` in this repo already takes a `CancellationToken`
  (`Providers/ILLMProvider.cs:19`), so the plumbing exists; what's missing is the
  run-scoped source that ties it to a budget.
- **Aggregate cost to the run, not the call.** Otherwise cost attribution
  (Day 18) reports twelve cheap calls instead of one expensive workflow.

And the blast-radius rule that ties back to Day 11: **the tool set available to
an agent should narrow as autonomy rises.** A supervised single-step agent can
have write tools. A fully autonomous 30-iteration agent should mostly read, and
route writes through confirmation or a queue. Autonomy and privilege should trade
against each other, not compound.

---

## 4. Napkin math

### 4.1 What an unbounded loop costs

Agent, 5,000 runs/day. Normally 8 iterations at $0.04 each = $0.32/run =
$1,600/day. Suppose 2% of runs hit a failure mode that loops until a 10-minute
timeout at ~140 iterations:

```
runaway runs/day = 100
cost each        = 140 × $0.04 = $5.60
runaway cost     = $560/day
normal cost      = 4,900 × $0.32 = $1,568/day
total            = $2,128/day  → 36% overhead from 2% of runs
```

With a 20-iteration cap:

```
capped runs      = 100 × 20 × $0.04 = $80/day
total            = $1,648/day  → 5% overhead
```

**$480/day = $175k/year** from one integer. And the capped version also returns a
useful `BudgetExhausted` state in 15 seconds instead of a timeout in 10 minutes,
which is worth more than the money.

### 4.2 Sizing a cap from data

Measured iteration counts for successful runs: p50 = 6, p90 = 11, p99 = 19,
max = 34. Failed runs: p50 = 22, p99 = 200+.

Set the cap at **20** (just above successful p99). Consequences:

- ~1% of successful runs get cut off → they return `BudgetExhausted` with partial
  results and a continuation option. Acceptable.
- Nearly all failing runs are terminated at 20 instead of 200 — a **10×**
  reduction in the cost of failure.

The asymmetry is the point: **the cost of a slightly-too-tight cap is a resumable
partial result; the cost of no cap is unbounded.** When in doubt, cap tighter and
make continuation good.

### 4.3 Hierarchical budget blowout

Parent: 20 iterations, may spawn up to 5 children per iteration, each child 20
iterations. Without hierarchical accounting:

```
worst case = 20 × 5 × 20 = 2,000 agent iterations for one user request
at $0.04   = $80 for one question
```

With a shared budget pool of, say, 60 iterations across the whole tree, the worst
case is $2.40. **Same code, one shared counter.**

---

## 5. Failure modes

| Failure | Missing guardrail | Fix |
| --- | --- | --- |
| Agent runs for an hour | Wall-clock and iteration budgets | All five budgets |
| Same failing tool called 30× | Per-tool budget | Per-tool caps + progress detection |
| Oscillates between two plans | Cycle detection | State hashing |
| "I've updated the docs" (no edit) | Code-verified completion | Verify the effect, not the claim |
| Partial work presented as complete | Terminal-state handling | Explicit terminal states + UI |
| $80 for one question | Hierarchical budgets | Shared budget pool across the tree |
| Cancelled request keeps running | Cancellation not propagated | Linked cancellation token per run |
| Budget fires 30% of the time | Cap too tight, or task too big | Decompose the task; re-derive from p99 |
| Budget never fires | Cap too loose to protect anything | Re-derive from measured distribution |
| Agent grades its own work as correct | Self-evaluation in the same context | Separate checker call (Day 16) |

---

## 6. Lab (85 min)

### Part A — Build the guardrail layer (45 min)

Create `Agents/RunGuard.cs` in the Warden:

```csharp
namespace RevenantWorkspaceWarden.Agents;

internal enum TerminalReason
{
    Completed, CompletedPartial, IterationBudget, TimeBudget, TokenBudget,
    CostBudget, ToolBudget, NoProgress, Blocked, Failed, Escalated
}

internal sealed record RunOutcome(
    TerminalReason Reason,
    IReadOnlyList<string> SatisfiedGoals,
    IReadOnlyList<string> UnsatisfiedGoals,
    RunState ResumableState,
    string UserFacingSummary);

internal sealed class RunGuard
{
    // TODO(you):
    //  1. All five budgets, checked before every model call and every tool call.
    //  2. ProgressMonitor per §3.3, with the three-step escalation ladder.
    //  3. Code-verified completion: take a Func<RunState, Task<Verdict>> —
    //     never accept a model claim as Completed.
    //  4. Hierarchical: a child RunGuard draws from the parent's pool.
    //  5. One linked CancellationTokenSource; pass its token into
    //     ILLMProvider.ChatAsync so cancellation actually reaches the provider.
    //  6. Emit on every terminal: reason, iterations, tokens, cost, elapsed,
    //     per-tool counts. (Feeds Days 17-18.)
}
```

Tests that must pass:

1. A run whose model always says "done" but changes nothing terminates as
   `NoProgress`, **not** `Completed`.
2. A run calling the same tool with identical arguments twice triggers the
   escalation ladder, and the injected observation appears in the next prompt.
3. A parent with a 30-iteration pool spawning three children cannot exceed 30
   iterations total across the tree.
4. Cancelling the parent's token cancels in-flight child provider calls.
5. Every `TerminalReason` has a distinct `UserFacingSummary` — assert none are
   empty and none say "an error occurred."

Test 5 sounds trivial and isn't: it forces you to decide what the user sees for
each failure, which is the §3.4 design work.

### Part B — Derive your budgets from data (25 min)

```python
# lab-output/day12_budget_sizing.py
import json, statistics

runs = json.load(open("agent_runs.json"))   # [{"iterations":n,"cost":x,"success":bool}]
ok   = [r for r in runs if r["success"]]
bad  = [r for r in runs if not r["success"]]

def q(xs, p): return statistics.quantiles(xs, n=100)[p-1]

it_ok = [r["iterations"] for r in ok]
print(f"successful: p50={q(it_ok,50):.0f} p90={q(it_ok,90):.0f} p99={q(it_ok,99):.0f}")
for cap in range(5, 61, 5):
    cut   = sum(1 for r in ok  if r["iterations"] > cap) / max(len(ok), 1)
    saved = sum(max(0, r["iterations"] - cap) for r in bad)
    print(f"cap={cap:3d}: cuts {cut:5.1%} of successes, "
          f"saves {saved:6d} wasted iterations")
```

Generate synthetic run data if you don't have real data yet — lognormal for
successes (μ≈1.8, σ≈0.5), heavy-tailed for failures. Choose your cap, then write
in `lab-notebook.md`: the cap, the percentage of successful runs it cuts, and the
sentence you'd say to a PM defending it.

### Part C — Design the degraded outcome (15 min)

For one agent task you care about, write the exact user-facing text for all seven
terminal states. Real sentences, not placeholders.

Then check each against: does it tell the user **what was done**, **what wasn't**,
**why it stopped**, and **what they can do next**? Any state missing one of those
four is unfinished.

---

## 7. Quiz

**1.** Name the five budgets and give a failure that only that budget catches.

**2.** Why is per-tool budgeting necessary when you already have an iteration cap?

**3.** Why is "the model said it's done" not a terminal state? What replaces it?

**4.** Give three signals of no-progress and the escalation ladder they trigger.

**5.** Why does injecting the observation "you have called this tool identically
twice" often recover a stuck run?

**6.** Parent budget 25 iterations, up to 4 children per iteration, each child 25.
Worst-case total without hierarchical accounting? With a shared 40-iteration pool?

**7.** Successful runs: p50=5, p90=9, p99=16, max=41. Where do you set the cap and
why not at 41?

**8.** List the seven terminal states and what the caller must do for each.

**9.** Your budget fires on 25% of runs. Give two distinct diagnoses and the
different fix for each.

**10.** State the autonomy/privilege rule and explain the reasoning behind it.

---

## 8. Review hooks

- *An agent must be able to fail, and failing must be a first-class outcome.*
- *A language model is not a reliable exit condition.*
- *Budgets are code, not instructions. All five, checked every step.*
- *Completion is verified by code, not claimed by the model.*
- *Show the agent its own trajectory — it can't see the loop, you can.*
- *Budgets are hierarchical or they're decorative.*
- *As autonomy rises, privilege must fall.*

---

## 9. Answer key

**1.** **Iterations** — an infinite loop of cheap fast calls that never trips
time or cost. **Wall-clock** — a single hung external tool call. **Tokens** —
context bloat where few iterations each carry enormous contexts. **Cost** —
a mix of cheap and expensive model calls where no single count is alarming.
**Per-tool** — a retry storm against one broken dependency inside an otherwise
normal iteration count.

**2.** Because an iteration cap of 25 permits 25 calls to the same broken tool.
A per-tool cap converts that into an early, specific, debuggable failure ("tool X
failed 5 times, last error: connection refused") instead of a late, generic one,
and it stops the waste ~5× sooner.

**3.** Because the model's claim is a token prediction, not an observation of the
world — it can and does report completing work it never performed. It's replaced
by **code-verified completion**: check the actual effect (files changed, tests
passing, records matching, required fields populated), and where that's
impossible, a separate structured checker call — never self-evaluation inside the
working context.

**4.** Any three: repeated identical tool calls (same canonical argument hash);
cyclic state (an artifact hash repeating a previous value); N consecutive
iterations with no artifact change; the same error code repeating from the same
tool; rising token spend with flat artifact count. **Ladder:** (1) inject the
observation into context and let the agent react; (2) if still stuck, remove the
failing tool from the available set and re-prompt; (3) terminate as `NoProgress`
with the full diagnostic trail.

**5.** Because the loop exists in the harness's data structures, not in the token
stream — the agent's context may not contain enough history (or may have compacted
it away, Day 2) for the repetition to be visible to it. Making the pattern
explicit gives the model information it genuinely lacked, and it will usually
change strategy once it can see it.

**6.** Without hierarchical accounting: `25 × 4 × 25 = 2,500` iterations. With a
shared 40-iteration pool: **40**, because every child's consumption decrements the
same counter the parent draws from.

**7.** Set it around **16–18**, i.e. at or just above the p99 of *successful*
runs. Not 41, because the max is a single outlier that costs you nothing to cut
(it returns a resumable partial result) while a cap of 41 lets every failing run
burn 41 iterations — you'd more than double the cost of failure to rescue well
under 1% of successes.

**8.** `Completed` — use the result. `CompletedPartial` — use verified parts and
surface the rest. `BudgetExhausted` — surface honestly, offer continuation.
`Blocked` — ask the user for the missing information or permission. `NoProgress`
— surface; do not retry identically. `Failed` — surface the error.
`Escalated` — route to a human for approval.

**9.** (i) **The cap is too tight for the task** — the work genuinely needs more
steps; fix by decomposing the task into smaller agent runs with checkpoints, or
raising the cap after re-deriving from the successful-run distribution.
(ii) **The agent is failing 25% of the time and the budget is just where you find
out** — the budget is doing its job and the real defect is upstream (bad tool
contracts, missing information, wrong task decomposition); fix by diagnosing the
failures, not by raising the cap. **Distinguish them** by checking whether runs
that hit the cap were making progress at the time.

**10.** As an agent's autonomy increases — more iterations, less human review per
step — the set of tools available to it should narrow toward read-only. Reasoning:
risk is roughly (probability of a wrong action) × (blast radius) × (number of
opportunities). Autonomy multiplies opportunities and removes the human check on
each one, so the only remaining lever is blast radius. A supervised single-step
agent can safely hold write tools because a human sees each action; a 30-iteration
unsupervised agent should mostly read, routing writes through confirmation gates
or an approval queue (Day 11 §3.5).

---

## 10. Going deeper

- **"ReAct: Synergizing Reasoning and Acting in Language Models"** — Yao, Zhao,
  Yu, Du, Shafran, Narasimhan, Cao (arXiv 2210.03629). The loop everyone is
  bounding.
- **"Reflexion: Language Agents with Verbal Reinforcement Learning"** — Shinn,
  Cassano, Berman, Gopinath, Narasimhan, Yao (arXiv 2303.11366). Self-critique —
  read it noting where the "agent judges itself" weakness applies.
- **"Building Effective Agents"** — Anthropic engineering blog, 2024. Its central
  advice is to prefer bounded workflows over open-ended autonomy, which is this
  lesson stated as an architecture principle.
- **Release It!, 2nd ed.** — Nygard. Circuit breakers, bulkheads, timeouts, and
  fail-fast. Agent budgets are these patterns rediscovered; the chapter on stability
  antipatterns reads like a list of agent incidents.
- **"τ-bench: A Benchmark for Tool-Agent-User Interaction"** — Yao et al.
  (arXiv 2406.12045). Measures whether agents follow rules and terminate
  correctly, not just whether they can complete tasks.

---

**Next:** [Day 13 — Model Routing and Graceful Fallback](day-13-routing-and-fallback.md).
The agent is bounded. Now handle the model being slow, expensive, or gone.
