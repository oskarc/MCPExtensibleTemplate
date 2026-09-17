/* Chapters 6–7: every way a call can be refused; risk classes and revocation delay. */
(function () {
  const { S, P, esc } = Guide;

  /* ---------- 6. The refusal ladder ---------- */
  const rows = [
    ['G1', 'Host is on AllowedHosts?', 'X1', '`400 · host not allowed'],
    ['G2', 'Origin absent, or allowed?', 'X2', '`403 · malicious_cors'],
    ['G3', 'Under the per-IP limit (L1)?', 'X3', '`429 · before authentication'],
    ['G4', 'Issuer is configured?', 'X4', '`401 · no key fetch made'],
    ['G5', 'Signature, audience, expiry, alg valid?', 'X5', '`401 · authn_login_fail'],
    ['G6', 'Principal not on the denylist?', 'X6', '`401 · suspended or revoked'],
    ['G7', 'Tool exists?', 'X7', '`error · rule unknown-tool'],
    ['G8', 'Provider bound to the caller’s issuer?', 'X8', '`authz_fail · rule idp-binding'],
    ['G9', 'Token carries the tool’s scope?', 'X9', '`authz_fail · rule scope'],
    ['G10', 'Risk gate satisfied? (chapter 7)', 'X10', '`denied · freshness or confirmation'],
    ['G11', 'Within quotas L2 · L3 · L4?', 'X11', '`excess_rate_limit_exceeded'],
    ['G12', 'Arguments match the schema exactly?', 'X12', '`malicious_extraneous'],
    ['G13', 'Redis and audit sink reachable?', 'X13', '`denied · fail closed'],
    ['RUN', 'Tool runs', null, null],
    ['G14', 'Egress allowed? (chapter 9)', 'X14', '`mcp_egress_denied'],
    ['OK', 'Result returned and recorded', null, null],
  ];
  const Y = (r) => 52 + r * 36;
  const order = rows.map((r) => r[0]);
  const upTo = (gate, deny) => ['IN', ...order.slice(0, order.indexOf(gate) + 1), deny];
  const groups = [
    [0, 2, ['Edge', 'before identity']],
    [3, 5, ['Edge', 'identity']],
    [6, 12, ['Policy filter']],
    [13, 15, ['Tool and', 'egress']],
  ];

  Guide.add({
    id: 'refusal',
    part: 'The call path',
    title: 'Every way a call can be refused',
    lede: 'One ladder, cheapest check first. Every exit carries its own event name or rule, so “why was this refused?” always has exactly one answer in the audit log.',
    takeaway: 'Identity is settled before anything provider-specific is consulted, and arguments are validated before the tool runs — a call with a bad argument never reaches an upstream.',
    refs: 'Roadmap §3.1, §3.2, §3.7 · Flowcharts §3',
    render(body) {
      const parts = [S.node('IN', 170, 10, 280, 28, ['tools/call arrives'], 'untr'), S.edge('IN>G1', P.v(310, 38, 52))];
      rows.forEach(([g, label, x, deny], r) => {
        const y = Y(r);
        const cls = g === 'RUN' ? 'pass' : g === 'OK' ? 'pass terminal' : 'gate';
        parts.push(S.node(g, 170, y, 280, 28, [label], cls));
        if (r < rows.length - 1) parts.push(S.edge(`${g}>${rows[r + 1][0]}`, P.v(310, y + 28, y + 36)));
        if (x) {
          parts.push(S.node(x, 490, y, 318, 28, [deny], 'deny'));
          parts.push(S.edge(`${g}>${x}`, P.h(450, y + 14, 490), { deny: true, label: 'no', lx: 470, ly: y + 10 }));
        }
      });
      groups.forEach(([a, b, label]) => {
        const y1 = Y(a) + 4, y2 = Y(b) + 24, mid = (y1 + y2) / 2;
        parts.push(`<path class="brk" d="M158 ${y1}h-6V${y2}h6"/>`);
        label.forEach((l, i) => parts.push(S.text(144, mid + 4 + (i - (label.length - 1) / 2) * 14, l, 'elabel', 'end')));
      });
      const svg = S.svg(820, Y(rows.length - 1) + 40, 'A ladder of fourteen checks from host validation to egress; each refusal exits to the right with the event that is logged.', parts.join(''));
      const st = Guide.stage({ svg, caption: 'Read top to bottom. A “yes” continues down; a “no” exits right with the event written to the audit log.' });
      body.appendChild(st);
      Guide.scenarios(st, {
        lead: 'What happens when',
        say: {
          IN: 'A tool call arrives. Nothing about it is trusted yet.',
          G1: 'The request must name a host this server answers to. <code>AllowedHosts</code> is never <code>*</code>.',
          G2: 'A browser page on a foreign origin is refused here — the DNS-rebinding defence.',
          G3: 'The per-IP limit runs <em>before</em> authentication, so a flood of junk tokens cannot exhaust signature checks.',
          G4: 'The token’s issuer must be configured. Unknown issuers stop before any key fetch.',
          G5: 'Full validation against that issuer’s keys: signature, audience, expiry, algorithm.',
          G6: 'The shared denylist is checked on every request, with no cache in front of it.',
          G7: 'The tool name must be one the server registered.',
          G8: 'The tool’s provider must be bound to the caller’s issuer.',
          G9: 'The token must carry the scope the tool’s policy requires, from the bound issuer’s catalog.',
          G10: 'Write and Irreversible tools demand fresher proof of authorisation.',
          G11: 'Per-caller, per-tool and per-provider budgets, shared across instances in Redis.',
          G12: 'Unknown argument names, schema violations, over-long strings and non-finite numbers all stop here.',
          G13: 'If quotas or the audit trail cannot be consulted, changes are not made.',
          RUN: 'Only now does the tool run, with validated arguments.',
          G14: 'Every outbound request passes the provider’s egress guard.',
          OK: 'The result goes back, and the audit filter records the outcome.',
        },
        list: [
          { label: 'A normal call', path: ['IN', ...order], end: 'Fourteen checks passed, one record written. <span class="ev pass">mcp_tool_call</span>' },
          { label: 'Page on another origin', path: upTo('G2', 'X2'), end: 'Refused with 403 before any token is examined. A malicious web page cannot drive this server through a user’s browser. <span class="ev deny">malicious_cors</span>' },
          { label: 'Junk-token flood', path: upTo('G3', 'X3'), end: 'Refused with 429 by the per-IP limiter. Because it runs before authentication, the flood never costs a signature check. <span class="ev deny">429</span>' },
          { label: 'Unknown issuer', path: upTo('G4', 'X4'), end: 'Refused with 401, and no request for signing keys was made. <span class="ev deny">401</span>' },
          { label: 'Token for another server', path: upTo('G5', 'X5'), end: 'Refused: the audience is another resource. <span class="ev deny">authn_login_fail</span>' },
          { label: 'Suspended caller', path: upTo('G6', 'X6'), end: 'The token is still cryptographically valid, but the principal is suspended. <span class="ev deny">authz_fail</span> and a 5-point strike. Had the principal been <em>revoked</em>, the same exit would log <span class="ev deny">authn_token_reuse</span> — a hard signal.' },
          { label: 'Tool of another issuer', path: upTo('G8', 'X8'), end: 'The provider is bound to a different identity provider. <span class="ev deny">authz_fail</span> rule <code>idp-binding</code>, 3-point strike.' },
          { label: 'Missing scope', path: upTo('G9', 'X9'), end: 'The token lacks the required scope. <span class="ev deny">authz_fail</span> rule <code>scope</code>, 3-point strike, and an <code>insufficient_scope</code> challenge naming the scope to request.' },
          { label: 'Write with a stale token', path: upTo('G10', 'X10'), end: 'The token was issued more than five minutes ago and no fresh introspection is available. Denied — the client must step up. <span class="ev gate">freshness</span>' },
          { label: 'Agent stuck in a loop', path: upTo('G11', 'X11'), end: 'The per-tool budget (L3) is spent. <span class="ev deny">excess_rate_limit_exceeded</span>, 1-point strike per refusal.' },
          { label: 'Smuggled extra argument', path: upTo('G12', 'X12'), end: 'An argument name that is not in the tool’s schema. The upstream is never called. <span class="ev deny">malicious_extraneous</span>, 3-point strike. A malformed value would log <code>input_validation_fail</code> instead.' },
          { label: 'Redis down, Write tool', path: upTo('G13', 'X13'), end: 'Quotas and suspensions cannot be checked, so a state-changing call is refused. Read tools follow configuration — default also deny. <span class="ev deny">fail closed</span>' },
          { label: 'Argument steers the URL', path: upTo('G14', 'X14'), end: 'The tool ran, but the outbound request pointed at a host outside the provider’s allowlist. <span class="ev deny">mcp_egress_denied</span>, 5-point strike.' },
        ],
      });
    },
  });

  /* ---------- 7. Risk classes ---------- */
  const classes = [
    { k: 'HR', name: 'Read', bands: ['B1'], ex: '<code>get_forecast</code>, <code>get_blog_post</code>', say: 'The baseline: a valid token, the provider binding, the scope, no suspension, quotas and clean arguments. Nothing more — which is why a Read tool keeps working until the token expires, even if the issuer has already revoked the caller.' },
    { k: 'HW', name: 'Write', bands: ['B1', 'B2'], ex: '<code>create_blog_post</code>, <code>create_user_todo</code>', say: 'Everything Read needs, plus fresh proof: the token issued within five minutes, or an introspection answer under sixty seconds old. A stale token is refused with a step-up challenge.' },
    { k: 'HI', name: 'Irreversible', bands: ['B1', 'B2', 'B3', 'B4'], ex: 'none in this template — for example a future <code>delete_project</code>', say: 'Everything Write needs, plus an introspection call on every request, with no cache, and a confirmation round-trip bound to the exact arguments. Chapter 8 shows the round-trip.' },
  ];

  Guide.add({
    id: 'risk',
    part: 'The call path',
    title: 'Risk classes and their gates',
    lede: 'Every tool declares a risk class. The class sets how much proof of a live, current authorisation a call needs — which is the same as setting how quickly a revocation takes effect.',
    takeaway: 'A signed token cannot be recalled, so its lifetime is the revocation delay. Read tolerates minutes; Irreversible tolerates none and pays with a call to the issuer every time.',
    refs: 'Roadmap §3.3 · Flowcharts §6',
    render(body) {
      const svg = S.svg(820, 290, 'Read, Write and Irreversible tools share a baseline of checks; Write adds a freshness requirement; Irreversible adds per-request introspection and a confirmation round-trip.', [
        S.node('HR', 40, 16, 240, 40, ['Read'], 'pass'),
        S.node('HW', 300, 16, 240, 40, ['Write'], 'gate'),
        S.node('HI', 560, 16, 240, 40, ['Irreversible'], 'deny'),
        S.node('B1', 40, 72, 760, 42, ['Valid token · provider binding · scope · not suspended · quotas · arguments']),
        S.node('B2', 300, 128, 500, 42, ['Fresh authorisation: iat within 5 min, or introspection under 60 s old'], 'gate'),
        S.node('B3', 560, 184, 240, 42, ['Introspect now, no cache'], 'deny'),
        S.node('B4', 560, 240, 240, 42, ['Confirmation round-trip'], 'deny'),
        S.text(160, 150, 'nothing more', 'elabel', 'middle'),
        S.text(420, 206, 'nothing more', 'elabel', 'middle'),
      ].join(''));
      const st = Guide.stage({ svg, caption: 'Each column is a risk class; the bands under it are the checks a call of that class must pass.' });
      body.appendChild(st);
      const bar = st.querySelector('.stage-bar'), fig = st.querySelector('.fig'), narr = st.querySelector('.narr');
      bar.innerHTML = `<span class="lbl">Risk class</span>` + classes.map((c, i) => `<button type="button" class="chip" data-i="${i}" aria-pressed="false">${c.name}</button>`).join('');
      const pick = (i) => {
        const c = classes[i];
        Guide.light(fig, [c.k, ...c.bands]);
        bar.querySelectorAll('.chip').forEach((b) => b.setAttribute('aria-pressed', String(+b.dataset.i === i)));
        narr.innerHTML = `<div class="step">${c.name} · examples: ${c.ex}</div><p>${c.say}</p>`;
      };
      bar.addEventListener('click', (e) => { const b = e.target.closest('.chip'); if (b) pick(+b.dataset.i); });
      pick(1);

      // Revocation-delay chart, drawn to scale from the slider.
      const chart = Guide.stage({
        bar: `<span class="lbl">Token lifetime</span><input type="range" id="risk-lifetime" min="1" max="60" step="1" value="5" aria-label="Token lifetime in minutes" style="max-width:320px"><span class="counter" data-val></span>`,
        svg: '<div data-chart></div>',
        caption: 'Bars are drawn to scale. The agent is assumed to make one call every five seconds.',
      });
      body.appendChild(chart);
      const slider = chart.querySelector('#risk-lifetime'), out = chart.querySelector('[data-val]'), host = chart.querySelector('[data-chart]'), cnarr = chart.querySelector('.narr');
      const draw = () => {
        const T = +slider.value;
        out.textContent = `${T} min`;
        const step = T <= 6 ? 1 : T <= 15 ? 3 : T <= 30 ? 5 : 10;
        const max = Math.max(step * Math.ceil(Math.max(T, 5) / step), 5);
        const x0 = 170, pw = 560, sx = (m) => x0 + (m / max) * pw;
        const rowsC = [['Read', T, `up to ${T} min · ≈ ${T * 12} calls`], ['Write', 1, 'up to 60 s · ≈ 12 calls'], ['Irreversible', 0, 'checked on every call · 0 calls']];
        let g = `<text class="elabel" x="${x0}" y="20">Revoked at the identity provider — how long this server keeps accepting the caller</text>`;
        rowsC.forEach(([name, m, label], i) => {
          const y = 40 + i * 44;
          g += S.text(x0 - 10, y + 16, name, 'elabel rowlbl', 'end');
          if (m > 0) g += `<rect class="bar-fill" x="${x0}" y="${y}" width="${(sx(m) - x0).toFixed(1)}" height="22" rx="2"/>`;
          else g += `<path class="bar-zero" d="M${x0} ${y - 2}v26"/>`;
          g += S.text(sx(m) + 8, y + 16, label, 'elabel', 'start');
        });
        const ay = 176;
        g += `<path class="axis" d="M${x0} ${ay}H${x0 + pw}"/>`;
        for (let m = 0; m <= max; m += step) g += `<path class="axis" d="M${sx(m)} ${ay}v5"/>` + S.text(sx(m), ay + 18, `${m}`, 'elabel', 'middle');
        g += S.text(x0 + pw + 10, ay + 18, 'minutes', 'elabel', 'start');
        host.innerHTML = S.svg(820, 204, `With a ${T}-minute token, a Read tool keeps accepting a revoked caller for up to ${T} minutes, a Write tool for up to 60 seconds, and an Irreversible tool not at all.`, g);
        cnarr.innerHTML = `<div class="step">Why lifetime matters</div><p>A revocation made <em>at the identity provider</em> reaches this server only when the token expires — unless the server asks. At ${T} minutes, a looping agent could make about <b class="num">${T * 12}</b> more Read calls. Write tools cap that at a minute; Irreversible tools ask the issuer every time. A suspension decided <em>by this server</em> is different: it takes effect on the next request, for every class (chapter 13).</p>`;
      };
      slider.addEventListener('input', draw);
      draw();
    },
  });
})();
