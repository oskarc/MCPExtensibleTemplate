/* Chapters 1–2: trust boundaries, a call end to end. */
(function () {
  const { S, P } = Guide;

  /* ---------- 1. Trust boundaries ---------- */
  Guide.add({
    id: 'boundaries',
    part: 'Orientation',
    title: 'Trust boundaries',
    lede: 'Two things are untrusted, for different reasons: the client, because it may be hostile — and the text upstream APIs return, because a model will read it.',
    takeaway: 'Treat upstream responses as input, not output. The server enforces policy in both directions, and it can end a session at the identity provider, not just refuse one call.',
    refs: 'Roadmap §3.1 · Flowcharts §1',
    render(body) {
      const svg = S.svg(800, 420, 'The MCP server sits between an untrusted client and upstream APIs, with identity providers, Redis and a SIEM as its control plane; upstream responses return as untrusted text.', [
        S.zone(190, 14, 420, 104, 'Control plane — trusted, external'),
        S.zone(14, 150, 136, 120, 'Untrusted'),
        S.zone(190, 150, 420, 120, 'MCP server — the enforcement point'),
        S.zone(626, 130, 160, 150, 'Upstream APIs'),
        S.zone(430, 316, 356, 92, 'Untrusted'),
        S.node('IDP', 206, 44, 120, 58, ['Identity', 'providers A · B'], 'ctrl'),
        S.node('RED', 338, 44, 130, 58, ['Redis', 'quotas · strikes', 'denylist'], 'ctrl'),
        S.node('SIEM', 480, 44, 116, 58, ['SIEM', 'audit sink'], 'ctrl'),
        S.node('C', 24, 178, 112, 60, ['MCP client', 'agent or IDE'], 'untr'),
        S.node('EDGE', 206, 178, 114, 60, ['Edge', 'authenticate']),
        S.node('FILT', 338, 178, 130, 60, ['Filters', 'decide · record', 'sanitise']),
        S.node('EGR', 486, 178, 110, 60, ['Egress guard', 'allowlist only']),
        S.node('UA', 640, 150, 132, 34, ['SMHI forecast']),
        S.node('UB', 640, 191, 132, 34, ['SMHI observations']),
        S.node('UC', 640, 232, 132, 34, ['Demo API']),
        S.node('U', 446, 340, 190, 52, ['Upstream response text', 'the model will read it'], 'untr'),
        S.edge('C>EDGE', P.h(136, 208, 206), { label: 'token + call', lx: 171, ly: 200 }),
        S.edge('IDP>EDGE', P.v(236, 102, 178), { dash: true, label: 'signing keys', lx: 230, ly: 144, anchor: 'end' }),
        S.edge('FILT>IDP', 'M352 178V124H300V102', { label: 'revoke', lx: 306, ly: 118, anchor: 'start' }),
        S.edge('FILT>RED', P.v(403, 178, 102), { both: true, label: 'reads · writes', lx: 409, ly: 128, anchor: 'start' }),
        S.edge('FILT>SIEM', 'M450 178V140H538V102', { label: 'every decision', lx: 544, ly: 128, anchor: 'start' }),
        S.edge('EDGE>FILT', P.h(320, 208, 338)),
        S.edge('FILT>EGR', P.h(468, 208, 486)),
        S.edge('EGR>UA', P.hvh(596, 208, 618, 167, 640)),
        S.edge('EGR>UB', P.h(596, 208, 640)),
        S.edge('EGR>UC', P.hvh(596, 208, 618, 249, 640)),
        S.edge('UC>U', 'M706 266V366H636', { label: 'responses', lx: 712, ly: 318, anchor: 'start' }),
        S.edge('U>FILT', 'M446 366H403V238', { label: 'sanitised before the model sees it', lx: 397, ly: 300, anchor: 'end' }),
      ].join(''));
      const st = Guide.stage({ svg, caption: 'Dashed borders mark untrusted input. Blue is the control plane. Nothing reaches an upstream without passing through the server.' });
      body.appendChild(st);
      Guide.steps(st, [
        { title: 'What is untrusted', k: ['C', 'U'], t: 'The client may be an agent that has been steered, or a hostile process. Upstream response text is untrusted for a different reason: a model will read it, and text can carry instructions.' },
        { title: 'Identity at the edge', k: ['C', 'C>EDGE', 'EDGE', 'IDP', 'IDP>EDGE'], t: 'Every call carries a bearer token. The edge validates it against signing keys the identity provider publishes — it never takes the client’s word for who it is.' },
        { title: 'Shared decisions', k: ['EDGE', 'EDGE>FILT', 'FILT', 'RED', 'FILT>RED'], t: 'The filters decide. Quotas, strike counts, and suspensions live in Redis, so every server instance reaches the same decision about the same caller.' },
        { title: 'Every decision recorded', k: ['FILT', 'FILT>SIEM', 'SIEM'], t: 'Allowed or refused, each decision produces exactly one audit record, shipped to the SIEM.' },
        { title: 'Out through one door', k: ['FILT', 'FILT>EGR', 'EGR', 'EGR>UA', 'EGR>UB', 'EGR>UC', 'UA', 'UB', 'UC'], t: 'Provider code reaches the network only through the egress guard, which enforces that provider’s allowlist of hosts and methods.' },
        { title: 'Responses come back as input', k: ['UC', 'UC>U', 'U', 'U>FILT', 'FILT'], t: 'Whatever an upstream returns is handled like input: size-capped and stripped of instruction-like markup before it can re-enter a model’s context.' },
        { title: 'A path back to the source', k: ['FILT', 'FILT>IDP', 'IDP'], t: 'When a caller must lose access, the server does more than refuse the next call — it asks that caller’s own identity provider to revoke the grant.' },
      ], { intro: 'The server is the only enforcement point: there is no route from a client to an upstream that avoids it. Press <b>Start</b> to walk each boundary.' });
    },
  });

  /* ---------- 2. A call end to end ---------- */
  const lanes = [
    ['C', ['MCP', 'client'], 'untr'], ['IDP', ['Identity', 'provider'], 'ctrl'], ['E', ['Edge', 'middleware'], 'lane'],
    ['A', ['Audit', 'filter'], 'lane'], ['P', ['Policy', 'filter'], 'lane'], ['T', ['Tool', 'method'], 'lane'],
    ['G', ['Egress', 'guard'], 'lane'], ['API', ['Upstream', 'API'], 'untr'], ['O', ['Output', 'guard'], 'lane'],
  ];
  const X = Object.fromEntries(lanes.map(([k], i) => [k, 70 + i * 100]));
  // [from, to, label, dashed, title, narration]
  const msgs = [
    ['C', 'IDP', 'token for this resource', 0, 'Ask for a token', 'Before calling anything, the client asks its identity provider for a token scoped to this server’s resource URI (RFC 8707 resource indicators).'],
    ['IDP', 'C', 'aud = this server · short exp', 1, 'A short-lived token', 'The token names this server as its audience and expires quickly. That short life is deliberate: for read-only tools, the token lifetime <em>is</em> the revocation delay.'],
    ['C', 'E', 'tools/call + bearer token', 0, 'The call arrives', 'The call arrives with the token as a bearer credential. Nothing about it is trusted yet.'],
    ['E', 'E', 'verify token · check denylist', 0, 'Edge checks', 'The edge rejects cheap failures first — wrong host, foreign origin, an IP flood — then routes the token to its issuer’s scheme, verifies signature, audience and expiry, and checks the shared denylist.'],
    ['E', 'A', 'principal idp · sub · client_id · jti', 0, 'A normalised principal', 'What leaves the edge is a normalised principal. Every limit, strike and audit record from here on is keyed on <code>idp:sub</code>.'],
    ['A', 'A', 'open record · trace id', 0, 'Open the record first', 'The audit filter opens the record before anything else runs, so even a refusal deeper in is recorded under this trace id.'],
    ['A', 'P', 'forward', 0, 'Inward', 'The request moves inward to policy.'],
    ['P', 'P', 'binding · scope · risk · quotas · args', 0, 'Policy decides', 'Policy runs its ordered checks: provider binding, scope, risk gate, quotas, then the argument schema. Chapter 6 shows every exit.'],
    ['P', 'T', 'validated arguments only', 0, 'Only clean arguments', 'Only arguments that survived validation reach the tool. A smuggled extra field never gets this far.'],
    ['T', 'G', 'HTTP via provider client', 0, 'One sanctioned client', 'The tool never constructs its own HTTP client; it uses the one the framework built for its provider, with the guard already attached.'],
    ['G', 'G', 'host · method · scheme · token leak', 0, 'Egress checks', 'The egress guard checks the destination against the provider’s allowlist and makes sure the caller’s token is not being forwarded upstream.'],
    ['G', 'API', 'request to vetted address', 0, 'A vetted connection', 'DNS is resolved once, the address is vetted against private and link-local ranges, and the connection goes to exactly that address. Redirects are never followed.'],
    ['API', 'G', 'response', 1, 'Untrusted bytes', 'The upstream answers. Its body is untrusted text.'],
    ['G', 'T', 'bounded read → payload', 1, 'A bounded read', 'The body is read through a stream that stops one byte past the provider’s cap, and is deserialised straight from that stream.'],
    ['T', 'O', 'formatted text', 0, 'Format for the model', 'The tool turns the payload into concise, labelled text for the model.'],
    ['O', 'A', 'size cap · markup stripped', 1, 'Sanitise the output', 'The output guard caps the size and strips instruction-like markup before the text can re-enter a model’s context.'],
    ['A', 'A', 'append · chain hash', 0, 'Close the record', 'The audit filter appends the record — outcome, rule, principal, trace id — and chains its hash to the previous one.'],
    ['A', 'C', 'tool result', 1, 'The result', 'The client receives the result. Every hop above left a trace under a single trace id.'],
  ];

  Guide.add({
    id: 'journey',
    part: 'Orientation',
    title: 'A call from end to end',
    lede: 'One successful call, hop by hop. Nine participants, each with one job — step through to see who checks what, and in which order.',
    takeaway: 'The audit filter is outermost and the argument guard runs before the tool, so every outcome is recorded and bad input never reaches an upstream.',
    refs: 'Roadmap §3.1, §3.5 · Flowcharts §2',
    render(body) {
      const top = 84, gap = 32, H = top + (msgs.length - 1) * gap + 34;
      const parts = [];
      lanes.forEach(([k, label, cls]) => {
        parts.push(`<line class="life" x1="${X[k]}" y1="54" x2="${X[k]}" y2="${H - 8}"/>`);
        parts.push(S.node('L-' + k, X[k] - 46, 12, 92, 40, label, cls));
      });
      msgs.forEach(([f, t, label, dash], i) => {
        const y = top + i * gap, k = 'm' + (i + 1);
        if (f === t) {
          const x = X[f];
          parts.push(S.edge(k, `M${x + 3} ${y - 6}h28v14h-24`, { label, lx: x + 38, ly: y + 5, anchor: 'start' }));
        } else {
          const x1 = X[f] + (X[t] > X[f] ? 4 : -4), x2 = X[t] + (X[t] > X[f] ? -4 : 4);
          parts.push(S.edge(k, P.h(x1, y, x2), { dash: !!dash, label, lx: (X[f] + X[t]) / 2, ly: y - 6 }));
        }
      });
      const svg = S.svg(940, H, 'Sequence of a successful tool call across client, identity provider, edge, audit filter, policy filter, tool, egress guard, upstream and output guard.', parts.join(''));
      const st = Guide.stage({ svg, caption: 'Solid arrows are requests, dashed arrows are replies, and loops are checks a participant makes on its own.' });
      body.appendChild(st);
      Guide.steps(
        st,
        msgs.map(([f, t, , , title, text], i) => ({ title, t: text, k: ['m' + (i + 1), 'L-' + f, 'L-' + t] })),
        { intro: 'Nothing on this diagram is optional — a call that skips a step is a bug. Press <b>Start</b> to follow the call.' }
      );
    },
  });
})();
