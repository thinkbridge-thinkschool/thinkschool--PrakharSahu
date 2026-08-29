# Deliverable 3 — Verification log

Everything below was produced by `scripts/verify.sh` against the deployed system on
**2026-08-29**. The captured run is [`docs/verification-run.txt`](docs/verification-run.txt)
(252 lines) and the Lighthouse report is
[`docs/lighthouse.report.html`](docs/lighthouse.report.html). Nothing here is asserted from
reading source.

**Result: 42 passed, 0 failed.**

---

## Live URLs

| | |
|---|---|
| **Frontend** | https://quotes-web.happydesert-51845d93.centralindia.azurecontainerapps.io |
| Broker (BFF) | https://quotes-bff.happydesert-51845d93.centralindia.azurecontainerapps.io |
| Week-1 API | https://quotes-api.happydesert-51845d93.centralindia.azurecontainerapps.io |

Subscription `132ef106-…` ("Azure for Students") · tenant `8d46a076-…` · resource group
`rg-quotes-day17` · region `centralindia`.

**On the generated hostname:** this uses Azure's own hostname by choice. No custom domain is
bound. Azure does not issue domains — Static Web Apps *binds* one you already own at a
registrar — and no domain is owned, so a custom hostname is something that would have to be
bought rather than deployed. The generated name gets the same auto-renewing TLS certificate
and behaves identically. Binding steps are recorded in
[DEPLOY.md](DEPLOY.md#custom-domain--deliberately-not-used) if that ever changes.

## ⚠️ Read this before the rest: the frontend is not on Static Web Apps

**Azure Static Web Apps could not be created in this subscription, and the frontend is served
by nginx on Container Apps instead.** This is the one requirement that was not met, and it was
not a choice.

```
Microsoft.Web/staticSites is available in : centralus, eastus2, westus2, westeurope, eastasia
sys.regionrestriction policy permits       : indonesiacentral, centralindia, malaysiawest,
                                             uaenorth, koreacentral
```

The sets are **disjoint**. Every attempt returns:

```
(RequestDisallowedByAzure) Resource 'swa-quotes-day17' was disallowed by Azure: This policy
maintains a set of best available regions where your subscription can deploy resources.
```

Attempting to widen the policy fails even as subscription **Owner** — it is Microsoft-locked:

```
(UnauthorizedApplicationId) The application id '04b07795-8ddb-461a-bbee-02f9e1bf7b46' is not
authorized to assign the policy .../b86dabb9-b578-4d7b-b842-3b45e95769a1
```

What that does and does not change:

| | |
|---|---|
| **Unaffected** | The entire managed-identity chain — app registration, app role, system-assigned identity, IMDS token acquisition, and the API validating it. None of it depends on where the static files are served from. |
| **Affected** | The host. `frontend/public/staticwebapp.config.json`, the SWA branch of `deploy.sh`, and the SWA CI workflow are all written and unchanged; they run against any subscription without this policy. |

The nginx config (`frontend/nginx.conf.template`) is a **hand-translation of every rule in
`staticwebapp.config.json`** — SPA fallback with asset exclusions, immutable vs `no-cache`
routes, and the full header set — so the header checks and the Lighthouse score below measure
the same rules the real host would serve. It is a faithful stand-in, not the real thing, and
the deliverable is one requirement short because of it.

---

## 1. The live URL loads

```
--- GET https://quotes-web.happydesert-51845d93.centralindia.azurecontainerapps.io
  HTTP 200
  [PASS] responds 200
  [PASS] index.html contains the Angular root element

--- Deep link falls back to index.html (client-side routing)
  [PASS] GET /quotes/1 -> 200 (navigationFallback works)

--- Security headers
  [PASS] content-security-policy present
  [PASS] strict-transport-security present
  [PASS] x-content-type-options present
  [PASS] referrer-policy present
```

The served CSP, verbatim:

```
default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:;
font-src 'self'; connect-src 'self' https://quotes-bff.happydesert-51845d93.centralindia.azurecontainerapps.io;
frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'
```

`connect-src` names the broker and nothing else, so an injected script has nowhere to
exfiltrate to.

## 2. Lighthouse — all four categories at 100

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

Desktop preset. Two real defects had to be fixed to get there — see §6.4; the first run scored
Best Practices 92 and Accessibility 98.

## 3. The API call carries a managed-identity token

### The token the broker holds — `GET /bff/identity`

```json
{
  "audience": "api://729a2be3-9609-4fd1-b7c5-e658386f9bfd",
  "issuer": "https://sts.windows.net/8d46a076-d093-416d-a57b-8692cde13bf8/",
  "tenantId": "8d46a076-d093-416d-a57b-8692cde13bf8",
  "applicationId": "419a890f-a260-4565-ab20-def876d48d8f",
  "objectId": "9283650e-f2aa-406d-b61b-bde2d3096c85",
  "roles": ["Api.Invoke"],
  "subjectIsUser": false,
  "expiresAt": "2026-08-30T08:12:25.000Z",
  "source": "ManagedIdentityCredential (IMDS)"
}
```

### What the API says it authenticated — `GET /api/whoami`

```json
{
  "callerIdentity": {
    "applicationId": "419a890f-a260-4565-ab20-def876d48d8f",
    "objectId": "9283650e-f2aa-406d-b61b-bde2d3096c85",
    "tenantId": "8d46a076-d093-416d-a57b-8692cde13bf8",
    "audience": "api://729a2be3-9609-4fd1-b7c5-e658386f9bfd",
    "roles": ["Api.Invoke"],
    "identityType": "app",
    "userPrincipalName": null
  },
  "endUser": null
}
```

**The strongest single line of evidence:** the `objectId` the API reports —
`9283650e-f2aa-406d-b61b-bde2d3096c85` — is byte-for-byte the broker Container App's
`identity.principalId` recorded in `.deploy-output.json` as `bffPrincipalId`. Two independent
sources, one produced by Azure at identity-creation time and one by the API decoding a token
it validated against Entra, agreeing on who called.

```
  [PASS] token came from IMDS (ManagedIdentityCredential), not a secret
  [PASS] token carries the Api.Invoke app role
  [PASS] app-only token — no user in the loop
  [PASS] API confirms the caller is an application identity
  [PASS] API validated the Api.Invoke role on the incoming token
  [PASS] no user principal on the caller token
```

### The negative half — the API refuses anything else

```
  [PASS] GET <api>/api/quotes  without X-Caller-Token -> 401
  [PASS] GET <api>/api/whoami  without X-Caller-Token -> 401
  [PASS] garbage caller token -> 401
```

```json
{"error":"caller-token-invalid",
 "detail":"This API only accepts requests carrying a valid X-Caller-Token."}
```

The API's public FQDN is reachable by anyone; without a token Entra minted for a principal
holding `Api.Invoke`, it serves nothing.

## 4. States exercised, against the real Week-1 endpoints

Every row ran through the deployed broker against the real routes from
`Extensions/QuoteEndpointExtensions.cs` and `Endpoints/AuthEndpoints.cs`.

| State | Exercised by | Result |
|---|---|---|
| **Loading** | `GET /api/quotes` through the broker | `200` ✅ |
| **Empty** | Same, on a fresh revision before seeding was added | `[]`, count 0 — empty state, not an error ✅ (see §6.7) |
| **Populated** | Same, after the startup seed | 5 quotes rendered ✅ |
| **Deep link (cold)** | `GET /quotes/3` typed straight into the address bar | index.html served, Angular routed, quote fetched and rendered ✅ |
| **Error (404)** | `GET /api/quotes/99999999` | `404` ✅ |
| **Failed auth (401)** | `POST /api/auth/login`, wrong credentials | `401` ✅ |
| **401, user vs service** | `POST /api/quotes` with a valid MI token but **no** user token | `401` ✅ |
| **Caller rejected** | `GET <api>/api/quotes` with no `X-Caller-Token` | `401 caller-token-invalid` ✅ |
| **Forged caller token** | `X-Caller-Token: Bearer not.a.token` | `401` ✅ |
| **CORS allowed** | Preflight from the frontend origin | origin echoed, `allow-credentials: true` ✅ |
| **CORS refused** | Preflight from `https://evil.example.com` | no CORS grant ✅ |

### Both identities on one request

A throwaway user is registered through the real `POST /api/auth/register` — so there is no
seed account and no bootstrap password to store anywhere — and then `/api/whoami` is called
again *with* that user's token:

```json
{
  "callerIdentity": { "objectId": "9283650e-…", "roles": ["Api.Invoke"], "identityType": "app",
                      "userPrincipalName": null },
  "endUser":        { "subject": "1", "email": "day17-verify-…@example.invalid",
                      "scopes": ["quotes.write"] }
}
```

Both are present and distinct. That is the two-header design working: the service identity and
the human identity travel on one request and neither masks the other.

### Full write round-trip

```
  [PASS] registration returned a usable access token          POST /api/auth/register -> 201
  [PASS] created -> 201                                       POST /api/quotes
  [PASS] response carries 'id','text','author','createdAt','isDeleted','userId'   (all six)
  [PASS] duplicate rejected -> 409                            same author+text again
  [PASS] read back -> 200                                     GET /api/quotes/1
  [PASS] owner delete -> 204                                  DELETE /api/quotes/1
  [PASS] soft-deleted quote -> 404                            GET /api/quotes/1
```

The created entity, as returned:

```json
{"id":1,"text":"That brain of mine is something more than merely mortal.",
 "author":"Ada Lovelace","createdAt":"2026-08-29T08:19:19.629512+00:00",
 "isDeleted":false,"userId":2}
```

All six fields, and `createdAt` with the explicit `+00:00` offset the frontend's `isQuote()`
guard requires. **`DELETE` returning 204 is load-bearing**: `IsOwnerHandler` compares
`quote.UserId` to `sub`, so a 204 proves ownership resolved against the *human*, not against
the managed identity that carried the request.

## 5. No secret anywhere in the managed-identity path

```
--- Repository scan
  [PASS] no client secret, account key, or password literal in Day17/

--- BFF app settings — every environment variable, in full
      UPSTREAM_API_BASE = https://quotes-api.happydesert-51845d93.centralindia.azurecontainerapps.io
      API_APP_ID_URI    = api://729a2be3-9609-4fd1-b7c5-e658386f9bfd
      ALLOWED_ORIGINS   = https://quotes-web.happydesert-51845d93.centralindia.azurecontainerapps.io
  Container Apps secrets defined on the BFF: 0
  [PASS] the BFF holds zero secrets — its only credential is minted at runtime from IMDS

--- App registration has no credentials of its own
  passwordCredentials: 0   keyCredentials: 0
  [PASS] no client secret and no certificate
```

Three environment variables, all public identifiers, no secrets at all. The registry is
`--admin-enabled false`, so even image pull uses each app's managed identity with `AcrPull`
rather than stored registry credentials.

### The one secret that does exist, stated plainly

`quotes-api` holds exactly one Container Apps secret: **`jwt-key`**. It signs the tokens the
API issues to its own end users. It is a signing key, not a credential for calling anything —
removing it would not remove a client secret, it would remove the ability to log a human in.
It is generated at deploy time, stored as a Container Apps secret, never written to disk or to
this repository, and regenerated only when absent so a redeploy does not invalidate live
sessions.

What the exercise asks about — a client secret authenticating the frontend tier to the API —
**does not exist**.

---

## 6. Bugs and wrong assumptions caught

### 6.1 The one the exercise asks for — the token was aimed at the wrong tenant

`Day5/piece6/QuotesApi/.env` carried working-looking Entra configuration from an earlier
experiment, and the API already had a matching `Entra` JwtBearer scheme keyed off it:

```
EntraId__TenantId=b69d82df-4ebe-474d-9ac7-00efbf13427e
EntraId__ClientId=4063cae8-18b1-45ca-af3d-258b014edb04
EntraId__Audience=api://4063cae8-18b1-45ca-af3d-258b014edb04
```

Reusing that audience is the move that looks like prudence rather than risk. It could never
have worked: **the subscription's tenant is a different one entirely.** A managed identity is
a service principal in its own subscription's tenant, and Entra will only issue it tokens for
applications registered in that same tenant. The request would have failed at acquisition with
`AADSTS500011: The resource principal was not found in the tenant` — an error naming a
*resource*, which reads like a typo in the App ID URI and sends you to check the wrong thing.

This then happened **for real, twice**: the deployment target moved from the personal
subscription (tenant `8733141f-…`) to the student one (tenant `8d46a076-…`) mid-build, and the
Week-1 API had to be redeployed rather than reused in place, because the identity and the app
registration cannot straddle two tenants.

Fixed by registering `quotes-api-day17` fresh in the correct tenant, naming the new settings
`CallerIdentity__*` so they can never be confused with the stale `EntraId__*` ones, and making
the mismatch fail loudly and immediately instead of three steps later:

```bash
ACTUAL_TENANT="$(az account show --query tenantId -o tsv)"
[ "$ACTUAL_TENANT" = "$TENANT_ID" ] || die \
"Signed into tenant $ACTUAL_TENANT but this script targets $TENANT_ID.
   The managed identity and the app registration must live in the same tenant."
```

### 6.2 Two real secrets were copied into the repository

Copying `Day5/piece6/QuotesApi` wholesale brought `.env` and `.azure/` with it:

```
.env                          Jwt__Key=…
.azure/quotes-env-123/.env    JWT_SIGNING_KEY=…   SEED_ADMIN_PASSWORD=…
```

Uncaught, the deliverable for an exercise about *not storing secrets* would have shipped with a
JWT signing key and an admin password inside it. Caught by scanning the copy before anything
was committed; both deleted, `.gitignore` extended to `.env`, `.env.*` and `.azure/`, and the
scan folded into `verify.sh` so it stays caught.

### 6.3 The API under-reported the identity it had just authenticated

`/api/whoami` initially returned `roles: []`, `objectId: null`, `identityType: "(not stamped)"`
— while `/bff/identity`, decoding the very same token, showed `roles: ["Api.Invoke"]`. Two
causes, both worth knowing:

- **ASP.NET's inbound claim mapping** rewrites short OIDC claim names into long WS-Federation
  URIs: `oid` becomes `…/identity/claims/objectidentifier`, `roles` becomes
  `…/identity/claims/role`. Nothing errors; the claims are simply not where anyone reading the
  token would look, so lookups return null. Fixed with `options.MapInboundClaims = false` and
  an explicit `RoleClaimType = "roles"`.
- **`idtyp` is a v2.0-only claim.** A managed identity asking IMDS for a token against a custom
  API gets a **v1.0** token back — issuer `sts.windows.net`, no `idtyp` at all. Reporting
  "(not stamped)" was accurate and useless. Now inferred from the token's shape: an `appid`
  with no `scp`/`upn`/`name` is an application, which is a statement about the token rather
  than a guess.

Enforcement was never actually broken — the middleware also checked `ClaimTypes.Role`, so the
role test passed — but the *evidence* was wrong, and a verification log built on it would have
been quietly false. This is the bug that would have mattered most had it shipped.

### 6.4 A Content-Security-Policy the app violated on its own first paint

First Lighthouse run: Best Practices **92**, with a console error:

```
Executing inline event handler violates the following Content Security Policy directive
'script-src 'self''
```

Angular's critical-CSS inlining (Beasties) emits
`<link rel="stylesheet" … onload="this.media='all'">` — an inline event handler that
`script-src 'self'` correctly blocks. The tempting fix is adding `'unsafe-inline'` to
`script-src`, which trades a real XSS control for a few milliseconds on a 7 kB stylesheet.
Turned off `inlineCritical` instead; FCP stayed at 0.4 s and Best Practices went to 100.

Accessibility 98 was a second, unrelated defect: the app had no `<main>` landmark, so
screen-reader users had no way to skip the masthead and nav on every page. Wrapping the router
outlet in `<main>` took it to 100.

### 6.5 A green build that was not green

`dotnet build … | tail -25` was reported as passing on exit code 0. In a shell pipeline the
exit status is the **last** command's — that 0 came from `tail`. The compile had failed:

```
error CS1061: 'HttpContext' does not contain a definition for 'AuthenticateAsync'
```

(a missing `using Microsoft.AspNetCore.Authentication`). Caught by the local smoke test, which
runs the API rather than reading a build log. Build commands are no longer piped anywhere
before their status is checked — the same masking hid a failed deploy twice more.

### 6.6 Configuration applied after the container was already starting

The first Container Apps deployment hung. `deploy.sh` set `Jwt__Key` *after* switching the app
to its real image, and the API refuses to start without a signing key — deliberately, so it can
never sign with a key from source control. So the first revision on the real image crash-looped
and `az containerapp update --image` sat waiting for a revision that was never going to become
healthy. Both apps now apply configuration through a pre-image hook. The broker had the
identical latent bug: `bff/src/config.js` calls `required()` on each variable and throws.

A related ordering trap in the same function: creating a Container App directly against a
private-registry image means the very first pull happens before the identity exists to hold
`AcrPull`, and it fails as an image-pull error that never mentions role assignments. Each app
now boots on a public image and is switched over once the grant is in place.

### 6.7 The live link looked broken because the database is ephemeral

Opening the deployed URL showed an empty list. Nothing was actually wrong — the app had loaded,
called `GET /api/quotes` through the broker, got `200 []`, and correctly rendered its empty
state. But "No quotes left" is indistinguishable from a broken deployment to anyone opening the
link, and the cause was structural rather than a one-off: **SQLite lives inside the container**,
so the table is emptied by every deploy, scale-to-zero, and platform restart. The verification
run had also just created a quote and deleted it again.

Seeding a few quotes at startup when the table is empty fixes it permanently:

```csharp
if (!db.Quotes.Any()) { /* five quotes, UserId 0 */ }
```

`UserId 0` deliberately belongs to nobody — `IsOwnerHandler` compares the quote's `UserId` to
the caller's `sub` and no real user is ever id 0, so the seed content is readable by everyone
and deletable by no one. The first person to sign in cannot wipe the demo.

Worth noting for the write-up: the seed text has to satisfy `TextRules`, which is an
**allow-list** — letters, digits, whitespace and `. , ' " - ? ( )` only. No exclamation marks,
semicolons, or em dashes. A seed quote that fails validation is logged rather than thrown, so a
typo in demo data can never stop the API from booting.

The durability limitation itself is accepted, not fixed: persisting the database would mean
attaching Azure Files or moving to Azure SQL, which is beyond a deployment exercise. It is
recorded here so nobody mistakes it for a bug later.

### 6.8 Five platform constraints, discovered one at a time

| Failure | Cause | Fix |
|---|---|---|
| `MaxNumberOfRegionalEnvironmentsInSubExceeded` | One Container Apps environment per region per subscription | Reuse the existing one |
| `--only-show-errors is not allowed` | `az containerapp up` rejects the flag | Removed |
| `Impossible to find the source directory /d/…` | `MSYS_NO_PATHCONV=1` protects Azure resource ids from Git Bash but leaves real paths untranslated for native `az`/`node`/`npx` | `winpath()` via `cygpath` at each path argument |
| `RequestDisallowedByAzure` (registry) | `az containerapp up` chose its own region for the ACR it auto-created | Create the ACR explicitly in an allowed region |
| `TasksOperationsNotAllowed` | **ACR Tasks is not offered on this subscription**, so `az acr build` and `az containerapp up --source` are both unusable | Build locally with Docker and push |

The last one improved the design: with cloud build gone, the registry is created
`--admin-enabled false` and images are pulled with each app's managed identity, so no registry
credentials are stored either — the convenient path would have written a username and password
into both apps' settings.

A sixth, in the verification harness rather than the deployment: on Windows the `curl` on PATH
is the native `curl.exe`, so `-o /tmp/x.json` writes to `C:\tmp\` while bash and Node read MSYS
`/tmp`. Checks failed with "No such file" on files curl had just written successfully.

---

## 7. What breaks if the API's auth or a key endpoint changes

### If the API's user auth changes

| Change | What breaks | Where it surfaces |
|---|---|---|
| `jwt-key` rotated | Nothing structural. Tokens issued before rotation stop validating and users are signed out once; the refresh cookie fails too, so it is a real logout rather than a silent renewal. | `POST /api/auth/refresh` → 401 → interceptor routes to sign-in |
| `Jwt:Issuer` / `Jwt:Audience` changed | Every existing access token rejected. Same visible outcome. | as above |
| Login stops returning `{ accessToken, expiresIn }` | `asAccessToken()` returns null and sign-in reports *"Sign-in succeeded but the response was unreadable."* rather than crashing. Deliberate. | `auth-api.client.ts` |
| Refresh moves out of the cookie | The broker's cookie rewrite becomes dead code; refresh breaks until the client changes too. | `refresh-on-401.interceptor.ts` |
| `quotes.write` scope renamed | `POST /api/quotes` and `PUT /…/author` return 403 for everyone. Nothing in the frontend references the scope name, so there is no compile-time warning — only a 403. | policy `can-edit-quotes` |

### If the managed-identity wiring changes

| Change | What breaks | Detected by |
|---|---|---|
| App registration deleted / App ID URI changed | Token acquisition fails; the broker returns **503 `managed-identity-unavailable`** rather than forwarding an unauthenticated request | `verify.sh`, CI smoke test |
| App-role assignment revoked | A token is still issued but carries no `roles`; the API returns **403 `caller-role-missing`** — distinguishable from a missing token on purpose | `verify.sh` |
| Broker's system-assigned identity turned off | `DefaultAzureCredential` finds no IMDS endpoint; 503 as above | CI smoke test |
| Broker recreated | New principal id, no role assignment. `deploy.sh` reassigns it; a manual recreate does not. | `verify.sh` |
| `CallerIdentity__*` removed from the API | Enforcement silently switches **off** and the API accepts anything. **This is the dangerous one — everything keeps working.** | CI fails the build if a direct call returns anything but 401; the API logs `CallerIdentity is DISABLED` at startup |

### If a key endpoint changes

The frontend validates the shape of everything it parses, so a changed payload fails visibly
rather than rendering wrong:

- **`GET /api/quotes` stops returning a bare array** (gains an `{ items, total }` envelope) →
  `isQuoteArray()` rejects it and the page shows *"The Quotes API returned something this app
  does not understand."* Pinned by `contract/week1-api.contract.spec.ts`.
- **Any of the six fields dropped or retyped** → same guard, same message.
- **`createdAt` switches from `+00:00` to `Z`** → still a string, so the guard passes and only
  rendering changes. The contract spec pins the current format.
- **`/api/quotes` moved or renamed** → 404 through the broker, which forwards paths verbatim
  and does not rewrite routes.
- **A new endpoint added** → works immediately; the broker proxies all of `/api/*` with no
  per-route allowlist.

The broker is deliberately incurious — it forwards paths, methods, bodies and headers unchanged
and adds one header. That is what keeps the blast radius of an API change confined to the
frontend and the API instead of spreading into the auth tier.

---

## 8. Reproducing this

```bash
az login --tenant 8d46a076-d093-416d-a57b-8692cde13bf8 \
         --scope "https://management.core.windows.net//.default"
./Day17/scripts/deploy.sh
./Day17/scripts/verify.sh | tee Day17/docs/verification-run.txt
```

`deploy.sh` is idempotent — the run that produced this log reused the app registration, app
role, service principal, registry and signing key rather than recreating them. A local Docker
daemon is required, because ACR Tasks is unavailable on this subscription.
