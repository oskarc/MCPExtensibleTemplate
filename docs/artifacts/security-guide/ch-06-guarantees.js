/* Chapters 14–16: startup refusal, dependency failures, the audit hash chain. */
(function () {
  const { S, P, esc } = Guide;

  /* ---------- 14. Startup refuses a misconfigured server ---------- */
  const vrows = [
    ['V1', 'stdio only in Development?', 'E1', '`exit 78 · stdio outside Development'],
    ['V2', 'Each provider bound to one configured issuer?', 'E2', '`exit 78 · binding missing or unknown'],
    ['V3', 'Every tool has exactly one policy?', 'E3', '`exit 78 · tool without a policy'],
    ['V4', 'Scopes in the bound catalog, no wildcards?', 'E4', '`exit 78 · unknown scope'],
    ['V5', 'Egress hosts are names; timeouts compose?', 'E5', '`exit 78 · bad egress policy'],
    ['V6', 'Tool definitions match tools.lock.json?', 'E6', '`exit 78 · mcp_tool_poisoning'],
    ['V7', 'TLS or a trusted proxy in Production?', 'E7', '`exit 78 · no transport security'],
    ['GO', 'Serving', null, null],
  ];
  const vorder = vrows.map((r) => r[0]);
  const VY = (r) => 52 + r * 36;
  const vUpTo = (g, e) => ['BOOT', ...vorder.slice(0, vorder.indexOf(g) + 1), e];
  const fatal = (why, detail) =>
    `[08:00:01 FTL] sys_startup  level=CRITICAL  ${why}\n               ${detail}\nprocess exited with code 78 (EX_CONFIG)`;
  const logs = {
    ok: '[08:00:01 INF] sys_startup  build=812340d  tools.lock=5e1c07a2…  environment=Production\n[08:00:01 INF] issuers A, B · providers Smhi→A, SmhiObs→A · 6 tools, 6 policies\n[08:00:01 INF] serving https://mcp.example.com/mcp',
    stdio: fatal('transport=stdio  environment=Production', 'stdio carries no identity and is allowed only in Development'),
    bind: fatal('provider=SmhiObs  Providers:SmhiObs:IdentityProvider=<missing>', 'every enabled provider must name exactly one configured issuer'),
    policy: fatal('tool=get_station_list  policy=<none>', 'a tool without a ToolPolicy is never exposed'),
    scope: fatal('tool=get_forecast  scope=weather:reed', 'not in the scope catalog of issuer A'),
    egress: fatal('provider=SmhiObs  egress host=10.20.0.7', 'egress hosts must be fully qualified names, not IP literals'),
    lock: fatal('event=mcp_tool_poisoning  tool=get_forecast', 'description hash 9ab24f1e… differs from tools.lock.json 5e1c07a2…'),
    tls: fatal('environment=Production  https=<none>  trusted proxies=<none>', 'refusing to serve tokens over plain http'),
  };

  Guide.add({
    id: 'startup',
    part: 'Guarantees',
    title: 'Startup refuses a misconfigured server',
    lede: 'Deny by default is enforced once, at startup, instead of argued about on every request. Pick a mistake and read the line the operator would see.',
    takeaway: 'A gap in configuration cannot become a gap in enforcement, because the server will not start with one. The tool-lock check is the defence against a tool description quietly changing after review.',
    refs: 'Roadmap §3.2, P0.3, P1.6, P2.2, P5.1 · Flowcharts §13',
    render(body) {
      const parts = [S.node('BOOT', 170, 10, 280, 28, ['Process starts']), S.edge('BOOT>V1', P.v(310, 38, 52))];
      vrows.forEach(([g, label, e, text], r) => {
        const y = VY(r);
        parts.push(S.node(g, 170, y, 280, 28, [label], g === 'GO' ? 'pass terminal' : 'gate'));
        if (r < vrows.length - 1) parts.push(S.edge(`${g}>${vrows[r + 1][0]}`, P.v(310, y + 28, y + 36)));
        if (e) {
          parts.push(S.node(e, 490, y, 318, 28, [text], 'deny'));
          parts.push(S.edge(`${g}>${e}`, P.h(450, y + 14, 490), { deny: true, label: 'no', lx: 470, ly: y + 10 }));
        }
      });
      const svg = S.svg(820, VY(vrows.length - 1) + 40, 'Seven startup checks; any failure exits with code 78 before the server accepts a request.', parts.join(''));
      const st = Guide.stage({ svg: `${svg}<div style="height:10px"></div><pre class="console" data-log></pre>` });
      body.appendChild(st);
      const out = st.querySelector('[data-log]');
      Guide.scenarios(st, {
        lead: 'Configuration',
        onPick: (s) => { out.textContent = logs[s.log]; },
        say: {
          BOOT: 'The process starts. No request is accepted until every check below has passed.',
          V1: 'stdio has no way to carry a token, so it exists only for local development.',
          V2: 'Each enabled provider names exactly one configured identity provider — no default, no list.',
          V3: 'Every discovered tool has exactly one policy, and every policy names a real tool.',
          V4: 'Each required scope exists in the catalog of the provider’s bound issuer, and none is a wildcard.',
          V5: 'Egress hosts are fully qualified names, and the total timeout exceeds attempt timeout × (1 + retries).',
          V6: 'Each tool’s name, description, schema and annotations are hashed and compared with the reviewed lock file.',
          V7: 'In Production, tokens are only ever received over TLS, directly or through a trusted proxy.',
          GO: 'Serving. The startup event records the build hash and the tool-lock hash.',
        },
        list: [
          { label: 'Everything configured', path: ['BOOT', ...vorder], log: 'ok', end: 'All seven checks passed. <span class="ev pass">sys_startup</span> carries the build and tool-lock hashes, which also helps an inventory spot unapproved servers.' },
          { label: 'stdio in Production', path: vUpTo('V1', 'E1'), log: 'stdio', end: 'Refused. A stdio server has no caller identity, so none of the per-caller controls could work. <span class="ev deny">exit 78</span>' },
          { label: 'Provider with no issuer', path: vUpTo('V2', 'E2'), log: 'bind', end: 'Refused. Without a binding there is no answer to “who may call this provider?” — and the design does not guess. <span class="ev deny">exit 78</span>' },
          { label: 'New tool, no policy', path: vUpTo('V3', 'E3'), log: 'policy', end: 'Refused. A developer added a tool but no policy; it would otherwise have been exposed with no scope, risk class or budget. <span class="ev deny">exit 78</span>' },
          { label: 'Scope typo', path: vUpTo('V4', 'E4'), log: 'scope', end: 'Refused. <code>weather:reed</code> is in no catalog, so the tool could never be authorised — or worse, a typo in the catalog could match something unintended. <span class="ev deny">exit 78</span>' },
          { label: 'Egress host is an IP', path: vUpTo('V5', 'E5'), log: 'egress', end: 'Refused. IP literals bypass the name-based allowlist and make internal addresses easy to reach. <span class="ev deny">exit 78</span>' },
          { label: 'Description edited after review', path: vUpTo('V6', 'E6'), log: 'lock', end: 'Refused. A changed description is how a tool gets poisoned — new instructions aimed at the model. The server will not advertise what nobody reviewed. <span class="ev deny">mcp_tool_poisoning</span>' },
          { label: 'Production without TLS', path: vUpTo('V7', 'E7'), log: 'tls', end: 'Refused. Bearer tokens over plain http can be read by anyone on the path. <span class="ev deny">exit 78</span>' },
        ],
      });
    },
  });

  /* ---------- 15. When a dependency is down ---------- */
  const deps = [
    ['redis', 'Redis', 'quotas, strikes, denylist'],
    ['audit', 'Audit sink', 'where decisions are recorded'],
    ['keys', 'Issuer keys and introspection', 'the identity provider is unreachable'],
    ['expired', 'Cached keys have expired', 'only matters while the issuer is down'],
    ['revoker', 'Revocation API', 'the issuer’s admin endpoint'],
    ['upstream', 'Upstream API', 'the forecast service'],
  ];

  Guide.add({
    id: 'failure',
    part: 'Guarantees',
    title: 'When a dependency is down',
    lede: 'Fail closed, with one dial that is a deliberate configuration choice rather than an accident. Switch dependencies off and see what each kind of call does.',
    takeaway: 'If a decision cannot be recorded, a decision that changes something is not made — an unaudited write is worse than a refused one. Read is the only dial, and it defaults to deny.',
    refs: 'Roadmap §3.3, P3.6, P4.4, I7, decision D7 · Flowcharts §14 · Opens with Redis switched off as an example.',
    render(body) {
      const toggles = deps.map(([k, name, what]) =>
        `<label class="toggle" style="display:flex"><input type="checkbox" id="dep-${k}" data-dep="${k}"${k === 'redis' ? ' checked' : ''}><span><b>${name}</b> <small style="color:var(--muted)">— ${what}</small></span></label>`).join('');
      const html = `<div class="inset cols two" style="grid-template-columns:minmax(0,5fr) minmax(0,7fr)">
        <div class="panel"><h3 style="margin-bottom:8px">Unavailable</h3><div style="display:grid;gap:8px">${toggles}</div>
          <hr style="border:0;border-top:1px solid var(--rule);margin:12px 0">
          <label class="toggle" style="display:flex"><input type="checkbox" id="dep-readallow" data-dep="readallow"><span>Read tools may run while Redis or the audit sink is down <small style="color:var(--muted)">(default: off)</small></span></label>
        </div>
        <div class="panel"><table class="matrix"><thead><tr><th>What is attempted</th><th>Outcome</th></tr></thead><tbody data-rows></tbody></table>
          <p class="refs" data-events style="margin-top:10px"></p></div>
      </div>`;
      const st = Guide.stage({ bar: false, svg: html });
      body.appendChild(st);
      const narr = st.querySelector('.narr');
      const pill = (cls, text) => `<span class="pill ${cls}">${text}</span>`;
      const draw = () => {
        const on = Object.fromEntries([...st.querySelectorAll('[data-dep]')].map((i) => [i.dataset.dep, i.checked]));
        const exp = st.querySelector('#dep-expired');
        exp.disabled = !on.keys;
        const expired = on.keys && on.expired;
        const stateDown = on.redis || on.audit;
        const upstream = (cell) => (on.upstream && cell[0] !== 'deny' ? ['gate', 'breaker open · error with a recovery hint'] : cell);
        const rows = [
          ['A Read tool call', upstream(expired ? ['deny', '401 · tokens cannot be verified'] : stateDown ? (on.readallow ? ['gate', 'runs — limits or audit not enforced'] : ['deny', 'denied · fail closed']) : on.keys ? ['pass', 'runs on cached keys'] : ['pass', 'runs'])],
          ['A Write tool call', upstream(expired ? ['deny', '401 · tokens cannot be verified'] : stateDown ? ['deny', 'denied · fail closed'] : on.keys ? ['gate', 'runs only if iat is under 5 min'] : ['pass', 'runs'])],
          ['An Irreversible tool call', upstream(expired ? ['deny', '401 · tokens cannot be verified'] : stateDown ? ['deny', 'denied · fail closed'] : on.keys ? ['deny', 'denied · introspection unavailable'] : ['pass', 'runs after confirmation'])],
          ['Suspending a caller', on.redis ? ['deny', 'cannot be recorded — writes already fail closed'] : ['pass', 'immediate, every instance']],
          ['Revoking at the issuer', on.revoker || on.keys ? ['gate', 'retried behind a breaker · suspension holds'] : ['pass', 'immediate']],
        ];
        st.querySelector('[data-rows]').innerHTML = rows.map(([what, [cls, text]]) => `<tr><td>${what}</td><td>${pill(cls, text)}</td></tr>`).join('');
        const events = [];
        if (on.audit) events.push('<span class="ev deny">sys_monitor_disabled</span>');
        if (on.revoker || on.keys) events.push('<span class="ev deny">mcp_revocation_failed</span> if a revocation is attempted');
        if (expired) events.push('<span class="ev deny">authn_login_fail</span> on every call');
        st.querySelector('[data-events]').innerHTML = events.length ? `Raised: ${events.join(' · ')}` : 'No alerts raised.';
        let say = 'Everything is up: each call is limited, recorded and verified as usual.';
        if (expired) say = 'With the issuer down and its cached keys expired, no token can be verified, so every call fails authentication. That is the correct outcome — the alternative would be trusting unverifiable tokens.';
        else if (stateDown && on.readallow) say = 'Writes stop. Reads continue because the operator turned the dial — accepting calls whose limits or records cannot be enforced. That trade must be deliberate, which is why it is off by default.';
        else if (stateDown) say = 'Without Redis or the audit sink, the server cannot enforce quotas and suspensions or record decisions — so it makes no decisions that change anything, and with the default setting it refuses reads too.';
        else if (on.keys) say = 'The issuer is unreachable, but its keys are cached, so tokens still verify. Write tools fall back to the token’s own issue time; Irreversible tools need a live introspection answer and are refused.';
        else if (on.revoker) say = 'Revocation calls fail, but the suspension already in Redis keeps the caller out. The failure is raised at CRITICAL and retried behind a circuit breaker.';
        else if (on.upstream) say = 'The upstream is down. The circuit breaker opens and callers get an error with a recovery hint. Nobody is struck: an outage is not misbehaviour.';
        narr.innerHTML = `<div class="step">Outcome</div><p>${say}</p>`;
      };
      st.addEventListener('change', draw);
      draw();
    },
  });

  /* ---------- 16. A decision becomes an audit record ---------- */
  const GENESIS = '0'.repeat(64);
  const secret = 'Bearer eyJhbGciOiJSUzI1NiIsImtpZCI6ImExIn0.eyJzdWIiOiI5ZjFj';
  const baseRecords = () => [
    { datetime: '2026-09-17T08:12:40.004Z', event: 'mcp_tool_call', level: 'INFO', principal: 'A:9f1c4e20', tool: 'get_forecast', args: { latitude: 59.33, longitude: 18.07 }, decision: { outcome: 'allow', rule: null } },
    { datetime: '2026-09-17T08:12:41.310Z', event: 'authz_fail', level: 'CRITICAL', principal: 'B:5d77a912', tool: 'get_forecast', args: {}, decision: { outcome: 'deny', rule: 'idp-binding' } },
    { datetime: '2026-09-17T08:12:43.118Z', event: 'malicious_extraneous', level: 'CRITICAL', principal: 'A:9f1c4e20', tool: 'get_current_weather', args: { latitude: 59.33, longitude: 18.07, callback: `[redacted:sha256:${Guide.sha256(secret).slice(0, 8)}]` }, decision: { outcome: 'deny', rule: 'arguments' } },
    { datetime: '2026-09-17T08:12:47.902Z', event: 'mcp_tool_call', level: 'INFO', principal: 'A:9f1c4e20', tool: 'get_current_weather', args: { latitude: 59.33, longitude: 18.07 }, decision: { outcome: 'allow', rule: null } },
  ];
  const chainOf = (bodies) => {
    let prev = GENESIS;
    return bodies.map((b) => {
      const hash = Guide.sha256(prev + Guide.canon(b));
      const rec = { body: b, prev, hash };
      prev = hash;
      return rec;
    });
  };
  const verify = (recs) => {
    let prev = GENESIS;
    for (let i = 0; i < recs.length; i++) {
      const r = recs[i];
      if (r.prev !== prev) return { bad: i, why: `prev_hash does not match line ${i}` };
      if (Guide.sha256(prev + Guide.canon(r.body)) !== r.hash) return { bad: i, why: 'content does not match its hash' };
      prev = r.hash;
    }
    return { bad: -1, head: prev };
  };
  const short = (h) => `${h.slice(0, 8)}…${h.slice(-4)}`;

  Guide.add({
    id: 'audit',
    part: 'Guarantees',
    title: 'A decision becomes an audit record',
    lede: 'Every allow and every deny becomes one record, redacted and chained to the one before it. The hashes below are real SHA-256 values computed in your browser — tamper with the log and watch verification catch it.',
    takeaway: 'The chain makes an edited or deleted record detectable, and shipping each record to the SIEM as it is written catches even a full rewrite. The token itself never appears — only a hash of its id.',
    refs: 'Roadmap §3.5, P3.1–P3.3, LOG-01, LOG-02 · Flowcharts §15',
    render(body) {
      const html = `<div class="inset"><div class="fig-scroll"><div class="chain" data-chain></div></div>
        <div class="cols two" style="margin-top:12px">
          <pre class="console" data-verify></pre>
          <div class="panel"><h3 style="margin-bottom:6px">Redaction, line 3</h3>
            <p style="margin:0 0 6px;font-size:14px">The caller smuggled an argument whose value was a bearer token. It is recorded only as a short hash:</p>
            <p class="mono" style="margin:0;font-size:12.5px">callback: <s>${esc(secret.slice(0, 28))}…</s><br>callback: ${esc(`[redacted:sha256:${Guide.sha256(secret).slice(0, 8)}]`)}</p></div>
        </div></div>`;
      const st = Guide.stage({
        svg: html,
        bar: `<span class="lbl">Try</span>
          <button type="button" class="btn" data-t="edit">Change line 2 from deny to allow</button>
          <button type="button" class="btn" data-t="del">Delete line 3</button>
          <button type="button" class="btn" data-t="rewrite">Edit, then recompute every hash</button>
          <span class="sp"></span><button type="button" class="btn" data-t="reset">Reset</button>`,
      });
      body.appendChild(st);
      const narr = st.querySelector('.narr');
      let recs, siemHead;
      const draw = (say) => {
        const v = verify(recs);
        st.querySelector('[data-chain]').innerHTML = recs.map((r, i) => {
          const b = r.body, bad = v.bad === i, deny = b.decision.outcome === 'deny';
          return `<article class="rec${bad ? ' bad' : ''}">
            <div class="rec-h"><span class="mono">line ${i + 1}</span><span class="ev ${deny ? 'deny' : 'pass'}">${esc(b.event)}</span></div>
            <dl class="kv small"><dt>principal</dt><dd class="mono">${esc(b.principal)}</dd><dt>tool</dt><dd class="mono">${esc(b.tool)}</dd>
              <dt>decision</dt><dd>${esc(b.decision.outcome)}${b.decision.rule ? ` · <code>${esc(b.decision.rule)}</code>` : ''}</dd></dl>
            <p class="hashes">prev ${short(r.prev)}<br>hash ${short(r.hash)}</p>
            <p class="verdict">${v.bad === -1 || i < v.bad ? '✓ verifies' : bad ? '✗ chain breaks here' : '— after the break'}</p>
          </article>`;
        }).join('');
        const lines = [`$ mcp-server --verify-audit logs/audit-2026-09-17.jsonl`];
        if (v.bad >= 0) lines.push(`✗ chain broken at line ${v.bad + 1}: ${v.why}`);
        else {
          lines.push(`✓ chain intact · ${recs.length} records · head ${short(v.head)}`);
          lines.push(v.head === siemHead ? `✓ head matches the copy shipped to the SIEM` : `✗ head differs from the SIEM copy ${short(siemHead)} — the log was rewritten`);
        }
        st.querySelector('[data-verify]').textContent = lines.join('\n');
        narr.innerHTML = `<div class="step">Verification</div><p>${say}</p>`;
      };
      const reset = () => { recs = chainOf(baseRecords()); siemHead = recs[recs.length - 1].hash; };
      st.querySelector('.stage-bar').addEventListener('click', (e) => {
        const t = e.target.closest('button')?.dataset.t;
        if (!t) return;
        reset();
        if (t === 'edit') {
          recs[1].body = { ...recs[1].body, decision: { outcome: 'allow', rule: null } };
          draw('Someone flipped line 2 to “allow” to hide a refusal. Its stored hash no longer matches its content, so verification stops at line 2.');
        } else if (t === 'del') {
          recs.splice(2, 1);
          draw('Line 3 is gone. The record that followed it still points at the deleted record’s hash, so the break shows up where the gap is.');
        } else if (t === 'rewrite') {
          const bodies = recs.map((r) => r.body);
          bodies[1] = { ...bodies[1], decision: { outcome: 'allow', rule: null } };
          recs = chainOf(bodies);
          draw('A careful attacker edited line 2 and recomputed every hash after it. The file now verifies on its own — but its head no longer matches the copy already shipped to the SIEM, which is why records leave the machine as they are written.');
        } else draw('Four real records, each hashed over the previous hash plus its own canonical JSON. Everything verifies.');
      });
      reset();
      draw('Four real records, each hashed over the previous hash plus its own canonical JSON. Everything verifies.');
    },
  });
})();
