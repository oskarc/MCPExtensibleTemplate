#!/usr/bin/env bash
#
# contract-005 · T-12 (G-11) — every end-to-end test seen red on demand, on the assertion that carries its claim, and the
# record of it kept true, cheaply.
#
# Every end-to-end test has a named sabotage, declared on the test itself ([Sabotage]; the registry is
# McpServerTemplate.E2E/Harness/Sabotage.cs): one thing the harness weakens for that test alone — in its inputs, a
# container's environment or the network — when MCP_E2E_SABOTAGE names it. No code is edited. In a sabotage's run the
# harness runs the one test it targets and nothing else, and writes how that test ended under TestResults/sabotage/. A red
# counts only when the sabotage acted and the test failed on the assertion that carries its claim: its exception is a
# ClaimException, which only Claim throws. A red at a self-check, a positive control or startup fails with some other
# exception, and does not count. A test skipped by decision is held, and not run.
#
# The record, McpServerTemplate.E2E/SABOTAGE-RECORD.md, holds one entry per sabotage: its test, where it acts and what it
# weakens, its red and the failure message, and when, at which commit and on which version of its test's file the red was
# taken. By default this script retakes only the entries that are no longer true, and keeps every other as it is:
#   - missing: a new or renamed test or sabotage;
#   - stale: the test's own file, McpServerTemplate.E2E/{Class}.cs, has changed since the red was taken — each entry keeps
#     the first 12 hex digits of that file's SHA-256, line endings aside, and the harness computes it the same way;
#   - not red: it did not go red on its claim when it was last taken.
# An entry whose sabotage no longer exists is dropped. A change to the harness or the product marks no entry stale: after
# one that could change what a sabotage does, retake them all (--all). The suite's HarnessSelfTests fail while any entry
# is missing, stale or not red, so CI holds the record to the tests on every push; and CI refuses MCP_E2E_SABOTAGE.
#
#   bash scripts/e2e-sabotage.sh             retake what is missing, stale or not red, and keep the rest: about 35 s each
#   bash scripts/e2e-sabotage.sh --all       retake every sabotage: most of an hour
#   bash scripts/e2e-sabotage.sh NAME ...    retake these, whatever their state, and keep the rest
#   bash scripts/e2e-sabotage.sh --dry-run   say what would be retaken, and why; run nothing
#
# Local only: the harness refuses MCP_E2E_SABOTAGE in CI, and so does this script. It needs Docker: every sabotage starts
# the environment afresh.
#
# Exit 0: every entry of the record is red on its claim, or held, and nothing was left behind. Exit 1: an entry is not
# (named above), or the run could not be made.

set -uo pipefail

cd "$(dirname "$0")/.." || exit 1

all=0
dry=0
names=()
for arg in "$@"; do
  case "$arg" in
    --all) all=1 ;;
    --dry-run) dry=1 ;;
    -h|--help) sed -n '2,34p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) echo "Unknown option '$arg'; see --help." >&2; exit 1 ;;
    *) names+=("$arg") ;;
  esac
done

if [ "$all" -eq 1 ] && [ "${#names[@]}" -gt 0 ]; then
  echo "Give --all or the names of sabotages to retake, not both." >&2
  exit 1
fi

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

if [ "$dry" -eq 0 ] && ! engine_answers; then
  echo "The Docker engine does not answer (docker ps: $(docker ps 2>&1 | tail -n 1)). Start it, and run this again." >&2
  exit 1
fi

rm -rf "$out"
mkdir -p "$out/kept" "$out/new"

# A record half written is never left in place of the last whole one.
trap 'rm -f "$record.tmp"' EXIT

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
covered=$(tail -n +2 "$out/registry.tsv" | cut -f7 | sort -u | wc -l | tr -d ' ')
if [ "$listed" != "$covered" ]; then
  echo "The runner lists $listed end-to-end tests, and the registry covers $covered: they must be the same." >&2
  exit 1
fi

mapfile -t entries < <(tail -n +2 "$out/registry.tsv")
declare -A registered=()
for entry in "${entries[@]}"; do
  registered["${entry%%$'\t'*}"]=1
done

# ── The record as it stands: each entry's row, and its text ─────────────────────────────────────────────────────────
declare -A was_fingerprint=() was_result=()
if [ -f "$record" ]; then
  # | `name` | `Class.Method` | acts | `fingerprint` or - | result |
  while IFS=$'\t' read -r name fingerprint result; do
    was_fingerprint["$name"]=$fingerprint
    was_result["$name"]=$result
  done < <(awk -F'|' '/^\| `[a-z0-9.-]+` \|/ {
      name = $2; fingerprint = $5; result = $6
      gsub(/^[ `]+|[ `]+$/, "", name); gsub(/^[ `]+|[ `]+$/, "", fingerprint); gsub(/^ +| +$/, "", result)
      print name "\t" fingerprint "\t" result
    }' "$record")

  # Each entry's text, from its heading to the next one, kept as it stands: its trailing blank lines dropped, and one put
  # back, so an entry written and read again is the same text. A heading inside a message's fence is part of the message.
  awk -v dir="$out/kept" '
    /^~~~/ { fence = !fence }
    !fence && /^### `[a-z0-9.-]+`$/ {
      if (file != "") { print "" > file; close(file) }
      name = $2; gsub(/`/, "", name); file = dir "/" name ".block"; blank = 0
    }
    file != "" {
      if ($0 == "") { blank++; next }
      while (blank > 0) { print "" > file; blank-- }
      print > file
    }
    END { if (file != "") { print "" > file; close(file) } }' "$record"
fi

# ── What to retake, and why ──────────────────────────────────────────────────────────────────────────────────────────
declare -A named=()
for name in "${names[@]}"; do
  if [ -z "${registered[$name]:-}" ]; then
    echo "'$name' is no sabotage of this suite; $out/registry.tsv lists them." >&2
    exit 1
  fi
  named["$name"]=1
done

plan=()
declare -A why=()
for entry in "${entries[@]}"; do
  IFS=$'\t' read -r name class method acts hold fingerprint test weakens <<< "$entry"
  [ "$hold" != "-" ] && continue
  reason=""
  if [ "${#names[@]}" -gt 0 ]; then
    [ -n "${named[$name]:-}" ] && reason="named"
  elif [ "$all" -eq 1 ]; then
    reason="--all"
  elif [ -z "${was_result[$name]:-}" ]; then
    reason="missing: a new or renamed test or sabotage"
  elif [ "${was_fingerprint[$name]}" != "$fingerprint" ]; then
    reason="stale: McpServerTemplate.E2E/${class##*.}.cs is $fingerprint now, and the red was taken on ${was_fingerprint[$name]}"
  elif [ "${was_result[$name]}" != "red on its claim" ]; then
    reason="not red when last taken (${was_result[$name]})"
  fi

  if [ -n "$reason" ]; then
    plan+=("$name")
    why["$name"]=$reason
  fi
done

dropped=()
for name in "${!was_result[@]}"; do
  [ -z "${registered[$name]:-}" ] && dropped+=("$name")
done

echo
echo "${#entries[@]} sabotages; ${#plan[@]} to retake$([ "${#dropped[@]}" -gt 0 ] && echo "; ${#dropped[@]} no longer in the suite, dropped from the record")."
for name in "${plan[@]}"; do
  printf '  %-66s %s\n' "$name" "${why[$name]}"
done
for name in "${dropped[@]}"; do
  printf '  %-66s dropped: no sabotage of the suite\n' "$name"
done

if [ "$dry" -eq 1 ]; then
  exit 0
fi

# ── Each one to retake, in turn ──────────────────────────────────────────────────────────────────────────────────────
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) platform="Windows" ;;
  *) platform="$(uname -s)" ;;
esac
docker_version=$(docker version --format '{{.Server.Version}}' 2>/dev/null)
commit=$(git rev-parse --short=12 HEAD)
changed=$(git status --porcelain --untracked-files=all -- . ':(exclude).claude' ":(exclude)$record" | wc -l | tr -d ' ')
tree=$([ "$changed" -eq 0 ] && echo "as committed" || echo "with uncommitted changes")

field() { sed -n "s/^$2: //p" "$out/$1.outcome" 2>/dev/null | head -n 1; }

declare -A entry_of=()
for entry in "${entries[@]}"; do
  entry_of["${entry%%$'\t'*}"]=$entry
done

declare -A now_fingerprint=() now_result=()
engine_lost=""
i=0
for name in "${plan[@]}"; do
  i=$((i + 1))
  IFS=$'\t' read -r _ class method acts hold fingerprint test weakens <<< "${entry_of[$name]}"

  # An engine that has stopped answering fails every run that follows at the environment, which is no verdict on any
  # test: the runs stop, and what was taken so far is written. The last run before may have failed for the same reason;
  # it is recorded as it ended, not red, so the next run retakes it.
  if ! engine_answers; then
    engine_lost="$name ($i of ${#plan[@]})"
    break
  fi

  taken=$(date -u +%Y-%m-%dT%H:%M:%SZ)
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

  now_fingerprint["$name"]=$fingerprint
  now_result["$name"]=$verdict
  {
    printf '### `%s`\n\n' "$name"
    printf -- '- Test: `%s`\n' "$(field "$name" test)"
    printf -- '- Acts on %s: %s\n' "$acts" "$weakens"
    printf -- '- Result: %s, with `%s` after %s s\n' "$verdict" "$exception" "$(field "$name" seconds)"
    printf -- '- Taken %s at %s, %s, on %s with Docker %s; test file `%s`\n\n' "$taken" "$commit" "$tree" "$platform" "$docker_version" "$fingerprint"
    printf '~~~text\n'
    if [ -f "$out/$name.message" ]; then cat "$out/$name.message"; else printf 'No message: the run recorded no outcome.\n'; fi
    printf '~~~\n\n'
  } > "$out/new/$name.block"

  printf '%3d/%d  %-66s %s (%s s)\n' "$i" "${#plan[@]}" "$name" "$verdict" "$(field "$name" seconds)"
done

# ── Nothing a run created is left (contract-005 · T-13) ──────────────────────────────────────────────────────────────
left=()
while IFS= read -r c; do [ -n "$c" ] && left+=("container $c"); done < <(docker ps -a --filter "label=$label" --format '{{.Names}}' 2>/dev/null)
while IFS= read -r n; do [ -n "$n" ] && left+=("network $n"); done < <(docker network ls --filter "label=$label" --format '{{.Name}}' 2>/dev/null)
for dir in "${TMPDIR:-/tmp}"/mcp-e2e-*; do [ -e "$dir" ] && left+=("run directory $dir"); done
if docker image inspect mcp-server:latest > /dev/null 2>&1; then left+=("image tag mcp-server:latest"); fi
if [ "${#left[@]}" -gt 0 ]; then
  echo "Left behind by the runs: ${left[*]}" >&2
fi

# ── The record: every entry of the suite, retaken or kept, and a header that is only what they say ────────────────────
rows=()
blocks=()
red=0 held=0 not_red=0
for entry in "${entries[@]}"; do
  IFS=$'\t' read -r name class method acts hold fingerprint test weakens <<< "$entry"
  if [ "$hold" != "-" ]; then
    result="held"
    fingerprint_cell="-"
    printf '### `%s`\n\n- Test: `%s`\n- Acts on %s: %s\n- Held: the test is skipped by decision, so no red can be taken. %s\n\n' \
      "$name" "$test" "$acts" "$weakens" "$hold" > "$out/new/$name.block"
  elif [ -n "${now_result[$name]:-}" ]; then
    result=${now_result[$name]}
    fingerprint_cell="\`${now_fingerprint[$name]}\`"
  elif [ -f "$out/kept/$name.block" ] && [ -n "${was_result[$name]:-}" ]; then
    result=${was_result[$name]}
    fingerprint_cell=$([ "${was_fingerprint[$name]}" = "-" ] && echo "-" || echo "\`${was_fingerprint[$name]}\`")
    cp "$out/kept/$name.block" "$out/new/$name.block"
  else
    # New, and not taken this time: the engine stopped first.
    result="NOT RED: not taken yet"
    fingerprint_cell="-"
    printf '### `%s`\n\n- Test: `%s`\n- Acts on %s: %s\n- Result: not taken yet; run scripts/e2e-sabotage.sh again.\n\n' \
      "$name" "$test" "$acts" "$weakens" > "$out/new/$name.block"
  fi

  case "$result" in
    "red on its claim") red=$((red + 1)) ;;
    held) held=$((held + 1)) ;;
    *) not_red=$((not_red + 1)) ;;
  esac

  rows+=("| \`$name\` | \`${class##*.}.$method\` | $acts | $fingerprint_cell | $result |")
  blocks+=("$out/new/$name.block")
done

tests=$(tail -n +2 "$out/registry.tsv" | cut -f7 | sort -u | wc -l | tr -d ' ')
mapfile -t span < <(grep -h '^- Taken ' "${blocks[@]}" | awk '{print $3}' | sort | sed -n '1p;$p')

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
  echo "Each entry says when, at which commit and on which version of its test's file (the first 12 hex digits of the SHA-256"
  echo "of \`McpServerTemplate.E2E/{Class}.cs\`, line endings aside) its red was taken. An entry is retaken when it is missing,"
  echo "when its test's file has changed since, or when it was not red; the others are kept as they stand. A change to the"
  echo "harness or the product marks no entry stale: after one, \`--all\` retakes every red. Written by"
  echo "\`scripts/e2e-sabotage.sh\`, and held to by \`HarnessSelfTests\`: the suite fails while an entry is missing, stale or"
  echo "not red. CI refuses \`MCP_E2E_SABOTAGE\`."
  echo
  echo "- ${#entries[@]} sabotages over the $tests end-to-end tests the runner lists, theory rows included: $red red on their test's claim, $not_red not, $held held"
  if [ "${#span[@]}" -gt 0 ]; then
    echo "- Reds taken from ${span[0]} to ${span[${#span[@]}-1]}"
  fi
  while IFS=$'\t' read -r nclass ntests nreason; do
    echo "- Not end-to-end, so no sabotage: \`$nclass\` ($ntests tests). $nreason"
  done < "$out/not-end-to-end.tsv"
  echo
  echo "| Sabotage | Test | Acts on | Test file | Result |"
  echo "|---|---|---|---|---|"
  printf '%s\n' "${rows[@]}"
  echo
  echo "## Each sabotage, and its red"
  echo

  # Each entry ends with the blank line that parts it from the next; the file ends with the last entry's last line.
  last=$((${#blocks[@]} - 1))
  [ "$last" -gt 0 ] && cat "${blocks[@]:0:$last}"
  sed '$d' "${blocks[$last]}"
} > "$record.tmp"
mv "$record.tmp" "$record"

echo
echo "${#entries[@]} sabotages: $red red on their claim, $not_red not, $held held; ${#plan[@]} to retake this time. Wrote $record."
if [ -n "$engine_lost" ]; then
  echo "The Docker engine stopped answering before $engine_lost: the runs stopped there. Start it, and run this again." >&2
  exit 1
fi

[ "$not_red" -eq 0 ] && [ "${#left[@]}" -eq 0 ]
