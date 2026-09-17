/* MCP Hardening Field Guide — framework. Chapters register with Guide.add(); Guide.boot() renders. */
(function () {
  'use strict';
  const Guide = (window.Guide = { chapters: [] });
  Guide.add = (ch) => Guide.chapters.push(ch);

  const esc = (Guide.esc = (s) =>
    String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]));

  Guide.h = (html) => {
    const t = document.createElement('template');
    t.innerHTML = html.trim();
    return t.content.firstElementChild;
  };

  /* ---------- SVG builders ---------- */
  const S = (Guide.S = {
    svg: (w, h, label, inner) =>
      `<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${esc(label)}">${inner}</svg>`,
    // lines prefixed with ` render in monospace
    node(k, x, y, w, h, lines, cls = '') {
      lines = [].concat(lines);
      const cx = x + w / 2, lh = 14.5;
      const y0 = y + h / 2 - ((lines.length - 1) * lh) / 2 + 4.2;
      const t = lines
        .map((l, i) => {
          const mono = l.startsWith('`');
          return `<tspan x="${cx}" y="${(y0 + i * lh).toFixed(1)}"${mono ? ' class="m"' : ''}>${esc(mono ? l.slice(1) : l)}</tspan>`;
        })
        .join('');
      return `<g class="nd ${cls}" data-k="${k}"><rect x="${x}" y="${y}" width="${w}" height="${h}" rx="5"/><text text-anchor="middle">${t}</text></g>`;
    },
    edge(k, d, o = {}) {
      const cls = 'edge' + (o.dash ? ' dash' : '') + (o.deny ? ' to-deny' : '');
      const start = o.both ? ' marker-start="url(#g-arr)"' : '';
      let s = `<g data-k="${k}"><path class="${cls}" d="${d}" marker-end="url(#g-arr)"${start}/>`;
      if (o.label)
        s += `<text class="elabel${o.mono ? ' m' : ''}" x="${o.lx}" y="${o.ly}" text-anchor="${o.anchor || 'middle'}">${esc(o.label)}</text>`;
      return s + '</g>';
    },
    zone: (x, y, w, h, label) =>
      `<g class="zone"><rect x="${x}" y="${y}" width="${w}" height="${h}" rx="6"/><text x="${x + 10}" y="${y + 17}">${esc(label)}</text></g>`,
    text: (x, y, str, cls = 'elabel', anchor = 'start') =>
      `<text class="${cls}" x="${x}" y="${y}" text-anchor="${anchor}">${esc(str)}</text>`,
  });
  Guide.P = {
    h: (x1, y, x2) => `M${x1} ${y}H${x2}`,
    v: (x, y1, y2) => `M${x} ${y1}V${y2}`,
    hv: (x1, y1, x2, y2) => `M${x1} ${y1}H${x2}V${y2}`,
    vh: (x1, y1, x2, y2) => `M${x1} ${y1}V${y2}H${x2}`,
    hvh: (x1, y1, xm, y2, x2) => `M${x1} ${y1}H${xm}V${y2}H${x2}`,
    vhv: (x1, y1, ym, x2, y2) => `M${x1} ${y1}V${ym}H${x2}V${y2}`,
  };

  /* ---------- stage: controls + figure + narration ---------- */
  Guide.stage = (o) =>
    Guide.h(`<div class="stage">
      ${o.bar === false ? '' : `<div class="stage-bar">${o.bar || ''}</div>`}
      <figure class="fig"><div class="fig-scroll">${o.svg}</div>${o.caption ? `<figcaption>${o.caption}</figcaption>` : ''}</figure>
      ${o.narr === false ? '' : `<div class="narr" aria-live="polite">${o.narr || ''}</div>`}
    </div>`);

  Guide.light = (fig, keys) => {
    const set = keys ? new Set(keys) : null;
    fig.classList.toggle('stepping', !!set);
    fig.querySelectorAll('[data-k]').forEach((el) => {
      const on = !!set && set.has(el.dataset.k);
      el.classList.toggle('is-on', on);
      el.classList.toggle('is-dim', !!set && !on);
    });
  };

  // ['A','B','C'] -> ['A','A>B','B','B>C','C']
  Guide.expand = (path) => path.flatMap((n, i) => (i ? [`${path[i - 1]}>${n}`, n] : [n]));

  const labelOf = (fig, k) => {
    const t = fig.querySelector(`[data-k="${CSS.escape(k)}"] text`);
    return t ? esc(t.textContent.replace(/\s+/g, ' ').trim()) : '';
  };
  const reduced = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  /* Scenario player: chips pick a path; it animates node by node, then shows the outcome. */
  Guide.scenarios = (root, { list, say = {}, lead = 'Scenario', onPick }) => {
    const bar = root.querySelector('.stage-bar');
    const fig = root.querySelector('.fig');
    const narr = root.querySelector('.narr');
    bar.innerHTML =
      `<span class="lbl">${esc(lead)}</span>` +
      list.map((s, i) => `<button type="button" class="chip" data-i="${i}" aria-pressed="false">${esc(s.label)}</button>`).join('') +
      `<span class="sp"></span><button type="button" class="btn" data-replay>Replay</button>`;
    let run = 0, cur = 0;
    const finish = (s, token) => {
      if (token !== run) return;
      Guide.light(fig, Guide.expand(s.path).concat(s.extra || []));
      narr.innerHTML = `<div class="step">${esc(s.label)} · outcome</div><p>${s.end}</p>`;
    };
    const play = (i, animate) => {
      const token = ++run;
      cur = i;
      const s = list[i];
      bar.querySelectorAll('.chip').forEach((b) => b.setAttribute('aria-pressed', String(+b.dataset.i === i)));
      if (onPick) onPick(s, i);
      if (!animate || reduced()) return finish(s, token);
      const keys = Guide.expand(s.path);
      let n = 0;
      const tick = () => {
        if (token !== run) return;
        n++;
        Guide.light(fig, keys.slice(0, n * 2 - 1));
        const node = s.path[n - 1];
        narr.innerHTML = `<div class="step">${esc(s.label)} · ${n} of ${s.path.length}</div><p>${say[node] || labelOf(fig, node)}</p>`;
        setTimeout(n < s.path.length ? tick : () => finish(s, token), n < s.path.length ? 560 : 1000);
      };
      tick();
    };
    bar.addEventListener('click', (e) => {
      const b = e.target.closest('button');
      if (!b) return;
      play(b.hasAttribute('data-replay') ? cur : +b.dataset.i, true);
    });
    play(0, false);
    return { play };
  };

  /* Stepper: an overview state (everything lit) followed by numbered steps. */
  Guide.steps = (root, steps, { intro = '' } = {}) => {
    const bar = root.querySelector('.stage-bar');
    const fig = root.querySelector('.fig');
    const narr = root.querySelector('.narr');
    bar.innerHTML = `<span class="lbl">Walk through</span>
      <button type="button" class="btn" data-a="prev">◀ Back</button>
      <span class="counter" aria-live="off"></span>
      <button type="button" class="btn primary" data-a="next">Start ▶</button>
      <span class="sp"></span>
      <button type="button" class="btn" data-a="all">Show whole picture</button>`;
    const [prev, counter, next] = [bar.querySelector('[data-a=prev]'), bar.querySelector('.counter'), bar.querySelector('[data-a=next]')];
    let i = -1;
    const render = () => {
      if (i < 0) {
        Guide.light(fig, null);
        narr.innerHTML = `<div class="step">Overview</div><p>${intro}</p>`;
        counter.textContent = `0 / ${steps.length}`;
      } else {
        const s = steps[i];
        Guide.light(fig, s.k);
        narr.innerHTML = `<div class="step">Step ${i + 1} of ${steps.length}${s.title ? ' · ' + esc(s.title) : ''}</div><p>${s.t}</p>`;
        counter.textContent = `${i + 1} / ${steps.length}`;
      }
      prev.disabled = i < 0;
      next.disabled = i >= steps.length - 1;
      next.textContent = i < 0 ? 'Start ▶' : 'Next ▶';
    };
    bar.addEventListener('click', (e) => {
      const a = e.target.closest('button')?.dataset.a;
      if (a === 'prev') i = Math.max(-1, i - 1);
      else if (a === 'next') i = Math.min(steps.length - 1, i + 1);
      else if (a === 'all') i = -1;
      else return;
      render();
    });
    render();
  };

  /* ---------- SHA-256 (sync, UTF-8) and canonical JSON for the audit chapter ---------- */
  const K = new Uint32Array([
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
  ]);
  const rotr = (x, n) => (x >>> n) | (x << (32 - n));
  Guide.sha256 = (str) => {
    const bytes = new TextEncoder().encode(str);
    const len = bytes.length, total = ((len + 9 + 63) >> 6) << 6;
    const m = new Uint8Array(total);
    m.set(bytes);
    m[len] = 0x80;
    const dv = new DataView(m.buffer);
    dv.setUint32(total - 8, Math.floor((len * 8) / 4294967296));
    dv.setUint32(total - 4, (len * 8) >>> 0);
    const H = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
    const W = new Uint32Array(64);
    for (let off = 0; off < total; off += 64) {
      for (let i = 0; i < 16; i++) W[i] = dv.getUint32(off + i * 4);
      for (let i = 16; i < 64; i++) {
        const a = W[i - 15], b = W[i - 2];
        W[i] = (W[i - 16] + (rotr(a, 7) ^ rotr(a, 18) ^ (a >>> 3)) + W[i - 7] + (rotr(b, 17) ^ rotr(b, 19) ^ (b >>> 10))) | 0;
      }
      let [a, b, c, d, e, f, g, h] = H;
      for (let i = 0; i < 64; i++) {
        const t1 = (h + (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) + ((e & f) ^ (~e & g)) + K[i] + W[i]) | 0;
        const t2 = ((rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c))) | 0;
        h = g; g = f; f = e; e = (d + t1) | 0; d = c; c = b; b = a; a = (t1 + t2) | 0;
      }
      H[0] += a; H[1] += b; H[2] += c; H[3] += d; H[4] += e; H[5] += f; H[6] += g; H[7] += h;
    }
    return Array.from(H, (x) => x.toString(16).padStart(8, '0')).join('');
  };
  Guide.canon = function canon(v) {
    if (Array.isArray(v)) return '[' + v.map(canon).join(',') + ']';
    if (v && typeof v === 'object')
      return '{' + Object.keys(v).sort().map((k) => JSON.stringify(k) + ':' + canon(v[k])).join(',') + '}';
    return JSON.stringify(v);
  };

  /* ---------- page ---------- */
  const parts = () => [...new Set(Guide.chapters.map((c) => c.part))];
  const num = (n) => String(n).padStart(2, '0');

  function cover() {
    const groups = parts()
      .map((p) => `<section><h3>${esc(p)}</h3><ol>${Guide.chapters
        .filter((c) => c.part === p)
        .map((c) => `<li><span class="n">${num(c.n)}</span><a href="#${c.id}">${esc(c.title)}</a></li>`)
        .join('')}</ol></section>`)
      .join('');
    return Guide.h(`<header class="cover">
      <p class="eyebrow">MCP Server Template · Security</p>
      <h1>MCP Hardening Field Guide</h1>
      <p class="lede">How the hardened server decides, refuses, remembers, and responds — one mechanism at a time. Each chapter is a working diagram: pick a scenario or step through it, and the narration explains what just happened and why it was built that way.</p>
      <p>This guide describes the <strong>target design</strong> from the security roadmap, not the code as it runs today. The written specification lives in <code>docs/06-SECURITY-ROADMAP.md</code>; the static diagrams in <code>docs/07-SECURITY-FLOWCHARTS.md</code>.</p>
      <div class="legend" aria-label="Colour key">
        <span><i class="sw pass"></i>Continues, or a guarantee holds</span>
        <span><i class="sw deny"></i>Stops here — with the event that is logged</span>
        <span><i class="sw gate"></i>A check that can go either way</span>
        <span><i class="sw ctrl"></i>Control plane: identity providers, Redis, SIEM</span>
        <span><i class="sw untr"></i>Untrusted input</span>
      </div>
      <nav class="map" aria-label="Chapters by part">${groups}</nav>
    </header>`);
  }

  function rail() {
    return parts()
      .map((p) => `<p class="part">${esc(p)}</p><ol>${Guide.chapters
        .filter((c) => c.part === p)
        .map((c) => `<li><a href="#${c.id}" data-id="${c.id}"><span class="n">${num(c.n)}</span><span>${esc(c.title)}</span></a></li>`)
        .join('')}</ol>`)
      .join('');
  }

  Guide.boot = () => {
    const main = document.getElementById('guide');
    const nav = document.getElementById('rail');
    Guide.chapters.forEach((c, i) => (c.n = i + 1));
    main.appendChild(cover());
    Guide.chapters.forEach((c, i) => {
      const next = Guide.chapters[i + 1];
      const sec = Guide.h(`<section class="chapter" id="${c.id}" aria-labelledby="h-${c.id}">
        <p class="eyebrow">${esc(c.part)} · ${num(c.n)}</p>
        <h2 id="h-${c.id}">${esc(c.title)}</h2>
        <p class="lede">${c.lede}</p>
        <div class="body"></div>
        ${c.takeaway ? `<p class="takeaway"><b>Remember.</b> ${c.takeaway}</p>` : ''}
        ${c.refs ? `<p class="refs">${c.refs}</p>` : ''}
        ${next ? `<a class="nextlink" href="#${next.id}">Next: ${esc(next.title)} →</a>` : ''}
      </section>`);
      main.appendChild(sec);
      try {
        c.render(sec.querySelector('.body'), c);
      } catch (err) {
        sec.querySelector('.body').innerHTML = `<p class="panel">This visual could not be drawn: ${esc(err.message)}</p>`;
        console.error(err);
      }
    });
    nav.innerHTML = `<p class="brand"><a href="#top" style="text-decoration:none;color:inherit">Field Guide</a></p>` + rail();

    const links = new Map([...nav.querySelectorAll('a[data-id]')].map((a) => [a.dataset.id, a]));
    if ('IntersectionObserver' in window) {
      const io = new IntersectionObserver(
        (entries) => entries.forEach((en) => {
          if (!en.isIntersecting) return;
          links.forEach((a) => a.classList.remove('is-here'));
          links.get(en.target.id)?.classList.add('is-here');
        }),
        { rootMargin: '-25% 0px -65% 0px' }
      );
      main.querySelectorAll('.chapter').forEach((s) => io.observe(s));
    }
    if (location.hash.length > 1) document.getElementById(decodeURIComponent(location.hash.slice(1)))?.scrollIntoView();
  };
})();
