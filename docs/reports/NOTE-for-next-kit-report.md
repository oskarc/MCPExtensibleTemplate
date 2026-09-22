# Held for the next report to the kit's maintainer

Raised by the pioneer during review batch B-001, 2026-09-22, while deciding the candidate that
adds a line to INTENT.md about surfacing an accumulating backlog.

Their question: **"would it be enforced or remembered?"**

What we found, checked against the installed 0.31:

- **Enforced:** `hooks/session-start.sh` fires on its own and, when something is queued on the
  pioneer's decision, instructs the agent to run `checks/waiting-on-you.sh` and put the list to
  them before other work. The agent does not choose whether that instruction arrives.
- **Remembered:** whether the agent runs it, and whether what it writes is readable. Neither is
  enforceable by a hook.
- **The gap:** it fires at *session start only*. The backlog that prompted the pioneer's correction
  grew across a long session — fifty-one candidates accumulated over many turns — and a check that
  runs once at the beginning would not have caught it. That territory is still remembered.

The pioneer's framing is the feedback: an aim stated in the always-loaded file is remembered; a
hook is enforced; and the kit should be explicit about which of the two any given line is, because
a line that reads as a rule and is carried only by memory will fail exactly when the session is
long enough to matter.
