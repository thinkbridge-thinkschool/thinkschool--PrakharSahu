# Day 17 — Deploy to Azure Static Web Apps

The Week-16 Angular app, live, calling the Week-1 Quotes API with a **managed identity** and no
stored client secret.

## Live

| | |
|---|---|
| **Frontend** | https://quotes-web.happydesert-51845d93.centralindia.azurecontainerapps.io |
| Broker (BFF) | https://quotes-bff.happydesert-51845d93.centralindia.azurecontainerapps.io |
| Week-1 API | https://quotes-api.happydesert-51845d93.centralindia.azurecontainerapps.io |

**Lighthouse 100 / 100 / 100 / 100** (performance, accessibility, best practices, SEO) ·
verification **42 passed, 0 failed** · Azure-generated hostname, no custom domain by choice.

> **One requirement was not met.** Azure Static Web Apps cannot be created in this
> subscription: SWA exists only in `centralus, eastus2, westus2, westeurope, eastasia`, the
> subscription's `sys.regionrestriction` policy permits only `indonesiacentral, centralindia,
> malaysiawest, uaenorth, koreacentral`, and the sets are disjoint. The policy is
> Microsoft-locked — widening it fails even as subscription Owner. The frontend is served by
> nginx on Container Apps with a hand-translated copy of `staticwebapp.config.json`; the SWA
> configuration and CI remain in the repository and run unchanged against any subscription
> without that policy. Full detail and evidence:
> [VERIFICATION.md](VERIFICATION.md#️-read-this-before-the-rest-the-frontend-is-not-on-static-web-apps).

## The three deliverables

| | |
|---|---|
| [BRIEF.md](BRIEF.md) | The brief given to the agent — target, real endpoints and fields, auth requirement |
| [AGENT-OUTPUT.md](AGENT-OUTPUT.md) | What it built — SWA + CI/CD config and the managed-identity code |
| [VERIFICATION.md](VERIFICATION.md) | Live URL, Lighthouse, the MI proof, states exercised, and the bug caught |

## Layout

```
Day17/
├── frontend/    Angular 21 app          (from Day16/piece2/quotes-web)
├── backend/     .NET 10 Quotes API      (from Day5/piece6/QuotesApi)
├── bff/         managed-identity token broker   (new)
├── scripts/     deploy · verify · OIDC setup · URL stamping
└── docs/        Lighthouse report and captured verification run
```

The CI workflow lives at `.github/workflows/day17-deploy.yml` in the repository root, because
that is the only place GitHub Actions looks.

## How the pieces fit

```
browser ──► quotes-web    Angular bundle. Static files only, no identity.
              │           (intended: Static Web Apps Free — see the note above)
              │ cross-origin, credentialed
              ▼
            quotes-bff (Container App)   ← system-assigned managed identity
              │  gets a token from IMDS, attaches it as X-Caller-Token
              ▼
            quotes-api (Container App)   ← validates it against Entra, requires Api.Invoke
```

The broker exists because **neither a browser nor a Free-plan Static Web App can hold a
managed identity** — SWA's own identity feature only retrieves Key Vault secrets, and the
"bring your own Functions app" route Microsoft points you at needs the Standard plan. A
Container App with a system-assigned identity costs nothing extra.

Two identities travel on every request and neither masks the other:

| Header | Identity | Issued by | Validated by |
|---|---|---|---|
| `Authorization` | the end user | the Quotes API itself | `SelfHosted` JwtBearer scheme |
| `X-Caller-Token` | the calling service | Microsoft Entra | `CallerIdentity` JwtBearer scheme |

Putting the managed-identity token in `Authorization` — the obvious first design — collapses
them and breaks per-user ownership on every write.

## Running it

Deploy, then verify:

```bash
./scripts/deploy.sh
./scripts/verify.sh | tee docs/verification-run.txt
```

Full runbook, including the custom-domain steps that were documented but not executed:
[DEPLOY.md](DEPLOY.md).

## Locally

The two tiers run independently; the broker is only needed to exercise the managed-identity
path.

```bash
# API — needs a signing key, which is deliberately never defaulted
cd backend && Jwt__Key="$(openssl rand -base64 48)" dotnet run     # :5267

# Frontend — proxies /api to :5267, so no CORS and no broker involved
cd frontend && npm start                                            # :4200
```

To exercise the broker locally, `DefaultAzureCredential` falls through to whoever is signed
in to `az`, so that account needs the `Api.Invoke` role:

```bash
cd bff
BFF_LOCAL=true \
UPSTREAM_API_BASE=http://localhost:5267 \
API_APP_ID_URI=api://<app-id> \
ALLOWED_ORIGINS=http://localhost:4200 \
npm start                                                           # :8080
```

## Relationship to the other days

`frontend/` and `backend/` are **copies**, taken so Day 5 and Day 16 stay as their own days'
record. The backend copy gained two files and four lines in `Program.cs`; the frontend copy
gained an environments folder and three small edits. Nothing else was changed — the full list
is at the end of [AGENT-OUTPUT.md](AGENT-OUTPUT.md#changes-to-the-copied-week-1-code).
