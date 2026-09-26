# Held for the next report to the kit's maintainer

*Carried into report-008 (F-6, F-7) on 2026-09-26. Kept as the record of what was held.*

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

---

## From review B-002 (2026-09-23) — the pioneer's rule for what a review shows

All three items came from one moment of B-001: the pioneer's instruction to lead with strong
candidates, and their "stay true to the skill" minutes later. Decided:

- The card of the instruction itself, ranked **[B, A]** — the instruction above what the agent did —
  with: *"Not sure this should be declined, but it should be fixed in the meta kit repo"*.
- The candidate, **declined locally** with: *"oh, that will be handled by the next version of the
  meta kit"*. Their read before evidence sharpened it: *"the agent should only present candidates
  that score high on reliability..."*.
- The card of the three ways the batch could run, **declined** with: *"The order is not important as
  long as only the candidates and cards with HIGH certainty are the only ones showed."*

So the rule to carry upstream: **a review shows only items rated HIGH certainty; order does not
matter.** Two things the kit would need for it to work:

1. **Cards carry no certainty.** The rule names cards explicitly, and there is nothing to filter
   them on (report-007 F-3, again).
2. **The vocabulary had no exit for "yes, but upstream".** Decline reads as refusal; update/add
   edit only this project's copy of a base skill. The pioneer's intent — accepted, to be fixed in
   the kit — was recorded as decline with the reason carrying it.

Also: the presentation forms the pioneer asked to be "used and enforced from now on" in B-001 lived
only in conversation. After the conversation was condensed, the agent rebuilt them from the batch
file's layout, used that for B-002's first item, and wrote it into report-007 as the pioneer's
forms. Enforced vs remembered, again — this one was remembered, and was lost.
