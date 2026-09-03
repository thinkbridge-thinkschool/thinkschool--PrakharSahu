# Day 21 — what changed, where, and why

Day 21 is a small delta on Day 20. This file lists **only the code I added or changed**, the
exact file and method it lives in, what the thing being used actually *is*, and why it was done
that way.

Nothing from Day 17–20 was removed. Day 20 is untouched.

---

## Definitions first

**HybridCache** (.NET 9+, `Microsoft.Extensions.Caching.Hybrid`) is a two-level cache with one
API. **L1** is in-memory inside the process; **L2** is any registered `IDistributedCache` —
Redis here. A read checks L1, then L2, then your factory; a fill populates both on the way back.
It replaces the hand-rolled "check memory, check Redis, take a lock, re-check, query, write both
back" that almost everybody writes slightly wrong.

**A cache stampede** (thundering herd) is what happens when a hot key expires while traffic is
high: every in-flight request misses at the same instant, and each one runs the expensive query
— N identical queries for one value. It is worst exactly when the system is busiest, and it is
self-reinforcing, because those duplicate queries slow the database, which widens the miss
window, which admits more duplicates.

**Stampede protection** is HybridCache coalescing concurrent callers of the *same key* onto a
**single factory invocation**. The first caller runs the factory; the rest await its result.
Measured below: 200 simultaneous requests on a cold key produced **1** database query.

---

## 1. Packages added

**File:** `backend/QuotesApi.csproj`

```xml
<PackageReference Include="Microsoft.Extensions.Caching.Hybrid" Version="10.9.0" />
<PackageReference Include="Microsoft.Extensions.Caching.StackExchangeRedis" Version="10.0.11" />
```

**Why:** the first is HybridCache itself. The second registers an `IDistributedCache` backed by
Redis — HybridCache **discovers L2 through the container**, so there is no explicit "use Redis"
call anywhere. Worth knowing, because it also means a stray `IDistributedCache` registration
silently becomes your L2.

---

## 2. New file — the cached read (this is the core of the day)

**File:** `backend/Caching/CachedQuoteReader.cs`
**Method:** `CachedQuoteReader.GetAllAsync`

```csharp
public async Task<IReadOnlyList<CachedQuote>> GetAllAsync(CancellationToken cancellationToken)
{
    _metrics.RecordRead();

    // The load test's "before" arm — same binary, same endpoint, only this flag differs.
    if (!_options.Enabled)
    {
        return await LoadFromDatabaseAsync(cancellationToken);
    }

    return await _cache.GetOrCreateAsync(
        ListCacheKey,                       // "quotes:all:v1"
        state: this,
        factory: static (reader, ct) =>
            new ValueTask<IReadOnlyList<CachedQuote>>(reader.LoadFromDatabaseAsync(ct)),
        options: new HybridCacheEntryOptions
        {
            Expiration           = _options.Expiration,       // L2 (Redis)
            LocalCacheExpiration = _options.LocalExpiration   // L1 (in-memory), shorter
        },
        tags: new[] { QuotesTag },
        cancellationToken: cancellationToken);
}
```

**Why each part:**

- **`GetOrCreateAsync` *is* the stampede protection.** Nothing in this class implements
  coalescing — it is a property of this call. That is the entire reason to use HybridCache
  instead of `IDistributedCache` directly, where you would have to write the single-flight lock
  yourself.
- **`state: this` + a `static` factory.** Passing state explicitly means the delegate captures
  nothing, so it can be `static` and allocated once rather than a fresh closure per request. On
  the hot path this class exists to make fast, that matters.
- **`LocalCacheExpiration` shorter than `Expiration`.** L1 is per-process and *cannot be
  invalidated from another instance*, so it is the worst-case cross-replica staleness window.
  Long L1 = fast and stale; short L1 = fresh and more Redis round trips.
- **`v1` in the key.** A manual schema version. Change the cached shape without bumping it and a
  rolling deploy has new code deserialising old Redis entries.

**Method:** `CachedQuoteReader.LoadFromDatabaseAsync` — the expensive path. It calls
`_metrics.RecordFactoryInvocation()`, which is how misses are counted: **the factory only runs
on a miss**, so `misses == factory invocations` and `hits == reads − misses`.

**Method:** `CachedQuoteReader.InvalidateAsync` — `_cache.RemoveAsync(key)` removes from L1 **and**
L2 together. Dropping only Redis leaves every instance serving its own stale in-memory copy until
L1 expires: the classic "I cleared the cache and nothing changed" bug.

### The DTO, and why it is not the entity

**File:** same file — `record CachedQuote(int Id, string Text, string Author, DateTimeOffset CreatedAt, bool IsDeleted, int UserId)`

**Why:** `Quote` has **private setters and a private parameterless constructor**. System.Text.Json
— HybridCache's default serializer — will happily *serialise* it and then fail to reconstruct it,
handing back objects full of default values rather than throwing. Caching a DTO also stops a
tracked EF entity graph from being handed to a second request.

The six properties match the wire contract Day 16's `isQuote()` guard checks, so a cached
response is byte-identical to an uncached one. **Verified live**: `id, text, author, createdAt,
isDeleted, userId`, with `createdAt` still in `+00:00` form rather than `Z`.

---

## 3. New file — runtime cache configuration

**File:** `backend/Caching/CacheOptions.cs`

Registered as a **mutable singleton**, deliberately **not** `IOptions<T>`.

**Why:** the exercise needs a before/after comparison, and the only way to make two numbers
comparable is to change exactly one thing. Restarting the process with different configuration
would also change JIT warmth, connection-pool state and OS page cache — the delta would no longer
belong to the cache. `POST /api/cache/mode` flips `Enabled` on the running process instead.

`SimulatedQueryCost` (250 ms) is an artificial delay inside the factory. The demo database is a
handful of rows in SQLite; a real query returns in microseconds, far too fast for concurrent
callers to overlap, so **an unprotected stampede would not be observable at all**.

---

## 4. New file — the measurement counters

**File:** `backend/Caching/CacheMetrics.cs` — `RecordRead`, `RecordFactoryInvocation`, `Hits`,
`HitRatePercent`.

**Why counted this way:** HybridCache does not expose hit/miss counters, and it cannot — a hit may
come from L1 or L2, and from the caller's side both look like the factory simply not running. So
the **miss** is what gets counted and hits are inferred. That inference is exactly what makes
stampede protection measurable.

**File:** `backend/Data/DbQueryCounter.cs` — `DbQueryCounter` + `DbQueryCounterInterceptor`
(a `DbCommandInterceptor` overriding `ReaderExecuting(Async)` and `NonQueryExecuting(Async)`).

**Why:** "DB queries/sec" is the number the exercise asks for, and the only trustworthy place to
count it is where EF hands the command to the provider. Counting in the repository would miss
anything EF issues on its own; counting requests would miss the point entirely — the whole claim
of a cache is that requests and queries **stop being the same number**.

---

## 5. Changed — DbContext registration gains the interceptor

**File:** `backend/Extensions/InfrastructureExtensions.cs`
**Method:** `AddInfrastructure`

```csharp
services.AddSingleton<DbQueryCounter>();

// Service-provider overload, so the interceptor resolves the singleton counter regardless of
// the order these extension methods are called in.
services.AddDbContext<AppDbContext>((serviceProvider, options) =>
    options
        .UseSqlite(ResolveConnectionString(config, environment))
        .AddInterceptors(new DbQueryCounterInterceptor(
            serviceProvider.GetRequiredService<DbQueryCounter>())));
```

**Why the counter is registered *here* and not with the cache:** it belongs to the DbContext it
counts, and it must exist whether or not caching is wired up — otherwise the **"before" arm has
nothing to count.**

---

## 6. New file — DI wiring

**File:** `backend/Extensions/CachingExtensions.cs`
**Method:** `AddQuoteCaching`

```csharp
services.AddStackExchangeRedisCache(redis => {          // becomes HybridCache's L2
    redis.Configuration = redisConnection;
    redis.InstanceName  = "quotes:";
});

services.AddHybridCache(hybrid => {
    hybrid.DefaultEntryOptions   = new HybridCacheEntryOptions { … };
    hybrid.MaximumPayloadBytes   = 1024 * 1024;   // 1 MB
    hybrid.MaximumKeyLength      = 512;
});

services.AddScoped<IQuoteReader, CachedQuoteReader>();
```

**Why Redis is optional:** with no connection string, HybridCache still runs **L1-only**, so the
API and its tests work on a machine with no Redis. Same switch Day 17 used for caller identity and
Day 19 for messaging. What changes without Redis is not correctness but blast radius — L1 is
per-process, so each replica has its own copy and a restart starts cold.

**Why the size limits:** a cache that accepts anything is a memory leak with a good reputation.
These make an oversized entry fail loudly in development rather than quietly evicting everything
useful in production.

---

## 7. Changed — the hot read now goes through the cache

**File:** `backend/Extensions/QuoteEndpointExtensions.cs`
**Method:** `MapQuoteEndpoints`, the `GET /api/quotes` handler

```diff
- group.MapGet("/", async (IQuoteRepository repo, CancellationToken ct) =>
-     Results.Ok(await repo.GetAllAsync(ct)));
+ group.MapGet("/", async (QuotesApi.Caching.IQuoteReader reader, CancellationToken ct) =>
+     Results.Ok(await reader.GetAllAsync(ct)));
```

**Why this endpoint:** every visitor loading the home page hits it and it is the same query every
time — the definition of a hot read.

---

## 8. Changed — invalidation on every write path

**File:** `backend/Extensions/QuoteEndpointExtensions.cs`
**Methods:** the `POST /`, `PUT /{id}/author` and `DELETE /{id}` handlers — three call sites.

```csharp
await transaction.CommitAsync(ct);
await reader.InvalidateAsync(ct);      // AFTER the commit, never before
```

**Why after the commit, never before:** invalidating first opens a window in which a concurrent
reader repopulates the cache from the **pre-commit** state, and that stale value then survives a
full expiration. It only appears under concurrency and looks like the cache "randomly" serving old
data.

**Why all three:** the author is part of the cached list, so `PUT /author` makes the cached copy
wrong just as surely as a create or delete does. Miss one write path and the cache is stale in a
way only that one operation triggers — the hardest kind of cache bug to find.

---

## 9. New file — measurement and control endpoints

**File:** `backend/Endpoints/CacheEndpoints.cs`

| Route | Purpose |
|---|---|
| `GET /api/cache/stats` | reads, hits, misses, hit rate, **dbQueries**, layers |
| `POST /api/cache/reset` | zero the counters and drop the entry, so each arm measures itself |
| `POST /api/cache/mode` | flip caching on/off at runtime — **Development only** |
| `POST /api/cache/invalidate` | drop the entry without touching counters |

`mode` is gated on `IWebHostEnvironment.IsDevelopment()`, for the obvious reason: an endpoint that
can switch off production's cache under load is a liability regardless of its name.

---

## 10. Changed — configuration

**File:** `backend/appsettings.json` — new `Cache` section (`Enabled`, `Expiration`,
`LocalExpiration`, `SimulatedQueryCost`). Redis comes from `ConnectionStrings:Redis`, supplied at
runtime, never committed.

---

## 11. New file — the load test

**File:** `scripts/loadtest-cache.sh`

Runs the **same** load against the **same** endpoint on the **same** process twice, flipping only
`POST /api/cache/mode` between arms, then proves stampede protection with N simultaneous requests
against a cold key.

---

## Results

Full output: [`docs/loadtest-results.txt`](docs/loadtest-results.txt).
50 connections, 15s, `GET /api/quotes`, 250 ms simulated query cost, L1 + L2 Redis.

| | BEFORE (no cache) | AFTER (HybridCache) |
|---|---:|---:|
| requests served | 2,850 | 790,608 |
| **requests/sec** | 185.69 | **52,868.08** |
| **p99 latency** | 299.40 ms | **2.24 ms** |
| **DB queries** | 2,858 | **8** |
| hit rate | 0% | **100%** |
| DB queries per request | 1.003 | 0.000 |

**~285× throughput, ~133× better p99, DB load down ~100%.**

### Stampede protection

```
Cold cache, then 200 simultaneous requests for the same key
  200 concurrent requests issued, 200 returned 200, in 409ms
  reads (requests)     : 200
  misses (factory runs): 1
  DB queries           : 1
  hit rate             : 99.5%
```

**200 concurrent readers → 1 factory invocation → 1 database query.** Without stampede protection
that number is ~200: one query per caller, all for the same value, precisely when the system is
busiest.

---

## Screenshots

Captured live and stored in [`Screenshots/`](Screenshots/), referenced from the README:

| File | Shows |
|---|---|
| `01-app-quotes-list.jpg` | the Angular app on :4200 proxying to :5267, rendering "5 from GET /api/quotes" through the cache |
| `02-loadtest-before-after.jpg` | the before/after table and both bombardier runs |
| `03-stampede-protection.jpg` | 200 concurrent readers producing 1 factory invocation |
| `04-redis-l2-and-cache-stats.jpg` | `docker ps`, the real Redis key with its TTL, and `/api/cache/stats` |
| `05-cache-stats-raw-json.jpg` | the raw stats endpoint in the browser |

The three report cards (02, 03, 04) are HTML pages generated from the actual output files —
`docs/loadtest-results.txt`, `docker ps`, `redis-cli` — and then screenshotted. They are not
photos of a terminal, and the text inside them is unedited.

## Everything that was run

| Check | Result |
|---|---|
| Backend build | ✅ |
| `QuotesApi.Outbox.Tests` | ✅ 8/8 |
| `QuotesApi.Messaging.Tests` | ✅ 7/7 |
| `QuotesApi.Jobs.Tests` | ✅ 8/8 |
| Frontend `ng test` | ✅ 147/147, 12 files |
| Backend + frontend running together | ✅ `ng serve` on :4200 proxying `/api` → :5267 |
| Cached response contract | ✅ all six fields, `createdAt` still `+00:00` |
| Load test | ✅ 3/3 |

Live sanity check with both running:

```
layers: L1 in-memory + L2 Redis | redis: true
reads: 6  hits: 5  misses: 1  hitRate: 83.33%
```

## One honest note on `dbQueries`

The counter is app-wide, so the **outbox relay's own polling sweeps are included**. That is why
the "after" arm shows 8 rather than 1, and why a longer idle period raises the number without any
HTTP traffic at all. It is deliberate — the relay polls on a timer whether or not anyone is
loading the site, and its queries are part of the database's real load. The load test resets the
counter immediately before each arm so that background noise is bounded and visible rather than
silently folded into the result.
