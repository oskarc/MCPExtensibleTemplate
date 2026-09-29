#!/usr/bin/env bash
#
# contract-005 · T-12 (G-11) — every end-to-end test seen red on demand, on the assertion that carries its claim.
#
# Every end-to-end test has a named sabotage, declared on the test itself ([Sabotage]; the registry is
# McpServerTemplate.E2E/Harness/Sabotage.cs): one thing the harness weakens for that test alone — in its inputs, a
# container's environment or the network — when MCP_E2E_SABOTAGE names it. No code is edited. This script runs each in
# turn; in a sabotage's run the harness runs the one test it targets and nothing else, and writes how that test ended
# under TestResults/sabotage/. A red counts only when the sabotage acted and the test failed on the assertion that
# carries its claim: its exception is a ClaimException, which only Claim throws. A red at a self-check, a positive control
# or startup fails with some other exception, and does not count. A test skipped by decision is held, and not run.
#
# A full run writes the record, McpServerTemplate.E2E/SABOTAGE-RECORD.md: every sabotage, its test, where it acts and what
# it weakens, and the failure message, with the run's date and commit. The record is committed, and the suite's
# HarnessSelfTests fail while any sabotage has no red on its claim in it.
#
# Local only: the harness refuses MCP_E2E_SABOTAGE in CI, and so does this script. It needs Docker, and a full run takes
# most of two hours, since every sabotage starts the environment afresh.
#
#   bash scripts/e2e-sabotage.sh            every sabotage, then the record
#   bash scripts/e2e-sabotage.sh NAME ...   only these, reported here and under TestResults/sabotage/; the record is untouched
#
# Exit 0: every sabotage run went red on its test's claim. Exit 1: one did not, named above, or the run could not be made.

set -uo pipefail

cd "$(dirname "$0")/.." || exit 1

if [ -n "${CI+set}" ]; then
  echo "Refused: CI is set. A sabotage weakens the environment on purpose; it is run locally, never in CI (contract-005 · G-11)." >&2
  exit 1
fi

if [ -n "${MCP_E2E_SABOTAGE+set}" ]; then
  echo "Refused: MCP_E2E_SABOTAGE is already set ('${MCP_E2E_SABOTAGE}'). This script names each sabotage itself." >&2
  exit 1
fi

project=McpServerTemplate.E2E/McpServerTemplate.E2E.csproj
out=TestResults/sabotage
record=McpServerTemplate.E2E/SABOTAGE-RECORD.md
claim=McpServerTemplate.E2E.Harness.ClaimException
label=org.mcp-server-template.e2e.run

# Whether the Docker engine answers. docker ps fails when it does not (docker info does not).
engine_answers() { docker ps -q > /dev/null 2>&1; }

if ! engine_answers; then
  echo "The Docker engine does not answer (docker ps: $(docker ps 2>&1 | tail -n 1)). Start it, and run this again." >&2
  exit 1
fi

rm -rf "$out"
mkdir -p "$out"

# A record half written is never left in place of the last whole one.
trap 'rm -f "$record.tmp"' EXIT

started=$(date -u +%Y-%m-%dT%H:%M:%SZ)

echo "Building (Release, warnings as errors)..."
if ! dotnet build McpServerTemplate.sln -c Release -warnaserror > "$out/build.log" 2>&1; then
  echo "The build failed; see $out/build.log." >&2
  exit 1
fi

# ── The registry, as the harness reads it, checked against every end-to-end test ────────────────────────────────────
echo "Reading the registry..."
if ! dotnet test "$project" --no-build -c Release \
    --filter "FullyQualifiedName=McpServerTemplate.E2E.HarnessSelfTests.Every_end_to_end_test_has_a_named_sabotage" \
    > "$out/registry.log" 2>&1; then
  echo "Not every end-to-end test has a named sabotage; see $out/registry.log:" >&2
  grep -A20 "Error Message" "$out/registry.log" >&2
  exit 1
fi

# The runner's own list, theory rows included, less the classes that are not end-to-end: the same tests the registry
# covers, counted the way the runner counts them.
not_end_to_end=$(cut -f1 "$out/not-end-to-end.tsv" | paste -sd'|' -)
listed=$(dotnet test "$project" --no-build -c Release --list-tests 2>/dev/null \
  | grep -E '^    McpServerTemplate\.E2E\.' \
  | grep -vcE "^    McpServerTemplate\.E2E\.(${not_end_to_end:-^})\.")
covered=$(tail -n +2 "$out/registry.tsv" | cut -f6 | sort -u | wc -l | tr -d ' ')
if [ "$listed" != "$covered" ]; then
  echo "The runner lists $listed end-to-end tests, and the registry covers $covered: they must be the same." >&2
  exit 1
fi

# ── Each sabotage in turn ────────────────────────────────────────────────────────────────────────────────────────────
mapfile -t entries < <(tail -n +2 "$out/registry.tsv")
if [ "$#" -gt 0 ]; then
  selected=()
  for name in "$@"; do
    match=$(printf '%s\n' "${entries[@]}" | awk -F'\t' -v n="$name" '$1 == n')
    if [ -z "$match" ]; then
      echo "'$name' is no sabotage of this suite; $out/registry.tsv lists them." >&2
      exit 1
    fi
    selected+=("$match")
  done
  entries=("${selected[@]}")
fi

field() { sed -n "s/^$2: //p" "$out/$1.outcome" 2>/dev/null | head -n 1; }

total=${#entries[@]}
red=0 held=0 not_red=0 i=0
for entry in "${entries[@]}"; do
  IFS=$'\t' read -r name class method acts hold test weakens <<< "$entry"
  i=$((i + 1))

  if [ "$hold" != "-" ]; then
    held=$((held + 1))
    printf '%3d/%d  %-66s held: skipped by decision, not run\n' "$i" "$total" "$name"
    continue
  fi

  # An engine that has stopped answering fails every run that follows at the environment, which is no verdict on any
  # test: the run stops, and no record is written from it.
  if ! engine_answers; then
    echo "The Docker engine stopped answering before $name ($i of $total); the run stops here, and the record is not written." >&2
    echo "The last run before it may have failed for the same reason: see $out/." >&2
    exit 1
  fi

  MCP_E2E_SABOTAGE="$name" dotnet test "$project" --no-build -c Release \
    --filter "FullyQualifiedName=$class.$method" \
    --logger "console;verbosity=normal" \
    --blame-hang --blame-hang-timeout 10m --blame-hang-dump-type none \
    > "$out/$name.log" 2>&1

  outcome=$(field "$name" outcome)
  acted=$(field "$name" acted)
  exception=$(field "$name" exception)
  if [ ! -f "$out/$name.outcome" ]; then
    verdict="NOT RED: no outcome was recorded (see $out/$name.log)"
  elif [ "$acted" != yes ]; then
    verdict="NOT RED: the sabotage never acted, and the test $outcome"
  elif [ "$outcome" != failed ]; then
    verdict="NOT RED: the test $outcome"
  elif [ "$exception" != "$claim" ]; then
    verdict="NOT RED on its claim: it failed with $exception"
  else
    verdict="red on its claim"
  fi

  if [ "$verdict" = "red on its claim" ]; then
    red=$((red + 1))
  else
    not_red=$((not_red + 1))
  fi

  printf '%s\n' "$verdict" > "$out/$name.verdict"
  printf '%3d/%d  %-66s %s (%s s)\n' "$i" "$total" "$name" "$verdict" "$(field "$name" seconds)"
done

finished=$(date -u +%Y-%m-%dT%H:%M:%SZ)

# ── Nothing a run created is left (contract-005 · T-13) ──────────────────────────────────────────────────────────────
left=()
while IFS= read -r c; do [ -n "$c" ] && left+=("container $c"); done < <(docker ps -a --filter "label=$label" --format '{{.Names}}')
while IFS= read -r n; do [ -n "$n" ] && left+=("network $n"); done < <(docker network ls --filter "label=$label" --format '{{.Name}}')
for dir in "${TMPDIR:-/tmp}"/mcp-e2e-*; do [ -e "$dir" ] && left+=("run directory $dir"); done
if docker image inspect mcp-server:latest > /dev/null 2>&1; then left+=("image tag mcp-server:latest"); fi
if [ "${#left[@]}" -gt 0 ]; then
  echo "Left behind by the runs: ${left[*]}" >&2
fi

echo
echo "$total sabotages: $red red on their claim, $not_red not, $held held."

if [ "$#" -gt 0 ]; then
  echo "Only the sabotages named were run; $record is left as it is."
  [ "$not_red" -eq 0 ] && [ "${#left[@]}" -eq 0 ]
  exit $?
fi

# ── The record ───────────────────────────────────────────────────────────────────────────────────────────────────────
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) platform="Windows" ;;
  *) platform="$(uname -s)" ;;
esac
commit=$(git rev-parse HEAD)
changed=$(git status --porcelain --untracked-files=all -- . ':(exclude).claude' ":(exclude)$record" | wc -l | tr -d ' ')
tree=$([ "$changed" -eq 0 ] && echo "the working tree as committed" || echo "with uncommitted changes in $changed paths (the record and the kit's own files aside)")
tests=$(tail -n +2 "$out/registry.tsv" | cut -f6 | sort -u | wc -l | tr -d ' ')

{
  echo "# Sabotage record — contract-005 · T-12 (G-11)"
  echo
  echo "Every end-to-end test has a named sabotage: one thing the harness weakens for that test alone — in its inputs, a"
  echo "container's environment or the network — when \`MCP_E2E_SABOTAGE\` names it, with no code edited. Each is declared on"
  echo "its test (\`[Sabotage]\`, \`McpServerTemplate.E2E/Harness/Sabotage.cs\`), and each was run on its own, with the test it"
  echo "targets and nothing else. A red below counts only when the sabotage acted and the test failed on the assertion that"
  echo "carries its claim: a \`ClaimException\`, which only \`Claim\` throws. A red at a self-check, a positive control or"
  echo "startup fails with another exception, and is not one. Each message is the runner's, with any token in it replaced by"
  echo "\`[token]\`."
  echo
  echo "A message is the test's own, and says what the test was checking, not what the sabotage did. Where a sabotage leaves"
  echo "out the misconfiguration a startup test must refuse (\`…-left-out\`), the message still names that setting — \"BindAddress"
  echo "is 255.255.255.255: the server started\" — but the server that started ran without it, on the environment's own settings:"
  echo "the product refused none of them, and was given none of them. Each entry's \"Acts on\" line says what its sabotage did."
  echo
  echo "Written by \`scripts/e2e-sabotage.sh\`, and held to by \`HarnessSelfTests\`: the suite fails while a sabotage has no red"
  echo "on its claim here. CI refuses \`MCP_E2E_SABOTAGE\`."
  echo
  echo "- Run: $started to $finished, on $platform with Docker $(docker version --format '{{.Server.Version}}' 2>/dev/null)"
  echo "- Commit: \`$commit\`, $tree"
  echo "- $total sabotages over the $tests end-to-end tests the runner lists, theory rows included: $red red on their test's claim, $not_red not, $held held"
  while IFS=$'\t' read -r nclass ntests nreason; do
    echo "- Not end-to-end, so no sabotage: \`$nclass\` ($ntests tests). $nreason"
  done < "$out/not-end-to-end.tsv"
  echo
  echo "| Sabotage | Test | Acts on | Result |"
  echo "|---|---|---|---|"
  for entry in "${entries[@]}"; do
    IFS=$'\t' read -r name class method acts hold test weakens <<< "$entry"
    if [ "$hold" != "-" ]; then
      result="held"
    else
      result=$(cat "$out/$name.verdict")
    fi
    echo "| \`$name\` | \`${class##*.}.$method\` | $acts | $result |"
  done
  echo
  echo "## Each sabotage, and its red"
  for entry in "${entries[@]}"; do
    IFS=$'\t' read -r name class method acts hold test weakens <<< "$entry"
    echo
    echo "### \`$name\`"
    echo
    if [ "$hold" != "-" ]; then
      echo "- Test: \`$test\`"
      echo "- Acts on $acts: $weakens"
      echo "- Held: the test is skipped by decision, so no red can be taken. $hold"
      continue
    fi
    echo "- Test: \`$(field "$name" test)\`"
    echo "- Acts on $acts: $weakens"
    echo "- Result: $(cat "$out/$name.verdict"), with \`$(field "$name" exception)\` after $(field "$name" seconds) s"
    echo
    echo "~~~text"
    cat "$out/$name.message"
    echo "~~~"
  done
} > "$record.tmp"
mv "$record.tmp" "$record"
echo "Wrote $record."

[ "$not_red" -eq 0 ] && [ "${#left[@]}" -eq 0 ]
