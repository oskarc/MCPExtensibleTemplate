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
# exception, and does not count. A test skipped by decision is held, and not run: its Skip names a decision
# McpServerTemplate.E2E/HELD.md holds.
#
# contract-005 · G-17 round 1 (HAR-R1-07) — and a red counts only when the same test passed without its sabotage, in the
# same run of this script: before the sabotages, one control run (MCP_E2E_CONTROL) runs every test to be retaken with no
# sabotage applied, and the record keeps both, the control beside the red. A test that did not pass there is not retaken,
# and its entry says why.
#
# The record, McpServerTemplate.E2E/SABOTAGE-RECORD.md, holds one entry per sabotage: its test, where it acts and what it
# weakens, its control, its red and the failure message, and when, at which commit and on which version of its test's file
# the red was taken. By default this script retakes only the entries that are no longer true, and keeps every other as it
# is:
#   - missing: a new or renamed test or sabotage;
#   - stale: the test's own file, McpServerTemplate.E2E/{Class}.cs, has changed since the red was taken — each entry keeps
#     the first 12 hex digits of that file's SHA-256, line endings aside, and the harness computes it the same way;
#   - not red: it did not go red on its claim when it was last taken.
# An entry whose sabotage no longer exists is dropped.
#
# contract-005 · G-17 round 1 (HAR-R1-01, HAR-R1-02) — two checks hold the record, and they prove different things. The
# per-push check, HarnessSelfTests, proves coverage and freshness of each test file: every test the runner lists has a
# sabotage, every sabotage an entry, and each entry's red was taken on its test's file as that file is now, with its result
# and its message kept. It cannot prove a red was taken — a fingerprint anyone can compute is no proof of a run, and CI's
# push and pull-request runs refuse sabotages — and a change to the harness or the product marks no entry stale. The
# scheduled retake, .github/workflows/sabotage-retake.yml (weekly, and by hand), proves the reds: it runs this script with
# --all --same-verdicts on a clean checkout, which fails when any sabotage does not go red on its claim or when a verdict
# differs from the committed record's, and keeps the record it wrote as the run's artifact. It pushes nothing.
#
#   bash scripts/e2e-sabotage.sh               retake what is missing, stale or not red, and keep the rest: each red took
#                                              49 s here (23 in 18 min 45 s, 2026-09-29), and the control run is one run of
#                                              those tests beside them
#   bash scripts/e2e-sabotage.sh --all         retake every sabotage: 99 reds at that rate, and the control run
#   bash scripts/e2e-sabotage.sh NAME ...      retake these, whatever their state, and keep the rest
#   bash scripts/e2e-sabotage.sh --dry-run     say what would be retaken, and why; run nothing
#   bash scripts/e2e-sabotage.sh --left-behind say whether anything a run created is left, as every run of this script
#                                              ends by saying; run nothing, and build nothing
#   ... --same-verdicts                        and fail when any verdict differs from the record as it stood before the run
#                                              (in the scheduled retake, a clean checkout: the committed record's)
#
# Local, or the scheduled retake: CI's push and pull-request runs refuse MCP_E2E_SABOTAGE and MCP_E2E_CONTROL, the harness
# does, and so does this script. It needs Docker: every sabotage starts the environment afresh.
#
# Exit 0: every entry of the record is red on its claim, or held, nothing was left behind, and with --same-verdicts no
# verdict changed. Exit 1: one of those is not so (named above), or the run could not be made. With --left-behind: exit 0
# when nothing a run created is left, and 1, naming each thing, when something is.

set -uo pipefail

cd "$(dirname "$0")/.." || exit 1

all=0
dry=0
same=0
check=0
names=()
for arg in "$@"; do
  case "$arg" in
    --all) all=1 ;;
    --dry-run) dry=1 ;;
    --same-verdicts) same=1 ;;
    --left-behind) check=1 ;;
    -h|--help) sed -n '2,55p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) echo "Unknown option '$arg'; see --help." >&2; exit 1 ;;
    *) names+=("$arg") ;;
  esac
done

if [ "$all" -eq 1 ] && [ "${#names[@]}" -gt 0 ]; then
  echo "Give --all or the names of sabotages to retake, not both." >&2
  exit 1
fi

if [ "$check" -eq 1 ] && [ $((all + dry + same + ${#names[@]})) -gt 0 ]; then
  echo "Give --left-behind alone: it runs nothing, and says only whether anything a run created is left." >&2
  exit 1
fi

label=org.mcp-server-template.e2e.run

# Whether the Docker engine answers. docker ps fails when it does not (docker info does not).
engine_answers() { docker ps -q > /dev/null 2>&1; }

# ── What a run created and left (contract-005 · T-13) ─────────────────────────────────────────────────────────────────
# Every container and network carrying the run label, and every run directory in the temp folder, which holds a run's keys.
#
# contract-005 · G-17 round 1 (CON-R1-03) — and mcp-server:latest, the name docs/04's compose file deploys, which a developer
# may well have an image of their own under. A run gives that name to its own build only for the docs profile, and puts it
# back after; a run killed before that leaves the record of it in its run directory (tags.tsv, RecordingDockerClient: one
# line per name given, the name, what it named before, what the run gave it). In CI the runner has no image of its own, so
# any mcp-server:latest there is a run's, and is left behind. Locally, it is left behind only when a run's record shows that
# run gave the name the image it names now, which it did not name before that run: a developer's own image of that name, or
# one put back as it was, is theirs, and is not flagged. Sets left, one entry for each thing left.
docs_image=mcp-server:latest
left_behind() {
  left=()
  while IFS= read -r c; do [ -n "$c" ] && left+=("container $c"); done < <(docker ps -a --filter "label=$label" --format '{{.Names}}' 2>/dev/null)
  while IFS= read -r n; do [ -n "$n" ] && left+=("network $n"); done < <(docker network ls --filter "label=$label" --format '{{.Name}}' 2>/dev/null)
  for dir in "${TMPDIR:-/tmp}"/mcp-e2e-*; do [ -e "$dir" ] && left+=("run directory $dir"); done
  if [ -n "${CI+set}" ]; then
    if docker image inspect "$docs_image" > /dev/null 2>&1; then left+=("image tag $docs_image"); fi
    return
  fi

  local now tags
  now=$(docker image inspect "$docs_image" --format '{{.Id}}' 2>/dev/null)
  [ -n "$now" ] || return
  for tags in "${TMPDIR:-/tmp}"/mcp-e2e-*/tags.tsv; do
    [ -f "$tags" ] || continue
    # The run gave the name the image it names now, and the name did not name that image before the run.
    if awk -F'\t' -v name="$docs_image" -v id="$now" '
        $1 == name && !seen { seen = 1; before = $2 }
        $1 == name && $3 == id { given = 1 }
        END { exit !(given && before != id) }' "$tags"; then
      left+=("image tag $docs_image, given $now by the run whose directory is $(dirname "$tags")")
    fi
  done
}

# --left-behind: the check alone. It sends no sabotage and runs no test, so CI's refusal below is not its to meet.
if [ "$check" -eq 1 ]; then
  if ! engine_answers; then
    echo "The Docker engine does not answer (docker ps: $(docker ps 2>&1 | tail -n 1)). Start it, and run this again." >&2
    exit 1
  fi
  left_behind
  if [ "${#left[@]}" -gt 0 ]; then
    echo "Left behind by the runs:" >&2
    printf '  %s\n' "${left[@]}" >&2
    exit 1
  fi
  echo "Nothing a run created is left$([ -n "${CI+set}" ] && echo " (CI: no $docs_image at all)" || echo " (a $docs_image no run's record gave is not a run's)")."
  exit 0
fi

# contract-005 · G-17 round 1 (HAR-R1-01) — in CI, only the scheduled retake: its own workflow, on its schedule or by hand. The
# harness admits the same run and no other (Sabotage.Run.IsScheduledRetake).
retake=.github/workflows/sabotage-retake.yml
if [ -n "${CI+set}" ]; then
  case "${GITHUB_EVENT_NAME:-}:${GITHUB_WORKFLOW_REF:-}" in
    schedule:*/"$retake"@*|workflow_dispatch:*/"$retake"@*) ;;
    *)
      echo "Refused: CI is set, and this is not the scheduled retake ($retake, on its schedule or by hand; this is" \
        "'${GITHUB_EVENT_NAME:-no event}' of '${GITHUB_WORKFLOW_REF:-no workflow}'). A sabotage weakens the environment on purpose;" \
        "CI's push and pull-request runs never run one (contract-005 · G-11)." >&2
      exit 1
      ;;
  esac
fi

for variable in MCP_E2E_SABOTAGE MCP_E2E_CONTROL; do
  if [ -n "${!variable+set}" ]; then
    echo "Refused: $variable is already set ('${!variable}'). This script names each sabotage, and each control, itself." >&2
    exit 1
  fi
done

project=McpServerTemplate.E2E/McpServerTemplate.E2E.csproj
out=TestResults/sabotage
record=McpServerTemplate.E2E/SABOTAGE-RECORD.md
claim=McpServerTemplate.E2E.Harness.ClaimException

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

# ── The registry, as the harness reads it, checked against every end-to-end test the runner lists ─────────────────────
# contract-005 · G-17 round 1 (HAR-R1-03) — the check compares the runner's own list (dotnet test --list-tests) with the
# registry, test method by test method, and refuses [NotEndToEnd] on any class but HarnessSelfTests; CI runs it on every push.
echo "Reading the registry..."
if ! dotnet test "$project" --no-build -c Release \
    --filter "FullyQualifiedName=McpServerTemplate.E2E.HarnessSelfTests.Every_end_to_end_test_has_a_named_sabotage" \
    > "$out/registry.log" 2>&1; then
  echo "Not every end-to-end test the runner lists has a named sabotage; see $out/registry.log:" >&2
  grep -A20 "Error Message" "$out/registry.log" >&2
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

# contract-005 · G-17 round 1 (HAR-R1-07) — the control run: every test to be retaken, once, with no sabotage applied, in one
# run of the suite. A red counts only for a test that passed here.
if [ "${#plan[@]}" -gt 0 ]; then
  filter=$(for name in "${plan[@]}"; do
      IFS=$'\t' read -r _ class method _ <<< "${entry_of[$name]}"
      echo "FullyQualifiedName=$class.$method"
    done | sort -u | paste -sd'|' -)
  controlled=$(IFS=,; echo "${plan[*]}")
  echo
  echo "The control run: the ${#plan[@]} tests to retake, with no sabotage..."
  control_taken=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  MCP_E2E_CONTROL="$controlled" dotnet test "$project" --no-build -c Release \
    --filter "$filter" \
    --logger "console;verbosity=normal" \
    --blame-hang --blame-hang-timeout 10m --blame-hang-dump-type none \
    > "$out/control.log" 2>&1
  if ! ls "$out"/*.control.outcome > /dev/null 2>&1; then
    echo "The control run recorded no outcome for any test (see $out/control.log): nothing was retaken, and the record is as it was." >&2
    exit 1
  fi
  if ! engine_answers; then
    engine_lost="the control run"
  fi
fi

i=0
for name in "${plan[@]}"; do
  i=$((i + 1))
  [ -n "$engine_lost" ] && break
  IFS=$'\t' read -r _ class method acts hold fingerprint test weakens <<< "${entry_of[$name]}"

  # An engine that has stopped answering fails every run that follows at the environment, which is no verdict on any
  # test: the runs stop, and what was taken so far is written. The last run before may have failed for the same reason;
  # it is recorded as it ended, not red, so the next run retakes it.
  if ! engine_answers; then
    engine_lost="$name ($i of ${#plan[@]})"
    break
  fi

  control=$(field "$name.control" outcome)
  control_line="- Control: $([ -n "$control" ] && echo "$control" || echo "no outcome recorded") without the sabotage, in the control run of $control_taken$([ -n "$control" ] && echo " after $(field "$name.control" seconds) s")"
  taken=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  if [ "$control" != passed ]; then
    # Not shown to be the sabotage's: its test does not pass without it. No sabotage is run; the control's message is kept.
    verdict="NOT RED: without the sabotage its test ${control:-recorded no outcome}$([ "$control" = failed ] && echo ", with $(field "$name.control" exception)")"
    exception=$(field "$name.control" exception)
    seconds=$(field "$name.control" seconds)
    message="$out/$name.control.message"
  else
    MCP_E2E_SABOTAGE="$name" dotnet test "$project" --no-build -c Release \
      --filter "FullyQualifiedName=$class.$method" \
      --logger "console;verbosity=normal" \
      --blame-hang --blame-hang-timeout 10m --blame-hang-dump-type none \
      > "$out/$name.log" 2>&1

    outcome=$(field "$name" outcome)
    acted=$(field "$name" acted)
    exception=$(field "$name" exception)
    seconds=$(field "$name" seconds)
    message="$out/$name.message"
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
  fi

  now_fingerprint["$name"]=$fingerprint
  now_result["$name"]=$verdict
  ran=$(field "$name" test)
  [ -z "$ran" ] && ran=$(field "$name.control" test)
  [ -z "$ran" ] && ran=$test
  {
    printf '### `%s`\n\n' "$name"
    printf -- '- Test: `%s`\n' "$ran"
    printf -- '- Acts on %s: %s\n' "$acts" "$weakens"
    printf -- '%s\n' "$control_line"
    printf -- '- Result: %s, with `%s` after %s s\n' "$verdict" "${exception:--}" "${seconds:-0.0}"
    printf -- '- Taken %s at %s, %s, on %s with Docker %s; test file `%s`\n\n' "$taken" "$commit" "$tree" "$platform" "$docker_version" "$fingerprint"
    printf '~~~text\n'
    if [ -f "$message" ]; then cat "$message"; else printf 'No message: the run recorded no outcome.\n'; fi
    printf '~~~\n\n'
  } > "$out/new/$name.block"

  printf '%3d/%d  %-66s %s (%s s)\n' "$i" "${#plan[@]}" "$name" "$verdict" "${seconds:-0.0}"
done

# ── Nothing a run created is left (contract-005 · T-13; left_behind, above) ─────────────────────────────────────────────
left_behind
if [ "${#left[@]}" -gt 0 ]; then
  echo "Left behind by the runs: ${left[*]}" >&2
fi

# ── The record: every entry of the suite, retaken or kept, and a header that is only what they say ────────────────────
rows=()
blocks=()
red=0 held=0 not_red=0
declare -A final_result=()
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

  final_result["$name"]=$result
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
  echo "of \`McpServerTemplate.E2E/{Class}.cs\`, line endings aside) its red was taken. An entry taken since contract-005 · G-17"
  echo "round 1 also says how its test ended in the control run taken beside it — the same test, in the same run of"
  echo "\`scripts/e2e-sabotage.sh\`, with no sabotage applied — and its red counts only when that passed. An entry is retaken"
  echo "when it is missing, when its test's file has changed since, or when it was not red; the others are kept as they stand."
  echo "A test skipped by decision is held, and its Skip names the decision \`McpServerTemplate.E2E/HELD.md\` holds."
  echo
  echo "Two checks hold this record, and prove different things. On every push, \`HarnessSelfTests\` proves coverage and"
  echo "freshness of each test file: every end-to-end test the runner lists has a sabotage, every sabotage has an entry here,"
  echo "and each entry's red was taken on its test's file as that file is now, with its result and its message kept. It cannot"
  echo "prove that a red was taken — a fingerprint anyone can compute is no proof of a run — and CI's push and pull-request"
  echo "runs refuse \`MCP_E2E_SABOTAGE\`. The scheduled retake (\`.github/workflows/sabotage-retake.yml\`, weekly and by hand)"
  echo "proves the reds: it takes every one afresh on a clean checkout, and fails when any sabotage does not go red on its claim"
  echo "or when a verdict differs from the one committed here. A change to the harness or the product marks no entry stale;"
  echo "the scheduled retake takes it again. Written by \`scripts/e2e-sabotage.sh\`."
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

# contract-005 · G-17 round 1 (HAR-R1-01) — --same-verdicts: every entry's verdict is the one the record held before this run.
changed_verdicts=()
if [ "$same" -eq 1 ]; then
  for name in $(printf '%s\n' "${!final_result[@]}" "${!was_result[@]}" | sort -u); do
    if [ "${was_result[$name]:-(no entry)}" != "${final_result[$name]:-(no entry)}" ]; then
      changed_verdicts+=("$name: '${was_result[$name]:-(no entry)}' before, '${final_result[$name]:-(no entry)}' now")
    fi
  done
  if [ "${#changed_verdicts[@]}" -gt 0 ]; then
    echo "The verdicts differ from the record's before this run, in ${#changed_verdicts[@]} entries:" >&2
    printf '  %s\n' "${changed_verdicts[@]}" >&2
  else
    echo "Every verdict is the record's before this run."
  fi
fi

[ "$not_red" -eq 0 ] && [ "${#left[@]}" -eq 0 ] && [ "${#changed_verdicts[@]}" -eq 0 ]
