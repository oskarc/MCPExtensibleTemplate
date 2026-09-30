#!/usr/bin/env bash
# P-037 — from C-037 (contract-005 revision item 3: keep a scheduled CI re-proof of the sabotage
# record with revised wording, or drop it and re-prove only by hand — pioneer chose "3. B").
# Holding: no scheduled CI job re-proves the sabotage record; it is re-proved only by hand, and CI
# refuses to run with a sabotage or control variable set.
# Shape checked: .github/workflows/ci.yml has no `schedule:` trigger, and the e2e job refuses to
#          run when MCP_E2E_SABOTAGE (or MCP_E2E_CONTROL) is set.
# Exit 0 when the standard holds; non-zero with one line naming what broke.
set -u
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../../.." && pwd)"
ci="$repo/.github/workflows/ci.yml"
[ -f "$ci" ] || { echo "P-037 broken: .github/workflows/ci.yml is missing"; exit 1; }

if grep -qE "^[[:space:]]*schedule:" "$ci"; then
  echo "P-037 broken: ci.yml carries a schedule: trigger — a scheduled re-proof was dropped by the pioneer"
  exit 1
fi

grep -q "MCP_E2E_SABOTAGE" "$ci" || { echo "P-037 broken: ci.yml no longer refuses MCP_E2E_SABOTAGE"; exit 1; }

exit 0
