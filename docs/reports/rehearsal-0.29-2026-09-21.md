# Upgrade rehearsal — base-building-kit 0.27 → 0.29

Throwaway copy at `/d/kit-rehearsal`. Staged release: 0.29, commit `f89a984`, 88 files, built with
`meta-bootstrap/release.sh`. Project at `9b61ed4`. Preflight passed.

## The three numbers

| | |
|---|---|
| **Decisions asked** | **3** — the stale line, the workspace line, and the refusal-registry question again |
| **Files that genuinely differed** | **0** |
| **Troubleshooting steps** | **0** |

All five `G*` checks exit 0 after the bases are replaced. `hooks-selftest`: every hook behaved.
`walk.sh`: all **65** states match (was 56 at 0.27). `records-index.sh` runs and produces output.

**`G2-migration.sh` exits 1** — on the contradiction report-005 reported this morning, unchanged in
0.29. See below. By contract-008 G-2 that means the rehearsal does not pass on its own terms, and
the reason is an upstream defect rather than anything about this project's records.

## What 0.29 is

Contracts 020 and 021, and both are answers to this project's reports.

| Our finding | 0.29 |
|---|---|
| r004, r005 — the self-score fails generously at the point it matters | **The drift score block is gone.** Every output now answers four questions about what the framing rests on, two to three sentences each, no identifiers. A new node, `meta-understanding`, holds the reasoning. |
| r005 F-1 — every agent exhausts; cost tracks record size | `checks/records-index.sh`. Its header carries our measurement verbatim: *"one consolidation merged sixteen observations inside its budget and a later one merged two and exhausted … Telling an agent to write early did not help, which rules out thoroughness and leaves size."* |
| r005 F-2 — the gate has no notion of work in flight | `hooks/agent-launch.sh`, a PreToolUse hook that records an agent **starting**, which nothing did before. Its header names all three symptoms we reported: an exhausted agent indistinguishable from one that never ran, two agents dispatched at the same record, and a task in progress looking untouched for three turns. |
| r001 F-5 — the gate never reads `type` | `stop-gate.sh` now reads it (3 occurrences, was 0). |

**Still open, and now across four releases:**

- **The seal.** `templates/settings.template.json` still ships `"deny": ["Read(kit-sealed/**)"]`,
  and the assembler still declares `kit-sealed/` writable. Unchanged at 0.24, 0.25, 0.27 and 0.29.
  Fifty-three candidates, eight due, eleven drift entries and eleven scenario cards sit behind it.
- **Agent turn budgets.** Byte-identical: auditor, clerk, consolidator, assembler, reconstructor at
  30; verifier at 40; steward 25; recorder 8. The index addresses what they read; the budgets
  themselves did not move.
- **`G2-migration` versus contract-018's rule.** Still no exemption for `type: analysis-report`
  (0 occurrences in the script). See below.

## Defects the copy caught

1. **`G2-migration` fails on `report-005`, which was written exactly as the rule requires.**
   contract-018 says an analysis report *"takes none of the fields below"*; `G2-migration.sh:101`
   demands `verification_state`, `audited`, `disappointment` and `premortem` on every entry keyed by
   `contract_id`, with no exemption. Reports 001 through 004 pass only because they carry `legacy`
   in those fields — they pass by being wrong in the way the check wants. There is no state that
   satisfies both. Reported as report-005 F-3 this morning; unchanged.

2. **The refusal registry lost this project's lines, exactly as predicted.** At the 0.27 upgrade the
   sheet's question 3 warned that registering `P-010.sh`'s two refusals in
   `checks/refusal-nextsteps.txt` — a kit file — would make those lines residue at every future
   upgrade. `install.sh` replaced the file and `G6-refusals` went red. Re-added in the rehearsal;
   it will need re-adding at the real run and at every upgrade after, until either the registry
   takes project entries somewhere of its own or `P-010.sh` carries its next steps in its own
   messages.

3. **`meta-understanding` ships as a skill and is registered in no manifest node.** The staged
   `MANIFEST.template.yaml` contains zero references to it, yet `meta-understanding/SKILL.md`
   travels in the release and `M-28` now points at it. `G2-migration` checks that every node of the
   staged template is registered; a node the template omits is invisible to that check. The node
   has no `owns:`, no `triggers`, no maturity status, and nothing will report it as thin or
   missing.

4. **`G4-pointers` fails before the bases are replaced**, on `templates/MAP.template.md:19` naming
   a heading 0.29 renamed. Same expected pattern as `G3-retired`, for the same reason: the checks
   run against the old shipped copies on purpose. It exits 0 once the bases are replaced. Recorded
   so a future run does not read it as a defect.

## What the migration actually changes

- `MANIFEST.yaml` — `base_kit_version: 0.27 → 0.29`. No new base nodes in the template.
- `MAP.md` — two rows refreshed, **M-28** and **M-18**, keeping their status. M-28 is the important
  one: its target moves from `meta-antidrift` to `meta-understanding`, and its pointer from
  *Close every output with this block* to *Close every output by answering these four*. No entries
  added or withdrawn.
- All eight record headers refreshed, `merge.sh --header`, no pioneer notes to carry.
- `settings.json` — written from the staged template, which now carries the `agent-launch` hook.
- Three new files: `checks/records-index.sh`, `hooks/agent-launch.sh`, `meta-understanding/SKILL.md`.
- `CLAUDE.md` — kit block replaced, no applications line.
- **No record needed a field migration.**
- 78 files taken, 3 agents deployed, 0 stale removed. `P-010.sh` kept.

## Every figure, and the command that produced it

| Figure | Command |
|---|---|
| 88 files in the release | `cat .claude/kit-incoming/RELEASE` |
| 0 skills evolved here | recomputed baseline joined against `INSTALLED.sha1` |
| 3 files new in 0.29 | staged list tested against `.claude/skills/` |
| 78 taken, 3 agents, 0 stale | `install.sh upgrade plan.txt` |
| 2 map rows refreshed, 0 added | staged vs installed `MAP.template.md` |
| 0 base nodes added | node ids compared across both templates |
| 0 references to meta-understanding in the manifest template | `grep -c` on the staged template |
| seal unchanged | `grep -A3 '"deny"'` on the staged settings template |
| budgets unchanged | `grep maxTurns` across the staged `agents/` |
| stop-gate reads `type`: 3 | `grep -c type` on the staged hook |
| G2 exempts reports: 0 | `grep -c 'analysis-report\|report-'` on the staged check |
| all five G checks exit 0 after bases replaced | each run in `.claude/skills` |
| G2-migration exit 1 | same, with the staged template |
| every hook behaved | `checks/hooks-selftest.sh` from the project root |
| walk: 65 states | `meta-mechanisms/tests/walk.sh` |
| 125 files in the new baseline | the 6j command |

## What this rehearsal did not check

- **The four closing questions in practice.** The format change is read, not exercised — no output
  has yet been written under it, and whether it surfaces a thin basis or merely describes one is
  unknown until it has been used against real work.
- **`records-index.sh` under load.** It runs and prints, but no agent has used it, so whether it
  actually keeps a run inside its budget is untested. The budgets themselves did not change.
- **`agent-launch.sh` firing.** `hooks-selftest` exercises each hook once against a copy; no kit
  agent was launched in the rehearsal, so the start-record and the collision detection it enables
  were not seen working.
- **The seal.** `kit-sealed/` is empty and no batch exists, so the defect behind every blocked
  adoption in this project was not exercised for the fourth upgrade running.
- **`merge.sh`'s three-way merge.** Zero residue again — third upgrade with no skill evolved here,
  so conflicts and the reapply/replace question remain untested.
