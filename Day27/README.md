# Day 27 — Security pass

A STRIDE-lite threat model of the capstone, the data tier moved behind private endpoints, the
OpenAPI surface hardened, and an OWASP ZAP baseline run before and after.

**Deliverable:** [EXERCISE.md](EXERCISE.md) — the threat model, the private-endpoint change, and
the ZAP summary with what was fixed.
**Change-by-change walkthrough:** [update_code.md](update_code.md).

## What the day actually found

Three of the eleven threats were not theoretical. They were in the code as of yesterday:

| | |
|---|---|
| `/api/cache` and `/upstream` | **no authorization at all**, and they expose `POST /mode`, `POST /reset`, `POST /breaker/{action}` and fault injection |
| authorization was **opt-in** | no fallback policy, so any endpoint that forgot `RequireAuthorization()` was public |
| the data tier | reachable from the internet, protected by identity alone |

The first is a symptom of the second. The fix for both is one line — a fallback policy that
inverts the default — which is why the threat model was worth writing before touching anything.

## Layout

```
Day27/
├── backend/                          Day 26's API, hardened
│   ├── Security/
│   │   ├── ApiHardening.cs           deny-by-default, rate limits, security headers
│   │   ├── ApiVersioning.cs          /api/v1, and why the version is in the path
│   │   └── TextGuard.cs              Span<T> input limits — allocation-free validation
│   ├── Program.cs                    the versioned group, dev-only gating, Kestrel caps
│   └── Endpoints/                    re-parented onto the versioned group
├── infra/
│   ├── main.bicep                    VNet + data tier with publicNetworkAccess Disabled
│   └── modules/private-endpoint.bicep  endpoint + DNS zone + zone group, always together
├── scripts/
│   ├── verify-hardening.sh           17 assertions against a running API
│   ├── deploy-private.sh             deploy, prove public access is refused, tear down
│   └── zap-baseline.sh               before/after ZAP scans
└── docs/
    ├── threat-model.md               the STRIDE-lite table
    ├── hardening-verification.txt    17 passed, 0 failed
    ├── private-endpoint-verification.txt
    └── zap-before.txt / zap-after.txt
```

## Running it

```bash
cd Day27

./scripts/verify-hardening.sh        # 17 assertions, no Azure needed
./scripts/zap-baseline.sh before     # scans Day26/backend — the code as it was
./scripts/zap-baseline.sh after      # scans Day27/backend — the same code, hardened

./scripts/deploy-private.sh          # ~USD 1/hour, dominated by Service Bus Premium
./scripts/deploy-private.sh --down   # tear it down
```

`verify-hardening.sh` asserts the things ZAP structurally cannot: that an endpoint which
*should* require a token does, that the rate limiter is attached to the group it was meant to be
attached to, and that the span-based guard — not the pre-existing domain validation — is what
rejects bad input.

## The breaking change

Every path moves from `/api/...` to `/api/v1/...`. That breaks every existing caller, and now is
the moment to do it: before today the surface was implicitly v1 forever with nowhere to put a v2.
Paths move once so that the next change does not have to.

One side effect worth knowing: with a deny-by-default fallback policy, a request to an unmatched
route returns **401 rather than 404**, because the authorization middleware applies the policy
before routing concludes there is no endpoint. That incidentally blocks path enumeration — a
scanner can no longer tell a real endpoint from an imaginary one — but it is a consequence rather
than a design, and it is worth knowing before someone debugs a "missing" endpoint.

## Cost

`deploy-private.sh` runs about **USD 1/hour**, almost all of it Service Bus **Premium**. Private
endpoints are unavailable on Standard and Basic, so the requirement forces the dedicated tier
regardless of throughput — the sharpest system-design tradeoff in the exercise, and a pricing
decision rather than a technical one.

## What is not done

- **The application still runs outside the VNet.** `snet-app` exists and is empty. Private
  endpoints protect the data tier from the internet; the API reaching it still requires the
  compute to move inside, which is a Container Apps or App Service VNet-integration change.
- **Day 25's SQL grant and Day 26's local worker stop working** against a private-endpoint
  deployment, by design. A laptop is outside the VNet. The replacements are a jump box, a VPN,
  or running those tools in the VNet.
- **No active pen test.** The ZAP baseline is passive — it spiders and observes. Injection,
  broken access control and business-logic flaws need an authenticated active scan, which needs
  a ZAP context and credentials.
- **T9, T10 and T11 in the threat model are accepted, not fixed** — audit-log repudiation, JWT
  revocation, and the BCrypt work factor. Each says why in one line.
