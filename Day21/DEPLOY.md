# Deploying Day 17

## Prerequisites

- Azure CLI ≥ 2.60, signed in to tenant `8d46a076-d093-416d-a57b-8692cde13bf8`
- Node ≥ 20
- Nothing pre-existing. The Week-1 API is deployed from `Day17/backend` as part of this script.

Sign in first. An expired session is the single most common failure, and it surfaces as
`AADSTS50076` on every ARM call rather than as a login prompt:

```bash
az login --tenant 8d46a076-d093-416d-a57b-8692cde13bf8 \
         --scope "https://management.core.windows.net//.default"
```

## One command

```bash
./Day17/scripts/deploy.sh
```

On Windows, run it from Git Bash. The script sets `MSYS_NO_PATHCONV=1` because Git Bash
otherwise rewrites every `/subscriptions/...` resource id into a Windows path and the
failures that follow name nothing useful.

It is idempotent — every step checks for what it is about to create. Re-running after a
failure resumes rather than duplicating, which matters because the Entra steps in the middle
are the ones most likely to fail on permissions.

### What it does, in order

1. **Preflight** — verifies the signed-in tenant *matches the target*. Getting this wrong is
   silent and fatal: a managed identity can only be issued tokens for applications registered
   in its own tenant.
2. **Creates the resource group and Container Apps environment**, then **deploys the Week-1
   API** from `Day17/backend` via an ACR cloud build. Generates a JWT signing key and stores
   it as a Container Apps secret — only if one is not already there, since rotating it on
   every deploy would invalidate every live session for no reason. No seed account is
   created; `verify.sh` registers a throwaway user instead, so there is no bootstrap password
   to generate or leak. The API's URL is resolved, never hard-coded.
3. **Registers the Entra application** `quotes-api-day17` with identifier URI
   `api://<app-id>` and an app role `Api.Invoke` whose `allowedMemberTypes` is
   `["Application"]`. A role limited to `User` cannot be assigned to a service principal at
   all, and the assignment fails later with a message that never mentions why.
4. **Creates the Static Web App** first, so its hostname can be baked into the broker's CORS
   allowlist without a second pass.
5. **Builds and deploys the broker** from source via ACR Tasks — no local Docker needed —
   then assigns it a system-assigned managed identity.
6. **Grants the app role to that identity**, retrying for up to a minute. Entra takes a
   moment to make a brand-new managed identity visible to Graph, and the assignment 404s
   until it is; this fires most times the identity was created in the same run.
7. **Enables enforcement on the API** — set *after* the role assignment, so there is no
   window in which the broker is locked out of the API it fronts.
8. **Stamps the broker hostname** into the Angular bundle and the CSP, then builds.
9. **Publishes to Static Web Apps**, fetching the deployment token at run time.
10. Writes `Day17/.deploy-output.json` with every resolved URL and id.

## Verifying

```bash
./Day17/scripts/verify.sh | tee Day17/docs/verification-run.txt
```

Runs every check in [VERIFICATION.md](VERIFICATION.md) against the live system, including
Lighthouse, and exits non-zero if anything fails.

## CI/CD

One-time setup:

```bash
./Day17/scripts/setup-github-oidc.sh <owner>/<repo>
```

Then add the three printed values as repository **variables** and create an environment named
`day17`. Nothing goes into repository secrets.

## Rollback

Static Web Apps keeps the previous deployment; redeploy the prior commit to revert. To turn
enforcement off in an emergency and let the API serve traffic without a caller token:

```bash
az containerapp update -n quotes-api -g rg-quotes-day17 \
  --remove-env-vars CallerIdentity__TenantId CallerIdentity__Audience CallerIdentity__RequiredRole
```

The middleware logs a warning at startup whenever it is disabled, so this state is visible in
the logs rather than silent.

## Teardown

```bash
az staticwebapp delete -n swa-quotes-day17 -g rg-quotes-day17 --yes
az containerapp delete -n quotes-bff -g rg-quotes-day17 --yes
az ad app delete --id "$(az ad app list --display-name quotes-api-day17 --query '[0].appId' -o tsv)"
```

Then remove the `CallerIdentity__*` variables from `quotes-api` as shown under Rollback,
or it will reject all traffic once the broker is gone.

---

# Custom domain — deliberately not used

**Decision: this deployment uses the Azure-generated hostname.** No custom domain is bound,
and none is planned.

The reasoning is simple and worth stating rather than leaving implied. Azure does not issue
domains — Static Web Apps *binds* a domain you already control at a registrar, it does not
register one for you. With no domain owned, a custom hostname is not something the deployment
can produce; it is something that would have to be bought first. The Free plan permits two
custom domains per app, so nothing here is blocked by the plan.

Nothing about the architecture depends on the hostname. The generated name gets the same
automatically renewing TLS certificate, the same global edge, and the same behaviour.

The procedure is recorded below so that binding one later is a lookup rather than a research
task.

## With a subdomain (`www.example.com`) — CNAME

```bash
# 1. Read the target to point at.
az staticwebapp show -n swa-quotes-day17 -g rg-quotes-day17 \
  --query defaultHostname -o tsv
# -> swa-quotes-day17.<hash>.<region>.azurestaticapps.net

# 2. At your DNS provider, create:
#      www   CNAME   swa-quotes-day17.<hash>.<region>.azurestaticapps.net

# 3. Register the hostname. Azure validates by resolving the CNAME.
az staticwebapp hostname set \
  -n swa-quotes-day17 -g rg-quotes-day17 \
  --hostname www.example.com

# 4. Watch until it goes Ready. The managed certificate is issued automatically
#    and renews on its own; there is no cert to install or rotate.
az staticwebapp hostname show \
  -n swa-quotes-day17 -g rg-quotes-day17 \
  --hostname www.example.com --query status -o tsv
```

## With an apex domain (`example.com`) — TXT validation

An apex record cannot be a CNAME, so validation happens out of band:

```bash
# 1. Ask Azure for the validation token.
az staticwebapp hostname set \
  -n swa-quotes-day17 -g rg-quotes-day17 \
  --hostname example.com --validation-method dns-txt-token

az staticwebapp hostname show \
  -n swa-quotes-day17 -g rg-quotes-day17 \
  --hostname example.com --query validationToken -o tsv

# 2. Create a TXT record at the apex containing that token, and an ALIAS/ANAME
#    (or A record, if your provider supports neither) pointing at the SWA.
# 3. Re-run `hostname show` until status is Ready.
```

## What else would have to change

Binding a domain is not the whole job — three things elsewhere assume the generated hostname:

1. **The broker's CORS allowlist.** `ALLOWED_ORIGINS` is exact-match, so the new origin has
   to be added or every call from it fails preflight:
   ```bash
   az containerapp update -n quotes-bff -g rg-quotes-day17 \
     --set-env-vars "ALLOWED_ORIGINS=https://www.example.com,https://swa-quotes-day17.<hash>.azurestaticapps.net"
   ```
   Keep both while DNS propagates; drop the generated one afterwards.
2. **Nothing in the CSP.** `connect-src` names the *broker*, not the frontend, so a new
   frontend hostname does not touch it.
3. **Nothing in the refresh cookie.** It is set by the broker on the broker's own host, so it
   is unaffected by the frontend's domain — one of the few things the cross-origin design
   makes *easier* rather than harder.
