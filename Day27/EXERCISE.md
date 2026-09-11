# Day 27 — Security pass

| | |
|---|---:|
| Threats modelled / fixed / accepted | **11 / 8 / 3** |
| Hardening assertions | **17 passed, 0 failed** |
| ZAP baseline warnings | **3 → 1** |
| Data-tier resources behind private endpoints | **3 of 3** |

Evidence: [`docs/threat-model.md`](docs/threat-model.md) ·
[`docs/hardening-verification.txt`](docs/hardening-verification.txt) ·
[`docs/private-endpoint-verification.txt`](docs/private-endpoint-verification.txt) ·
[`docs/zap-before.txt`](docs/zap-before.txt) · [`docs/zap-after.txt`](docs/zap-after.txt)

---

## 1. Threat model (STRIDE-lite)

Full table in [`docs/threat-model.md`](docs/threat-model.md). The three that were real, in the
code, yesterday:

| Threat | STRIDE | Status |
|---|---|---|
| **T1** `/api/cache` and `/upstream` had **no authorization at all** — and expose `POST /mode`, `POST /reset`, `POST /breaker/{action}` and fault injection | D, E | fixed |
| **T2** Authorization was **opt-in**. No fallback policy, so any endpoint that forgot `RequireAuthorization()` was public. T1 is the symptom; this is the cause | E | fixed |
| **T7** Data tier reachable from the internet, protected by identity alone — a stolen token was usable from anywhere | S, I, E | fixed |

Also fixed: no login rate limit (T3), 30 MB default request bodies (T4), missing security headers
(T5), `Server: Kestrel` banner (T6), no API versioning (T8).

**Accepted, with reasons:** audit-log repudiation (T9) needs a second store with different
credentials; JWT revocation (T10) would make a stateless token stateful and add a dependency to
the hot path — exposure is already bounded at two hours; BCrypt work factor (T11) is adequate
today, and the real fix is rehash-on-login rather than a bigger number.

**The fix for T1 and T2 is one line**, which is why modelling first was worth it:

```csharp
options.FallbackPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser()
    .Build();
```

Patching the two groups would have fixed the instances and left the cause. This inverts the
default: forgetting now produces a 401 in development instead of an open door in production.

---

## 2. The private-endpoint change

`publicNetworkAccess: 'Disabled'` on SQL, Service Bus and Key Vault, plus an endpoint and a DNS
zone for each. The module deploys all three parts together on purpose:

```bicep
// modules/private-endpoint.bicep — endpoint + zone + zone group, never apart.
resource privateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  properties: {
    subnet: { id: subnetId }
    privateLinkServiceConnections: [{
      name: '${name}-connection'
      properties: { privateLinkServiceId: targetResourceId, groupIds: [ groupId ] }
    }]
  }
}

// Without THIS the endpoint exists, the name still resolves publicly, and nothing works.
resource zoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: privateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [{ name: ..., properties: { privateDnsZoneId: privateDnsZone.id } }]
  }
}
```

A private endpoint alone does almost nothing: it allocates a private IP, but clients connect by
*name* and public DNS still answers with a public address. The zone group is what makes Azure
write the private A record. Deploying them apart is the usual way a rollout half-works.

**Verified live** ([`docs/private-endpoint-verification.txt`](docs/private-endpoint-verification.txt)):

```
SQL / ServiceBus / KeyVault  publicNetworkAccess = Disabled
pe-sql 10.20.1.4   pe-servicebus 10.20.1.5   pe-keyvault 10.20.1.6

privatelink.database.windows.net    sql-quotes-dev-pxd5zf   10.20.1.4
privatelink.servicebus.windows.net  sb-quotes-dev-pxd5zf    10.20.1.5
privatelink.vaultcore.azure.net     kv-quotes-dev-pxd5zf    10.20.1.6

$ az keyvault secret list --vault-name kv-quotes-dev-pxd5zf
  ERROR: (Forbidden) Connection is not an approved private link and caller was ignored
  because bypass is not set to 'AzureServices' and PublicNetworkAccess is set to 'Disabled'.
```

**Two things worth knowing.** A bare TCP connect to `…database.windows.net:1433` still succeeds —
Azure SQL's public endpoint is a shared *regional gateway*, so the handshake terminates there and
says nothing about your server. My first verification connected, concluded public access was not
blocked, and was wrong. Key Vault is the clean proof because it refuses at the application layer,
in words.

And **Service Bus Premium is mandatory** — private endpoints do not exist on Standard or Basic.
The same namespace running the same code costs roughly seventy times more because it is on a
VNet. That is the sharpest tradeoff in the day, and it is a pricing decision, not a technical one.

---

## 3. ZAP baseline, and what was fixed

Before scans `Day26/backend`; after scans `Day27/backend`. Day 27 started as a byte-for-byte copy,
so the security pass is the whole difference.

```
before   FAIL-NEW: 0   WARN-NEW: 3   PASS: 64
after    FAIL-NEW: 0   WARN-NEW: 1   PASS: 66
```

| Finding | Fix |
|---|---|
| **X-Content-Type-Options Header Missing** | `nosniff`, plus `X-Frame-Options`, `CSP: default-src 'none'`, `Referrer-Policy`, `Permissions-Policy` |
| **Cross-Origin-Resource-Policy Missing or Invalid** | `Cross-Origin-Resource-Policy: same-origin` + COOP + COEP. **I had missed this set entirely** — the scanner is what caught it |
| Non-Storable Content | **Not fixed, and correct as-is.** ZAP notes the responses are not cacheable; for an authenticated API that is the desired behaviour, not a defect |

Headers are added **first** in the pipeline so they appear on 401s, 429s and 500s too — precisely
the responses an attacker is most interested in.

The after-scan also shows `/` and `/robots.txt` returning **401 instead of 404**: a side effect of
the fallback policy, which incidentally prevents endpoint enumeration.

### What the scan could not do

Two of my own scans were worthless before they were right, and both failed *green*:

- `--add-host=host.docker.internal:host-gateway` overrode Docker Desktop's own resolution, so ZAP
  never reached the target — and reported **PASS: 66** against nothing.
- Pointed at `/`, the spider got a 404 and had no links to follow, so it tested one URL. It now
  targets `/health`, and the script aborts if the log contains "Network is unreachable".

A baseline scan is **passive**. It found none of T1, T2 or T7 — authorization and network-topology
failures are invisible to a spider that has no notion of which endpoints should be privileged.
That is why `scripts/verify-hardening.sh` exists: 17 assertions ZAP structurally cannot make.

---

## 4. Input limits with `Span<T>`

`Security/TextGuard.cs` validates over `ReadOnlySpan<char>` and allocates nothing, which is a
security property rather than an optimisation. Validation is the *first* code to touch a request
body — a validator that allocates proportionally to its input hands the attacker a lever on the
server's memory, and becomes the denial of service it was meant to prevent.

```csharp
if (value.Length > maxLength) return Rejection.TooLong;   // length FIRST — one comparison
var trimmed = value.Trim();                                // a re-slice, not a copy
return trimmed.IndexOfAnyExcept(AllowedText) >= 0          // SearchValues<char>, vectorised
    ? Rejection.DisallowedCharacter : Rejection.None;
```

`SearchValues<char>` is built once at startup and picks a bitmap strategy for a small ASCII set;
the obvious `value.Any(c => allowed.Contains(c))` allocates an enumerator and a closure and runs a
delegate call per character. It is an **allow**-list: a deny-list is wrong the moment somebody
finds an encoding it did not anticipate.

---

## 5. Honest gaps

- **The application still runs outside the VNet.** `snet-app` exists and is empty. The data tier is
  closed to the internet; moving compute inside is a separate change.
- **Private endpoints break laptop access by design** — Day 25's SQL grant and Day 26's local
  worker stop working. The replacements are a jump box, a VPN, or running them in the VNet.
- **No active pen test.** Injection and broken access control need an authenticated active scan
  driven by the OpenAPI document (`zap-api-scan.py`), not a passive baseline.
- **The baseline scans one endpoint's headers**, because it cannot authenticate.
- **T9, T10, T11 accepted**, each with a stated reason rather than silence.
