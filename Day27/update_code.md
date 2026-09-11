# Day 27 — every file, and why

Day 26's API carried forward and hardened. Most of the code is untouched; this is what changed.

---

## The one line that mattered most

```csharp
services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
```

Before it, authorization was **opt-in**: an endpoint was public unless somebody remembered
`.RequireAuthorization()`. Two groups had forgotten — `/api/cache`, which can disable caching and
reset counters, and `/upstream`, which can trip the circuit breaker and inject faults. Both were
reachable with no credential at all.

Patching those two would have fixed the instances and left the cause. A fallback policy inverts
the default: a new endpoint is protected unless it explicitly says `.AllowAnonymous()`, so
forgetting produces a 401 in development rather than an open door in production.

**One consequence is worth knowing before it confuses somebody.** The authorization middleware
applies the fallback policy when *no endpoint matched* as well as when a matched endpoint has no
authorization metadata. So an unmatched route now returns **401 instead of 404**. That
incidentally prevents endpoint enumeration — a scanner cannot distinguish a real path from an
imaginary one — but it is a side effect, not a design, and an authenticated caller still gets a
proper 404.

---

## New files

### `Security/TextGuard.cs` — input limits over spans

Length and character limits enforced against `ReadOnlySpan<char>`, allocating nothing.

The allocation-free part is a security property rather than a micro-optimisation. Validation is
the **first** code to touch a request body, running on data an attacker chose and before any limit
has been applied. A validator that allocates proportionally to its input hands the attacker a
lever on the server's memory — `text.Trim().ToLowerInvariant().Split(' ')` on a 10 MB string
allocates several copies of it, and a few hundred concurrent requests turn the check into the
denial of service it was meant to prevent.

Three details do real work:

**Length is checked first**, before anything walks the input, so rejecting a 200 KB body costs one
comparison rather than a scan.

**`SearchValues<char>`** holds the allow-list. Built once at startup, it picks a strategy from the
set — a bitmap for a small ASCII set like this one — and `IndexOfAnyExcept` then examines several
characters per instruction. The obvious alternative, `value.Any(c => allowed.Contains(c))`,
allocates an enumerator and a closure and runs a delegate call per character.

**An allow-list, not a deny-list.** A deny-list has to enumerate every dangerous character and is
wrong the moment somebody finds an encoding it did not anticipate. An allow-list is wrong only by
being too strict, which arrives as a user complaint rather than as an incident.

`FixedTimeEquals` is included for comparing secrets: `a == b` on strings returns at the first
mismatch, so its timing leaks how many leading characters were right. It encodes into
`stackalloc` buffers and zeroes them in a `finally`, so the secret is not left in a heap array
waiting for a GC.

### `Security/ApiHardening.cs`

The fallback policy above, two rate-limit policies, the Kestrel body cap and the response headers.

**Two rate-limit policies, because the two paths are attacked differently.** `auth` is 5/minute —
login is where credential stuffing lands, and BCrypt is deliberately slow, so an unthrottled login
endpoint is simultaneously a brute-force surface and a cheap CPU exhaustion vector. `global` is
100/minute and exists to stop a runaway client, not to police normal use; set near real traffic it
becomes an outage generator.

`QueueLimit = 0` on both. Queueing a rejected request holds a connection and a thread — the exact
resource the limiter is protecting. Refusing immediately with 429 is cheaper and more honest.

**The partition key** is the authenticated subject when there is one and the remote address
otherwise. IP alone is wrong in both directions: everyone behind one corporate NAT shares a bucket,
while an attacker with a /64 of IPv6 has unlimited buckets. `X-Forwarded-For` is deliberately not
consulted — it is caller-supplied unless a trusted proxy overwrote it, so trusting it would let
anyone choose their own partition and turn the limiter off with a header.

**Security headers go on first** in the pipeline, so they are present on the 401s, 429s and 500s
produced further down — which are precisely the responses an attacker is most interested in.

### `Security/ApiVersioning.cs`

The version is in the **path**, not a header, for operational reasons: it appears in access logs
so "who is still on v1" is a query rather than a guess; it is part of the cache key, so a proxy
cannot serve a v1 response to a v2 client; and it survives a curl pasted into a chat window.

The refresh cookie is scoped to `/api/v1/auth` rather than `/`. A cookie at `/` is attached to
every request to the origin, including ones with no use for it, so one reflected-content flaw
anywhere on the origin can reach it.

### `infra/modules/private-endpoint.bicep`

Endpoint, private DNS zone and zone group in **one module**, because deploying them apart is the
most common way a private-endpoint rollout half-works. The endpoint alone allocates a private IP
and changes nothing: clients connect by name, and public DNS still answers with a public address.
The zone group is what makes Azure write the private A record. Keeping the three together means
they cannot be deployed separately.

`location: 'global'` on the zone is not a placeholder — private DNS zones are global resources and
passing a region is a deployment error, which catches people out because every neighbouring
resource in the template is regional.

---

## Modified

### `Program.cs`

Registers the hardening, sets `MaxRequestBodySize` to 64 KB (Kestrel's default is 30 MB, for an
API whose largest legitimate body is a 1 KB quote) and `AddServerHeader = false`.

Every endpoint now hangs off **one versioned, rate-limited group**, so the prefix and the limiter
come from the group rather than from each endpoint remembering to ask.

The cache-control, fault-injection and fake-upstream endpoints are mapped **only in Development**.
The fallback policy already closed the authentication hole, but authenticating a debug lever is
the wrong fix — the right one is that it is not present in an environment that did not ask for it.
An endpoint that does not exist cannot be misconfigured.

OpenAPI is served in Development only. Publishing a complete map of the surface, including
endpoints an attacker has not found yet, is free reconnaissance; the people who need the document
have a non-production environment.

### `Endpoints/*.cs`, `Extensions/QuoteEndpointExtensions.cs`

Signatures changed from `this WebApplication app` to `this IEndpointRouteBuilder app` so they can
be mapped onto the versioned group, and the `/api` prefix removed from each `MapGroup` since the
parent supplies it. `Location` headers and the cookie path now derive from `ApiVersioning.Prefix`
rather than being hardcoded.

The auth group is the one place `AllowAnonymous` is used — you cannot present a credential to the
endpoint that issues credentials — and it is paired with the tight limiter for exactly that
reason. The policy sits on the group so `/refresh` and `/logout` inherit it: a refresh token is a
credential too.

Quote creation now runs `TextGuard` **before** `Quote.Create`. Both layers stay. `Quote.Create`
works on `string`, so by the time it runs the body is already deserialised and materialised; its
job is the domain's rules, and `TextGuard`'s is making hostile input cheap to reject.

---

## Two things the verification caught

**A status code is not evidence.** The first version of the input-limit assertion checked only
for HTTP 400 — which `Quote.Create` also returns for bad characters. It passed before `TextGuard`
was wired into the endpoint at all. It now asserts on the **message wording**, so it fails if the
span guard is not the thing doing the rejecting, and a second case (2,000 characters, under the
64 KB body cap and over the 1,000-character text limit) isolates the length check from both the
Kestrel cap and the allow-list.

**The rate limiter was attached to the wrong thing.** Nine failed logins returned nine 401s. The
`global` policy was on the versioned group, but the `auth` policy had never been attached to
anything — so login was throttled at 100/minute rather than 5. The assertion is what surfaced it;
reading the code, the policy was plainly defined and looked done.
