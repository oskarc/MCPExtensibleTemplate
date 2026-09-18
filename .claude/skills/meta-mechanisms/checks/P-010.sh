#!/usr/bin/env bash
# P-010 — from C-010 (contract-002 was approved and left half-implemented — six of twelve guardrails
# done, two of thirteen tests written — while the agent proposed surveying Phase 2 / drafting
# contract-003 alongside it). Holding: finish the approved contract before proposing work beyond it.
# Shape checked: CONTRACT-LOG.yaml carries at most one contract whose status is not yet
#          verified/learned/legacy (i.e. status: approved or status: implemented) at a time.
# Exit 0 when the standard holds; non-zero with one line naming the contracts left open together.
set -u
here="$(cd "$(dirname "$0")" && pwd)"
kit="$(cd "$here/../.." && pwd)"                 # .claude/skills in a project; the repo root in the base kit
log="$kit/meta-contract-before-execution/CONTRACT-LOG.yaml"
[ -f "$log" ] || { echo "P-010 broken: CONTRACT-LOG.yaml is missing"; exit 1; }

count=0
open_ids=""
id=""
while IFS= read -r line; do
  case "$line" in
    "  - contract_id: "*) id="${line#*: }" ;;
    "    status: approved"|"    status: implemented")
      count=$((count+1))
      open_ids="$open_ids $id"
      ;;
  esac
done < "$log"

[ "$count" -le 1 ] || { echo "P-010 broken: more than one contract left open at once —$open_ids"; exit 1; }
exit 0
