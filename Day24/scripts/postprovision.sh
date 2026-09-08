#!/usr/bin/env bash
#
# Day 24 — the azd postprovision hook.
#
# Runs automatically after every `azd provision`, in every environment. Not invoked by hand.
#
# ---------------------------------------------------------------------------------------------
# WHY THIS EXISTS
#
# Granting the API's managed identity access to the DATABASE is T-SQL executed inside the
# database — `CREATE USER [app] FROM EXTERNAL PROVIDER` — and there is no ARM resource for it.
# So a template alone leaves the API able to REACH SQL and unable to read anything from it, and
# the symptom is a login error that reads like a credential problem:
#
#   Login failed for user '<token-identified principal>'
#
# Day 23 printed the statement and asked a human to run it. This runs it.
#
# The failure mode it removes is not "somebody forgot". It is that the statement was in a script
# nobody read after the first time, so a fresh environment was subtly broken and the error
# pointed at authentication rather than at a missing GRANT.
# ---------------------------------------------------------------------------------------------
set -uo pipefail
export MSYS_NO_PATHCONV=1

# azd exports every template output into the hook's environment, which is why nothing here has
# to query Azure or parse a deployment. The names are the output names from main.bicep.
SQL_FQDN="${SQL_SERVER_FQDN:-}"
DATABASE="${SQL_DATABASE_NAME:-dispatch}"
GRANT="${GRANT_DATABASE_ACCESS_SCRIPT:-}"
API="${API_URL:-}"

echo "=== postprovision ==="

if [ -z "$GRANT" ]; then
  echo "No GRANT_DATABASE_ACCESS_SCRIPT output; nothing to do."
  exit 0
fi

# ---- find a SQL client ----------------------------------------------------------------------
# Checked rather than assumed. A hard dependency on sqlcmd would make the common path fail on a
# machine that does not have it, for a step that is recoverable by hand.
CLIENT=""
command -v sqlcmd >/dev/null 2>&1 && CLIENT="sqlcmd"

if [ -z "$CLIENT" ]; then
  cat <<EOF

  sqlcmd is not on PATH, so the database grant was NOT applied.

  The infrastructure is complete and correct. What remains is one statement, run against
  ${SQL_FQDN} signed in as the Entra SQL admin:

    ${GRANT}

  Until it runs, the API starts, answers /health, and fails every query with
  "Login failed for user '<token-identified principal>'".

  Install a client and re-run \`azd provision\` to apply it automatically:
    winget install Microsoft.Sqlcmd

EOF
  # Exit 0 on purpose. The provision SUCCEEDED; this is a follow-up step, and failing the
  # command here would report a successful deployment as a failed one.
  exit 0
fi

# ---- apply it -------------------------------------------------------------------------------
# `-G` is Entra auth, which is the only kind this server accepts — azureADOnlyAuthentication is
# on, so there is no password to pass and none to leak into a process listing or a log.
echo "  applying the database grant via $CLIENT (Entra auth)"

if sqlcmd -S "$SQL_FQDN" -d "$DATABASE" -G -Q "$GRANT" 2>&1; then
  echo "  grant applied. The API's managed identity is now a database user."
else
  cat <<EOF

  The grant did not apply. The infrastructure is fine; this step is not.

  Most likely: the signed-in principal is not a member of the Entra group that administers this
  server, so it can reach the database and cannot create users in it. Add yourself to the admin
  group named in infra/profiles/*.json and re-run \`azd provision\`.

  To apply it by hand:
    sqlcmd -S ${SQL_FQDN} -d ${DATABASE} -G -Q "${GRANT}"

EOF
  exit 0
fi

[ -n "$API" ] && echo "  API: $API"
exit 0
