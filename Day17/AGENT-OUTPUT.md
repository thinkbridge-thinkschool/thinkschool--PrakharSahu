# Deliverable 2 — What the agent built

Two things the brief asked for: the SWA + CI/CD configuration, and the code that
authenticates to the Week-1 API with a managed identity. Both are summarised here and live
in full in the files named against each section.

---

## The problem the brief did not anticipate

The brief said "the frontend tier must authenticate to the API with a managed identity" and
left the placement open. Working it through produced one hard constraint the brief had not
accounted for:

> A browser cannot hold a managed identity, and — less obviously — **neither can a Static Web
> App on the Free plan.**

Static Web Apps *does* have a managed identity feature, which is what makes this trap easy to
fall into. It is scoped to retrieving Key Vault secrets for the platform's own use. It cannot
be used by application code. Microsoft's own guidance is explicit: if your API needs a managed
identity, use "bring your own Functions app" — and **BYOF requires the Standard plan**
(~USD 9/month). The Free plan gives you *managed* functions only, which run in a
Microsoft-owned environment where no identity can be assigned.

So there were exactly three options:

| Option | Cost | Verdict |
|---|---|---|
| SWA managed functions | free | **Impossible.** No managed identity available. |
| SWA Standard + BYOF Functions | ~$9/mo | Works, same-origin `/api/*`, no trade-offs. |
| SWA Free + broker as a Container App | ~$0 | Works. Cross-origin, so CORS and a cookie downgrade. |

The third was chosen, deliberately and with the cost named. See
[VERIFICATION.md](VERIFICATION.md#what-this-architecture-costs) for the wart it buys.

## Architecture as built

> Static Web Apps turned out to be **uncreatable in the target subscription** — its five
> regions and the subscription's `sys.regionrestriction` allow-list do not intersect, and the
> policy is Microsoft-locked. The frontend is served by nginx on Container Apps with a
> hand-translated copy of `staticwebapp.config.json`. Everything below about the identity
> chain is unchanged and live; only the first box differs. Evidence and the exact errors:
> [VERIFICATION.md](VERIFICATION.md#️-read-this-before-the-rest-the-frontend-is-not-on-static-web-apps).

```
                 https://quotes-web.happydesert-51845d93.centralindia.azurecontainerapps.io
  browser ─────► Angular 21 bundle, 83 kB transferred. Static files only —
     │            no functions, no identity, nothing server-side.
     │            (intended host: Azure Static Web Apps, Free)
     │
     │  fetch(), cross-origin, credentialed
     ▼
  https://quotes-bff.happydesert-51845d93.centralindia.azurecontainerapps.io
     quotes-bff — Container App, SYSTEM-ASSIGNED MANAGED IDENTITY
       · DefaultAzureCredential asks IMDS for a token, per request, cached to expiry
       · scope: api://<app-id>/.default
       · attaches it as  X-Caller-Token: Bearer <token>
       · forwards the end user's own Authorization header untouched
     │
     ▼
  https://quotes-api.happydesert-51845d93.centralindia.azurecontainerapps.io
     quotes-api — the unchanged Week-1 API, plus one middleware
       · validates X-Caller-Token against Entra: issuer, audience, lifetime, signature
       · requires the Api.Invoke app role
       · rejects /api/* with 401 when the token is absent or invalid
       · still authenticates the end user from Authorization, exactly as before
```

Nothing in this diagram holds a credential. The only one that exists is minted by the
platform, per request, and expires.

## Why two headers instead of one

The obvious design — put the managed-identity token in `Authorization` — was tried first and
is wrong. `Authorization` already carries the end user, and the API's ownership rule reads
`sub` off whatever principal it finds there:

```csharp
// Day17/backend/Authorization/IsOwnerHandler.cs — unchanged from Week 1
quote.UserId == sub
```

Overwrite that header and every quote appears to belong to the broker's service principal.
`DELETE /api/quotes/{id}` would then either succeed for everyone or fail for everyone,
depending on which way the comparison fell. The two identities are genuinely different facts
about one request — *which service* is calling, and *which human* it is calling for — so they
travel in two headers.

| Header | Identity | Issued by | Validated by |
|---|---|---|---|
| `Authorization` | the end user | the Quotes API itself (HS256) | `SelfHosted` JwtBearer scheme |
| `X-Caller-Token` | the calling service | Microsoft Entra | `CallerIdentity` JwtBearer scheme |

## The code

### Acquiring the token — `bff/src/token-broker.js`

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

Three details that are load-bearing:

- **`/.default`, not the bare App ID URI.** For an app-only token this suffix means "every
  app role already granted to this identity". Passing the bare URI yields a token the API
  rejects, and the error does not say why.
- **A five-minute refresh margin**, because the API validates lifetime with
  `ClockSkew = TimeSpan.Zero`. With no tolerance on the far side, a token that is "valid for
  another few seconds" here is already expired there, and it presents as an intermittent 401.
- **Throws rather than forwarding without a token**, so a broken identity surfaces as a 503
  naming this tier instead of a confusing 401 from the API.

### Attaching it — `bff/src/server.js`

```js
headers.set('X-Caller-Token', `Bearer ${callerToken}`);
```

The same file also rewrites the API's refresh cookie for the cross-site hop, and forwards
the request body as an opaque `Buffer` so nothing is re-serialised in transit.

### Validating it — `backend/Extensions/CallerIdentityExtensions.cs`

A second JwtBearer scheme whose only unusual part is where it reads the token:

```csharp
options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
options.Audience  = audience;
options.Events = new JwtBearerEvents
{
    OnMessageReceived = context =>
    {
        var header = context.Request.Headers[CallerTokenHeader].ToString();
        // …strip "Bearer ", set context.Token
    }
};
```

Then middleware over `/api/*` that requires the token to validate, to be app-only
(`idtyp == "app"`), and to carry the `Api.Invoke` role. `/health` is deliberately excluded —
a Container Apps probe cannot hold an identity, and failing it would take the revision down
rather than secure it.

**The whole scheme is inert unless `CallerIdentity:TenantId` and `CallerIdentity:Audience`
are both set**, so Week-1's local runs and integration tests behave exactly as before.

### Proving it — `backend/Endpoints/WhoAmIEndpoint.cs`

`GET /api/whoami` reports what the API just authenticated: the caller's `appid`, `oid`,
`roles`, and `idtyp`, alongside the end user if there is one. It turns "the call carries a
managed-identity token" from a claim about source code into something the server says out
loud. Identifiers only — never tokens.

## What the subscription refused, and what it improved

Five deployment attempts failed before one succeeded, each on a genuine platform constraint
rather than a typo. Two of them changed the design for the better.

| Failure | Cause |
|---|---|
| `MaxNumberOfRegionalEnvironmentsInSubExceeded` | One Container Apps environment per region per subscription. Reused the existing one; an environment is a shared boundary, not a per-project resource. |
| `Impossible to find the source directory /d/…` | `MSYS_NO_PATHCONV=1` stops Git Bash mangling Azure resource ids, but then real filesystem paths reach `az`, `node` and `npx` — native Windows processes — untranslated. Added `winpath()` at each such argument. |
| `RequestDisallowedByAzure` | An "Allowed resource deployment regions" policy limits this subscription to five regions, and `az containerapp up` chose its own region for the registry it auto-created. |
| `TasksOperationsNotAllowed` | **ACR Tasks is not offered on this subscription at all**, so `az acr build` and `az containerapp up --source` are both unusable. |

The last two forced the registry to be created explicitly — and that turned out to be the
better design anyway:

```bash
az acr create -n "$ACR_NAME" -g "$RESOURCE_GROUP" -l "$LOCATION" \
  --sku Basic --admin-enabled false
```

`--admin-enabled false` means there is no registry username and password to store as a
Container Apps secret. Images are pulled with each app's **own managed identity**:

```bash
az role assignment create --assignee-object-id "$principal" \
  --role AcrPull --scope "$ACR_ID"
az containerapp registry set -n "$name" -g "$RESOURCE_GROUP" \
  --server "$ACR_SERVER" --identity system
```

So the managed-identity story is not confined to the API call. The same mechanism pulls the
images, and the convenient path — `containerapp up` with an admin-enabled registry — would
have quietly written a credential into both apps' settings.

The order of that sequence is load-bearing. The app must exist before it has an identity, the
identity before it can hold `AcrPull`, and the role before the app is pointed at a private
image. Creating the app directly against the private image inverts it: the first pull runs
with no credentials, the revision never goes healthy, and the failure reads as an image-pull
error that says nothing about role assignments. The script therefore boots each app on a
public image and switches it over once the grant is in place.

## SWA configuration — `frontend/public/staticwebapp.config.json`

Lands at the bundle root via Angular's `public/` assets glob.

- `navigationFallback` to `/index.html`, **with asset extensions excluded** — without the
  exclusions a mistyped asset path returns `index.html` with a `200`, the browser tries to
  parse HTML as JavaScript, and the failure surfaces nowhere near its cause.
- Immutable year-long caching for content-hashed files; `no-cache` for `index.html`, the one
  file whose name never changes.
- A CSP whose `connect-src` names the broker and nothing else, so an injected script has
  nowhere to exfiltrate to. `style-src` allows `'unsafe-inline'` because the Angular build
  inlines critical CSS; `script-src` does not, because every script it emits is an external
  file.
- HSTS, `nosniff`, `Referrer-Policy`, `Permissions-Policy`, `frame-ancestors 'none'`.

The broker's hostname is not knowable until its Container App exists, so it is stamped into
both the CSP and the Angular bundle at deploy time by `scripts/set-bff-url.mjs` — which fails
loudly if it finds nothing to replace, because the alternative is shipping a bundle pointed at
yesterday's host.

## CI/CD — `.github/workflows/day17-deploy.yml`

Path-filtered to `Day17/**`, so no other day's work triggers a deployment.

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

Those are repository **variables**, not secrets — a client id, tenant id and subscription id
are public identifiers, and hiding them only obscures which values actually matter. There is
no `AZURE_CREDENTIALS` blob and no client secret.

The Static Web Apps deployment token is the one credential this pipeline needs, and it is
**not stored either**: it is fetched at run time through the OIDC session with
`az staticwebapp secrets list`, used in the same step, and never written down. That is one
fewer long-lived credential than the standard SWA workflow template creates.

The last step is a smoke test that fails the build on two conditions: `/api/whoami` not
reporting an application identity, and the API answering anything other than `401` when
called directly. If someone quietly switches enforcement off, the pipeline goes red.

Federated credentials are created by `scripts/setup-github-oidc.sh`, scoped to the one
resource group these deployments touch rather than the whole subscription.

## File map

| Path | What it is |
|---|---|
| `frontend/` | Angular 21 app, copied from `Day16/piece2/quotes-web` |
| `frontend/public/staticwebapp.config.json` | SWA routing, caching, CSP, security headers |
| `frontend/src/environments/` | dev uses relative `/api`; production points at the broker |
| `backend/` | .NET 10 Quotes API, copied from `Day5/piece6/QuotesApi` |
| `backend/Extensions/CallerIdentityExtensions.cs` | **new** — validates the managed-identity token |
| `backend/Endpoints/WhoAmIEndpoint.cs` | **new** — reports the authenticated identities |
| `bff/` | **new** — the managed-identity token broker |
| `scripts/deploy.sh` | idempotent end-to-end provision and deploy |
| `scripts/verify.sh` | the verification log, generated by hitting the live system |
| `scripts/set-bff-url.mjs` | stamps the broker hostname into the bundle and the CSP |
| `scripts/setup-github-oidc.sh` | one-time federated-credential setup |
| `.github/workflows/day17-deploy.yml` | CI/CD (at the repository root, where Actions looks) |

## Changes to the copied Week-1 code

Kept deliberately small, so this stays a deployment exercise rather than a rewrite.

**Backend** — two new files, four lines added to `Program.cs`:

```csharp
builder.Services.AddCallerIdentity(builder.Configuration);
…
app.UseCallerIdentity();      // ahead of UseAuthentication
app.MapWhoAmI();
```

**Frontend** — three edits:

- `app.config.ts`: `provideQuotesApiBaseUrl('/api')` → `provideQuotesApiBaseUrl(environment.apiBaseUrl)`
- `angular.json`: a `fileReplacements` entry for the production environment file
- `index.html`: added a meta description and `theme-color` — Lighthouse's SEO audit fails
  outright without a description, and it was missing

No business logic, no endpoint, and no auth rule from Week 1 was altered.
