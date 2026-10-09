# The Audit Trail

Every decision the server makes about a request is written down before it takes effect: who asked, for what, whether it
was let through, and by which rule. This document says what is written, where, how to check it, and what happens when it
cannot be written. It describes the server as built by contract-006 (roadmap Phase 3).

## What is recorded

One record per decision.

- **Every message the server receives leaves one record**: a tool call, a resource read, a prompt, a completion, a list,
  sign-on, discovery, a notification, and any kind of request the server does not accept.
- **The record is written first.** An allowed tool call's record is on disk before the tool runs. A refusal's record is
  on disk before the caller is answered.
- **A second record** is written for the one decision made after a tool has run: an answer larger than the tool's cap is
  withheld, and that is recorded too.
- **The HTTP layer's refusals** are recorded as well: a refused token, a request from an origin the server does not
  answer for, and an address that has reached the per-address limit (once per address per minute, however many requests
  are turned away).
- **The server's own life**: started, stopped cleanly, stopped on a failure, and the trail itself having been down.

Two things are deliberately not recorded. A request with no token at all is answered with where to get one; that is how
every standard client begins, and it is not a refused token. And a request whose `Host` header the server does not
answer for is refused by the web host before the server's own code runs.

How long a call took and whether the tool succeeded are not on the audit trail. They are in the ordinary log, and the
two are joined by the trace id every record carries.

## Where it is written

JSON lines, in `logs/audit/` under the working directory unless `Audit:Path` names another directory.

- **One file per server instance per UTC day**, named `audit-{yyyyMMdd}-{host}-{instance}.jsonl`. The instance is eight
  hex characters chosen when the server starts, so two servers, or one server restarted, never write the same file.
- A file can be read while the server holds it open, so a log forwarder can follow it.
- Files older than `Audit:RetentionDays` (400 unless set) are removed when a new file is opened.

## One record

```json
{
  "datetime": "2026-10-09T08:49:42.415Z",
  "appid": "mcp-server",
  "event": "authz_fail:sub=alice,resource=get_user_todos",
  "level": "CRITICAL",
  "description": "The tool 'get_user_todos' requires the scope 'demo:read', which your token does not carry. Request a token with that scope and call again.",
  "trace_id": "8efbc4b8874564b2ad70beb3c380caf2",
  "request_id": "0HN8K3M2QJ4R7",
  "principal": { "idp": "corp", "sub": "alice", "client_id": "ide-vscode", "jti_sha256": "5964da05…", "scopes": ["weather:read"] },
  "source_ip": "10.4.2.17",
  "hostname": "mcp-01",
  "request_uri": "/mcp",
  "request_method": "POST",
  "useragent": "mcp-client/2.2",
  "mcp": { "method": "tools/call", "provider": "JsonPlaceholder", "kind": "tool", "target": "get_user_todos", "tool": "get_user_todos", "risk": "Read" },
  "args": { "userId": 1 },
  "decision": { "outcome": "deny", "rule": "insufficient_scope" },
  "upstream": null,
  "detail": null,
  "instance": "36fa008c",
  "seq": 5,
  "prev_hash": "cd713896…",
  "hash": "a4d0540d…"
}
```

In the file each record is one line, with its members in exactly this order.

| Member | What it holds |
|---|---|
| `datetime` | When, in UTC. |
| `appid` | `Audit:AppId`, `mcp-server` unless set. |
| `event` | The event's name, then who and what where they are known. The name is the text before the first colon. |
| `level` | `INFO`, `WARN` or `CRITICAL`. |
| `description` | What happened, in words. For a refusal, the server's own reason, which is not always what the caller was told. |
| `trace_id`, `request_id` | The W3C trace the request belongs to (the caller's own when it sent a `traceparent`), and the server's id for the request. |
| `principal` | The identity provider, the subject, the client, a hash of the token's id, and the scopes. Never the token. `null` when nobody is signed in. |
| `source_ip`, `hostname`, `request_uri`, `request_method`, `useragent` | The HTTP facts. The address is the one a trusted proxy forwarded. The path never includes a query. All `null` over stdio except the host name. |
| `mcp` | The method, the provider that owns the target, what kind of thing the target is, the target, and a tool's risk class. |
| `args` | The arguments as sent, with secrets removed (below). |
| `decision` | `allow`, `deny` or `challenge`, and the rule that refused or asked. |
| `upstream` | For an outbound call's record: the host and method. |
| `detail` | Further facts of some events: the build's hash on `sys_startup`, the scheme that refused a token. |
| `instance`, `seq` | Which server instance wrote it, and its number in that instance's chain, from 1. |
| `prev_hash`, `hash` | The hash of the record before, and this record's own. |

## The events

| Event | Level | Written when |
|---|---|---|
| `mcp_tool_call` | INFO | A tool call was admitted. Written before the tool runs. |
| `mcp_tool_confirmed` | INFO | An irreversible tool call was admitted on a verified confirmation. Written before the tool runs. |
| `mcp_confirmation_required` | INFO | An irreversible tool call was answered with a request to confirm. Nothing ran. |
| `mcp_resource_read`, `mcp_prompt_get` | INFO | A resource or a prompt was admitted. |
| `mcp_request` | INFO | A request with no item of its own was admitted: sign-on, discovery, a list, a completion, a notification. |
| `authz_fail` | CRITICAL | A request the caller was not allowed to make. `decision.rule` says which check refused it. |
| `malicious_extraneous` | CRITICAL | An argument the tool does not declare. |
| `input_validation_fail` | WARN | Arguments that do not fit the tool's schema, or a request that could not be read at all. |
| `excess_rate_limit_exceeded` | WARN | A caller's limit, a tool's limit, or the per-address limit before sign-in. |
| `mcp_tool_denied` | WARN | A tool's answer was withheld for being over its cap. |
| `sys_availability` | WARN | The limit store did not answer, so the request was refused. |
| `authn_login_fail` | WARN | A token was refused. `detail.scheme` says which scheme refused it. |
| `malicious_cors` | CRITICAL | A request from an origin the server does not answer for. |
| `sys_startup`, `sys_shutdown`, `sys_crash` | WARN | The server began serving, stopped cleanly, or stopped on a failure nobody handled. |
| `sys_monitor_disabled` | CRITICAL | The trail is writing again after it could not. `detail` says since when and how many events were not recorded. |

Five more names are in the server's list and nothing writes them yet: `mcp_egress_denied`, `mcp_resource_exhaustion`,
`mcp_tool_poisoning`, `mcp_prompt_injection`, and the token and revocation events. They belong to later parts of the
roadmap.

`sys_startup` carries, in `detail`: the version, `build_sha256` (the SHA-256 of the server's own assembly file),
`frame_sha256` (the SHA-256 of the installed checks and every policy in force), which store holds the limits, the
providers served, and where the trail is written.

## When the trail cannot be written

The server does not act on a decision it could not record.

- A **Write or Irreversible tool is refused**, with the rule `audit-unavailable`.
- **Everything else follows `Audit:ReadWhenUnavailable`.** Unset, or `deny`, nothing is served at all. Set to `allow`,
  requests that change nothing are served and are not recorded.
- The ordinary log says `sys_monitor_disabled` once when the trail stops, and names each request it could not record.
- The server tries the trail again on every request. When it writes again, the first record is `sys_monitor_disabled`
  with how many events were missed, so the trail itself shows the gap.
- A server whose start cannot be recorded does not start.

## Checking a file

```
McpServerTemplate --verify-audit logs/audit/audit-20261009-mcp-01-36fa008c.jsonl
```

It starts no server and reads no settings. Give it several files of one instance, oldest first, to check them as one
chain.

| Exit code | Meaning |
|---|---|
| 0 | Intact. It prints how many records, their numbers, the hash the chain continues from and the last hash. |
| 65 | A line breaks the chain. It prints the file, the line, and why. |
| 66 | A file cannot be read. |
| 64 | No file was named. |

Each record's hash is the SHA-256 of its line without the hash itself, and each line holds the hash of the line before.
A changed, removed, added or moved line is found at its line.

**What this does and does not prove.** It proves nobody edited the file without redoing every line after the edit. It
does not prove the end of the file is the end of what was written, and it does not stop someone who can rewrite the whole
file: the hashes are not keyed. For that, compare the last hash it prints with a copy held outside the machine: the
collector's copy, below, carries every record's hash and number.

## What is kept out of a record

- An argument a tool's policy lists under `SensitiveArgs` is written as `[redacted:sha256:1a2b3c4d]`: eight characters
  of the value's hash, enough to tell two values apart.
- A value shaped like a credential is replaced the same way wherever it is, in any argument and at any depth: a JSON web
  token, a `Bearer` header's value, an AWS access key id, an `sk-` key.
- A string longer than 2 KB is cut, and carries the hash of the whole.
- Arguments that are still larger than 16 KB together are replaced by their size and their hash.
- The token appears only as `jti_sha256`, the hash of its id.
- Nothing a caller sends can start a line of its own: every control character is written as an escape.

A provider declares a tool's sensitive arguments in its policy. A name the tool's schema does not declare stops the
server at startup.

The ordinary log holds no argument values at any level; it names the arguments and nothing more.

## A copy outside the machine

When `Audit:Otlp:Endpoint` is set, every record is also sent to that collector as an OTLP log record, over HTTP in the
protocol's JSON encoding. The body is the record's line as the file holds it; its hash, the previous hash and its number
are attributes, so the copy can be checked against the file and a gap in the numbers shows a missing record.

It is a copy. A collector that is slow, down or refusing never delays or refuses a request. What cannot be delivered is
dropped and counted, and the ordinary log says so at most once a minute. The file is the record.

The endpoint must be `https`, and must carry no user information, query or fragment; send a credential as a header
under `Audit:Otlp:Headers`. Plain `http` is accepted in Development only, to a collector on loopback. Redirects are not
followed.

## Settings

| Setting | Default | What it does |
|---|---|---|
| `Audit:Path` | `logs/audit` | The directory the audit files are written in. The server proves it can write there at startup. |
| `Audit:ReadWhenUnavailable` | `deny` | `deny` or `allow`: whether requests that change nothing are served while the trail cannot be written. |
| `Audit:RetentionDays` | `400` | How many days of audit files are kept. |
| `Audit:AppId` | `mcp-server` | The application's name in every record. |
| `Audit:Otlp:Endpoint` | none | The collector's OTLP/HTTP logs endpoint, whole, for example `https://collector.example.com:4318/v1/logs`. |
| `Audit:Otlp:Headers:{name}` | none | A header sent with every export. |

An `Audit` setting the server does not read stops it at startup, as in every other governed section.

## Traces

The server opens spans under the activity source `McpServer`: `mcp.request` for every message and `mcp.tool` around a
tool. Every record carries the request's trace id. When the caller sends a `traceparent`, that trace is the one used, and
whatever a tool calls through the platform's HTTP client carries it on.

## Alerts

Rules for a monitoring system are in [siem/alerts](siem/alerts), with what each needs in [siem/README.md](siem/README.md).

## Where this departs from the roadmap

- The roadmap words `mcp_tool_call` as "allowed and completed". It is written when the call is admitted, before it
  runs, so that nothing irreversible can happen and have no record. Completion is in the ordinary log and the trace.
- Requests the SDK turns away before the server's own checks run (a method the negotiated protocol version does not
  have) are recorded on the way out, after the fact, with the rule `rejected-by-protocol`.
- The collector copy is OTLP over HTTP written with the platform's own HTTP client; no OpenTelemetry package is used.
- The tool-lock hash the roadmap names on `sys_startup` is written as absent until the tool lock exists.
