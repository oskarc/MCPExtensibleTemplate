#!/usr/bin/env bash
# P-040 - from C-040 (contract-005: the end-to-end test network, only mocked or also cut off from
# the internet - pioneer: "oh yea, no internet access mock everything").
# Holding: the tests have no internet access. The run network is created internal, so no run
# container has a route off it, and a test (NetworkCutTests) holds the cut.
# Shape checked: E2ENetwork.cs sets parameters.Internal from the sabotage switch's negation (true
# unless the t6 sabotage is applied) and NetworkCutTests.cs exists.
# Exit 0 when the standard holds; non-zero with one line naming what broke.
set -u
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../../.." && pwd)"
net="$repo/McpServerTemplate.E2E/Harness/E2ENetwork.cs"
cut="$repo/McpServerTemplate.E2E/NetworkCutTests.cs"
[ -f "$net" ] || { echo "P-040 broken: McpServerTemplate.E2E/Harness/E2ENetwork.cs is missing"; exit 1; }
[ -f "$cut" ] || { echo "P-040 broken: McpServerTemplate.E2E/NetworkCutTests.cs is missing - nothing holds the network cut"; exit 1; }
grep -qE "Internal[[:space:]]*=[[:space:]]*!Sabotage\.Applies\(NotInternal\)|Internal[[:space:]]*=[[:space:]]*true" "$net" \
  || { echo "P-040 broken: E2ENetwork.cs no longer creates the run network internal - the tests have a route to the internet"; exit 1; }
exit 0
