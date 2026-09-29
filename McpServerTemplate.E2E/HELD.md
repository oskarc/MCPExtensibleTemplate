# Held decisions — contract-005 · T-12 (G-11)

An end-to-end test is skipped only while it waits on a decision named here, and its Skip reason names the entry
(`held: {id}`). `HarnessSelfTests` fails on a skipped end-to-end test whose Skip names no entry of this list, on an entry
no skipped test names, and on a sabotage record that calls a test held without one (contract-005 · G-17 round 1,
HAR-R1-03). A held test runs no sabotage, and the record says so. When the decision is made, the Skip and its entry go
together, and the test's sabotage is taken like any other.

No test is held. The last one, T-5's tool row (`t5-tool-refusal-words`, held since 2026-09-29), was released by the
pioneer's decision of 2026-09-29 (contract-005's revision of that date, "1: A"): a tool of another trust domain is
refused in the words for a tool that does not exist, and the server's log keeps the rule that refused it, `idp-binding`.

| Id | Test | Waits on | Held since |
|---|---|---|---|
