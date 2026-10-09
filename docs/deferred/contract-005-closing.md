# Deferred — closing the shipped-image contract (contract-005)

Written 2026-10-09 by the session that built it, following the pioneer's instruction to finish each task as well as it can
be finished and to put what could not be finished, or was not this session's to decide, in a file instead of a question.

The work is on `epic/security-hardening`, committed and not pushed. The build and the attack rounds are over. The
documentation pass is done. What is left is the evidence that needs a container engine, then the push.

## 1. Choices that were yours, and what I chose

Each of these has a default in force now; say the word on any of them and it changes.

| # | The choice | What is in force | What changing it costs |
|---|---|---|---|
| 1 | The documentation pass before the evidence runs | I had told you the order would be: sabotage retake, final end-to-end run, then documentation. Docker has been down since 1 October, so the documentation pass was done first. It changes no behaviour, and no entry of the sabotage record depends on a document. | Nothing. The runs below are then taken on the finished documents, the documented deployment among them. |
| 2 | Who restarts Docker Desktop | Nobody has. I stopped two of its processes by mistake on 1 October and have not restarted a tool your other work shares. | A click for you, or a word to me. Everything in section 2 waits on it. |
| 3 | Whether the kit's own agents may run | None has run since your "use no subagents" reached the sessions. Corrections C-041 and C-042 are recorded and not yet filed as precedents; my observations for the ledger are held in notes; the session audit and the verification of this contract are not run. | A word: the kit's agents are small and are how the record of your decisions keeps up. |
| 4 | Redis in the documented deployment has no password | The compose file is unchanged. The guide now says so, says why it is reachable only from the two other services, and says what to do if that changes. | Adding a password to the compose file is a change to the deployment the end-to-end suite runs as written, so it needs the engine to prove it. It fits the deployment-hardening phase. |
| 5 | A Redis password containing a comma | The server cannot use one: the Redis client reads a comma as the end of the value, quoted or not, and takes the rest as another server to connect to. The refusal message and the guide say so. Last week's message said to quote it, which was wrong; it is corrected. | Supporting one means building the Redis options in code from separate settings. A few hours, and a new setting. |
| 6 | Harness gaps that need a test author to cheat | Written down as known limits in the testing guide, not chased, by your decision of 1 October. | Closing them all is a sweep of the harness's self-checks onto compiled code. |
| 7 | One provider reaching another provider's outbound calls | Left open, recorded in the roadmap's amendment of 2026-10-09 and handed to the session drawing the outbound half of Phase 2. | It belongs where the frame takes ownership of providers' outbound clients. |

## 2. What could not be done today

| What | Why not | What unblocks it |
|---|---|---|
| The sabotage record retaken in full | It needs the container engine. A retake on 1 October reached 38 of 113 before the engine went down: 37 went red as they should, one was spoiled by the engine stopping under it, and 14 newer ones were never taken. That partial record is in the working tree, uncommitted, and the script continues from it. | Docker Desktop running. Then `bash scripts/e2e-sabotage.sh --all`, started with Git Bash by its full path, and nothing else running the end-to-end suite meanwhile. |
| One final end-to-end run, from cold | The same. The last full runs were on 1 October: 135 of 136, the one failure being the stale record. | The same. It also runs the documented deployment as the guide now words it. |
| 136 tests of the fast suite | They need Redis in a container. The other 663 pass, the stronger retired-words test among them. | The same. |
| The push, and CI | Held by your plan until the two runs above are green: the branch is 25 or more commits ahead of origin, and CI would show the stale record red. The three roadmap branches were cut from this one and carry the same commits, so they are held too. | The two runs. Then one push, and CI's own two jobs. |
| Closing the contract in the kit's record | It is marked done only when the runs are in. Closing it also closes the contract before it, and with those two the check that counts open contracts goes green. | The runs, and your word on the kit's agents for the audit and the verification. |

## 3. Owed in the documents by other work

Nothing of mine. The three roadmap sessions list what their work owes these documents in their own deferred files, to be
merged after this pass is committed.

## 4. Questions still open with you

1. Should your newer rule, that builders may run tests when useful, replace the older one, no tests until the build is done? The kit holds both until you say.
2. Was the real network cut what you meant by "no internet access mock everything"? It is built, and a check holds it.
3. The review of what would change the standard is parked by your word of 29 September, and stays parked until you ask for it.
