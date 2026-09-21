# Upgrade rehearsal — base-building-kit 0.29 → 0.31

Throwaway copy at `/d/kit-rehearsal`. Staged release: 0.31, commit `e29cac2`, 90 files. Project at
`6e56cd5`. Preflight passed.

## The three numbers

| | |
|---|---|
| **Decisions asked** | **3** — the stale line, the workspace line, the refusal registry again |
| **Files that genuinely differed** | **0** |
| **Troubleshooting steps** | **0** |

All five `G*` checks exit 0. **`G2-migration.sh` exits 0 — the first clean migration check this
project has had.** `hooks-selftest`: every hook behaved. `walk.sh`: all **78** states (was 65).
**The rehearsal passes.**

## The headline: the seal is fixed

`hooks/seal-key.sh` is new. The key is now written by a script run through Bash, because the
program refuses the *file tools* in that folder but does not refuse a script — which is how the key
was always opened. The assembler's step 6 changed accordingly: *"with Bash, through the sealing
script, never with Write or Edit."* Its `write-scope.sh` frontmatter no longer claims
`kit-sealed/`, which removes the contradiction between what it was permitted and what it was told.

The kit's own note on contract-023 says where it came from:

> Contract-023 came from reading the whole kit for places where one part contradicts another, after
> a downstream report argued that the sealed key had survived four releases because it lives in the
> one layer no shell test can reach. There were nineteen, and nine had been introduced by the four
> contracts before this one — each of which passed its own tests, because each test asked whether
> the new rule did what it said and none asked what else passes through the point that was changed.

And: *"a review batch can be assembled for the first time."*

**report-006's mechanism was wrong.** Their first-hand reproduction found the program treating the
folder as a sensitive location and asking a human, with nobody present to answer — not a Read deny
blocking the Write tool, which is what this project reported second-hand from a subagent's refusal
message. Their finding notes the read rule alone did **not** stop a new file being written there in
their test, so the behaviour differs between versions of the program. The report's argument was
right that it was broken and right that it had gone unfixed; it was wrong about why, and the report
needs correcting.

## Also fixed, both from report-006

- **`G2-migration` no longer demands contract fields of an analysis report.** The contradiction with
  contract-018 that failed this project's check all day is gone.
- **`meta-understanding` is registered**, as `base-understanding`, with `owns:`, `triggers: [M-28]`
  and `status: new`. It shipped unregistered in 0.29.

## Defects the copy caught

1. **The refusal registry lost this project's lines for the second consecutive upgrade.**
   contract-023 edited `checks/refusal-nextsteps.txt`, `install.sh` replaced it, and our two
   `P-010` entries went with it. Predicted on the 0.27 sheet, confirmed at 0.29, confirmed again
   here. Re-added in the rehearsal and it will need re-adding at the real run.

2. **One migration step the standard list does not name.** `G2-migration` fails until
   `base-understanding` is registered in this project's manifest — node line and coverage line, both
   taken from the staged template. Step 7's list covers fields on existing entries and says new base
   nodes are registered, but the check is what surfaced it; following the written list alone leaves
   the check red.

## What the migration actually changes

- `MANIFEST.yaml` — `base_kit_version: 0.29 → 0.31`, and the `base-understanding` node and coverage
  line added.
- `MAP.md` — **no rows refreshed, none added.** The map is unchanged at 0.31.
- Eight record headers refreshed, no pioneer notes to carry.
- `settings.json` — written from the staged template.
- Two new files: `hooks/seal-key.sh`, `checks/waiting-on-you.sh`.
- 80 files taken, 4 agents deployed, 0 stale removed. `P-010.sh` kept.
- **No record needed a field migration.**

## Every figure, and the command that produced it

| Figure | Command |
|---|---|
| 90 files in the release | `cat .claude/kit-incoming/RELEASE` |
| 0 skills evolved here | recomputed baseline joined against `INSTALLED.sha1` |
| 2 files new in 0.31 | staged list tested against `.claude/skills/` |
| 80 taken, 4 agents, 0 stale | `install.sh upgrade plan.txt` |
| 0 map rows refreshed, 0 added | staged vs installed `MAP.template.md` |
| our registry lines lost | `grep -c "P-010 broken"` after install, returned 0 |
| all five G checks exit 0 | each run in `.claude/skills` |
| G2-migration exit 0 | same, after registering the new node |
| every hook behaved | `checks/hooks-selftest.sh` from the project root |
| walk: 78 states | `meta-mechanisms/tests/walk.sh` |
| the seal fixed | `git show e29cac2` — `hooks/seal-key.sh` added, assembler step 6 rewritten, `write-scope.sh` no longer lists `kit-sealed/` |

## What this rehearsal did not check

- **Whether a batch can now actually be assembled.** `seal-key.sh` exists and the assembler's
  instructions changed; no assembler has run under them. The claim that a review batch can be
  assembled for the first time is the kit's, not yet this project's.
- **`waiting-on-you.sh`.** New, unexamined, not exercised.
- **The four closing questions under load.** In use since 0.29 but not yet tested against a
  contract being drawn.
- **`records-index.sh` keeping an agent inside budget.** Still unexercised; no kit agent has run
  since it arrived.
- **`merge.sh`'s three-way merge.** Zero residue for the fourth upgrade running.
