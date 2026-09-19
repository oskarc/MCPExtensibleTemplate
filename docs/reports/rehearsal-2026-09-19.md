# Upgrade rehearsal — base-building-kit 0.16 → 0.25

Run on a throwaway copy at `/d/kit-rehearsal` (short path chosen under step 2's allowance).
Staged release: `RELEASE` = base-building-kit 0.25, commit `0d2f55a`, 81 files, built with
`meta-bootstrap/release.sh` from the kit repository. Project at the time: commit `ecb526f`.

## The three numbers

| | |
|---|---|
| **Decisions asked** | **3** — all of them standing questions (stale list, workspace, contract-log fields). The acceptance line is not counted. |
| **Files that genuinely differed** | **0** |
| **Troubleshooting steps** | **0** |

`checks/G2-migration.sh` exits **0** on the migrated copy. **The rehearsal passes.**

## Why "files that genuinely differed" is zero

A file differs when it differs from the baseline *and* its content differs from the staged copy.
A record has no staged copy, so it counts only when the template rule puts a question about it on
the sheet.

- **No skill was ever evolved here.** Every `meta-*/SKILL.md`, `INTENT.md`, hook, check and agent
  hashes identical to `INSTALLED.sha1`. No residue, therefore no merge, no conflict, and no
  reapply/replace question.
- **Seven records differ from the baseline** — CASEBOOK, CONTRACT-LOG, CORRECTIONS, DRIFTLOG,
  LEARNINGLOG, LEDGER and MAP.md. That is the lifecycle's own writing, which is never replaced.
- **The template rule raises no question.** All eight header blocks refresh mechanically
  (`merge.sh --header`, exit 0 each, `0 line(s) that the old template never had` every time). All
  30 base map entries are identical to the installed template apart from the status column, which
  the rule excepts. All 26 base node lines are unchanged from the installed template.

## Defects the copy caught

1. **MAP.md's header was missed on the first pass.** Step 7 says the header rule covers "the six
   above, and the learning log and the casebook" — six includes MAP.md. The first pass refreshed
   the seven YAML records and skipped the map. `G3-retired.sh` surfaced it, but only as a **note**
   (`meta-map/MAP.md:4 says "copies this file" in a line that is the project's own`) once the
   comparison base had been replaced — which is precisely the weakness step 9 names. Fixed in the
   procedure being followed; the real run refreshes eight headers, not seven.

2. **`P-010.sh` is not stale, and a careless read would have deleted it.** The rule makes a
   `P-NNN.sh` check stale when its precedent id is *not* in this project's casebook. `P-010` **is**
   in this casebook — it came from C-010, contract-002 left half-implemented — so the check is this
   project's own and stays. It is absent from the baseline only because it was written after the
   last install.

3. **`G3-retired.sh` exits 1 before the bases are replaced.** Expected, and documented: the failing
   line is in the *old* `templates/MANIFEST.template.yaml`, which step 9 replaces after the checks.
   It exits 0 once the bases are replaced. Recorded so a future run does not read it as a defect.

Nothing else. No residue line failed to reapply (there was no residue). No reapplied line names
anything the staged kit removed or renamed (there were no reapplied lines).

## Every figure, and the command that produced it

| Figure | Command |
|---|---|
| 81 files in the staged release | `cat .claude/kit-incoming/RELEASE` |
| 94 kit files hashed now; 93 in the baseline | the 6j `find … sha1sum` command |
| 0 skills evolved; 7 records differ | `join` of the recomputed baseline against `INSTALLED.sha1` |
| 10 files new in the kit | staged file list tested against `.claude/skills/` |
| 5 stale files (P-010.sh excluded) | project `*.sh`/`*.expected` tested against the staged release |
| 8 headers refresh with 0 notes kept | `checks/merge.sh --header` per record |
| 30 base map entries, 0 changed | rows compared with CRs stripped and the status column dropped |
| 26 base nodes, 0 lines changed | `- {id: base-…}` lines compared to the installed template |
| 1 map row changed by 0.25 (M-25) | installed vs staged `MAP.template.md` |
| 0 base nodes added, 0 withdrawn by 0.25 | node ids compared across both templates |
| 1 contract-log entry missing fields (report-001) | parsed and tested for the six keys |
| 0 corrections missing the three probes | parsed and tested for the three keys |
| 0 retired ledger keys, 0 `lower_bound` | parsed `scores` and `candidates` |
| 7 drift entries, all `open`, 0 `mitigated` | `grep -oE '^\s+status: [a-z]+' \| uniq -c` |
| 0 project roots to draft into the map | `checks/roots.sh MANIFEST.yaml` |
| G1/G3/G4/G5 exit 0 after bases replaced | each script run in `.claude/skills` |
| G2-migration exit 0 | `G2-migration.sh . <staged template> /tmp/pre` |
| 98 files in the regenerated baseline | the 6j command |
| every hook behaved | `checks/hooks-selftest.sh` |
| walk: all 47 states match | `meta-mechanisms/tests/walk.sh` |
| MANIFEST lost exactly 1 line, gained 2 | `comm` of pre- and post-migration, comments dropped |

## What the migration actually changes

- `MANIFEST.yaml` — `base_kit_version: 0.16 → 0.25`, and `workspace: []` added. Nothing else.
- `CONTRACT-LOG.yaml` — `report-001` gains `verification_state`, `audited`, `disappointment`,
  `premortem`, `red_test`, `cost`, each `legacy`. `contract-001` and `contract-002` already carry
  every field and are untouched.
- `CORRECTIONS.yaml`, `LEDGER.yaml`, `DRIFTLOG.yaml`, `CASEBOOK.yaml`, `LEARNINGLOG.yaml` — header
  block only; no field migration was due.
- `MAP.md` — header refreshed; the M-25 row takes the staged template's text and keeps its
  `ratified` status. The project name in the title is preserved.
- `settings.json` — all 7 kit hook groups replaced (there are no project groups). 0.25's hooks carry
  a root-finding wrapper so they fire correctly from a subdirectory. `permissions.deny` keeps
  `Read(kit-sealed/**)`; `additionalDirectories: []` is added.
- `CLAUDE.md` — **unchanged** while `workspace` is empty: 0.25's block adds only an
  `Applications in the system flow` line, which is removed when the list is empty.

## What this rehearsal did not check

- That the upgrade fixes anything in `report-001`. It does not — all five findings were verified
  still open at 0.25 before this began.
- Behaviour of the new hooks under a real Claude Code session. `hooks-selftest.sh` runs each hook
  once against a copy; it does not exercise them as events in a live session.
- The seal. `kit-sealed/` is empty in this project and no batch exists, so no upgrade step touched
  the mechanism `report-001` F-1 describes.
- Whether `status: published` on `report-001` survives the migration's intent. It is outside the
  documented enum by necessity (F-5); the migration leaves present values alone, so it survives.
