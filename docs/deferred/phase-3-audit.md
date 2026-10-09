# Deferred — the audit trail (roadmap Phase 3, contract-006)

Written 2026-10-09 by the session that built it, on the pioneer's instruction to finish each task as well as it can be
finished and to put what could not be finished, or was not this session's to decide, in a file instead of a question.

The work was built on the branch `roadmap/phase-3-audit`, in its own worktree. On 2026-10-09 the pioneer said "merge the
branches and push", and it was merged into `epic/security-hardening` (94eda73) and pushed, ahead of the open contract's
closing pass, which the sessions had agreed to wait for. It has never run in a container on the machine it was built on.

## 1. Choices that were yours, and what I chose

No approval gate was held for this contract. Each of these would have been a question to you. Each has a default in
force now; say the word on any of them and it changes.

| # | The choice | What is in force | What changing it costs |
|---|---|---|---|
| 1 | When an allowed tool call is recorded | When it is admitted, before the tool runs. The roadmap words the event as "allowed and completed". | Recording at completion instead is a small change, and reopens the moment in which an irreversible act has happened with no record. |
| 2 | What a failing collector does | Nothing to requests: the copy is dropped, counted and reported. Only the file failing refuses requests. | Making a collector outage refuse changes is a few lines; it ties every write to a remote system's uptime. |
| 3 | Requests that change nothing, while the trail is down | Refused (`Audit:ReadWhenUnavailable` defaults to `deny`). This is the roadmap's default for a decision (D7) you never made. | It is a setting. `allow` serves reads unrecorded. |
| 4 | How long files are kept | 400 days. The roadmap's default for a decision (D6) you never made. | It is a setting. |
| 5 | Whether the chain is keyed | It is not: plain SHA-256, as the roadmap states. Someone who can rewrite the whole file can rewrite the chain. | A keyed chain needs a key to manage and a check that needs the key. Roughly a day, plus the key's rotation story. |
| 6 | One file per server instance | Yes. A restart starts a new file and a new chain. | Appending to one file across restarts makes two processes able to write one chain. |
| 7 | Where the files go by default | `logs/audit`, the one directory the image's user can write, so the Dockerfile is untouched. | A directory of its own means a Dockerfile change, which is another session's phase. |
| 8 | Whether sign-on, discovery and notifications are recorded | Yes, like everything else. | Leaving them out halves the volume for a chatty client and puts an exception in the rule. |
| 9 | How the collector copy is sent | OTLP over HTTP in JSON, written by hand. No package added. | The OpenTelemetry packages would replace about 250 lines and add a dependency tree while the supply-chain contract is being drawn. |
| 10 | A request with no token | Not recorded. It is answered with where to get a token, which is how every standard client begins. | Recording it is one line, and every client's first request becomes a record. |
| 11 | A request for a host name the server does not answer for | Not recorded. The web host refuses it before the server's code runs. | Recording it needs a small piece of middleware in front of the host filter. |
| 12 | The level of "the trail stopped" | CRITICAL. The OWASP vocabulary says WARN. | One line. |
| 13 | The alert threshold | Five refusals for one caller in ten minutes. The roadmap leaves the number open; nothing was measured. | It is a number in a rule file. |
| 14 | A new document | `docs/08-AUDIT-TRAIL.md`. The contract before last added no documents by rule. | Folding it into the configuration guide waits on the documentation pass another session holds. |

Four figures are authored judgement with no measurement behind them: a record's arguments are capped at 16 KB; the
collector queue holds 10,000 records, sends 100 at a time, waits one second to gather a batch, and gives an export five
seconds.

## 2. What could not be done today

| What | Why not | What unblocks it |
|---|---|---|
| The audit trail shown on the shipped image | The container engine is down, and restarting it is yours. | Docker Desktop running. Then end-to-end tests, each with its sabotage: the trail in Production behind the TLS front with Redis; the file's owner and mode in the image; the last records on SIGTERM; a tool call with a real token leaving one record; the trail unwritable (a read-only mount) refusing a write. |
| 136 existing tests of the fast suite | They need Redis in a container. They failed the same way before any change. | The same. Until they run, nothing shows this work left them unbroken. I kept every existing log line and reply as it was for that reason. |
| The adversarial rounds your ruling puts after every build | They are subagents; you told the sessions to use none. | Your word that they may run, or another way to attack the work. |
| The kit's own steps after a build: the session audit, the independent verification, and writing what was learned to the ledger | The first two are kit agents. The third needs the ledger, which is closed while a review is open, or a kit agent. | Your word on whether the kit's own agents count as subagents, which three sessions have asked. The contract is recorded as implemented and unverified. |
| Merging and pushing | Done on 2026-10-09, on the pioneer's word, ahead of the open contract's closing pass. The push carried that contract's unpushed commits too, and its sabotage record is stale until the container engine is back. | Nothing. |

## 3. What was owed in files another session held

The session closing the open contract held the README, `docs/01` to `docs/05` and the dated amendments in `docs/06` and
`docs/07` while its documentation pass ran. That pass was committed on 2026-10-09 (3b2228f), this branch took it in by
merging `epic/security-hardening`, and the edits owed were then made here:

- `docs/README.md`: `08-AUDIT-TRAIL.md` in the index, the table and the security links.
- `docs/04-CONFIGURATION.md`: an Audit Settings section with the six settings, and `Audit` among the governed sections.
- `docs/01-ARCHITECTURE.md`: the audit step in front of the request-kind gate, every decision recorded before it takes
  effect, and the ordinary log holding no argument values. Two sentences there had become false and are corrected.
- `docs/02-ARCHITECTURE-FLOWCHARTS.md`: the audit step in the filter-chain diagram (section 5). Its other diagrams do
  not draw the audit step; none of them says anything that is now false.
- `docs/03-TESTING-STRATEGY.md`: the audit tests in the test tree.
- `docs/06-SECURITY-ROADMAP.md`: a dated amendment at the end of Phase 3, eleven points.
- `docs/07-SECURITY-FLOWCHARTS.md`: dated amendments under flows 14 and 15.
- `README.md`: the audit trail among the security features and in the file tree; one sentence about Debug logging that
  had become false is corrected.

The documentation tests, the stronger ones from that pass included, pass with these in place.

Still as they were: two lines the roadmap names were left, because the end-to-end suite reads them and cannot be run. The
fatal line of a server that cannot start does not begin with `sys_startup`, and the line of one that crashes does not
begin with `sys_crash`. A crash is on the audit trail as `sys_crash` where the trail still exists.

## 4. For the merge

Files this branch changes that the other two contracts also touch, and what it changes in each:

| File | The change |
|---|---|
| `Frame/GovernedServer.cs` | The audit registrations before the gate's; the gate is given the trail; two incoming message filters where there was one, and one outgoing. |
| `Frame/FrameIntegrity.cs` | `VerifyInstalled` holds the audit step first and the request-kind gate second; the manifest gains `Audit` and `OwnAudit`. |
| `Frame/SettingsAllowlist.cs` | Six `Audit` keys and `Audit` among the governed sections. |
| `Frame/Policy.cs` | `ToolPolicy` gains an optional last parameter, `SensitiveArgs`. |
| `Frame/PolicyRegistry.cs` | One check where a tool's entry is built: a sensitive argument the schema does not declare. |
| `Frame/RequestGate.cs` | Every refusal and every admission recorded; a new last constructor parameter. |
| `Infrastructure/HttpServerComposition.cs` | One line in the pipeline after forwarded headers; the rate limiter's rejection recorded. |
| `Tests/Identity/InProcessServer.cs` | Each in-process server gets an audit directory of its own, removed with it. |

The "Frame installed" line changes with the filters: the message filter lists are longer. Both suites compare the line
between two servers built the same way, so it changes equally on both sides.

For the two contracts being drawn beside this one:

- **Egress.** A refused outbound call is recorded with `AuditScope.Current?.Event(AuditEvents.McpEgressDenied, description, new AuditDecision(AuditOutcome.Deny, rule))` and `trail.Record(...)`, adding `Upstream = new AuditUpstream(host, method)`. The budget and concurrency refusals use `AuditEvents.McpResourceExhaustion`. A refusal is a refusal whether or not the record was written.
- **Tool lock.** A mismatch at startup is `AuditEvent.System(AuditEvents.McpToolPoisoning, description, detail)`. The hash for `sys_startup` goes in `AuditLifecycle`, where `tool_lock_sha256` is written as null today.

## 5. Known limits

- The check proves no line was changed, removed, added or moved without redoing every line after it. It does not prove
  the file's end is the end of what was written.
- A request the SDK turns away before the server's checks run is recorded after it was refused, not before. Nothing
  governed ran for it.
- If a client sends more than 10,000 requests on one connection without reading an answer, a request refused by a check
  may be recorded a second time on its way out. Never fewer than once.
- A refusal's description is the server's own reason. For arguments that do not fit a schema it quotes the schema
  library's message, which can name a value. Anything shaped like a credential is replaced there too.
- One flushed write per request: measured here at 1.8 ms each (300 in 531 ms, one run, this machine). That is a ceiling
  of a few hundred recorded requests a second through one server. It is one figure, not a load test.
- Survival of a power cut rests on the flush to disk and was not tested.
- An audit file is created with the process's default mode: in the image, readable by any user inside the container.
  The records hold argument values. Narrowing it is a few lines that run only on Linux, which could not be run today, so
  it is left for the hardening phase with the rest of the image's file modes.
- Over a stream or stdio connection, a client on a protocol version with no way to answer a confirmation gets a generic
  error for an irreversible tool instead of the refusal that names the reason. Nothing runs. Over HTTP it is refused by
  name. Found while testing; not this contract's to change.

## 6. What the work taught, held here

These are observations for the kit's ledger. The ledger is closed to this session and the agent that records for it is
a subagent, so they are kept here until one of those changes.

1. The SDK rejects some requests before any incoming filter runs. A guarantee worded "every request passes the frame"
   is false for them, and only reading the trail after sending such a request showed it.
2. A test harness that needs no container made every claim of this contract checkable on a day the container engine was
   down. The contract before this one could not close for that reason alone.
3. The in-process HTTP server starts and serves everything in front of the limit checks when pointed at a Redis that is
   not there. Much of what was called "needs a container" needed only that.
4. Restoring a file from a copy after a deliberate break keeps the copy's older timestamp, and the build then reuses the
   broken binary. The red test looked permanently red until the file was touched.
5. A component the container builds and the trail also disposes is disposed twice. Only a test that composed the whole
   frame found it.
6. Writing a rule for every refusal needed the rules to be findable. A scan of the source for rule names, tied to test
   names, found the two that no request can reach.
7. Three sessions on one branch needed a rule for record ids, for shared files and for who presents a review before any
   of them could start; the kit has no entry for it.

## 7. Next on the roadmap for this session

Phase 4, taking access away, reads these records and is the next thing that depends on them. Three of its decisions are
listed in the roadmap as yours with no default, and it cannot be drawn whole without them:

- **Which identity providers the deployment uses** (D1). Each decides how a caller's access is revoked.
- **Which identity provider owns the administrative plane** (D10).
- **Whether a person approves a permanent revocation on soft signals** (D8). The roadmap's default is yes.

What can be drawn without them: the strike counts, throttling, and suspension through a deny list, with revocation left
as an interface nothing calls yet. Its limits and its deny list live in Redis, so its tests need the container engine
too.
