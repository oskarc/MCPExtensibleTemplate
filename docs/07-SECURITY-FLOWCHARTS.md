# Security Flowcharts — The Hardened Server

Sixteen numbered flows — seventeen diagrams, since §5 carries two — covering the mechanics described in [06-SECURITY-ROADMAP.md](06-SECURITY-ROADMAP.md). That document is the normative one: it says *what* must be true and *why*. This one shows *how the parts move*, so the design can be followed without reading the tables.

These diagrams describe the **target** design, not the code as it stands today. For the current architecture, see [02-ARCHITECTURE-FLOWCHARTS.md](02-ARCHITECTURE-FLOWCHARTS.md).

## How to read these diagrams

Colour means the same thing everywhere:

| Colour | Meaning |
|--------|---------|
| 🟩 Green | The request continues, or a guarantee holds |
| 🟥 Red | The request stops here, with the event name that is logged |
| 🟧 Amber | A decision point — a gate that can go either way |
| 🟦 Blue | The control plane: identity providers, Redis, the SIEM |
| ⬜ Grey | Untrusted input — never acted on without a check |

Where a diagram names an event (`authz_fail`, `mcp_egress_denied`), that is the exact string written to the audit log, from the [OWASP logging vocabulary](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Vocabulary_Cheat_Sheet.html). Where it names a `decision.rule`, that is the field an investigator filters on.

### Contents

**The shape of the system** — [1. Trust boundaries](#1-trust-boundaries) · [2. A call from end to end](#2-a-call-from-end-to-end) · [3. Every way a call can be refused](#3-every-way-a-call-can-be-refused)

**Identity** — [4. Routing a token to its identity provider](#4-routing-a-token-to-its-identity-provider) · [5. Binding providers to identity providers](#5-binding-providers-to-identity-providers)

**The call path** — [6. Risk classes and their gates](#6-risk-classes-and-their-gates) · [7. Confirming an irreversible action](#7-confirming-an-irreversible-action) · [8. The egress guard](#8-the-egress-guard) · [9. The four rate-limit layers](#9-the-four-rate-limit-layers)

**Consequences** — [10. The strike ladder](#10-the-strike-ladder) · [11. Injection is not malice](#11-injection-is-not-malice) · [12. How a revocation actually lands](#12-how-a-revocation-actually-lands)

**Guarantees** — [13. Startup refuses a misconfigured server](#13-startup-refuses-a-misconfigured-server) · [14. What happens when a dependency is down](#14-what-happens-when-a-dependency-is-down) · [15. A decision becomes an audit record](#15-a-decision-becomes-an-audit-record)

**Delivery** — [16. Phase dependencies](#16-phase-dependencies)

---

## 1. Trust boundaries

The single most useful thing to hold in your head: **two things are untrusted, and they are not the same thing.** The client is untrusted because it may be hostile. Upstream responses are untrusted because they are text the model will read, and text can carry instructions.

```mermaid
flowchart TB
    subgraph Untrusted["Untrusted — never acted on without a check"]
        C["MCP client<br/>agent or IDE"]
        U["Upstream response text<br/>the model will read this"]
    end

    subgraph Control["Control plane — trusted, external"]
        IDPA["Identity provider A"]
        IDPB["Identity provider B"]
        RED["Redis<br/>quotas, strikes, denylist"]
        SIEM["SIEM<br/>audit sink"]
    end

    subgraph Server["The MCP server — the enforcement point"]
        EDGE["Edge<br/>authenticate and identify"]
        FILT["Filters<br/>decide, record, sanitise"]
        EGR["Egress guard<br/>constrain what goes out"]
    end

    subgraph Ups["Upstream APIs — one allowlist per provider"]
        UA["SMHI forecast"]
        UB["SMHI observations"]
        UC["Demo API"]
    end

    C -->|"bearer token and tool call"| EDGE
    IDPA -.->|"signing keys"| EDGE
    IDPB -.->|"signing keys"| EDGE
    EDGE --> FILT
    FILT <-->|"quotas, strikes, suspensions"| RED
    FILT -->|"one record per decision"| SIEM
    FILT -->|"suspend or revoke"| IDPA
    FILT --> EGR
    EGR --> UA
    EGR --> UB
    EGR --> UC
    UA --> U
    U -->|"sanitised before the model sees it"| FILT

    classDef untrusted fill:#eceff1,stroke:#607d8b,color:#000
    classDef control fill:#bbdefb,stroke:#1565c0,color:#000
    classDef server fill:#c8e6c9,stroke:#2e7d32,color:#000
    classDef ups fill:#f1f8e9,stroke:#689f38,color:#000
    class C,U untrusted
    class IDPA,IDPB,RED,SIEM control
    class EDGE,FILT,EGR server
    class UA,UB,UC ups
```

**What to notice.** The server is the only place where policy is enforced — there is no path from client to upstream that skips it. The arrow from the filters back to identity provider A is the revocation path: the server does not merely refuse a caller, it can end that caller's session at the source.

→ Roadmap §3.1

---

## 2. A call from end to end

The happy path, with each participant doing exactly one job. Nothing here is optional; a call that skips a step is a bug.

```mermaid
sequenceDiagram
    autonumber
    participant C as MCP client
    participant IDP as Identity provider
    participant E as Edge middleware
    participant A as AuditFilter
    participant P as PolicyFilter
    participant T as Tool method
    participant G as Egress guard
    participant API as Upstream API
    participant O as OutputGuardFilter

    C->>IDP: request a token for this resource
    IDP-->>C: access token, aud is the resource URI, short lifetime
    C->>E: tools/call with bearer token
    E->>E: host, origin, per-IP limit
    E->>E: route by iss, then verify signature, aud, exp, alg
    E->>E: normalise to idp, sub, client_id, jti, scopes
    E->>A: authenticated request
    A->>A: open the record, assign a trace id
    A->>P: forward
    P->>P: binding, scope, risk gate, quotas, arguments
    P->>T: invoke with validated arguments only
    T->>G: HTTP request through the provider client
    G->>G: host, method, scheme, token-leak check
    G->>API: request to a resolved and vetted address
    API-->>G: response
    G->>G: bounded read, abort past the cap
    G-->>T: payload deserialised from the stream
    T-->>O: formatted text
    O->>O: size cap, strip instruction-like markup
    O-->>A: clean result
    A->>A: append the record, chain the hash, ship to the SIEM
    A-->>C: tool result
```

**What to notice.** The audit filter is outermost, so it brackets everything — including the refusals in the next diagram. Steps 4–6 happen before any MCP handler runs, and step 12 passes the tool *only* arguments that survived validation.

→ Roadmap §3.1, §3.5

---

## 3. Every way a call can be refused

One ladder, top to bottom, cheapest check first. Every red exit carries a distinct event name or `decision.rule`, which is what makes the audit log answerable: "why was this refused?" always has one answer.

```mermaid
flowchart TB
    IN["tools/call arrives"] --> G1{"Host in<br/>AllowedHosts?"}
    G1 -->|"no"| X1["400"]
    G1 -->|"yes"| G2{"Origin absent<br/>or allowed?"}
    G2 -->|"no"| X2["403<br/>malicious_cors"]
    G2 -->|"yes"| G3{"Per-IP limit<br/>not exceeded?"}
    G3 -->|"no"| X3["429"]
    G3 -->|"yes"| G4{"Issuer registered?"}
    G4 -->|"no"| X4["401<br/>refused before any key lookup"]
    G4 -->|"yes"| G5{"Signature, aud,<br/>exp and alg valid?"}
    G5 -->|"no"| X5["401<br/>authn_login_fail"]
    G5 -->|"yes"| G6{"Principal on<br/>the denylist?"}
    G6 -->|"yes"| X6["401<br/>authn_token_reuse"]
    G6 -->|"no"| G7{"Tool known?"}
    G7 -->|"no"| X7["error<br/>rule unknown-tool"]
    G7 -->|"yes"| G8{"Provider bound to<br/>the caller's identity provider?"}
    G8 -->|"no"| X8["authz_fail<br/>rule idp-binding"]
    G8 -->|"yes"| G9{"Required scope<br/>present in the token?"}
    G9 -->|"no"| X9["authz_fail<br/>rule scope"]
    G9 -->|"yes"| G10{"Risk gate satisfied?<br/>see diagram 6"}
    G10 -->|"no"| X10["denied<br/>rule freshness or confirmation"]
    G10 -->|"yes"| G11{"Quotas L2, L3, L4<br/>within budget?"}
    G11 -->|"no"| X11["excess_rate_limit_exceeded<br/>or mcp_resource_exhaustion"]
    G11 -->|"yes"| G12{"Arguments match<br/>the schema exactly?"}
    G12 -->|"no"| X12["malicious_extraneous<br/>or input_validation_fail"]
    G12 -->|"yes"| G13{"Redis and audit sink<br/>available?"}
    G13 -->|"no"| X13["denied for Write<br/>configured for Read"]
    G13 -->|"yes"| RUN["Tool runs"]
    RUN --> G14{"Egress allowed?<br/>see diagram 8"}
    G14 -->|"no"| X14["mcp_egress_denied"]
    G14 -->|"yes"| OK["Result returned<br/>and audited"]

    classDef deny fill:#ffcdd2,stroke:#c62828,color:#000
    classDef gate fill:#ffe0b2,stroke:#ef6c00,color:#000
    classDef pass fill:#c8e6c9,stroke:#2e7d32,color:#000
    class X1,X2,X3,X4,X5,X6,X7,X8,X9,X10,X11,X12,X13,X14 deny
    class G1,G2,G3,G4,G5,G6,G7,G8,G9,G10,G11,G12,G13,G14 gate
    class OK,RUN pass
```

**What to notice.** The order is deliberate. Identity is established (G4–G6) before anything provider-specific is consulted, and the argument guard (G12) runs *before* the tool is invoked — a call with a bad argument never reaches the upstream at all.

→ Roadmap §3.1, §3.2, §3.7

---

> **Amended 2026-09-24 (contract-003 as built).** Before any of the refusals above, an incoming-message gate refuses any request kind the frame does not govern. The binding and scope refusals apply to resources, prompts and completions as well as tools, and a hidden item is refused in the same words as a forbidden one, so a refusal does not confirm it exists.

## 4. Routing a token to its identity provider

Several identity providers, each with its own keys and claim names. The subtle part is at the top: the `iss` claim is read **without being trusted**, purely to pick a scheme. All real validation happens after the fork.

```mermaid
flowchart TB
    T["Bearer token arrives"] --> S1{"Larger than 8 KB?"}
    S1 -->|"yes"| R1["401<br/>refused before parsing"]
    S1 -->|"no"| S2["Read the iss claim<br/>without validating it"]
    S2 --> S3{"Is that issuer configured?"}
    S3 -->|"no"| R2["401<br/>no key lookup is made"]
    S3 -->|"yes"| S4["Forward to that<br/>issuer's own JWT scheme"]
    S4 --> S5{"Signature verifies against<br/>that issuer's keys?"}
    S5 -->|"no"| R3["401<br/>token claimed A, was signed by B"]
    S5 -->|"yes"| S6{"aud is the resource URI,<br/>exp valid, alg allowed?"}
    S6 -->|"no"| R4["401"]
    S6 -->|"yes"| S7["Normalise claims with<br/>this provider's mapping<br/>scp or scope, azp or client_id"]
    S7 --> OUT["Principal<br/>idp, sub, client_id, jti, scopes"]

    classDef deny fill:#ffcdd2,stroke:#c62828,color:#000
    classDef gate fill:#ffe0b2,stroke:#ef6c00,color:#000
    classDef pass fill:#c8e6c9,stroke:#2e7d32,color:#000
    class R1,R2,R3,R4 deny
    class S1,S3,S5,S6 gate
    class OUT,S7 pass
```

**What to notice.** Reading `iss` before validating it sounds dangerous and is not: it selects a scheme, nothing more. A token naming issuer A but signed by B dies at the signature check. And an unregistered issuer is refused *before* any network call, so an attacker cannot use a made-up issuer to make the server fetch a URL.

→ Roadmap §3.8, P1.10

---

## 5. Binding providers to identity providers

Each provider is bound to **exactly one** identity provider. Not zero, not a list. Two providers may share one.

```mermaid
flowchart TB
    subgraph IdPs["Identity providers"]
        A["Identity provider A<br/>corporate staff"]
        B["Identity provider B<br/>partner tenant"]
    end

    subgraph Bind["Binding — set in configuration, validated at startup"]
        PA["Provider Smhi<br/>IdentityProvider = A"]
        PB["Provider SmhiObs<br/>IdentityProvider = A"]
        PC["Provider Demo<br/>IdentityProvider = B"]
    end

    subgraph Tools["Tools reachable through that binding"]
        TA["get_forecast<br/>get_current_weather"]
        TB["get_recent_temperature<br/>get_temperature_history"]
        TC["get_blog_post<br/>create_blog_post"]
    end

    A --> PA
    A --> PB
    B --> PC
    PA --> TA
    PB --> TB
    PC --> TC

    classDef idp fill:#bbdefb,stroke:#1565c0,color:#000
    classDef prov fill:#ffe0b2,stroke:#ef6c00,color:#000
    classDef tool fill:#c8e6c9,stroke:#2e7d32,color:#000
    class A,B idp
    class PA,PB,PC prov
    class TA,TB,TC tool
```

A caller authenticated by B can neither see nor call the weather tools. Visibility is filtered first, and the same rule is checked again on the call itself:

```mermaid
flowchart LR
    ALL["Every registered tool"] --> F1{"Provider bound to<br/>the caller's identity provider?"}
    F1 -->|"no"| H1["Hidden from tools/list"]
    F1 -->|"yes"| F2{"Required scope<br/>in the token?"}
    F2 -->|"no"| H2["Hidden from tools/list"]
    F2 -->|"yes"| F3["Served from the pinned<br/>definition in tools.lock.json"]
    F3 --> VIS["Visible"]
    VIS -.->|"a hidden tool called directly<br/>is still refused — diagram 3, G8"| F1

    classDef hide fill:#eceff1,stroke:#607d8b,color:#000
    classDef gate fill:#ffe0b2,stroke:#ef6c00,color:#000
    classDef pass fill:#c8e6c9,stroke:#2e7d32,color:#000
    class H1,H2 hide
    class F1,F2 gate
    class VIS,F3 pass
```

**What to notice.** Hiding a tool is a convenience, not the control. `PolicyFilter` enforces the same binding on every call, so guessing a tool name gains nothing. The dotted arrow is that second check.

→ Roadmap §3.8, I9, GOAL-06

---

## 6. Risk classes and their gates

Every tool declares a risk class. The class decides how much proof of a live, current authorisation is required — and therefore how quickly a revocation takes effect.

```mermaid
flowchart TB
    CALL["Binding and scope have passed"] --> R{"Risk class<br/>in ToolPolicy"}

    R -->|"Read"| RD["Quotas and argument guard"]
    RD --> RUN1["Runs<br/>revocation delay is the token lifetime<br/>target 5 min"]

    R -->|"Write"| WR{"iat within 5 min,<br/>or an introspection result<br/>less than 60 s old?"}
    WR -->|"no"| DW["Denied<br/>step-up required"]
    WR -->|"yes"| RUN2["Runs<br/>revocation delay 60 s"]

    R -->|"Irreversible"| IR["Introspect now, no cache"]
    IR --> IC{"Confirmation round-trip<br/>completed? see diagram 7"}
    IC -->|"no"| DI["Denied"]
    IC -->|"yes"| RUN3["Runs<br/>revocation checked per request"]

    classDef deny fill:#ffcdd2,stroke:#c62828,color:#000
    classDef gate fill:#ffe0b2,stroke:#ef6c00,color:#000
    classDef pass fill:#c8e6c9,stroke:#2e7d32,color:#000
    class DW,DI deny
    class R,WR,IC gate
    class RUN1,RUN2,RUN3 pass
```

**What to notice.** A signed token cannot be recalled, so **its lifetime is the revocation delay** — that is the whole reason the three classes exist. Read tolerates a few minutes of staleness; Irreversible tolerates none and pays for it with a round-trip to the identity provider on every call.

→ Roadmap §3.3

---

## 7. Confirming an irreversible action

The server cannot force a client to show a human anything. What it *can* do is refuse to act without a confirmation it can verify — and bind that confirmation to the exact arguments, so it cannot be answered in advance.

```mermaid
sequenceDiagram
    autonumber
    participant C as MCP client
    participant P as PolicyFilter
    participant T as Tool
    participant API as Upstream

    C->>P: call an Irreversible tool with arguments
    P->>P: introspect the token now, no cache
    P->>T: invoke
    T->>T: nonce is an HMAC over sub, tool,<br/>a hash of the arguments, and a timestamp
    T-->>C: InputRequiredException carrying the nonce
    Note over C: The client shows the operation<br/>and collects an answer
    C->>P: resume with the same arguments and the nonce
    P->>P: recompute the HMAC and compare

    alt matches, and within 120 s
        P->>T: proceed
        T->>API: perform the irreversible action
        API-->>T: done
        T-->>C: result, audited as mcp_tool_confirmed
    else missing, stale, or computed for different arguments
        P-->>C: denied
    end
```

**What to notice.** The HMAC means the server stores nothing between the two halves — it recomputes and compares, which keeps the stateless model intact. Because the argument hash is inside the HMAC, a client cannot collect a confirmation for a harmless call and reuse it for a damaging one.

→ Roadmap §3.4

---

> **Amended 2026-09-24 (contract-003 as built).** A confirmation is also single-use: its id is claimed in Redis on first use, and the same confirmation presented again within its 120 seconds is refused. Without that, the stateless check above would run an irreversible tool twice on one confirmation. A client reaches the round-trip on the 2026-07-28 protocol revision, through `server/discover`.

## 8. The egress guard

Provider code never constructs an `HttpClient`; it receives one the framework built, with this handler already attached. That is what makes a provider's allowance a fact rather than a promise.

```mermaid
flowchart TB
    REQ["Provider calls its HttpClient"] --> E1{"Host exactly matches<br/>an entry in EgressPolicy.Hosts?"}
    E1 -->|"no"| B1["Abort<br/>mcp_egress_denied"]
    E1 -->|"yes"| E2{"Scheme is https?"}
    E2 -->|"no"| B2["Abort"]
    E2 -->|"yes"| E3{"Method in<br/>EgressPolicy.Methods?"}
    E3 -->|"no"| B3["Abort"]
    E3 -->|"yes"| E4{"Outbound Authorization header<br/>equals the inbound token?"}
    E4 -->|"yes"| B4["Abort<br/>the caller's token would have leaked"]
    E4 -->|"no"| E5["Resilience<br/>attempt timeout, retry,<br/>breaker, concurrency"]
    E5 --> E6["ConnectCallback<br/>resolve DNS once"]
    E6 --> E7{"Resolved address is private,<br/>loopback, link-local or multicast?"}
    E7 -->|"yes"| B5["Abort<br/>SSRF blocked"]
    E7 -->|"no"| E8["Connect to that resolved address<br/>redirects are never followed"]
    E8 --> E9["BoundedReadStream<br/>throws one byte past the cap"]
    E9 --> OK["Payload deserialised<br/>from the stream"]

    classDef deny fill:#ffcdd2,stroke:#c62828,color:#000
    classDef gate fill:#ffe0b2,stroke:#ef6c00,color:#000
    classDef pass fill:#c8e6c9,stroke:#2e7d32,color:#000
    class B1,B2,B3,B4,B5 deny
    class E1,E2,E3,E4,E7 gate
    class OK,E5,E6,E8,E9 pass
```

**What to notice.** Three details do real work here. Resolving DNS and connecting inside the same callback closes the gap where a name could resolve to a safe address during the check and a private one during the request. Not following redirects stops an allowlisted host from forwarding the request somewhere else. And the bounded stream aborts *while reading* — the current code buffers the whole body before checking its size, which means the cap does not actually bound memory.

→ Roadmap §3.2, P2.6–P2.8

---

## 9. The four rate-limit layers

Four limits, four different things being protected. They are not redundant.

```mermaid
flowchart TB
    REQ["Request"] --> L1["L1 — by source IP<br/>fixed 1 min window<br/>runs before authentication"]
    L1 -->|"over"| O1["429"]
    L1 -->|"under"| AUTH["Principal established"]
    AUTH --> L2["L2 — by idp and sub<br/>sliding 1 min<br/>caps one caller's total activity"]
    L2 -->|"over"| O2["excess_rate_limit_exceeded"]
    L2 -->|"under"| L3["L3 — by idp, sub and tool<br/>sliding 1 min from ToolPolicy<br/>catches an agent looping on one tool"]
    L3 -->|"over"| O3["excess_rate_limit_exceeded"]
    L3 -->|"under"| L4["L4 — by provider<br/>concurrency and daily budget<br/>protects the upstream and the bill"]
    L4 -->|"over"| O4["mcp_resource_exhaustion"]
    L4 -->|"under"| RUN["Tool runs"]

    classDef deny fill:#ffcdd2,stroke:#c62828,color:#000
    classDef layer fill:#ffe0b2,stroke:#ef6c00,color:#000
    classDef pass fill:#c8e6c9,stroke:#2e7d32,color:#000
    class O1,O2,O3,O4 deny
    class L1,L2,L3,L4 layer
    class RUN,AUTH pass
```

**What to notice.** L1 runs before authentication, so a flood of garbage tokens cannot exhaust signature verification. L2 to L4 are keyed on the principal and live in Redis, so they hold across instances — the current implementation keys per tool *globally*, which lets one noisy caller consume everyone else's budget.

→ Roadmap §3.6

---

> **Amended 2026-09-24 (contract-003 as built).** L2 and L3 are built, in Redis. L4 — provider concurrency and the daily budget — is built with the egress guard, in the second half of Phase 2.

## 10. The strike ladder

Violations accumulate weighted strikes in a ten-minute window, per principal. Three consequences, escalating.

```mermaid
stateDiagram-v2
    [*] --> Normal
    Normal --> Throttled: 3 strikes in 10 minutes
    Throttled --> Normal: 10 minutes elapse
    Throttled --> Suspended: 6 strikes
    Suspended --> Normal: 30 minutes elapse
    Suspended --> Revoked: 10 strikes
    Normal --> Revoked: hard signal
    Throttled --> Revoked: hard signal
    Suspended --> Revoked: hard signal
    Revoked --> Normal: human review with the admin scope

    note right of Throttled
        L2 and L3 quotas halved.
        Reverses on its own.
    end note

    note right of Suspended
        Added to the shared denylist.
        The next request on any instance
        fails authentication.
        Reverses on its own.
    end note

    note right of Revoked
        Grant or refresh token revoked
        at the principal's own provider.
        Only a human clears this.
    end note
```

**What to notice.** Only the last step is permanent, and it needs either a hard signal — a revoked principal presenting a token again — or a person. Everything below it expires by itself. That asymmetry is deliberate: an automatic system that can permanently lock out real users will eventually do so.

→ Roadmap §3.7

---

## 11. Injection is not malice

The most important branch in the design. An agent can be steered into a violation by text the *upstream* returned. Punishing the user for that would turn any poisoned data source into a denial-of-service against legitimate callers.

```mermaid
flowchart TB
    V["A violation is detected"] --> Q{"Where did it originate?"}

    Q -->|"The caller's own request<br/>wrong scope, extra argument,<br/>quota, blocked egress"| S["Strike the principal<br/>weight 1 to 5"]
    Q -->|"Text inside an upstream response"| N["mcp_prompt_injection<br/>zero strikes for the caller"]

    S --> LAD["Strike ladder<br/>throttle, suspend, revoke"]
    N --> NP["Output sanitised<br/>before the model sees it"]
    N --> NH["Provider health counter increments"]

    LAD --> REC["Audit record carries<br/>preceding_tool_output_hash"]
    NP --> REC
    NH --> REC
    REC --> INV["An investigator can tell<br/>the two cases apart"]

    classDef bad fill:#ffcdd2,stroke:#c62828,color:#000
    classDef warn fill:#ffe0b2,stroke:#ef6c00,color:#000
    classDef pass fill:#c8e6c9,stroke:#2e7d32,color:#000
    class S,LAD bad
    class Q,N,NH warn
    class NP,REC,INV pass
```

**What to notice.** The violation is still recorded either way — this is not a way of ignoring it. What changes is who bears the consequence: the caller, or the provider whose data source is producing hostile content.

→ Roadmap §3.7

---

## 12. How a revocation actually lands

Two mechanisms with very different timing, and one common mistake.

```mermaid
sequenceDiagram
    autonumber
    participant SE as StrikeEngine
    participant RD as Redis denylist
    participant N1 as Server instance 1
    participant N2 as Server instance 2
    participant IDP as The principal's identity provider
    participant AG as The agent

    SE->>RD: add a deny entry for this idp and sub
    Note over RD,N2: TTL covers the longest token lifetime.<br/>No cache sits in front of this lookup.
    AG->>N1: next call, with a still-valid token
    N1->>RD: check during token validation
    RD-->>N1: denied
    N1-->>AG: 401 authn_token_reuse
    AG->>N2: retry against another instance
    N2->>RD: check
    RD-->>N2: denied
    N2-->>AG: 401

    SE->>IDP: revoke the grant or the refresh token
    Note over SE,IDP: Revoking only the access token changes nothing.<br/>RFC 7009 cascades from the refresh token, not to it.
    IDP-->>SE: revoked, audited as authn_token_revoked
    AG->>IDP: attempt to refresh
    IDP-->>AG: invalid_grant — the agent stops and checkpoints
```

**What to notice.** The denylist is what makes suspension immediate, and it is immediate *because* nothing caches it — a sixty-second cache in front of it would simply be a sixty-second revocation delay wearing a different name. The identity-provider call is what makes revocation stick beyond this server.

→ Roadmap §3.3, P4.3–P4.4

---

## 13. Startup refuses a misconfigured server

Deny by default, enforced once at startup rather than argued about per request. Every failure exits `78` (`EX_CONFIG`) after logging `sys_startup` at CRITICAL.

```mermaid
flowchart TB
    S["Process starts"] --> V1{"stdio transport<br/>outside Development?"}
    V1 -->|"yes"| E1["exit 78"]
    V1 -->|"no"| V2{"Does every enabled provider name<br/>exactly one configured identity provider?"}
    V2 -->|"no"| E2["exit 78"]
    V2 -->|"yes"| V3{"Does every tool have exactly one policy,<br/>and every policy a real tool?"}
    V3 -->|"no"| E3["exit 78"]
    V3 -->|"yes"| V4{"Is every scope in the bound provider's<br/>catalog, with no wildcards?"}
    V4 -->|"no"| E4["exit 78"]
    V4 -->|"yes"| V5{"Are egress hosts fully qualified names,<br/>and do the timeouts compose?"}
    V5 -->|"no"| E5["exit 78"]
    V5 -->|"yes"| V6{"Do tool hashes match<br/>tools.lock.json?"}
    V6 -->|"no"| E6["exit 78<br/>mcp_tool_poisoning"]
    V6 -->|"yes"| V7{"Production without TLS<br/>or a trusted proxy?"}
    V7 -->|"yes"| E7["exit 78"]
    V7 -->|"no"| GO["Serving<br/>sys_startup with the build<br/>and tool-lock hashes"]

    classDef deny fill:#ffcdd2,stroke:#c62828,color:#000
    classDef gate fill:#ffe0b2,stroke:#ef6c00,color:#000
    classDef pass fill:#c8e6c9,stroke:#2e7d32,color:#000
    class E1,E2,E3,E4,E5,E6,E7 deny
    class V1,V2,V3,V4,V5,V6,V7 gate
    class GO pass
```

**What to notice.** A gap in configuration cannot become a gap in enforcement, because the server will not start with one. `V6` is the rug-pull check: if a tool description changed since it was reviewed, the server refuses rather than advertising it.

→ Roadmap §3.2, P5.1

---

> **Amended 2026-09-24 (contract-003 as built).** Startup also refuses: a setting in a governed section the server does not read (a typo or a retired key, named with the nearest real one); a provider that removed a registration, or registered one belonging to the frame, the SDK, the identity layer or the host; request checks installed that are not the frame's; and a primitive a module declares that the server does not serve.

## 14. What happens when a dependency is down

Fail closed, with one deliberate exception that is a configuration choice rather than an accident.

```mermaid
flowchart TB
    D{"Which dependency<br/>is unavailable?"}

    D -->|"Redis<br/>quotas, strikes, denylist"| R1["Write and Irreversible: denied"]
    R1 --> R2["Read: follows configuration<br/>default is deny"]

    D -->|"Audit sink"| A1["Write and Irreversible: denied<br/>sys_monitor_disabled raised"]
    A1 --> A2["Read: Audit ReadWhenUnavailable<br/>default is deny"]

    D -->|"Identity provider keys"| J1["Cached keys serve until they expire"]
    J1 --> J2["After that, everything<br/>fails authentication"]

    D -->|"Revoker API"| K1["The suspension stays in force"]
    K1 --> K2["mcp_revocation_failed at CRITICAL<br/>retried behind a breaker"]

    D -->|"Upstream API"| U1["Circuit breaker opens"]
    U1 --> U2["McpException with a recovery hint<br/>the caller is not struck"]

    classDef gate fill:#ffe0b2,stroke:#ef6c00,color:#000
    classDef deny fill:#ffcdd2,stroke:#c62828,color:#000
    classDef pass fill:#c8e6c9,stroke:#2e7d32,color:#000
    class D gate
    class R1,A1,J2,K2 deny
    class R2,A2,J1,K1,U1,U2 pass
```

**What to notice.** If decisions cannot be recorded, decisions that change things are not made — an unaudited write is worse than a refused one. Read is the only case with a dial, and it is set to deny by default, so choosing availability over strictness has to be deliberate.

→ Roadmap §3.3, P3.6, I7

---

## 15. A decision becomes an audit record

Every allow and every deny. One record, redacted, chained, and shipped.

```mermaid
flowchart TB
    DEC["A decision is made<br/>allow or deny"] --> B["Build the record"]

    B --> B1["OWASP event name and level"]
    B --> B2["principal — idp, sub, client_id,<br/>and the token id as a hash only"]
    B --> B3["provider, tool, risk class,<br/>decision.rule"]
    B --> B4["trace_id and request_id"]

    B1 --> RED["Redact"]
    B2 --> RED
    B3 --> RED
    B4 --> RED

    RED --> RD1["Arguments named in SensitiveArgs"]
    RED --> RD2["Anything matching a secret pattern"]
    RED --> RD3["Values over 2 KB, kept as a hash"]

    RD1 --> CH["hash is SHA-256 over<br/>the previous hash and this record"]
    RD2 --> CH
    RD3 --> CH

    CH --> F1["Append-only JSONL<br/>checked by --verify-audit"]
    CH --> F2["OTLP export to the SIEM"]

    classDef build fill:#e1bee7,stroke:#6a1b9a,color:#000
    classDef pass fill:#c8e6c9,stroke:#2e7d32,color:#000
    class B,B1,B2,B3,B4,RED,RD1,RD2,RD3 build
    class CH,F1,F2 pass
```

**What to notice.** The token itself never appears — only a hash of its id, which is enough to correlate and useless if the log leaks. The chain makes a deleted or edited record detectable: `--verify-audit` reports the first line where the chain breaks.

→ Roadmap §3.5, P3.1–P3.3

---

## 16. Phase dependencies

Where the work can run in parallel, and where it cannot. Identity comes before policy because policy is keyed on the principal; audit comes before guardrails because guardrails consume audit events.

```mermaid
flowchart LR
    P0["Phase 0<br/>Foundation<br/>3-5 days"] --> P1["Phase 1<br/>Identity<br/>5-8 days"]
    P1 --> P2["Phase 2<br/>Policy frame<br/>and egress<br/>8-12 days"]
    P2 --> P3["Phase 3<br/>Audit<br/>4-6 days"]
    P3 --> P4["Phase 4<br/>Guardrails and<br/>revocation<br/>6-10 days"]
    P1 --> P4
    P2 --> P5["Phase 5<br/>Tool integrity<br/>2-4 days"]
    P2 --> P6["Phase 6<br/>Deployment<br/>3-5 days"]
    P1 --> P6
    P4 --> P7["Phase 7<br/>Verification<br/>5-8 days"]
    P5 --> P7
    P6 --> P7

    classDef crit fill:#ffe0b2,stroke:#ef6c00,color:#000
    classDef par fill:#c8e6c9,stroke:#2e7d32,color:#000
    class P0,P1,P2,P3,P4,P7 crit
    class P5,P6 par
```

**What to notice.** Amber is the critical path; green can run alongside it. Phases 5 and 6 need only Phase 2, so a second person can take them while the audit and guardrail work proceeds.

→ Roadmap §4

---

## Where to go next

| If you want to | Read |
|----------------|------|
| The normative requirements and their IDs | [06-SECURITY-ROADMAP.md §2](06-SECURITY-ROADMAP.md#2-requirement-register) |
| What proves each control works | [06-SECURITY-ROADMAP.md §5](06-SECURITY-ROADMAP.md#5-compliance-matrix) |
| Decisions still open | [06-SECURITY-ROADMAP.md §6](06-SECURITY-ROADMAP.md#6-decisions-needed-before-phase-1) |
| How the server works today | [02-ARCHITECTURE-FLOWCHARTS.md](02-ARCHITECTURE-FLOWCHARTS.md) |
