# Held decisions — contract-005 · T-12 (G-11)

An end-to-end test is skipped only while it waits on a decision named here, and its Skip reason names the entry
(`held: {id}`). `HarnessSelfTests` fails on a skipped end-to-end test whose Skip names no entry of this list, on an entry
no skipped test names, and on a sabotage record that calls a test held without one (contract-005 · G-17 round 1,
HAR-R1-03). A held test runs no sabotage, and the record says so. When the decision is made, the Skip and its entry go
together, and the test's sabotage is taken like any other.

| Id | Test | Waits on | Held since |
|---|---|---|---|
| `t5-tool-refusal-words` | `TrustDomainTests.T5_using_a_bound_providers_item_from_the_other_issuer_is_refused_in_the_words_for_one_that_does_not_exist("tool")` | The pioneer's decision between contract-005 T-5 (a tool of the other trust domain is refused in the words for a tool that does not exist) and contract-002 T-6 with the roadmap's Phase 1 exit criterion (it is refused by its own rule, idp-binding): meeting one breaks the other. | 2026-09-29 |
