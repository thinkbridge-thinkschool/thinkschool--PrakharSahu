#!/usr/bin/env bash
#
# Day 24 — promote dev to prod.
#
#   ./scripts/promote.sh
#
# ---------------------------------------------------------------------------------------------
# WHAT PROMOTION MEANS HERE, AND WHAT IT DELIBERATELY DOES NOT
#
# There is no build, no artefact, no image copy. Promotion is the claim that the SAME template
# that produced a working dev environment now produces prod, and the only thing that changes is
# which azd environment is selected:
#
#   azd env select prod
#   azd provision
#
# That is the whole mechanism. If promoting needed a different template, a different command
# line, or an extra flag, then dev was never a rehearsal for prod and the two environments only
# resembled each other by coincidence.
#
# The gate below is the part worth having. Promoting from a dev environment that never deployed
# cleanly promotes nothing — it just runs an unproven template against expensive resources for
# the first time. So dev has to be in a succeeded state before prod is touched, and the check is
# made against Azure rather than against a local file that could be stale.
# ---------------------------------------------------------------------------------------------
set -uo pipefail
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.." || exit 1

FROM="${FROM:-dev}"
TO="${TO:-prod}"

die() { printf '\n%s\n' "$*" >&2; exit 1; }

echo "=============================================================================="
echo " Promote ${FROM} -> ${TO}"
echo "=============================================================================="

# ---- 1. the gate ------------------------------------------------------------------------------
echo
echo "--- 1. Is ${FROM} actually in a good state? -----------------------------------"

FROM_STATE="$(az stack sub show --name "azd-stack-${FROM}" \
  --query provisioningState -o tsv 2>/dev/null)"

if [ -z "$FROM_STATE" ]; then
  die "No stack 'azd-stack-${FROM}'. Deploy ${FROM} before promoting it."
fi

echo "  azd-stack-${FROM}: ${FROM_STATE}"
[ "$FROM_STATE" = "succeeded" ] \
  || die "Refusing to promote: ${FROM} is '${FROM_STATE}', not 'succeeded'."

FROM_COUNT="$(az stack sub show --name "azd-stack-${FROM}" --query "length(resources)" -o tsv 2>/dev/null)"
echo "  managing ${FROM_COUNT} resources"
echo "  gate passed."

# ---- 2. show what differs ---------------------------------------------------------------------
# The whole dev-versus-prod story, read out of the committed profiles rather than asserted. This
# is the reason the sizing lives in two files instead of sixteen inline ternaries: the difference
# between the environments is something you can print.
echo
echo "--- 2. What changes between ${FROM} and ${TO} ---------------------------------"
python - "$FROM" "$TO" <<'PY'
import io, json, sys
a, b = sys.argv[1], sys.argv[2]
load = lambda n: json.load(io.open(f'infra/profiles/{n}.json', encoding='utf-8'))
pa, pb = load(a), load(b)
keys = [k for k in pa if not k.startswith('_')]
w = max(len(k) for k in keys)
for k in keys:
    va, vb = json.dumps(pa[k]), json.dumps(pb.get(k))
    flag = '  ' if va == vb else '->'
    print(f'  {flag} {k:<{w}}  {va}   {vb}')
PY

# ---- 3. promote --------------------------------------------------------------------------------
echo
echo "--- 3. Selecting ${TO} --------------------------------------------------------"

# The environment has to exist and carry its own identifiers before it can be selected. Running
# setup here rather than documenting it as a prerequisite is what makes promotion one command.
bash scripts/env-setup.sh "$TO" >/dev/null 2>&1 || die "Could not prepare the ${TO} environment."
azd env select "$TO" || die "Could not select ${TO}."
echo "  selected: $(azd env get-value AZURE_ENV_NAME 2>/dev/null)"

echo
echo "--- 4. Provisioning ${TO} -----------------------------------------------------"
echo "  Same template. Same command. Different environment."
echo

azd provision --no-prompt || die "Provisioning ${TO} failed. Nothing was promoted."

echo
echo "=============================================================================="
echo " ${TO} provisioned from the same template that produced ${FROM}."
echo "=============================================================================="
az stack sub show --name "azd-stack-${TO}" \
  --query "{state:provisioningState,managed:length(resources),denyMode:denySettings.mode}" -o yaml 2>/dev/null
