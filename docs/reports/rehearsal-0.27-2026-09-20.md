# Upgrade rehearsal — base-building-kit 0.25 → 0.27

Throwaway copy at `/d/kit-rehearsal`. Staged release: 0.27, commit `0159092`, 85 files, built with
`meta-bootstrap/release.sh`. Project at `8889fca`. Preflight passed.

## The three numbers

| | |
|---|---|
| **Decisions asked** | **3** — the stale line, the workspace line, and one new question this upgrade creates (below) |
| **Files that genuinely differed** | **0** |
| **Troubleshooting steps** | **0** |

`G2-migration.sh` exits 0. All five `G*` checks exit 0. `hooks-selftest`: every hook behaved.
`walk.sh`: all **56** states match (was 47 at 0.25). **The rehearsal passes.**

## What 0.27 is

Contracts 018 and 019, and both are answers to this project's reports.

| Our finding | 0.27 |
|---|---|
| r002 F-2 — no install script, step 5 written twice | `checks/install.sh`, called identically by both runs |
| r002 F-4 — the stale rule is one clause from deleting a project's own check | `install.sh` **refuses**, and names the file |
| r002 F-1 — "the six above" is an enumeration by count | step 7 now reads "The records are **eight**" and lists them |
| r002 F-7 / r001 F-5 — the log has no model of a report | an analysis report "is left alone"; `status: published` is sanctioned |
| r003 F-1 — the auditor's search is unbounded on JSONL | `checks/transcript-digest.sh`; its header is our measurement, restated |
| r003 F-2 — no way to record that a task is impossible | `blocked` with `blocked_since`, `blocked_reason`, `blocked_waiting_for` |
| r003 F-3 — strict priority starves everything below a stuck task | a new **position 0**: a blocked task is put to the pioneer once, then "the rest of the queue is routed as usual" (M-32) |
| r003 rec 3 — let the gate notice repetition | the gate now says "this is the third turn running with the same kit task and nothing has changed — which is a fault, not a backlog" |
| r003 rec 4 — stop scoring an honest refusal as a deviation | `meta-antidrift`: "A kit task that cannot be done is not a deviation when its record says so" |

Still open from report-001: **F-1 the seal** (the deny rule and the assembler's write scope are
unchanged), **F-3 the walk's fixture** (no `settings.json`, no `kit-sealed`), **F-5 the gate reading
`type`** (still zero occurrences).

## Defects the copy caught

1. **`G6-refusals.sh` fails on this project's own `P-010.sh`.** The new check requires every refusal
   the kit can print to be registered with a way forward. `P-010.sh` — written here, from precedent
   P-010 — prints two refusals that say what is wrong and not what to do. The check is correct and
   the failure is ours.

   The prescribed remedy works: both messages added to `checks/refusal-nextsteps.txt`, each with a
   `-> ` line. The second had to be written in the check's normalised form, with its interpolated
   variable as `—<>`; the first attempt registered only one of the two and the check still failed,
   which is how the form was found.

   **The cost, and why it is on the sheet:** `refusal-nextsteps.txt` is a kit file that ships.
   Appending to it makes those lines residue, so every future upgrade will ask about this file.

2. **A hazard I raised and then retracted.** I reported that 0.27 ships `P-004.sh`–`P-007.sh`
   enforcing the base kit's precedents 4–7, while this casebook has entirely different precedents
   under those numbers — so the stale rule would keep all four and silently enforce foreign holdings.
   **It is not real.** I had checked the repository tree, not the release. `release.sh` excludes the
   base kit's own `P-NNN.sh` checks; they never travel. The only way to hit it is to stage a raw
   clone, which preflight refuses. Recorded because the reasoning was sound and the artifact was
   wrong, which is a repeatable mistake.

3. **`hooks-selftest.sh` must be run from the project root.** Run from `.claude/skills` it says so
   and exits. My invocation error, corrected on the spot; not a kit defect and not counted as
   troubleshooting.

## What the migration actually changes

- `MANIFEST.yaml` — `base_kit_version: 0.25 → 0.27`. No new base nodes.
- `MAP.md` — six base entries refreshed (M-05, M-19, M-20, M-21, M-27, M-30), keeping their status;
  **one new entry added, M-32** — `task-blocked | hook: Stop | must | a kit task cannot be completed,
  or the gate reports one three turns running`, which arrives as `proposed` for the pioneer.
- All eight record headers refreshed, `merge.sh --header`, 0 pioneer notes to carry in each.
- `settings.json` — written from the staged template by `install.sh`; `permissions.deny` kept.
- `CLAUDE.md` — kit block replaced, no applications line (workspace empty).
- **No record needed a field migration.** Contracts carry every field; reports carry
  `type: analysis-report` and `status: published`, which 0.27 now sanctions; corrections carry the
  three probes; the ledger has no retired keys; all eight drift entries carry `status`.
- `.gitignore` gained `.claude/skills/meta-ledger/.session-started`; `meta-ledger/batches` recreated.
- 75 files taken, 1 agent deployed, 0 stale removed, 1 spared.

## Every figure, and the command that produced it

| Figure | Command |
|---|---|
| 85 files in the release | `cat .claude/kit-incoming/RELEASE` |
| 0 skills evolved; 5 records differ | recomputed baseline joined against `INSTALLED.sha1` |
| 4 files new in 0.27 | staged file list tested against `.claude/skills/` |
| 75 taken, 1 spared, 0 stale | `install.sh upgrade plan.txt` |
| 8 headers, 0 notes each | `merge.sh --header` per record |
| 6 map rows refreshed, 1 added | staged vs installed `MAP.template.md` |
| 0 base nodes added | node ids compared across both templates |
| 0 records missing a field | parsed and tested per the step 7 list |
| all G checks + G2 exit 0 | each run in `.claude/skills` |
| every hook behaved | `hooks-selftest.sh` from the project root |
| walk: 56 states | `meta-mechanisms/tests/walk.sh` |
| 121 files in the new baseline | the 6j command |

## What this rehearsal did not check

- **The seal.** `kit-sealed/` is empty and no batch exists, so report-001 F-1 was not exercised and
  will fail the same way at the next batch.
- **The blocked state end to end.** The copy did not record a task blocked, so the new position-0
  gate message, the three-turn repetition detector and the "not a deviation" rule were read, not run.
- **The transcript digest.** `transcript-digest.sh` was not executed against this session's 70 MB
  transcript; whether the digest is small enough for the auditor is still arithmetic, not observation.
- **`merge.sh`'s three-way merge.** Zero residue again, so conflicts and the reapply/replace question
  went untested for the second upgrade running.
