/* Chapters 3–5: token anatomy, issuer routing, provider binding and tool visibility. */
(function () {
  const { S, P, esc } = Guide;

  /* ---------- 3. Anatomy of a token ---------- */
  const issuers = {
    A: {
      label: 'Provider A · scp / azp',
      header: { alg: 'RS256', kid: 'a1-2026-09' },
      payload: {
        iss: 'https://login.a.example/tenant/v2.0', aud: 'https://mcp.example.com/mcp', sub: '9f1c4e20-77b1',
        azp: 'ide-vscode', scp: 'weather:read observations:read', jti: 'b41d0c6e-2f9a', iat: 1789550000, exp: 1789550300,
      },
      map: { scp: 'scopes', azp: 'client' },
      principal: { idp: 'A', sub: '9f1c4e20-77b1', client_id: 'ide-vscode', jti_sha256: '3a1f…', scopes: ['weather:read', 'observations:read'] },
    },
    B: {
      label: 'Provider B · scope / client_id',
      header: { alg: 'PS256', kid: 'b-rs-7' },
      payload: {
        iss: 'https://id.b.example/realms/partners', aud: 'https://mcp.example.com/mcp', sub: '9f1c4e20-77b1',
        client_id: 'partner-portal', scope: 'demo:read demo:write', jti: '6c02e9d1-80b4', iat: 1789550120, exp: 1789550420,
      },
      map: { scope: 'scopes', client_id: 'client' },
      principal: { idp: 'B', sub: '9f1c4e20-77b1', client_id: 'partner-portal', jti_sha256: 'c90e…', scopes: ['demo:read', 'demo:write'] },
    },
  };
  const claimInfo = {
    alg: ['The signature algorithm.', 'The edge, through the issuer’s own scheme. Allowlist: RS256, PS256, ES256.', '<code>none</code> or an HMAC algorithm such as <code>HS256</code> → 401 <span class="ev deny">authn_login_fail</span>. An HMAC token can be forged by anyone holding the shared secret; an asymmetric one cannot.', 'Not recorded.'],
    kid: ['Which of the issuer’s published keys signed the token.', 'The edge, looked up in that issuer’s key set only — never another issuer’s.', 'An unknown key id → one refresh of the key set, then 401.', 'Not recorded.'],
    iss: ['Who issued the token.', 'Read <em>unverified</em> to choose a scheme (next chapter), then pinned and verified by that scheme.', 'An issuer that is not configured → 401 before any key fetch.', '<code>principal.idp</code>'],
    aud: ['Which resource the token is for. Must equal this server’s resource URI.', 'The edge. Every issuer’s scheme expects the same resource URI.', 'A token minted for another server → 401 <span class="ev deny">authn_login_fail</span>. This is the check that stops tokens being replayed across servers.', 'Not recorded; the mismatch is.'],
    sub: ['The caller’s identity, as its issuer knows it.', 'Everything downstream, always paired with the issuer as <code>idp:sub</code> — limits, strikes, the denylist, audit.', 'Missing → 401. The same <code>sub</code> at two issuers is two different principals.', '<code>principal.sub</code>'],
    client: ['Which client application is acting for the user.', 'Normalised from this issuer’s claim name into <code>client_id</code>. Required.', 'Missing → 401.', '<code>principal.client_id</code>'],
    scopes: ['What the caller may do, as a space-separated list.', 'Policy, against the scope catalog of the provider’s bound issuer; also filters <code>tools/list</code>.', 'Missing scope → the tool is hidden; a direct call → <span class="ev deny">authz_fail</span> with rule <code>scope</code>.', '<code>principal.scopes</code>'],
    jti: ['This token’s unique id.', 'The edge. Required; checked against the denylist key <code>deny:idp:jti</code>.', 'Missing → 401. On the denylist → 401 <span class="ev deny">authn_token_reuse</span>.', 'Only as <code>jti_sha256</code>. The raw id is never logged.'],
    iat: ['When the token was issued.', 'The risk gate. Write tools need <code>iat</code> within 5 minutes, or an introspection result under 60 seconds old.', 'Too old for a Write tool → denied, step-up required.', 'Not recorded.'],
    exp: ['When the token stops working.', 'The edge, with 30 seconds of clock skew.', 'Expired → 401.', 'Its lifetime is the revocation delay for Read tools.'],
  };

  Guide.add({
    id: 'token',
    part: 'Identity',
    title: 'Anatomy of a token',
    lede: 'Every decision the server makes starts from a handful of claims in a signed token. Pick a claim to see who reads it, what happens when it is wrong, and where it ends up.',
    takeaway: 'A token is only trusted after its issuer, signature, audience and expiry all check out — and then only through a normalised principal, never the raw claims.',
    refs: 'Roadmap §3.8, P1.1 · Flowcharts §4',
    render(body) {
      const figure = `<div class="inset">
        <div class="tok" data-tok></div>
        <div class="cols two" style="margin-top:12px">
          <div class="panel"><div class="claims" data-claims></div></div>
          <div class="panel" data-detail></div>
        </div>
      </div>`;
      const st = Guide.stage({ svg: figure, caption: 'Blue is the header, teal the payload, green the signature. Claim names differ between issuers; the principal on the right of the narration never does.' });
      st.querySelector('.fig').classList.add('html-fig');
      body.appendChild(st);
      const bar = st.querySelector('.stage-bar'), narr = st.querySelector('.narr');
      bar.innerHTML = `<span class="lbl">Issued by</span>` +
        Object.entries(issuers).map(([k, v]) => `<button type="button" class="chip" data-iss="${k}" aria-pressed="false">${esc(v.label)}</button>`).join('');
      let iss = 'A', claim = 'aud';
      const b64 = (o) => btoa(JSON.stringify(o)).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
      const draw = () => {
        const d = issuers[iss];
        bar.querySelectorAll('[data-iss]').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.iss === iss)));
        st.querySelector('[data-tok]').innerHTML =
          `<span class="h">${b64(d.header).slice(0, 36)}…</span>.<span class="p">${b64(d.payload).slice(0, 64)}…</span>.<span class="s">Kx9r…Qw2</span>`;
        const row = (name, val, key) =>
          `<button type="button" class="claim" data-c="${key}" aria-pressed="${key === claim}"><span class="ck">"${name}"</span>: ${esc(JSON.stringify(val))}</button>`;
        const keyOf = (n) => d.map[n] || n;
        st.querySelector('[data-claims]').innerHTML =
          `<p class="grp">Header</p>` + Object.entries(d.header).map(([n, v]) => row(n, v, n)).join('') +
          `<p class="grp">Payload</p>` + Object.entries(d.payload).map(([n, v]) => row(n, v, keyOf(n))).join('');
        const name = Object.keys(d.payload).find((n) => keyOf(n) === claim) || claim;
        const [says, who, wrong, where] = claimInfo[claim];
        st.querySelector('[data-detail]').innerHTML = `<h3 class="mono" style="margin-bottom:10px">${esc(name)}</h3>
          <dl class="kv"><dt>What it says</dt><dd>${says}</dd><dt>Who checks it</dt><dd>${who}</dd><dt>If it is wrong</dt><dd>${wrong}</dd><dt>Where it ends up</dt><dd>${where}</dd></dl>`;
        const mapped = Object.entries(d.map).map(([from, to]) => `<code>${from}</code> → <code>${to === 'client' ? 'client_id' : to}</code>`).join(' · ');
        narr.innerHTML = `<div class="step">Normalised principal · ${mapped}</div><p class="mono">${esc(JSON.stringify(d.principal))}</p>`;
      };
      st.addEventListener('click', (e) => {
        const b = e.target.closest('button');
        if (!b) return;
        if (b.dataset.iss) iss = b.dataset.iss;
        else if (b.dataset.c) claim = b.dataset.c;
        else return;
        draw();
      });
      draw();
    },
  });

  /* ---------- 4. Routing a token to its identity provider ---------- */
  Guide.add({
    id: 'routing',
    part: 'Identity',
    title: 'Routing a token to its issuer',
    lede: 'Several identity providers can issue tokens, each with its own keys and claim names. The subtle part is at the top: the issuer is read without being trusted, only to decide who validates everything else.',
    takeaway: 'Reading <code>iss</code> before verifying it only selects a scheme — it grants nothing. Each token is then fully validated by the one issuer it names.',
    refs: 'Roadmap §3.8, P1.10 · Flowcharts §4',
    render(body) {
      const svg = S.svg(800, 526, 'Token routing: size check, read issuer unverified, check the issuer is configured, forward to that issuer’s scheme, verify signature, audience, expiry and algorithm, then normalise the claims into a principal.', [
        S.node('T', 20, 40, 120, 44, ['Bearer token'], 'untr'),
        S.node('S1', 170, 40, 120, 44, ['Under 8 KB?'], 'gate'),
        S.node('R1', 170, 122, 120, 44, ['401', 'too large'], 'deny'),
        S.node('S2', 320, 40, 140, 44, ['Read iss', 'without trusting it']),
        S.node('S3', 490, 40, 130, 44, ['Issuer', 'configured?'], 'gate'),
        S.node('R2', 650, 40, 130, 44, ['401', 'no key fetch made'], 'deny'),
        S.node('SA', 400, 140, 120, 44, ['Scheme A', 'keys of A'], 'ctrl'),
        S.node('SB', 580, 140, 120, 44, ['Scheme B', 'keys of B'], 'ctrl'),
        S.node('V1', 430, 240, 240, 44, ['Signature verifies with', 'that issuer’s keys?'], 'gate'),
        S.node('R3', 120, 240, 250, 44, ['401', 'named A, signed by B'], 'deny'),
        S.node('V2', 430, 320, 240, 44, ['Audience, expiry and', 'algorithm all valid?'], 'gate'),
        S.node('R4', 120, 320, 250, 44, ['401', '`authn_login_fail'], 'deny'),
        S.node('NORM', 430, 400, 240, 44, ['Normalise claims with', 'that issuer’s mapping']),
        S.node('OUT', 430, 474, 240, 40, ['`idp · sub · client_id · jti · scopes'], 'pass terminal'),
        S.edge('T>S1', P.h(140, 62, 170)),
        S.edge('S1>R1', P.v(230, 84, 122), { deny: true, label: 'no', lx: 236, ly: 108, anchor: 'start' }),
        S.edge('S1>S2', P.h(290, 62, 320)),
        S.edge('S2>S3', P.h(460, 62, 490)),
        S.edge('S3>R2', P.h(620, 62, 650), { deny: true, label: 'no', lx: 635, ly: 56 }),
        S.edge('S3>SA', 'M555 84V112H460V140', { label: 'iss = A', lx: 505, ly: 106 }),
        S.edge('S3>SB', 'M555 84V112H640V140', { label: 'iss = B', lx: 602, ly: 106 }),
        S.edge('SA>V1', P.v(460, 184, 240)),
        S.edge('SB>V1', P.v(640, 184, 240)),
        S.edge('V1>R3', P.h(430, 262, 370), { deny: true, label: 'no', lx: 400, ly: 256 }),
        S.edge('V1>V2', P.v(550, 284, 320)),
        S.edge('V2>R4', P.h(430, 342, 370), { deny: true, label: 'no', lx: 400, ly: 336 }),
        S.edge('V2>NORM', P.v(550, 364, 400)),
        S.edge('NORM>OUT', P.v(550, 444, 474)),
      ].join(''));
      const st = Guide.stage({ svg });
      body.appendChild(st);
      Guide.scenarios(st, {
        lead: 'Token',
        say: {
          T: 'A bearer token arrives. Nothing in it is trusted yet.',
          S1: 'Tokens over 8 KB are refused before any parsing — a cheap guard against parser abuse.',
          S2: 'The <code>iss</code> claim is read without checking the signature. It is used for exactly one thing: choosing which scheme validates the token.',
          S3: 'Only issuers listed in configuration have a scheme. Anything else stops here, before the server requests any signing keys.',
          SA: 'Scheme A holds provider A’s authority, keys, allowed algorithms and claim names.',
          SB: 'Scheme B is independent of A: its own keys, algorithms and claim names.',
          V1: 'Full validation starts here. The signature must verify against the keys of the issuer the token named.',
          V2: 'The audience must be this server’s resource URI, the token unexpired, and the algorithm on the allowlist.',
          NORM: 'Claims are mapped with that issuer’s names — <code>scp</code> or <code>scope</code>, <code>azp</code> or <code>client_id</code> — into one shape.',
          OUT: 'A normalised principal. From here on, everything is keyed on <code>idp:sub</code>.',
        },
        list: [
          { label: 'Valid token from A', path: ['T', 'S1', 'S2', 'S3', 'SA', 'V1', 'V2', 'NORM', 'OUT'], end: 'Accepted. The principal carries <code>idp = A</code>, and limits, strikes and audit all use <code>A:sub</code>. <span class="ev pass">authenticated</span>' },
          { label: 'Valid token from B', path: ['T', 'S1', 'S2', 'S3', 'SB', 'V1', 'V2', 'NORM', 'OUT'], end: 'Accepted through scheme B. The same <code>sub</code> value issued by A would be a different principal. <span class="ev pass">authenticated</span>' },
          { label: 'Unknown issuer', path: ['T', 'S1', 'S2', 'S3', 'R2'], end: 'Refused with 401 before any key lookup, so an invented issuer URL cannot make the server reach out to it. <span class="ev deny">401</span>' },
          { label: 'Names A, signed by B', path: ['T', 'S1', 'S2', 'S3', 'SA', 'V1', 'R3'], end: 'Refused. Reading <code>iss</code> unverified is safe precisely because scheme A trusts only A’s keys — a token signed by B dies here. <span class="ev deny">authn_login_fail</span>' },
          { label: 'Minted for another server', path: ['T', 'S1', 'S2', 'S3', 'SA', 'V1', 'V2', 'R4'], end: 'Refused. The signature is genuine, but the audience is another resource. Accepting it would let one server’s tokens be replayed against this one. <span class="ev deny">authn_login_fail</span>' },
          { label: 'Oversized token', path: ['T', 'S1', 'R1'], end: 'Refused before parsing. <span class="ev deny">401</span>' },
        ],
      });
    },
  });

  /* ---------- 5. Provider binding and tool visibility ---------- */
  const binding = { Smhi: 'A', SmhiObs: 'A', Demo: 'B' };
  const provY = { Smhi: 50, SmhiObs: 160, Demo: 300 };
  const tools = [
    ['get_forecast', 'Smhi', 'weather:read'], ['get_current_weather', 'Smhi', 'weather:read'], ['get_forecast_model_info', 'Smhi', 'weather:read'],
    ['get_recent_temperature', 'SmhiObs', 'observations:read'], ['get_temperature_history', 'SmhiObs', 'observations:read'], ['get_precipitation_history', 'SmhiObs', 'observations:read'],
    ['get_blog_post', 'Demo', 'demo:read'], ['get_post_comments', 'Demo', 'demo:read'], ['get_user_todos', 'Demo', 'demo:read'],
    ['create_blog_post', 'Demo', 'demo:write'], ['add_post_comment', 'Demo', 'demo:write'], ['create_user_todo', 'Demo', 'demo:write'],
  ].map(([name, prov, scope], i) => ({ k: 't' + i, name, prov, scope, y: 20 + i * 30 + (i >= 3 ? 20 : 0) + (i >= 6 ? 30 : 0) }));
  const callers = [
    { label: 'Staff at A · weather:read', idp: 'A', scopes: ['weather:read'], say: 'Only the forecast tools are listed. The observation tools share the same issuer but need a scope this token lacks; the demo tools belong to another issuer entirely.' },
    { label: 'Staff at A · weather + observations', idp: 'A', scopes: ['weather:read', 'observations:read'], say: 'Both providers bound to A are visible. Nothing bound to B appears, whatever scopes the token carries.' },
    { label: 'Partner at B · demo:read', idp: 'B', scopes: ['demo:read'], say: 'Only the read-only demo tools. The write tools need <code>demo:write</code>, and nothing bound to A is reachable from B.' },
    { label: 'Partner at B · demo read + write', idp: 'B', scopes: ['demo:read', 'demo:write'], say: 'All six demo tools. The write tools are Write-class, so each call also has to pass the freshness gate in chapter 7.' },
    { label: 'Admin at A · mcp:admin only', idp: 'A', scopes: ['mcp:admin'], say: 'No tools at all. The admin scope opens the administrative endpoints — suspend, reinstate, list strikes — not provider tools. It is honoured only from the one issuer configured as the admin issuer.' },
    { label: 'Partner at B calls get_forecast anyway', idp: 'B', scopes: ['demo:read'], refuse: 't0', say: 'The tool was never listed, but the partner guessed its name and called it. Policy checks the binding on every call: <span class="ev deny">authz_fail</span> with rule <code>idp-binding</code>, and a 3-point strike.' },
  ];

  Guide.add({
    id: 'binding',
    part: 'Identity',
    title: 'Who sees which tools',
    lede: 'Each provider is bound to exactly one identity provider — never none, never several. Pick a caller to see which tools it can list, and why the others are hidden.',
    takeaway: 'Hiding a tool is a courtesy; the refusal is the control. Policy checks the same binding on every call, so guessing a tool name gains nothing.',
    refs: 'Roadmap §3.8, I9, GOAL-06 · Flowcharts §5',
    render(body) {
      const parts = [
        S.node('A', 20, 70, 130, 52, ['Identity provider A', 'staff'], 'ctrl'),
        S.node('B', 20, 300, 130, 52, ['Identity provider B', 'partners'], 'ctrl'),
      ];
      Object.entries(provY).forEach(([p, y]) => {
        parts.push(S.node(p, 200, y, 140, 44, [p, `bound to ${binding[p]}`]));
        const from = binding[p] === 'A' ? 96 : 326;
        parts.push(S.edge(`${binding[p]}>${p}`, `M150 ${from}H175V${y + 22}H200`));
      });
      tools.forEach((t) => {
        parts.push(S.node(t.k, 400, t.y, 220, 26, ['`' + t.name]));
        parts.push(S.edge(`${t.prov}>${t.k}`, `M340 ${provY[t.prov] + 22}H370V${t.y + 13}H400`));
        parts.push(`<text class="elabel m" x="632" y="${t.y + 17}">${esc(t.scope)}</text>`);
        parts.push(`<text class="elabel st" data-status="${t.k}" x="750" y="${t.y + 17}"></text>`);
      });
      const svg = S.svg(900, 430, 'Identity provider A is bound to the Smhi and SmhiObs providers; identity provider B is bound to the Demo provider. A caller sees only tools of providers bound to its issuer and covered by its scopes.', parts.join(''));
      const st = Guide.stage({ svg, caption: 'Each tool shows the scope it requires; the right-hand column shows whether the selected caller can see it, and why not.' });
      body.appendChild(st);
      const bar = st.querySelector('.stage-bar'), fig = st.querySelector('.fig'), narr = st.querySelector('.narr');
      bar.innerHTML = `<span class="lbl">Caller</span>` + callers.map((c, i) => `<button type="button" class="chip" data-i="${i}" aria-pressed="false">${esc(c.label)}</button>`).join('');
      const pick = (i) => {
        const c = callers[i];
        const lit = [c.idp];
        tools.forEach((t) => {
          const el = fig.querySelector(`[data-status="${t.k}"]`);
          let text, cls;
          if (c.refuse === t.k) { text = 'refused · idp-binding'; cls = 'st-ref'; lit.push(t.k); }
          else if (binding[t.prov] !== c.idp) { text = `hidden · bound to ${binding[t.prov]}`; cls = 'st-hid'; }
          else if (!c.scopes.includes(t.scope)) { text = 'hidden · missing scope'; cls = 'st-hid'; }
          else { text = 'visible'; cls = 'st-vis'; lit.push(t.k, t.prov, `${t.prov}>${t.k}`, `${c.idp}>${t.prov}`); }
          el.textContent = text;
          el.setAttribute('class', `elabel st ${cls}`);
        });
        Guide.light(fig, lit);
        bar.querySelectorAll('.chip').forEach((b) => b.setAttribute('aria-pressed', String(+b.dataset.i === i)));
        const n = tools.filter((t) => binding[t.prov] === c.idp && c.scopes.includes(t.scope)).length;
        narr.innerHTML = `<div class="step">${esc(c.label)} · ${n} of ${tools.length} tools listed</div><p>${c.say}</p>`;
      };
      bar.addEventListener('click', (e) => { const b = e.target.closest('.chip'); if (b) pick(+b.dataset.i); });
      pick(0);
    },
  });
})();
