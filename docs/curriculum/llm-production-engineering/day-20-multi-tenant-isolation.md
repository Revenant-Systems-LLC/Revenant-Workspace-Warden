# Day 20 — Multi-Tenant Isolation, Cache Safety, and Cross-User Context Contamination

**Time:** ~3 h · **Prereqs:** Days 2, 6, 14, 19 · **Lab needs:** .NET, Python

---

## 0. Warm-up review

1. Name the four data leakage channels. (Day 19)
2. What must a semantic cache key contain besides the embedding? (Day 6)
3. Why does permission filtering belong at retrieval? (Day 14)
4. What does the context-assembly `verify()` stage catch? (Day 2)

---

## 1. The one-paragraph version

Multi-tenant LLM systems leak between tenants through a small, enumerable set of
channels — **caches, shared context objects, retrieval indexes, model weights,
logs and traces, and shared agent state** — and every one of them fails silently,
producing a correct-looking answer built from someone else's data. The defense is
not vigilance; it's making tenant scope a **structural property of every key,
every query, and every stored object**, enforced by types and tests rather than
by discipline. The diagnostic question that separates teams who have thought about
this from teams who haven't: *"name the specific channel through which tenant A's
data could reach tenant B in your system."* Every system has an answer. "It can't"
is never it, and a team that gives that answer has simply not looked.

---

## 2. Why this bites you

**The cache key that forgot.** A semantic cache keyed on the query embedding
alone. Two customers ask "what's my current balance?" The second gets the first
one's number. It's a data breach delivered by a performance optimization, and it
looks like a normal successful response. (Day 6 §2; here we build the fix.)

**The static that wasn't.** A developer makes the conversation history object
`static` for convenience during a refactor. In a WPF desktop app that's mostly
harmless; in the same codebase deployed as a service it becomes a cross-user
context bleed under concurrency. The bug is one keyword, and the symptom is
"sometimes the assistant mentions things I never said."

**The index without a filter.** A shared vector index holds every tenant's
documents, with `tenant_id` in the metadata, and the filter is applied *after*
retrieval to drop foreign chunks. But the top-5 the model saw included two
foreign chunks, and the answer summarizes them. The filter removed the citations,
not the disclosure. (Day 19 §3.4, channel 2.)

**The trace store.** A support engineer debugging tenant A's complaint searches
traces by keyword and reads tenant B's prompts, which contain patient data. No
attack, no bug — the observability system (Day 17) was built without tenant
scoping, and now it's the largest unrestricted store of customer content in the
company.

---

## 3. Core concepts

### 3.1 The contamination channel inventory

Enumerate these for your system. Each row is a question you must be able to
answer with a specific mechanism.

| # | Channel | Failure | Control |
| --- | --- | --- | --- |
| 1 | **Response cache** (exact or semantic) | Key omits tenant/user/permissions | Tenant in the key, structurally |
| 2 | **Prompt/prefix cache** | Tenant data placed in the shared prefix | Only tenant-invariant content before the cache boundary |
| 3 | **Embedding cache** | Shared cache of query→vector | Usually safe (vectors aren't secrets) — but *results* caches aren't |
| 4 | **Retrieval index** | No filter, or post-filtering | Filter in the query; consider per-tenant namespaces |
| 5 | **Conversation/session state** | Shared or static objects | Per-request scoping; no mutable statics |
| 6 | **Model weights** | Fine-tuned on tenant data | Don't; or per-tenant adapters |
| 7 | **Logs and traces** | Unrestricted content store | Tenant-scoped access control |
| 8 | **Agent working state** | Reused run context across requests | New state object per run |
| 9 | **Tool credentials** | Service account spanning tenants | Request-scoped credentials (Day 19) |
| 10 | **Error messages** | Leaks entity names from another tenant | Sanitize; never echo raw upstream errors |
| 11 | **Rate limiters / counters** | Keyed globally, leaks activity levels | Minor, but scope them anyway |
| 12 | **Vector index statistics** | Global IDF/BM25 stats reveal corpus content | Rarely material; know it exists |

Rows 1, 4, 5, and 7 cause the overwhelming majority of real incidents. Row 2 is
the subtle one people get wrong when optimizing cost.

### 3.2 Cache safety: the key is the boundary

Every cache in the system needs a key that makes cross-tenant reuse
**impossible**, not unlikely.

```csharp
internal readonly record struct CacheScope(
    string TenantId,
    string PermissionScopeHash,   // hash of the caller's effective permissions
    string ModelId,
    string PromptTemplateVersion,
    string CorpusVersion,
    string Locale,
    string Environment);

internal static string Key(CacheScope scope, string payloadHash) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{scope.TenantId}|{scope.PermissionScopeHash}|{scope.ModelId}|" +
        $"{scope.PromptTemplateVersion}|{scope.CorpusVersion}|{scope.Locale}|" +
        $"{scope.Environment}|{payloadHash}")));
```

The design rule that makes this reliable: **make the unscoped call
unrepresentable.** If your cache API is `Get(string key)`, someone will eventually
pass an unscoped key. If it's `Get(CacheScope scope, string payloadHash)`, they
can't — the type system enforces what a code review would have to catch by
attention.

**`PermissionScopeHash` is the row people miss.** Two users in the *same* tenant
with different access levels must not share a cached answer. Hash the caller's
effective permission set (roles, groups, document ACLs that could affect
retrieval) into the key. If permissions are too dynamic to hash cheaply, that's a
signal that this response shouldn't be cached across users at all.

### 3.3 Prefix cache safety — the cost/isolation tension

Day 6 said: put stable content first to maximize prefix cache hits. Today's
constraint: **the shared prefix must contain nothing tenant-specific.**

These are compatible, but only if you're deliberate:

```
┌────────────────────────────────────────────┐
│ SHARED PREFIX — identical for ALL tenants  │  ← cacheable globally
│  system prompt, tool schemas, few-shot     │
├────────────────────────────────────────────┤  ═══ global cache boundary ═══
│ TENANT PREFIX — same for one tenant        │  ← cacheable per tenant
│  tenant policy, custom instructions,       │
│  tenant-specific tool config               │
├────────────────────────────────────────────┤  ═══ tenant cache boundary ═══
│ REQUEST — retrieved docs, history, query   │  ← not cacheable
└────────────────────────────────────────────┘
```

Two cache boundaries, two scopes. The failure mode is putting a tenant's custom
instructions in the *global* block "because it's stable" — stable is not the same
as tenant-invariant, and conflating them is how a tenant's business rules end up
in another tenant's prompt.

**The test:** assemble the global prefix for two different tenants and assert the
bytes are identical. That's the Day 6 lab's prefix-hash test, with a second
argument. It should be in CI.

### 3.4 Retrieval isolation: three architectures

| Approach | Isolation | Cost | Use when |
| --- | --- | --- | --- |
| **Shared index + metadata filter** | Logical only — one bug away | Lowest | Many small tenants, low sensitivity |
| **Namespace per tenant** | Strong — separate partitions | Medium | The common good default |
| **Separate index/DB per tenant** | Strongest — physical | Highest | Regulated data, enterprise contracts |

If you use a shared index:

- **The filter is part of the query, always.** Not applied afterward. Not
  optional. Wrap the client so an unfiltered query is impossible to express —
  same principle as §3.2.
- **`tenant_id` comes from the authenticated session** (Day 11, Day 19), never
  from a request parameter or model output.
- **Test the negative case.** Not "tenant A can find their document" but "tenant
  A *cannot* find tenant B's document" — including with queries containing exact
  text from B's document, which is the case that catches a broken filter.
- **Watch out for global statistics.** BM25 scoring uses corpus-wide term
  frequencies; in extreme cases these leak information about other tenants'
  content. Rarely material, but know it's there before someone asks.

### 3.5 Context contamination in the process

The subtlest class, because it's an ordinary concurrency bug with an unusual
consequence.

**Sources:**

- **Mutable statics and singletons.** A `static List<Message>` shared across
  requests. In this repo, `ChatSessionManager` is worth auditing for exactly this
  — a desktop app has one user, so the pattern is harmless; the moment the same
  code serves multiple users, it's a leak.
- **Object pooling without reset.** A pooled context builder retaining the
  previous request's chunks.
- **AsyncLocal misuse.** Context flowing into a continuation that outlives the
  request.
- **Long-lived clients holding state.** A provider instance that caches "last
  system prompt" as an optimization.
- **Background jobs inheriting ambient context.** A fire-and-forget task capturing
  the request's tenant scope, running after the request ends, under a different
  tenant's load.

**Defenses:**

1. **Immutable context objects.** A `record` you can't mutate can't be
   contaminated. Build a new one per request.
2. **No mutable static state, enforced.** Add an analyzer rule or a test that
   scans for mutable statics in the request path — this is mechanizable.
3. **Explicit scope, not ambient.** Passing `CostContext`/`CallerContext` as a
   parameter (Days 18–19) is more verbose than an ambient context, and it cannot
   silently leak across an async boundary.
4. **Assert in `verify()`.** The Day 2 context-assembly verification step should
   assert that every content block's tenant matches the request's tenant. Cheap,
   and it catches every variant of this class at the last possible moment.

```csharp
// In ContextBudget.Verify — the assertion that catches channel 5, 8, and part of 4
foreach (var block in blocks)
    if (block.TenantId is not null && block.TenantId != request.TenantId)
        throw new TenantIsolationViolation(
            $"Block {block.SourceId} belongs to tenant {block.TenantId}, " +
            $"request is tenant {request.TenantId}");
```

That's eight lines and it converts a silent breach into a loud exception. It
should exist in every multi-tenant LLM system and almost never does.

### 3.6 Observability isolation

Day 17 built a system that stores prompts and responses. That store is now a
copy of your customers' data, and it needs the same controls as the primary
store:

- **Tenant-scope trace access.** Support engineers see the tenant they're
  assigned to. Cross-tenant search requires elevated, audited access.
- **Redact by default** in the general-access tier; full content behind a
  separate, logged permission.
- **Short retention for content, long for metadata** (Day 17 §3.3).
- **Audit reads.** Who read whose traces, and when. This is the control that
  makes the rest credible.
- **Include traces in deletion requests.** A GDPR erasure that clears the primary
  database and leaves the trace store is not an erasure.

That last point is routinely missed and is a compliance problem with a simple
fix: make the trace store part of the deletion pipeline from the start.

### 3.7 Testing isolation

Isolation bugs are silent, so they must be caught by tests that assert the
*negative*. Build these as a permanent suite:

| Test | Asserts |
| --- | --- |
| Cross-tenant cache probe | Tenant A's request, then B's identical request, must produce distinct cache keys and a cache miss |
| Cross-tenant retrieval probe | Query using verbatim text from B's document, as A, returns nothing from B |
| Permission-tier probe | Two users, same tenant, different roles — different cache keys, different retrievable sets |
| Global prefix invariance | Global prefix bytes identical across tenants |
| Static state scan | No mutable statics reachable from the request path |
| Concurrency soak | N concurrent requests from M tenants; assert every response's context contained only its own tenant's blocks |
| Trace access | A user scoped to tenant A cannot read tenant B's traces |
| Deletion completeness | After an erasure request, no trace, cache, or index entry for that subject remains |

**The concurrency soak is the one that finds real bugs.** Single-threaded tests
never reproduce context bleed. Run 200 concurrent requests across 20 tenants with
distinctive per-tenant marker strings, and assert no response contains a marker
from another tenant. Run it in CI. It's the highest-yield test in this lesson.

---

## 4. Napkin math

### 4.1 Cache hit rate vs. isolation

Adding tenant scope to a response cache reduces hit rate, and people use this to
argue against it. Price it.

800 tenants, 2M requests/day, 30% global semantic hit rate.

```
Unscoped:  hit rate 30%  → saves 600,000 × $0.006 = $3,600/day
Scoped:    hit rate ~9%  → saves 180,000 × $0.006 = $1,080/day
Difference:                                          $2,520/day = $920k/year
```

Now the other side: one cross-tenant disclosure incident. Breach notification,
regulatory investigation, contractual penalties, remediation, churn, and the
security audit of everything you own. Published breach-cost studies put mid-size
incidents comfortably into seven figures, and enterprise contracts routinely carry
penalties that dwarf the operational saving.

**The $920k is not a saving; it's a premium you were charging yourself for
accepting an unbounded liability.** Note also that prompt caching (Day 6) is
unaffected by tenant scoping when the prefix is genuinely tenant-invariant — you
keep most of the cost benefit without the risk. The tradeoff is far less painful
than it first appears, which is the actual answer to the objection.

### 4.2 Isolation architecture cost

800 tenants, retrieval index.

```
Shared + filter:      1 index,    ~$400/month,  logical isolation
Namespaced:           1 cluster,  ~$550/month,  strong logical isolation
Per-tenant index:     800 indexes, ~$9,600/month + operational overhead
```

Namespacing costs ~$150/month more than a shared index and removes the entire
class of "the filter wasn't applied" bug. **That's the obvious buy.** Per-tenant
physical separation is 24× the cost and worth it only when a contract or
regulation requires it — which, for some customers, it will, so know your number
before the enterprise deal asks.

### 4.3 The soak test's value

A context-bleed bug reaching production:

```
- Time to detection: typically weeks (silent, correct-looking answers)
- Requests affected in that window: unknown and unbounded
- Disclosure obligation: yes, and you cannot scope it — you don't know who saw what
```

The soak test costs an hour to write and a couple of minutes per CI run. It is
one of the few tests where the expected value calculation isn't close.

---

## 5. Failure modes

| Failure | Channel | Fix |
| --- | --- | --- |
| User sees another tenant's answer | Response cache | Tenant + permission hash in the key |
| Same-tenant user sees privileged answer | Response cache | `PermissionScopeHash` in the key |
| Tenant's custom rules appear elsewhere | Prefix cache | Two cache boundaries (§3.3) |
| Answer summarizes a foreign document | Retrieval | Filter in the query, not after |
| "Assistant mentioned things I never said" | Context/session state | Immutable per-request context; soak test |
| Background job runs under the wrong tenant | Ambient context capture | Explicit scope parameters |
| Support engineer reads foreign prompts | Traces | Tenant-scoped trace access + audit |
| Erasure request leaves data in traces | Traces | Include the trace store in deletion |
| Fine-tuned model reveals another tenant's data | Weights | Don't train on tenant data; per-tenant adapters |
| Error message names a foreign entity | Error handling | Sanitize; never echo upstream errors |

---

## 6. Lab (85 min)

### Part A — Inventory your channels (20 min)

For this repo (imagining it deployed as a multi-tenant service rather than a
desktop app), fill in the §3.1 table completely. For each of the twelve channels:
does it exist here, is it currently scoped, and where in the code?

Specific things to look at:
- `ChatSessionManager.cs` — is session state per-user, and could it become static?
- `Providers/SecretsManager.cs` — is the key store per-tenant or global?
- Any caching added during Days 6 and 13 labs.
- The retrieval index from the Day 14 lab.
- The telemetry from Day 17 — who can read the traces?

Write the honest answer to the diagnostic question in `lab-notebook.md`:

> "Tenant A's data could reach tenant B through ______, because ______. The
> control that prevents it is ______, enforced at ______."

Fill it in for the **three** most plausible channels. If any "enforced at" is
blank, that's your next task.

### Part B — Make the unscoped call unrepresentable (35 min)

```csharp
namespace RevenantWorkspaceWarden.Isolation;

internal readonly record struct TenantId(string Value)
{
    public static TenantId FromSession(CallerContext ctx) => new(ctx.TenantId);
    // Deliberately no parse-from-string constructor for request input:
    // tenant identity comes from the authenticated session ONLY (Day 19).
}

internal sealed class ScopedCache<TValue>
{
    private readonly IDictionary<string, TValue> _store;

    // Note the signature: there is no Get(string) overload. By construction,
    // a caller cannot look up a value without providing a scope.
    public bool TryGet(CacheScope scope, string payloadHash, out TValue value) { /* ... */ }
    public void  Set(CacheScope scope, string payloadHash, TValue value)       { /* ... */ }
}

internal sealed class ScopedRetriever
{
    // Same principle: no Search(query) overload. The filter is not optional.
    public Task<IReadOnlyList<Chunk>> SearchAsync(TenantId tenant,
                                                  PermissionScope perms,
                                                  string query, int k);
}
```

Implement both, plus the §3.5 `Verify` assertion in `ContextBudget`.

Then write the negative tests from §3.7:

1. Two tenants, identical query → distinct cache keys, second is a miss.
2. Two users, same tenant, different permission scopes → distinct keys.
3. Search as tenant A using verbatim text from tenant B's document → zero results
   from B.
4. Global prefix bytes identical across two tenants.
5. Placing a block with a foreign `TenantId` into the context throws.

### Part C — The concurrency soak (30 min)

```python
# lab-output/day20_soak.py
import asyncio, httpx, random, re

TENANTS = {f"t{i:02d}": f"MARKER-{i:02d}-XYZZY" for i in range(20)}

async def one(client, tenant, marker):
    """Seed the tenant's corpus/session with its unique marker, then ask a
    question whose answer would surface any marker present in context."""
    r = await client.post("/chat", json={
        "tenant": tenant,
        "message": "Summarize everything you know from my documents.",
    }, timeout=120)
    body = r.json()["content"]
    foreign = [m for t, m in TENANTS.items() if t != tenant and m in body]
    return {"tenant": tenant, "own": marker in body, "foreign": foreign}

async def main(n=200):
    async with httpx.AsyncClient(base_url="http://localhost:5000") as c:
        tasks = []
        for _ in range(n):
            t = random.choice(list(TENANTS))
            tasks.append(one(c, t, TENANTS[t]))
        results = await asyncio.gather(*tasks)
    leaks = [r for r in results if r["foreign"]]
    print(f"requests={len(results)} leaks={len(leaks)}")
    for l in leaks[:10]:
        print(f"  LEAK: tenant={l['tenant']} saw {l['foreign']}")
    assert not leaks, "CROSS-TENANT CONTAMINATION DETECTED"

asyncio.run(main())
```

Adapt to whatever surface you can drive. Then:

1. Run it against your correct implementation — expect zero leaks.
2. **Deliberately introduce the bug**: make the session/context object `static`,
   or remove the tenant from the cache key. Re-run.
3. Confirm the soak test catches it and a single-threaded test does not.

That contrast is the lesson: **isolation bugs are invisible to sequential tests by
construction.** Write it down.

---

## 7. Quiz

**1.** Name six contamination channels. Which four cause most real incidents?

**2.** Why must `PermissionScopeHash` be in a cache key even within a single
tenant?

**3.** Explain the two-boundary prefix cache design. What's the failure it
prevents?

**4.** Compare the three retrieval isolation architectures on isolation strength,
cost, and when to choose each.

**5.** Why must the retrieval filter be part of the query rather than applied to
results?

**6.** Give four sources of in-process context contamination and the defense for
each.

**7.** Why is a concurrency soak test necessary when you already have unit tests
for scoping?

**8.** Compute: 1,200 tenants, 3M requests/day, unscoped cache hit rate 28%,
scoped 8%, saving $0.005/hit. Annual cost of scoping? How do you argue for it?

**9.** Why does the trace store need the same controls as the primary data store?
Name two specific requirements.

**10.** State the diagnostic question of this lesson, and explain why "it can't
happen" is always the wrong answer.

---

## 8. Review hooks

- *Every system has a channel. "It can't" means you haven't looked.*
- *Make the unscoped call unrepresentable. Types, not discipline.*
- *Tenant identity comes from the session. Never from a parameter or the model.*
- *Two cache boundaries: global-invariant, then tenant-invariant.*
- *Filter in the query. Post-filtering suppresses citations, not disclosure.*
- *Test the negative: assert A cannot see B.*
- *Isolation bugs are invisible to sequential tests. Soak concurrently.*

---

## 9. Answer key

**1.** Any six: response cache, prompt/prefix cache, retrieval index,
conversation/session state, model weights, logs and traces, agent working state,
tool credentials, error messages, rate limiters, index statistics, embedding
result caches. **Most real incidents:** response cache, retrieval index,
session/context state, and logs/traces.

**2.** Because two users in the same tenant can have different access — different
roles, groups, or document-level ACLs — so an answer computed with one user's
retrievable set may contain material the other cannot see. Keying only by tenant
lets a low-privilege user receive a cached answer built from documents their
colleague could read and they cannot.

**3.** A globally-shared prefix containing only tenant-invariant content (system
prompt, tool schemas, few-shot), then a per-tenant prefix containing tenant
policy and custom instructions, then the uncacheable request content — with a
cache boundary after each. It prevents putting tenant-specific content in the
globally-shared block "because it's stable": stable is not the same as
tenant-invariant, and conflating them places one tenant's business rules in
another tenant's prompt.

**4.** **Shared index + metadata filter:** logical isolation only, one bug from a
leak; cheapest; acceptable for many small low-sensitivity tenants.
**Namespace per tenant:** strong logical isolation via separate partitions;
modestly more expensive; the sensible default. **Separate index per tenant:**
physical isolation, strongest; substantially more expensive and operationally
heavier; required for regulated data or contractual isolation guarantees.

**5.** Because post-filtering removes foreign chunks from the *result list* after
they have already been retrieved and — critically — after they may already have
entered the model's context. The model can then paraphrase, summarize, or infer
from them, so the disclosure occurs even though the citation is suppressed
(Day 19 §3.4). Filtering in the query means the foreign chunks were never
candidates.

**6.** Any four: **mutable statics/singletons** holding request state → no mutable
static state in the request path, enforced by an analyzer or test. **Object
pooling without reset** → immutable per-request context objects. **AsyncLocal
flowing into continuations that outlive the request** → explicit scope parameters
instead of ambient context. **Long-lived clients caching state** → stateless
clients; pass everything per call. **Background jobs capturing ambient context**
→ pass an explicit scope to the job.

**7.** Because context contamination is a *concurrency* bug: shared mutable state,
pooling, and async-context leakage only manifest when multiple requests are in
flight simultaneously. A sequential unit test exercises one request at a time and
will pass against a `static` session object indefinitely. Only concurrent load
with per-tenant marker strings reproduces the condition.

**8.** `3,000,000 × (0.28 − 0.08) = 600,000` fewer hits/day × $0.005 =
**$3,000/day = $1.095M/year**. **The argument:** that figure is not a saving
foregone, it's the premium you were implicitly charging yourself for carrying
unbounded cross-tenant disclosure liability — a single incident across 1,200
tenants means breach notification, regulatory exposure, contractual penalties, and
churn that comfortably exceeds it. Additionally, prefix caching is unaffected by
tenant scoping when the prefix is genuinely tenant-invariant, so most of the cost
benefit survives; the real tradeoff is much smaller than the headline number.

**9.** Because it contains verbatim prompts and responses — a full copy of
customer content, often the largest unrestricted one in the company. **Two
requirements:** (i) tenant-scoped access control with audited cross-tenant reads,
so support engineers cannot browse other customers' data; (ii) inclusion in the
data-deletion pipeline, so an erasure request that clears the primary store also
clears traces — otherwise the erasure is incomplete and the compliance claim is
false. (Also acceptable: redaction by default, short content retention.)

**10.** **"Name the specific channel through which tenant A's data could reach
tenant B in your system."** "It can't happen" is always wrong because every
multi-tenant system shares *something* — a cache, an index, a process, a log
store, a model — and sharing is exactly what creates channels. The answer is
never "no channel exists"; it's "channel X exists and is bounded by control Y at
location Z." A team that answers "it can't" has not enumerated their channels,
which means they also have no tests asserting the negative.

---

## 10. Going deeper

- **"Multi-tenant SaaS architecture patterns"** — AWS and Azure both publish
  detailed guidance on silo/pool/bridge isolation models. The vocabulary transfers
  directly to vector indexes and cache tiers.
- **"Designing Data-Intensive Applications"** — Kleppmann, ch. 5–6 on replication
  and partitioning. Namespace-per-tenant is partitioning, with the same
  operational consequences.
- **OWASP Top 10 for LLM Applications** — LLM02 (sensitive information
  disclosure) and LLM06 map onto this lesson; use as a coverage checklist.
- **Your vector database's multi-tenancy documentation** — the namespace and
  filtering semantics differ substantially between products, and the differences
  determine which of §3.4's architectures you can actually implement.
- **Simon Willison's "lethal trifecta"** (again, from Day 19) — the isolation
  angle: multi-tenancy makes "private data" mean *someone else's* private data,
  which raises the stakes of every other channel.

---

**Checkpoint:** take **[Checkpoint 4](checkpoint-4.md)** after Day 21.

**Next:** [Day 21 — Full-Stack Tradeoffs](day-21-full-stack-tradeoffs.md). Every
lever is now on the table. Time to choose between them with a number.
