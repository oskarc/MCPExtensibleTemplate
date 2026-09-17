/* Chapter 17: the eight phases — dependency map, schedule to scale, and what each phase delivers. */
(function () {
  const { S, esc } = Guide;

  const phases = [
    { k: 'P0', name: 'Foundation', days: [3, 5], deps: [], unlocks: ['startup'],
      goal: 'A base that builds, tests and starts correctly in every mode, so nothing later is built on the defects found in review.',
      work: ['Solution file, analyzers and CI with locked restore, package audit and secret scanning', 'Retarget to .NET 10 and upgrade the MCP SDK from 1.2.0 to 2.2.0', 'Split hosting: stdio runs without Kestrel, and only in Development', 'Exit codes 70 and 78 instead of 0 on failure; health endpoints instead of the startup probe', 'Provider fixes: JSON casing, the CSV-only climate endpoint, month-then-cap, composable timeouts', 'Secrets out of appsettings'],
      exit: ['CI is green on main', 'A stdio test finds no listening TCP socket', 'A camelCase round-trip test returns a real post id and title', 'The fatal path exits non-zero', 'Every review defect has a regression test, or its code is gone'] },
    { k: 'P1', name: 'Identity', days: [5, 8], deps: ['P0'], unlocks: ['boundaries', 'token', 'routing', 'binding'],
      goal: 'Every HTTP request carries a verified principal from a configured issuer, and each provider is bound to exactly one issuer.',
      work: ['One JWT scheme per issuer, with issuer routing in front', 'Protected Resource Metadata listing every issuer', 'Authorization filters and class-level [Authorize] on every tool', 'Stateless streamable HTTP; host allowlist and Origin guard', 'TLS or a trusted proxy required in Production', 'API-key middleware deleted; per-issuer scope catalogs'],
      exit: ['No token → 401 with a resource_metadata challenge', 'Wrong audience, wrong issuer, expired, alg none or HS256, missing jti → 401', 'Unregistered issuer → 401 with no key request made', 'A token signed by B but naming A → 401', 'A token from B cannot list or call a provider bound to A', 'A provider with no binding → exit 78'] },
    { k: 'P2', name: 'Policy and egress', days: [8, 12], deps: ['P1'], unlocks: ['refusal', 'risk', 'confirm', 'egress', 'limits'],
      goal: 'Each provider operates inside a declared allowance that the framework enforces on the way in and on the way out.',
      work: ['ProviderPolicy, provider modules and the enabled-provider allowlist', 'Startup policy validation', 'Filter chain: audit, then policy, then output guard', 'Ordered policy checks and the confirmation round-trip', 'Egress guard, vetted connections, no redirects, bounded reads', 'Per-provider resilience and budgets; banned-API analyzer; provider isolation'],
      exit: ['Each misconfiguration refuses startup', 'A hidden tool called directly is refused', 'An extra argument never reaches the upstream', 'Every egress abuse in chapter 9 is refused', 'A stale token cannot call a Write tool', 'new HttpClient() in a provider fails the build'] },
    { k: 'P3', name: 'Audit', days: [4, 6], deps: ['P2'], unlocks: ['audit', 'failure'],
      goal: 'Every decision is recorded in a form a SIEM can alert on and an investigator can trust.',
      work: ['Audit events with two sinks: hash-chained JSONL and OTLP', 'A verify-audit command', 'Redaction of sensitive arguments, secret patterns and long values', 'W3C trace context on every event', 'Diagnostic logs stripped of arguments and tokens', 'Fail closed when the sink is down; SIEM alert rules in the repository'],
      exit: ['Every policy rule produces exactly one event with the right name and level', 'The verifier reports the exact line after one byte is flipped', 'Redaction holds for a secret, a JWT-shaped string and a 10 KB value', 'A sink outage denies Write tools'] },
    { k: 'P4', name: 'Guardrails', days: [6, 10], deps: ['P1', 'P3'], unlocks: ['strikes', 'injection', 'revocation', 'failure'],
      goal: 'A caller that breaks the rules loses access automatically, on every instance, and at its own issuer — without punishing users for content an upstream returned.',
      work: ['Strike engine with the throttle, suspend, review and revoke ladder', 'Redis denylist with no cache in front of it', 'Grant revocation adapter per issuer (plus 1–3 days each)', 'Freshness gates for Write and Irreversible tools', 'Injection-versus-malice rule; admin endpoints', 'A two-instance revocation drill that measures the real delay'],
      exit: ['A scripted attacker is throttled at 3 strikes, suspended at 6, escalated at 10', 'A suspension holds on both instances on the next request', 'A revoked principal’s token logs authn_token_reuse', 'A revoker outage leaves the suspension in force', 'Injected upstream text earns the caller zero strikes', 'Suspending a sub at A leaves the same sub at B untouched'] },
    { k: 'P5', name: 'Tool integrity', days: [2, 4], deps: ['P2'], unlocks: ['startup'],
      goal: 'What the server advertises is what was reviewed, and what it runs is what was built.',
      work: ['tools.lock.json with a required security reviewer', 'Clients are served the pinned definitions', 'Central package management and locked restore', 'Package audit findings as errors; SBOM on every build', 'Image scanning, digest-pinned base images, signed images'],
      exit: ['One changed character in a description refuses startup', 'CI fails on a vulnerable package, lock drift or a seeded secret', 'The SBOM lists every runtime dependency'] },
    { k: 'P6', name: 'Deployment', days: [3, 5], deps: ['P1', 'P2'], unlocks: ['boundaries', 'egress'],
      goal: 'The process runs with the fewest privileges that work, and the network enforces the same egress allowance the application does.',
      work: ['Chiseled, non-root image with a read-only file system', 'Network egress policy generated from the provider policies', 'Secrets mounted from a vault', 'TLS at the ingress with trusted proxies; HSTS', 'Probes, limits and graceful drain; runbooks'],
      exit: ['The container runs as non-root and cannot write to /', 'With the application guard disabled, a non-allowlisted host still fails at the network', 'No secret values appear in the image environment'] },
    { k: 'P7', name: 'Verification', days: [5, 8], deps: ['P4', 'P5', 'P6'], unlocks: [],
      goal: 'Compliance is demonstrated rather than asserted, and stays demonstrated as the code changes.',
      work: ['Requirement traits on every security test; COMPLIANCE.md generated from results', 'A negative test per requirement; property and chaos tests', 'STRIDE threat model reviewed with security', 'External penetration test before go-live', 'The revocation drill runs nightly'],
      exit: ['Every requirement has a passing test or a named owner', 'High and critical penetration-test findings are closed', 'COMPLIANCE.md is generated, current and linked'] },
  ];
  const byK = Object.fromEntries(phases.map((p) => [p.k, p]));
  const critical = new Set(['P0', 'P1', 'P2', 'P3', 'P4', 'P7']);
  const mid = (p) => (p.days[0] + p.days[1]) / 2;
  const plan = { P0: [0, 1], P1: [4, 1], P2: [10.5, 1], P3: [20.5, 1], P4: [25.5, 1], P7: [33.5, 1], P5: [20.5, 2], P6: [23.5, 2] };

  Guide.add({
    id: 'phases',
    part: 'Delivery',
    title: 'The eight phases',
    lede: 'Eight phases turn the template into the server this guide describes. The map shows what must finish first; pick a phase to see what it delivers, which chapters it makes real, and how you know it is done.',
    takeaway: 'Identity comes before policy because policy is keyed on the principal; audit comes before guardrails because guardrails consume audit events. Phases 5 and 6 need only Phase 2, so a second person can take them in parallel.',
    refs: 'Roadmap §4 · Flowcharts §16 · One engineer: 36–58 working days. With a second engineer on phases 5 and 6, the critical path is 31–49.',
    render(body) {
      const pos = { P0: [16, 80, 100, 54], P1: [146, 80, 100, 54], P2: [276, 80, 116, 54], P3: [424, 80, 100, 54], P4: [556, 80, 116, 54], P7: [714, 80, 110, 54], P5: [424, 170, 100, 44], P6: [424, 230, 100, 44] };
      const dag = S.svg(860, 300, 'Phase dependencies: 0, 1, 2, 3, 4 and 7 form the critical path; phases 5 and 6 depend on phase 2 (and 6 also on 1) and run in parallel before 7.', [
        S.text(16, 22, 'An arrow means “must finish first”. Amber is the critical path; green can run in parallel.', 'elabel', 'start'),
        ...phases.map((p) => { const [x, y, w, h] = pos[p.k]; return S.node(p.k, x, y, w, h, [`Phase ${p.k.slice(1)}`, p.name], critical.has(p.k) ? 'gate' : 'pass'); }),
        S.edge('P0>P1', 'M116 107H146'), S.edge('P1>P2', 'M246 107H276'), S.edge('P2>P3', 'M392 107H424'),
        S.edge('P3>P4', 'M524 107H556'), S.edge('P4>P7', 'M672 107H714'),
        S.edge('P1>P4', 'M196 80V44H614V80'),
        S.edge('P2>P5', 'M334 134V192H424'), S.edge('P2>P6', 'M334 134V252H424'),
        S.edge('P1>P6', 'M196 134V286H474V274'),
        S.edge('P5>P7', 'M524 192H769V134'), S.edge('P6>P7', 'M524 252H769V134'),
      ].join(''));

      const x0 = 200, pw = 630, sx = (d) => x0 + (d / 42) * pw;
      const laneY = { 1: 44, 2: 110 };
      let g = S.text(x0 - 12, laneY[1] + 17, 'Engineer 1 · critical path', 'elabel rowlbl', 'end') + S.text(x0 - 12, laneY[2] + 17, 'Engineer 2 · in parallel', 'elabel rowlbl', 'end');
      phases.forEach((p) => {
        const [start, lane] = plan[p.k], y = laneY[lane];
        const bar = `<rect class="gantt ${critical.has(p.k) ? 'crit' : 'par'}" x="${sx(start).toFixed(1)}" y="${y}" width="${(sx(start + mid(p)) - sx(start)).toFixed(1)}" height="26" rx="3"/>` +
          `<text class="gantt-t" x="${sx(start + mid(p) / 2).toFixed(1)}" y="${y + 17}" text-anchor="middle">P${p.k.slice(1)}</text>` +
          `<path class="whisker" d="M${sx(start + p.days[0]).toFixed(1)} ${y + 34}H${sx(start + p.days[1]).toFixed(1)}M${sx(start + p.days[0]).toFixed(1)} ${y + 30}v8M${sx(start + p.days[1]).toFixed(1)} ${y + 30}v8"/>`;
        g += `<g data-k="${p.k}">${bar}</g>`;
      });
      g += `<path class="axis" d="M${x0} 172H${x0 + pw}"/>`;
      for (let d = 0; d <= 40; d += 5) g += `<path class="axis" d="M${sx(d)} 172v5"/>` + S.text(sx(d), 191, `${d}`, 'elabel', 'middle');
      g += S.text(x0 + pw, 208, 'working days', 'elabel', 'end');
      const gantt = S.svg(860, 214, 'Schedule to scale using the midpoint of each estimate: the critical path finishes around day 40; phases 5 and 6 run in parallel after phase 2.', g);

      const st = Guide.stage({
        svg: dag + '<div style="height:6px"></div>' + gantt,
        caption: 'Schedule bars use the midpoint of each estimate; whiskers show each phase’s low–high duration measured from its start.',
      });
      body.appendChild(st);
      const bar = st.querySelector('.stage-bar'), fig = st.querySelector('.fig'), narr = st.querySelector('.narr');
      bar.innerHTML = `<span class="lbl">Phase</span>` + phases.map((p) => `<button type="button" class="chip" data-k="${p.k}" aria-pressed="false">${p.k.slice(1)} · ${esc(p.name)}</button>`).join('');
      const title = (id) => Guide.chapters.find((c) => c.id === id);
      const pick = (k) => {
        const p = byK[k];
        Guide.light(fig, [k, ...p.deps, ...p.deps.map((d) => `${d}>${k}`)]);
        bar.querySelectorAll('.chip').forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.k === k)));
        const unlocks = p.unlocks.length
          ? p.unlocks.map((id) => { const c = title(id); return c ? `<a href="#${id}">${String(c.n).padStart(2, '0')} ${esc(c.title)}</a>` : ''; }).join(' · ')
          : 'Evidence for every chapter';
        narr.innerHTML = `<div class="step">Phase ${k.slice(1)} · ${esc(p.name)} · ${p.days[0]}–${p.days[1]} days · ${p.deps.length ? 'after ' + p.deps.map((d) => 'Phase ' + d.slice(1)).join(' and ') : 'no dependencies'}</div>
          <p>${esc(p.goal)}</p>
          <p class="refs" style="margin:6px 0 10px">Makes real: ${unlocks}</p>
          <div class="cols two"><div><h3 style="margin-bottom:4px">Key work</h3><ul class="ticks">${p.work.map((w) => `<li>${esc(w)}</li>`).join('')}</ul></div>
          <div><h3 style="margin-bottom:4px">Done when</h3><ul class="ticks done">${p.exit.map((w) => `<li>${esc(w)}</li>`).join('')}</ul></div></div>`;
      };
      bar.addEventListener('click', (e) => { const b = e.target.closest('.chip'); if (b) pick(b.dataset.k); });
      pick('P2');
    },
  });
})();
