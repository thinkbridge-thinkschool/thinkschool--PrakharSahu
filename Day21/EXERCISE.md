# Day 21 — HybridCache + stampede protection

> **Exercise:** Paste the cache wiring + the load-test before/after (DB queries/sec, p99).
> Show stampede protection working under concurrency.

**Repository:** `<GITHUB_LINK_PLACEHOLDER>` — `Day21/`
**Change-by-change walkthrough:** [`update_code.md`](update_code.md)

---

## 1. The cache wiring

`backend/Extensions/CachingExtensions.cs` → `AddQuoteCaching`, called from `Program.cs`.

```csharp
// L2. HybridCache discovers this through the container — there is no explicit "use Redis"
// call. Convenient, and worth knowing, because a stray IDistributedCache registration
// silently becomes your L2.
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    services.AddStackExchangeRedisCache(redis =>
    {
        redis.Configuration = redisConnection;
        redis.InstanceName  = "quotes:";
    });
    options.RedisConnected = true;
}

services.AddHybridCache(hybrid =>
{
    hybrid.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration           = options.Expiration,       // L2 (Redis) lifetime
        LocalCacheExpiration = options.LocalExpiration    // L1 (in-memory), deliberately shorter
    };

    // A cache that accepts anything is a memory leak with a good reputation.
    hybrid.MaximumPayloadBytes = 1024 * 1024;
    hybrid.MaximumKeyLength    = 512;
});

services.AddScoped<IQuoteReader, CachedQuoteReader>();
```

Redis is **optional**: with no connection string HybridCache runs L1-only, so the API and its
tests work on a machine with no Redis. Same switch Day 17 used for caller identity. What changes
is not correctness but blast radius — L1 is per-process, so each replica has its own copy and a
restart starts cold.

### The cached read

`backend/Caching/CachedQuoteReader.cs` → `GetAllAsync`, behind `GET /api/quotes`.

```csharp
return await _cache.GetOrCreateAsync(
    ListCacheKey,                       // "quotes:all:v1"
    state: this,
    factory: static (reader, ct) =>
        new ValueTask<IReadOnlyList<CachedQuote>>(reader.LoadFromDatabaseAsync(ct)),
    options: new HybridCacheEntryOptions
    {
        Expiration           = _options.Expiration,
        LocalCacheExpiration = _options.LocalExpiration
    },
    tags: new[] { QuotesTag },
    cancellationToken: cancellationToken);
```

Three details that are load-bearing:

- **`GetOrCreateAsync` *is* the stampede protection.** Nothing in this class implements
  coalescing; it is a property of this call, and the whole reason to use HybridCache rather than
  `IDistributedCache` directly, where you would write the single-flight lock yourself.
- **`state: this` + a `static` factory** — the delegate captures nothing, so it is allocated once
  instead of per request. On the hot path this exists to make fast, that matters.
- **`LocalCacheExpiration` < `Expiration`** — L1 cannot be invalidated from another instance, so
  it *is* the worst-case cross-replica staleness window.

### What gets cached, and why not the entity

```csharp
public sealed record CachedQuote(
    int Id, string Text, string Author, DateTimeOffset CreatedAt, bool IsDeleted, int UserId);
```

`Quote` has private setters and a private constructor. System.Text.Json — HybridCache's default
serializer — serialises it happily and then fails to reconstruct it, returning objects full of
default values rather than throwing. The six properties match the wire contract Day 16's
`isQuote()` guard checks, so a cached response is byte-identical to an uncached one. Verified
live: all six fields, `createdAt` still `+00:00` rather than `Z`.

### Invalidation — three call sites, all after the commit

`backend/Extensions/QuoteEndpointExtensions.cs`, in the `POST`, `PUT /author` and `DELETE`
handlers:

```csharp
await transaction.CommitAsync(ct);
await reader.InvalidateAsync(ct);   // AFTER the commit, never before
```

Invalidating first opens a window in which a concurrent reader repopulates the cache from the
**pre-commit** state, and that stale value then survives a full expiration. It only appears under
concurrency and presents as the cache "randomly" serving old data.

`RemoveAsync` drops L1 **and** L2 together. Dropping only Redis leaves every instance serving its
own stale in-memory copy until L1 expires — the classic "I cleared the cache and nothing changed".

---

## 2. Load test — before / after

`scripts/loadtest-cache.sh`. Same load, same endpoint, **same process**, twice — only
`POST /api/cache/mode` differs between arms, so the delta belongs to the cache and not to JIT
warmth, connection-pool state or OS page cache.

50 connections · 15s · `GET /api/quotes` · 250 ms simulated query cost · L1 + L2 Redis.

### BEFORE — cache off

```
--- before — 50 connections for 15s against GET /api/quotes
      Reqs/sec       185.69     521.60    3239.16
      Latency      265.06ms     2.62ms   300.96ms
      Latency Distribution
         99%   299.40ms
        1xx - 0, 2xx - 2850, 3xx - 0, 4xx - 0, 5xx - 0

      requests served : 2850
      DB queries      : 2858
      cache hits      : 0 (0%)
```

### AFTER — HybridCache on

```
--- after — 50 connections for 15s against GET /api/quotes
      Reqs/sec     52868.08   10574.84   68700.84
      Latency        0.95ms     0.90ms   305.42ms
      Latency Distribution
         99%     2.24ms
        1xx - 0, 2xx - 790608, 3xx - 0, 4xx - 0, 5xx - 0

      requests served : 790608
      DB queries      : 8
      cache hits      : 790607 (100%)
```

### The comparison

| | BEFORE (no cache) | AFTER (HybridCache) | change |
|---|---:|---:|---|
| requests served | 2,850 | 790,608 | |
| **requests/sec** | 185.69 | **52,868.08** | **~285× more** |
| **p99 latency** | 299.40 ms | **2.24 ms** | **~133× lower** |
| **DB queries** | 2,858 | **8** | **~357× fewer** |
| cache hit rate | 0% | **100%** | |
| **DB queries per request** | 1.003 | **0.000** | **~100% reduction** |

Queries-per-request is the honest ratio: throughput differs enormously between arms, so comparing
raw query counts alone would flatter whichever arm served more requests. Without the cache it is
**one database query per request, by definition**. With it, the database is touched once per
expiry window regardless of traffic — which is the property that actually matters, because it
means load on the database stops scaling with load on the API.

The p99 tells the same story from the caller's side: before, p99 is pinned at the 250 ms query
cost plus queueing; after, it is 2.24 ms because almost every request is served from memory.

---

## 3. Stampede protection under concurrency

The load test above shows a *warm* cache. This shows a **cold** one, which is where stampedes
happen.

```
--- Cold cache, then 200 simultaneous requests for the same key
      200 concurrent requests issued, 200 returned 200, in 409ms

      reads (requests)     : 200
      misses (factory runs): 1
      DB queries           : 1
      hit rate             : 99.5%

  [PASS] 200 concurrent readers of a cold key caused only 1 factory invocation(s)
         — the stampede was collapsed
```

**200 concurrent readers → 1 factory invocation → 1 database query.**

Issued genuinely simultaneously (`Promise.all` over 200 `fetch` calls), not sequentially — a
sequential loop would let the first request populate the cache and prove nothing about
concurrency.

How the miss count is trustworthy: HybridCache exposes no hit/miss counters, and it cannot — a
hit may come from L1 or L2 and both look like the factory not running. So the **factory** is
instrumented, in `CachedQuoteReader.LoadFromDatabaseAsync`:

```csharp
_metrics.RecordFactoryInvocation();   // the factory ONLY runs on a miss
```

`misses == factory invocations`, `hits == reads − misses`. One factory invocation for 200 readers
is the coalescing, measured rather than asserted.

**Without stampede protection that number is ~200** — one query per concurrent caller, all for the
same value, at exactly the moment the system is busiest. Worse, it is self-reinforcing: the
duplicate queries slow the database, which widens the miss window, which admits more duplicates.

---

## What I learned

**A cache changes what scales.** The headline numbers are throughput and p99, but the number that
matters operationally is *DB queries per request*: 1.003 → 0.000. Before, database load scaled
linearly with traffic. After, it is one query per expiry window no matter how much traffic
arrives. That is the difference between "the database is the bottleneck" and "the database barely
notices".

**Stampede protection is a property of the API you call, not code you write.** The single-flight
behaviour came from using `GetOrCreateAsync` rather than reading and writing the cache by hand.
The equivalent with `IDistributedCache` is a lock, a double-check, and a careful decision about
what to do when the lock is held — three chances to get concurrency wrong.

**Two expirations, not one, and the shorter one is the interesting one.** L1 cannot be invalidated
across processes, so `LocalCacheExpiration` is the real cross-replica staleness window. On one
machine it looks like a tuning knob; at two replicas it is the correctness budget.

**Caching the EF entity would have failed silently.** Private setters plus System.Text.Json means
serialise-then-fail-to-reconstruct — objects of default values, no exception. Caching a DTO is not
tidiness, it is the difference between working and quietly wrong.

## What would break this

**The single key is the single point of failure.** Everything lives under `quotes:all:v1`, so any
write invalidates the whole list, and at high write rates the cache would spend its life cold —
all the stampede cost, none of the benefit. Per-page or per-query keys would fix that and cost a
fan-out on invalidation.

**L1 makes replicas disagree.** With two instances, a write on A invalidates A's L1 and Redis, but
B keeps serving its own L1 copy until it expires — up to `LocalExpiration` of staleness that no
invalidation can reach. The real fix is backplane invalidation (Redis pub/sub telling every
instance to drop the key), which HybridCache does not do for you.

**`SimulatedQueryCost` is doing real work in these numbers.** 250 ms of artificial delay is what
makes the before/after dramatic and the stampede observable. A genuinely fast query would show a
far smaller gap — the shape of the result holds, the magnitude is manufactured, and it would be
dishonest to quote 285× as if it came from a production workload.

**`dbQueries` counts the whole application.** The outbox relay polls on a timer, so its sweeps are
included — that is why the "after" arm shows 8 rather than 1. Deliberate, since the relay's
queries are part of the database's real load, but it means the counter is not a pure measure of
HTTP-driven traffic.

**Nothing here bounds a cache miss under failure.** If Redis is unreachable, every request falls
through to L1 and then the factory. There is no circuit breaker and no negative caching, so a
Redis outage during a traffic spike would hand the full load straight to the database — the exact
scenario the cache exists to prevent.
