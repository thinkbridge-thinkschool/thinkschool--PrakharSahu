# Day 25 — Identity end-to-end

No connection-string secrets anywhere. Managed identity for the API→SQL and API→Service Bus paths,
Entra ID for app auth, and Key Vault references for the one credential that cannot be replaced by
an identity.

**Deliverable:** [EXERCISE.md](EXERCISE.md) — the MI wiring, a Key Vault reference, and the proof
that app settings hold no plaintext secrets.
**Change-by-change walkthrough:** [update_code.md](update_code.md) — every file, and why.

## Result

| | |
|---|---:|
| App settings containing a credential | **0 of 10** |
| App Service connection-strings slot | **empty** |
| `secureString` parameters in deployment history | **0** |
| Client secrets / certificates on the app registration | **0 / 0** |
| Zero-secrets proof | **12 passed, 0 failed** |
| Live identity paths proven working | **3 of 3** |

## The shape of it

```
                    Entra ID
                       |  validates the caller's JWT (no client secret — see below)
                       v
  caller  --token-->  App Service  ----- user-assigned managed identity ----->  SQL
                        |                                                      Service Bus
                        |                                                      Key Vault
                        |
                        '-- @Microsoft.KeyVault(...) resolved at startup
```

Three paths, and none of them holds a secret:

| path | authenticates with | what does not exist |
|---|---|---|
| API → SQL | managed identity | no password — `azureADOnlyAuthentication: true` |
| API → Service Bus | managed identity | SAS keys exist and are **rejected** |
| caller → API | Entra ID, validation only | no client secret, no certificate |
| third-party config | Key Vault reference | the value never enters app settings |

## Layout

```
Day25/
├── infra/
│   ├── main.bicep                 composition root — the ordering IS the design
│   ├── main.bicepparam            no @secure() parameter, because none is needed
│   └── modules/
│       ├── identity.bicep         the user-assigned identity, created FIRST
│       ├── keyvault.bicep         RBAC-authorised, purge-protected, holds no secret in code
│       ├── sql.bicep              Entra-only — password auth is refused, not just unused
│       ├── servicebus.bicep       disableLocalAuth — its own SAS keys are worthless
│       ├── app.bicep              App Service, Key Vault reference, Easy Auth
│       ├── rbac.bicep             three narrow roles, granted BEFORE the app exists
│       └── observability.bicep    Log Analytics + App Insights
├── src/IdentityApi/               the app that proves the paths work
├── tools/SqlGrant/                the T-SQL grant no template can express
├── scripts/
│   ├── entra-app.sh               the app registration (Graph, so not Bicep)
│   ├── deploy.sh                  infra, then the secret, then a restart
│   ├── deploy-app.sh              publish and zip-deploy the API
│   ├── grant-sql.sh               temporary firewall rule + SqlGrant, rule removed after
│   └── prove-no-secrets.sh        the deliverable — 12 assertions
└── docs/                          captured output from every run
```

## Running it

```bash
cd Day25

./scripts/entra-app.sh                 # creates the app registration; prints ENTRA_CLIENT_ID
export ENTRA_CLIENT_ID=<that value>

./scripts/deploy.sh                    # infra -> write the secret -> restart
./scripts/grant-sql.sh                 # the database grant, via .NET (no sqlcmd needed)
./scripts/deploy-app.sh                # publish and deploy the API

./scripts/prove-no-secrets.sh          # 12 assertions, read-only
```

Then exercise the paths. `/health` is the only unauthenticated route:

```bash
HOST=<site>.azurewebsites.net
TOKEN=$(az account get-access-token --resource "api://$ENTRA_CLIENT_ID" --query accessToken -o tsv)

curl -s https://$HOST/health                                            # 200
curl -s -o /dev/null -w '%{http_code}\n' https://$HOST/probe/sql        # 401 — no token
curl -s -H "Authorization: Bearer $TOKEN" https://$HOST/probe/sql       # the real proof
curl -s -H "Authorization: Bearer $TOKEN" https://$HOST/probe/servicebus
curl -s -H "Authorization: Bearer $TOKEN" https://$HOST/probe/keyvault
```

`/probe/sql` is the strongest single piece of evidence: it returns `SUSER_SNAME()`, the principal
the database server itself reports, on a server that cannot accept a password.

## What each probe is for

| endpoint | proves | auth |
|---|---|---|
| `/health` | the app is up | open by design |
| `/whoami` | the platform validated an Entra token before app code ran | token |
| `/probe/sql` | API→SQL with a managed identity and no password | token |
| `/probe/servicebus` | send **and** receive with a token, local auth disabled | token |
| `/probe/keyvault` | the reference resolved; returns shape only, never the value | token |

`/probe/servicebus` does a full round trip on purpose. Service Bus will accept a connection and
then refuse the operation, so connecting alone would prove less than it appears to.

## Cost

Roughly USD 0.03/hour: a B1 App Service plan, a Standard Service Bus namespace, a serverless SQL
database that auto-pauses after an hour idle, and a Key Vault. Tear it down with:

```bash
az group delete -n rg-identity-dev --yes
```

Note the vault has **purge protection** enabled, which is irreversible and outlives the resource
group — the vault name cannot be reused until its retention window expires. That is why the name
carries a suffix derived from the resource group id rather than being a fixed string.

## What is not done

- **App Insights ingestion still uses a key.** The one key-shaped value left in app settings. It is
  write-only and cannot read telemetry, but `DisableLocalAuth: true` on the component plus Entra
  ingestion would remove it. Labelled rather than glossed over — see
  [EXERCISE.md](EXERCISE.md#6-honest-gaps).
- **The SQL admin is a person, not an Entra group.** A group survives someone leaving the team.
- **The app registration is not in code** — it is a Graph object, so a script creates it.
- **Everything is publicly reachable behind Entra**, not behind a network boundary. Private
  endpoints for SQL, Service Bus and the vault are the defence-in-depth answer.
- **No CI**, and no deployment stack. Day 17's OIDC workflow and Day 24's stack both layer on
  cleanly; this stays focused on identity.
