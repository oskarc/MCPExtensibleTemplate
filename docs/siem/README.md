# Alert rules for the audit trail

These are the alerts the security roadmap asks to ship with the server (Phase 3, P3.7), written as
[Sigma](https://sigmahq.io) rules so they can be converted for whichever monitoring system reads the trail.

They read the audit records the server writes: one JSON object per line, in `logs/audit/` unless `Audit:Path` says
otherwise, and the same lines as OTLP log records when `Audit:Otlp:Endpoint` is set. Point a log forwarder at the files,
or a collector at the endpoint, and map the fields as they are named in the record.

## What each rule needs from the monitoring system

| Rule | What it alerts on | What Sigma alone cannot say |
|---|---|---|
| `mcp-critical-events.yml` | Any prompt injection, token reuse, egress denial, revocation failure or tool poisoning | Nothing: one event is the alert. |
| `mcp-refusals-burst.yml` | Five or more `authz_fail` records for one caller in ten minutes | Nothing: a Sigma correlation rule. |
| `mcp-audit-trail-stopped.yml` | The trail stopped accepting records, or the server crashed | Nothing: one event is the alert. |
| `mcp-first-tool-use.yml` | A caller using a tool for the first time | "First time" needs the system's own memory of what it has seen (a "new terms" rule). The rule gives the selection and names the fields whose first combination is the alert. |
| `mcp-audit-chain-gap.yml` | A record missing from the copy the monitoring system holds | "Consecutive" needs a sequence check per server instance. The rule gives the selection and names the fields: `seq` must rise by one within each `hostname` and `instance`. |

## Reading the `event` field

The record's `event` is written the way the OWASP logging vocabulary writes one: the event's name, then who and what
where they are known, for example `authz_fail:sub=alice,resource=get_forecast`. The name is always the text before the
first colon, and the rules match it that way. The same facts are also in fields of their own (`principal.sub`,
`mcp.target`, `decision.rule`), which is what a rule should group and filter by.

## What these rules do not do

They alert; they do not act. Throttling, suspending and revoking a caller is the next phase of the roadmap, and reads the
same records inside the server.

The events `mcp_prompt_injection`, `authn_token_reuse`, `mcp_egress_denied`, `mcp_revocation_failed` and
`mcp_tool_poisoning` are in the server's vocabulary and nothing writes them yet: they belong to the outbound guard, the
tool lock and the strike engine, each a later part of the roadmap. The rules for them are here so that the day one is
written, something is already listening.

A test in the repository fails when a rule names an event the server cannot write.
