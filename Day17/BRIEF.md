# Deliverable 1 — The brief given to the agent

This is the instruction set the agent was told to build against. It is reproduced as written,
before any code existed, so the assumptions it got wrong are still visible in it.

---

## Objective

Take the Angular 21 app in `Day16/piece2/quotes-web` and the .NET 10 Quotes API in
`Day5/piece6/QuotesApi`, and put the frontend live on **Azure Static Web Apps**, calling the
real API. The frontend tier must authenticate to the API with a **managed identity**. No
client secret may exist in the repository, in any app setting, or in CI.

Copy both applications into `Day17/frontend` and `Day17/backend` — do not deploy from the
Day 5 and Day 16 folders, which stay as their own days' record.

## Target

> **Amended mid-build.** The brief originally targeted the personal subscription
> (`f3d23d11-…`, tenant `8733141f-…`) where the Week-1 API was already running. That
> subscription's session could not be renewed, and the account that *was* available belongs
> to a **different tenant**. Since a managed identity can only be issued tokens for
> applications registered in its own tenant, the stack could not be split across the two. The
> target moved to the student subscription and the Week-1 API is deployed there from
> `Day17/backend` rather than reused in place. The amendment is left visible here because the
> tenant constraint that forced it is the single most important thing this exercise taught.

| | |
|---|---|
| Subscription | `132ef106-f8ec-4352-83e4-9bc238274f25` ("Azure for Students") |
| Tenant | `8d46a076-d093-416d-a57b-8692cde13bf8` |
| Resource group | `rg-quotes-day17` (`centralindia`) |
| Static Web App | `swa-quotes-day17`, **Free** plan, `eastasia` |
| Frontend URL | the generated `https://<name>.azurestaticapps.net`. No custom domain: I do not own a DNS domain, so document the binding steps and ship on the generated hostname. |

## The Week-1 API — what it actually is

The .NET 10 minimal API from `Day5/piece6/QuotesApi`, deployed as the Container App
**`quotes-api`**. Base URL is its ingress FQDN; resolve it at deploy time rather than
hard-coding it:

```
az containerapp show -n quotes-api -g rg-quotes-day17 \
  --query properties.configuration.ingress.fqdn -o tsv
```

Backing store is **SQLite in the container**. Leave it that way. Do not migrate to Azure SQL.
It is not durable across revision restarts; that is accepted.

### Endpoints the frontend hits

These are the real routes, from `Extensions/QuoteEndpointExtensions.cs` and
`Endpoints/AuthEndpoints.cs`. Do not invent, rename, or "improve" any of them.

| Method | Route | Auth | Notes |
|---|---|---|---|
| `GET` | `/api/quotes` | anonymous | Returns a **bare array**, not a `{ items, total }` envelope. Accepts no paging parameters — `?page=&size=` is silently ignored. |
| `GET` | `/api/quotes/{id:int}` | anonymous | `404` for unknown, deleted, or non-integer id. |
| `POST` | `/api/quotes` | `can-edit-quotes` policy | Needs a `scope=quotes.write` claim. `201` with the created entity; `409` on a duplicate author+text pair. |
| `PUT` | `/api/quotes/{id:int}/author` | `can-edit-quotes` | `204`. `409` if the new author already has that text. |
| `DELETE` | `/api/quotes/{id:int}` | authenticated + `IsQuoteOwner` | Soft delete. `204` owned · `403` owned by someone else · `404` unknown · `401` anonymous. |
| `POST` | `/api/auth/register` | anonymous | `201`, and signs the new account straight in. `409` if the email is taken. |
| `POST` | `/api/auth/login` | anonymous | `200` with `{ accessToken, refreshToken: "", expiresIn }`. `401` on bad credentials. |
| `POST` | `/api/auth/refresh` | refresh cookie | Carries **no token in the body** — the browser sends the `quotes_rt` HttpOnly cookie and the server rotates it. Replaying a spent token revokes the whole family. |
| `POST` | `/api/auth/logout` | refresh cookie | Revokes server-side and clears the cookie. |

### Fields on the wire

`GET /api/quotes` returns the EF entity, so there are **six** fields, not three:

```
id: number · text: string · author: string
createdAt: string   ISO-8601 with an explicit +00:00 offset, NOT a Z suffix
isDeleted: boolean · userId: number
```

`Day16/piece2`'s `isQuote()` guard checks all six and rejects the response if any is missing.
If you reshape the payload, the app will refuse it at runtime rather than render it.

### Things that will bite you

- The refresh cookie is `HttpOnly; Secure; SameSite=Strict; Path=/api/auth`.
- The API validates token lifetime with `ClockSkew = TimeSpan.Zero`.
- `Day5/piece6/QuotesApi/.env` contains `EntraId__TenantId`, `EntraId__ClientId` and
  `EntraId__Audience` left over from an earlier experiment. **Verify them before reusing
  them.** They may not belong to the tenant above.
- The API refuses to start without `Jwt__Key`, and refuses a key shorter than 32 bytes.

## Auth requirement — the actual point of the exercise

**Managed identity. Not a client secret. Not a certificate. Not a shared key.**

- The tier that calls the Quotes API must obtain its token from the Azure platform at
  runtime, via `DefaultAzureCredential` / IMDS.
- The API must **verify** that token — validate issuer, audience, lifetime and signature
  against Entra, and require an app role. Attaching a token nobody checks proves nothing.
- A request to the API that does not carry a managed-identity token must be **rejected**.
  I will test this by calling the API's public FQDN directly and expecting a `401`.
- The end user's own session must keep working exactly as it does today. Per-user ownership
  on `DELETE /api/quotes/{id}` must still be enforced against the user, not against the
  managed identity.
- No `client_secret`, `AZURE_CLIENT_SECRET`, `AZURE_CREDENTIALS`, connection string with a
  password, or account key anywhere in: the repository, Container Apps settings, Static Web
  Apps settings, or GitHub secrets.

A browser cannot hold a managed identity. Work out where the token has to be minted and
justify the choice, including cost.

## CI/CD

A GitHub Actions workflow that builds and deploys both tiers. Authenticate to Azure with
**OIDC federated credentials**, not a stored secret. Fail the build if the managed-identity
hop stops working.

## Definition of done

1. The Static Web App URL loads over HTTPS and client-side deep links resolve.
2. **Lighthouse ≥ 95.** Report the numbers, do not assert them.
3. The call to the Week-1 API demonstrably carries a managed-identity token, and the API
   demonstrably rejects calls that do not.
4. A verification log grounded in the real endpoints above — loading, error, empty, and a
   failed-token/401 state exercised against the deployed system.
5. Zero secrets, demonstrated by a scan, not by assertion.

## Standing instruction

If any of the above is wrong, impossible, or more expensive than I have assumed, say so and
stop. Do not paper over it, and do not fall back to a client secret "temporarily".
