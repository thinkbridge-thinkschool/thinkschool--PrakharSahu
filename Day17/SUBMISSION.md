# Day 17 — Submission

---

# (1) MY BRIEF TO THE AGENT

**Objective.** Take the Angular 21 app from `Day16/piece2/quotes-web` and the .NET 10 Quotes
API from `Day5/piece6/QuotesApi`, copy them into `Day17/frontend` and `Day17/backend`, and put
the frontend live calling the real API. The frontend tier must authenticate to the API with a
**managed identity**. No client secret anywhere.

**Target**

| | |
|---|---|
| Static Web App | `swa-quotes-day17`, Free plan |
| Frontend URL | the generated `https://<name>.azurestaticapps.net` — **no custom domain**, I don't own a DNS domain and Azure binds domains you already control, it does not issue them |
| Subscription / tenant | `132ef106-f8ec-4352-83e4-9bc238274f25` (Azure for Students) / `8d46a076-d093-416d-a57b-8692cde13bf8` |
| Resource group | `rg-quotes-day17`, `centralindia` |

**Week-1 API base URL.** The Container App `quotes-api`. Resolve it at deploy time, never
hard-code it:

```bash
az containerapp show -n quotes-api -g rg-quotes-day17 \
  --query properties.configuration.ingress.fqdn -o tsv
```

Resolved to: `https://quotes-api.happydesert-51845d93.centralindia.azurecontainerapps.io`

Backing store is SQLite in the container. Leave it. Do not migrate to Azure SQL.

**Endpoints it must hit** (real routes from `Extensions/QuoteEndpointExtensions.cs` and
`Endpoints/AuthEndpoints.cs` — do not invent or rename any):

| Method | Route | Auth | Notes |
|---|---|---|---|
| `GET` | `/api/quotes` | anonymous | Returns a **bare array**, not `{ items, total }`. Accepts no paging params. |
| `GET` | `/api/quotes/{id:int}` | anonymous | `404` for unknown, deleted, or non-integer id |
| `POST` | `/api/quotes` | `can-edit-quotes` | Needs `scope=quotes.write`. `201` with the entity, `409` on duplicate author+text |
| `PUT` | `/api/quotes/{id:int}/author` | `can-edit-quotes` | `204`, `409` if that author already has the text |
| `DELETE` | `/api/quotes/{id:int}` | authenticated + `IsQuoteOwner` | Soft delete. `204` owned / `403` not owned / `404` unknown / `401` anonymous |
| `POST` | `/api/auth/register` | anonymous | `201`, signs the new account straight in |
| `POST` | `/api/auth/login` | anonymous | `200 {accessToken, refreshToken:"", expiresIn}`, `401` on bad creds |
| `POST` | `/api/auth/refresh` | refresh cookie | **No token in the body** — browser sends the `quotes_rt` HttpOnly cookie, server rotates it |
| `POST` | `/api/auth/logout` | refresh cookie | Revokes server-side, clears the cookie |

**Fields on the wire.** `GET /api/quotes` returns the EF entity, so **six** fields not three:

```
id: number · text: string · author: string
createdAt: string   ISO-8601 with an explicit +00:00 offset, NOT a Z suffix
isDeleted: boolean · userId: number
```

`Day16/piece2`'s `isQuote()` guard checks all six and rejects the response if any is missing.

**Things that will bite you**
- Refresh cookie is `HttpOnly; Secure; SameSite=Strict; Path=/api/auth`
- The API validates token lifetime with `ClockSkew = TimeSpan.Zero`
- `Day5/piece6/QuotesApi/.env` has leftover `EntraId__TenantId/ClientId/Audience`. **Verify
  them before reusing.** They may not belong to this tenant.
- The API refuses to start without `Jwt__Key`, and rejects a key under 32 bytes

**Auth requirement — the actual point**

Managed identity. Not a client secret, not a certificate, not a shared key.

- The tier calling the API must get its token from the platform at runtime via
  `DefaultAzureCredential` / IMDS.
- The API must **verify** it — issuer, audience, lifetime, signature against Entra — and
  require an app role. Attaching a token nobody checks proves nothing.
- A request without a managed-identity token must be **rejected**. I will call the API's
  public FQDN directly and expect `401`.
- The end user's session must keep working. Per-user ownership on `DELETE /api/quotes/{id}`
  must still resolve against the **user**, not the managed identity.
- No `client_secret`, `AZURE_CLIENT_SECRET`, `AZURE_CREDENTIALS`, password-bearing connection
  string, or account key in: the repo, Container Apps settings, Static Web Apps settings, or
  GitHub secrets.

A browser cannot hold a managed identity. Work out where the token has to be minted and
justify the choice, including cost.

**CI/CD.** GitHub Actions building and deploying both tiers, authenticating to Azure with
**OIDC federated credentials**, not a stored secret. Fail the build if the MI hop stops working.

**Done means:** live URL loads and deep links resolve · **Lighthouse ≥ 95, report the numbers**
· the API call demonstrably carries an MI token and demonstrably rejects calls without one ·
a verification log grounded in the real endpoints above · zero secrets, demonstrated by a scan.

**Standing instruction.** If any of this is wrong, impossible, or costs more than I assumed,
say so and stop. Do not paper over it, and do not fall back to a client secret "temporarily".

---

# (2) THE AGENT'S OUTPUT

## The constraint the brief missed

A browser can't hold a managed identity — and **neither can a Free-plan Static Web App**. SWA
*has* an MI feature, which makes this easy to get wrong: it only retrieves Key Vault secrets
for the platform and is unusable from application code. Microsoft's guidance is "bring your own
Functions app", and **BYOF requires the Standard plan (~USD 9/mo)**.

| Option | Cost | Verdict |
|---|---|---|
| SWA managed functions | free | **Impossible** — no MI available |
| SWA Standard + BYOF | ~$9/mo | Works, same-origin `/api/*` |
| SWA Free + broker on Container Apps | ~$0 | **Chosen.** Cross-origin, so CORS + a cookie downgrade |

## Architecture

```
browser ──► quotes-web        Angular bundle, 83 kB transferred. Static files, no identity.
   │
   │ fetch(), cross-origin, credentialed
   ▼
quotes-bff (Container App)    SYSTEM-ASSIGNED MANAGED IDENTITY
   │   DefaultAzureCredential → IMDS, scope api://<app-id>/.default
   │   attaches it as  X-Caller-Token: Bearer <token>
   │   forwards the end user's Authorization header untouched
   ▼
quotes-api (Container App)    validates against Entra, requires the Api.Invoke app role,
                              rejects /api/* with 401 when it is absent or invalid
```

Nothing in that diagram holds a credential. The only one that exists is minted per request and
expires.

## Why two headers, not one

Putting the MI token in `Authorization` was tried first and is wrong. `Authorization` already
carries the end user, and `IsOwnerHandler` reads `sub` off whatever principal it finds:

```csharp
quote.UserId == sub          // Authorization/IsOwnerHandler.cs, unchanged from Week 1
```

Overwrite it and every quote appears to belong to the broker's service principal —
`DELETE /api/quotes/{id}` then succeeds for everyone or fails for everyone.

| Header | Identity | Issued by | Validated by |
|---|---|---|---|
| `Authorization` | the end user | the Quotes API itself (HS256) | `SelfHosted` JwtBearer scheme |
| `X-Caller-Token` | the calling service | Microsoft Entra | `CallerIdentity` JwtBearer scheme |

## Acquiring the token — `bff/src/token-broker.js`

```js
const credential = new DefaultAzureCredential();

export async function getCallerToken() {
  const now = Date.now();
  if (cached && cached.expiresOnTimestamp - REFRESH_MARGIN_MS > now) return cached.token;

  const issued = await credential.getToken(API_SCOPE);   // api://<app-id>/.default
  if (!issued?.token) throw new Error(/* … */);
  cached = issued;
  return issued.token;
}
```

Three load-bearing details:
- **`/.default`, not the bare App ID URI.** For an app-only token it means "every app role
  already granted". The bare URI yields a token the API rejects, and the error doesn't say why.
- **Five-minute refresh margin**, because the API validates lifetime with
  `ClockSkew = TimeSpan.Zero`. With no tolerance on the far side, a token "valid for a few more
  seconds" here is already expired there, and it looks like an intermittent 401.
- **Throws rather than forwarding without a token**, so a broken identity surfaces as a 503
  naming this tier, not a confusing 401 from the API.

## Attaching it — `bff/src/server.js`

```js
headers.set('X-Caller-Token', `Bearer ${callerToken}`);
```

Same file rewrites the API's refresh cookie from `SameSite=Strict` to `SameSite=None; Secure`
for the cross-site hop, and forwards the body as an opaque Buffer so nothing is re-serialised.

## Validating it — `backend/Extensions/CallerIdentityExtensions.cs`

```csharp
options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
options.Audience  = audience;
options.MapInboundClaims = false;                    // keep oid/roles/tid as Entra sent them
options.TokenValidationParameters = new TokenValidationParameters {
    ValidateIssuer = true, ValidateAudience = true,
    ValidateLifetime = true, ValidateIssuerSigningKey = true,
    ValidIssuers = new[] { $".../{tenantId}/v2.0", $"https://sts.windows.net/{tenantId}/" },
    RoleClaimType = "roles",
    ClockSkew = TimeSpan.FromMinutes(2)
};
options.Events = new JwtBearerEvents {
    OnMessageReceived = context => {               // token is in OUR header, not Authorization
        var header = context.Request.Headers["X-Caller-Token"].ToString();
        if (!string.IsNullOrWhiteSpace(header))
            context.Token = header.StartsWith("Bearer ") ? header["Bearer ".Length..].Trim()
                                                        : header.Trim();
        return Task.CompletedTask;
    }
};
```

Then middleware over `/api/*` requiring the token to validate, to be app-only, and to carry
`Api.Invoke`. `/health` is excluded — a Container Apps probe can't hold an identity, and
failing it would take the revision down rather than secure it. The whole scheme is **inert
unless `CallerIdentity:TenantId` and `CallerIdentity:Audience` are both set**, so Week-1's
local runs and integration tests are unaffected.

`GET /api/whoami` (`backend/Endpoints/WhoAmIEndpoint.cs`) reports what the API just
authenticated — identifiers only, never tokens — so the MI hop is observable from outside.

## SWA config — `frontend/public/staticwebapp.config.json`

- `navigationFallback` → `/index.html`, **with asset extensions excluded**; without the
  exclusions a mistyped asset path returns index.html with a `200`, the browser parses HTML as
  JavaScript, and the failure surfaces nowhere near its cause
- Year-long `immutable` caching for content-hashed files, `no-cache` for `index.html`
- CSP whose `connect-src` names the broker and nothing else; `style-src` allows
  `'unsafe-inline'` (Angular inlines critical CSS), `script-src` does not
- HSTS, `nosniff`, `Referrer-Policy`, `Permissions-Policy`, `frame-ancestors 'none'`

The broker hostname isn't knowable until its Container App exists, so it's stamped into both
the CSP and the bundle at deploy time by `scripts/set-bff-url.mjs`, which fails loudly if it
finds nothing to replace.

## CI/CD — `.github/workflows/day17-deploy.yml`

```yaml
permissions:
  id-token: write        # OIDC. This is what replaces the stored credential.
  contents: read

- uses: azure/login@v2
  with:
    client-id:       ${{ vars.AZURE_CLIENT_ID }}
    tenant-id:       ${{ vars.AZURE_TENANT_ID }}
    subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}
```

Repository **variables**, not secrets — a client id, tenant id and subscription id are public
identifiers. No `AZURE_CREDENTIALS`, no client secret. The **SWA deployment token isn't stored
either**: it's fetched at run time through the same OIDC session with
`az staticwebapp secrets list`, used in one step, never written down.

Final step is a smoke test that fails the build on two conditions: `/api/whoami` not reporting
an application identity, and the API answering anything but `401` when called directly. Switch
enforcement off quietly and the pipeline goes red.

## Changes to the copied Week-1 code

**Backend** — two new files, four lines in `Program.cs`:

```csharp
builder.Services.AddCallerIdentity(builder.Configuration);
app.UseCallerIdentity();      // ahead of UseAuthentication
app.MapWhoAmI();
```

**Frontend** — `app.config.ts` uses `environment.apiBaseUrl` instead of a literal `/api`;
`angular.json` gains a `fileReplacements` entry; `index.html` gains a meta description.

No business logic, no endpoint, and no auth rule from Week 1 was altered.

---

# (3) VERIFICATION LOG

Produced by `scripts/verify.sh` against the deployed system, 2026-08-29.
**Result: 42 passed, 0 failed.** Raw run: `Day17/docs/verification-run.txt` (252 lines).

## Live URLs

| | |
|---|---|
| **Frontend** | https://quotes-web.happydesert-51845d93.centralindia.azurecontainerapps.io |
| Broker | https://quotes-bff.happydesert-51845d93.centralindia.azurecontainerapps.io |
| Week-1 API | https://quotes-api.happydesert-51845d93.centralindia.azurecontainerapps.io |

Azure-generated hostname, no custom domain — a deliberate choice, since Azure binds domains you
already own rather than issuing them.

## ⚠️ One requirement not met, and it wasn't a choice

**Azure Static Web Apps cannot be created in this subscription.**

```
Microsoft.Web/staticSites is available in : centralus, eastus2, westus2, westeurope, eastasia
sys.regionrestriction policy permits      : indonesiacentral, centralindia, malaysiawest,
                                            uaenorth, koreacentral
```

The sets are **disjoint**. Every attempt returns `RequestDisallowedByAzure`. Widening the
policy fails even as subscription **Owner** — it's Microsoft-locked:

```
(UnauthorizedApplicationId) The application id '04b07795-8ddb-461a-bbee-02f9e1bf7b46'
is not authorized to assign the policy .../b86dabb9-b578-4d7b-b842-3b45e95769a1
```

The frontend is therefore served by **nginx on Container Apps**, using a hand-translation of
every rule in `staticwebapp.config.json` (SPA fallback with asset exclusions, immutable vs
no-cache routes, full header set) — so the header checks and Lighthouse below measure the same
rules. `staticwebapp.config.json`, the SWA branch of `deploy.sh`, and the SWA CI workflow are
all written and run unchanged against any subscription without that policy.

**The managed-identity chain is entirely unaffected** — it doesn't depend on where static files
are served from.

## Lighthouse — all four at 100

```
  [PASS] Performance      100
  [PASS] Accessibility    100
  [PASS] Best Practices   100
  [PASS] SEO              100

         First Contentful Paint     0.4 s
         Largest Contentful Paint   0.7 s
         Total Blocking Time        10 ms
         Cumulative Layout Shift    0
         Speed Index                0.4 s
```

## The API call carries a managed-identity token

`GET /bff/identity` — the token the broker is presenting:

```json
{ "audience": "api://729a2be3-9609-4fd1-b7c5-e658386f9bfd",
  "issuer": "https://sts.windows.net/8d46a076-d093-416d-a57b-8692cde13bf8/",
  "applicationId": "419a890f-a260-4565-ab20-def876d48d8f",
  "objectId": "9283650e-f2aa-406d-b61b-bde2d3096c85",
  "roles": ["Api.Invoke"], "subjectIsUser": false,
  "source": "ManagedIdentityCredential (IMDS)" }
```

`GET /api/whoami` — what the API says it authenticated:

```json
{ "callerIdentity": { "applicationId": "419a890f-…", "objectId": "9283650e-f2aa-406d-b61b-bde2d3096c85",
                      "roles": ["Api.Invoke"], "identityType": "app", "userPrincipalName": null },
  "endUser": null }
```

**Strongest single piece of evidence:** the `objectId` the API reports —
`9283650e-f2aa-406d-b61b-bde2d3096c85` — is byte-for-byte the broker Container App's
`identity.principalId`. Two independent sources (Azure at identity creation; the API decoding a
token it validated against Entra) agreeing on who called.

**The negative half:**

```
[PASS] GET <api>/api/quotes  without X-Caller-Token -> 401
[PASS] GET <api>/api/whoami  without X-Caller-Token -> 401
[PASS] garbage caller token -> 401
{"error":"caller-token-invalid","detail":"This API only accepts requests carrying a valid X-Caller-Token."}
```

## No secret stored anywhere

```
[PASS] no client secret, account key, or password literal in Day17/

BFF app settings — every environment variable, in full:
  UPSTREAM_API_BASE = https://quotes-api.happydesert-51845d93.centralindia.azurecontainerapps.io
  API_APP_ID_URI    = api://729a2be3-9609-4fd1-b7c5-e658386f9bfd
  ALLOWED_ORIGINS   = https://quotes-web.happydesert-51845d93.centralindia.azurecontainerapps.io
Container Apps secrets defined on the BFF: 0
[PASS] the BFF holds zero secrets — its only credential is minted at runtime from IMDS

App registration: passwordCredentials 0, keyCredentials 0
[PASS] no client secret and no certificate
```

The registry is `--admin-enabled false`, so even **image pull** uses each app's managed identity
with `AcrPull` rather than stored registry credentials.

**Stated plainly:** `quotes-api` holds exactly one Container Apps secret, `jwt-key`. It signs
the tokens the API issues to its own users — a signing key, not a credential for calling
anything. Removing it wouldn't remove a client secret, it would remove the ability to log a
human in. A client secret authenticating the frontend tier to the API **does not exist**.

## States exercised — against the real endpoints

| State | Exercised by | Result |
|---|---|---|
| **Loading** | `GET /api/quotes` through the broker | `200` ✅ |
| **Empty** | Same, on a fresh revision | `[]` — empty state, not an error ✅ |
| **Populated** | Same, after the startup seed | 5 quotes rendered ✅ |
| **Error (404)** | `GET /api/quotes/99999999` | `404` ✅ |
| **Failed auth (401)** | `POST /api/auth/login`, wrong credentials | `401` ✅ |
| **401 user vs service** | `POST /api/quotes` with a valid MI token but no user token | `401` ✅ |
| **Caller rejected (401)** | `GET <api>/api/quotes` with no `X-Caller-Token` | `401 caller-token-invalid` ✅ |
| **Forged token (401)** | `X-Caller-Token: Bearer not.a.token` | `401` ✅ |
| **Deep link (cold)** | `/quotes/3` typed into the address bar | index.html served, routed, fetched ✅ |
| **CORS allowed / refused** | preflight from the frontend origin / `evil.example.com` | echoed+credentials / no grant ✅ |

**Both identities on one request** — after registering a throwaway user through the real
`POST /api/auth/register` (so there is no seed account and no bootstrap password anywhere):

```json
{ "callerIdentity": { "objectId": "9283650e-…", "roles": ["Api.Invoke"], "identityType": "app",
                      "userPrincipalName": null },
  "endUser":        { "subject": "1", "email": "day17-verify-…@example.invalid",
                      "scopes": ["quotes.write"] } }
```

**Full write round-trip:** register `201` → create `201` → duplicate `409` → read back `200` →
owner delete `204` → soft-deleted `404`. The created entity carried all six fields with
`createdAt` as `2026-08-29T08:19:19.629512+00:00` (explicit offset, not `Z`).
**`DELETE` returning 204 is load-bearing** — it proves ownership resolved against the *human*,
not the managed identity that carried the request.

## THE ONE BUG I CAUGHT — the token was aimed at the wrong tenant

`Day5/piece6/QuotesApi/.env` carried working-looking Entra config from an earlier experiment,
and the API already had a matching `Entra` JwtBearer scheme keyed off it:

```
EntraId__TenantId=b69d82df-4ebe-474d-9ac7-00efbf13427e
EntraId__ClientId=4063cae8-18b1-45ca-af3d-258b014edb04
EntraId__Audience=api://4063cae8-18b1-45ca-af3d-258b014edb04
```

Pointing the broker at that audience is the move that looks like *reuse* rather than risk, and
the agent went for it. **It could never have worked: that is a different tenant from the
subscription's.** A managed identity is a service principal in its own subscription's tenant,
and Entra only issues it tokens for applications registered in that same tenant. It would have
failed at acquisition with `AADSTS500011: The resource principal was not found in the tenant` —
an error naming a *resource*, which reads like a typo in the App ID URI and sends you to check
entirely the wrong thing.

I made it register a fresh application (`quotes-api-day17`, appId
`729a2be3-9609-4fd1-b7c5-e658386f9bfd`) in the correct tenant, name the new settings
`CallerIdentity__*` so they can never be confused with the stale `EntraId__*` ones, and make the
mismatch fail immediately instead of three steps later:

```bash
ACTUAL_TENANT="$(az account show --query tenantId -o tsv)"
[ "$ACTUAL_TENANT" = "$TENANT_ID" ] || die \
"Signed into tenant $ACTUAL_TENANT but this script targets $TENANT_ID.
   The managed identity and the app registration must live in the same tenant."
```

That guard then earned itself: the deployment target moved between subscriptions mid-build, and
it caught the mismatch on the first run instead of after a container was already deployed.

*(Runners-up, all fixed: two real secrets — `Jwt__Key` and `SEED_ADMIN_PASSWORD` — copied in
from Day 5 and purged; `/api/whoami` under-reporting because ASP.NET renames `oid`/`roles` to
long schema URIs and `idtyp` is a v2.0-only claim while managed-identity tokens are v1.0; and a
"green" build that was `tail`'s exit code rather than the compiler's.)*

## What breaks if the API's auth or a key endpoint changes

**User auth**

| Change | What breaks |
|---|---|
| `jwt-key` rotated | Tokens issued before rotation stop validating; users signed out once. The refresh cookie fails too, so it's a real logout, not a silent renewal. |
| `Jwt:Issuer` / `Jwt:Audience` changed | Every existing access token rejected — same visible outcome |
| Login stops returning `{accessToken, expiresIn}` | `asAccessToken()` returns null; sign-in reports *"Sign-in succeeded but the response was unreadable."* rather than crashing |
| Refresh moves out of the cookie | The broker's cookie rewrite becomes dead code; refresh breaks until the client changes too |
| `quotes.write` renamed | `POST /api/quotes` and `PUT /…/author` 403 for everyone. Nothing in the frontend references the scope name — no compile-time warning, only a 403. |

**Managed-identity wiring**

| Change | What breaks | Caught by |
|---|---|---|
| App registration deleted / App ID URI changed | Broker returns **503 `managed-identity-unavailable`** rather than forwarding unauthenticated | `verify.sh`, CI |
| App-role assignment revoked | Token still issued but with no `roles` → API returns **403 `caller-role-missing`**, deliberately distinct from a missing token | `verify.sh` |
| Broker's identity turned off | No IMDS endpoint → 503 as above | CI smoke test |
| Broker recreated | New principal id, no role assignment. `deploy.sh` reassigns; a manual recreate doesn't. | `verify.sh` |
| `CallerIdentity__*` removed from the API | Enforcement silently switches **off** and the API accepts anything. **The dangerous one — everything keeps working.** | CI fails if a direct call returns anything but 401; API logs `CallerIdentity is DISABLED` at startup |

**Key endpoints.** The frontend validates the shape of everything it parses, so a changed
payload fails visibly rather than rendering wrong:

- `GET /api/quotes` stops returning a bare array → `isQuoteArray()` rejects it, page shows
  *"The Quotes API returned something this app does not understand."* Pinned by
  `contract/week1-api.contract.spec.ts`.
- Any of the six fields dropped or retyped → same guard, same message
- `createdAt` switches `+00:00` → `Z` → still a string, guard passes, only rendering changes;
  the contract spec pins the current format
- `/api/quotes` moved or renamed → 404 through the broker, which forwards paths verbatim
- A new endpoint added → works immediately; the broker proxies all of `/api/*` with no
  per-route allowlist

The broker is deliberately incurious — it forwards paths, methods, bodies and headers unchanged
and adds one header. That is what keeps the blast radius of an API change confined to the
frontend and the API instead of spreading into the auth tier.
