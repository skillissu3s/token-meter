/* Token Meter UI. No framework: one render pass into innerHTML, delegated events. */

const app = document.getElementById('app');
const route = (location.hash || '#popup').slice(1) === 'dashboard' ? 'dashboard' : 'popup';
document.body.classList.add(route);

const state = {
  snapshot: null,
  settings: null,
  tab: localStorage.getItem('tm.tab') || 'claude',
  refreshing: false,
  settingsOpen: false,
};

/* ---------------- bridge ---------------- */

const bridge = window.chrome && window.chrome.webview;

function send(cmd, payload) {
  if (bridge) bridge.postMessage({ cmd, payload: payload || {} });
}

if (bridge) {
  bridge.addEventListener('message', (e) => {
    const msg = e.data;
    if (!msg || !msg.type) return;
    if (msg.type === 'snapshot') {
      state.snapshot = msg.data;
      state.refreshing = false;
    }
    if (msg.type === 'settings') state.settings = msg.data;
    render();
  });
}

/* ---------------- formatting ---------------- */

const fmt = {
  tokens(n) {
    n = n || 0;
    if (n >= 1e9) return round(n / 1e9) + 'B';
    if (n >= 1e6) return round(n / 1e6) + 'M';
    if (n >= 1e3) return round(n / 1e3) + 'K';
    return String(Math.round(n));
  },
  money(n) {
    n = n || 0;
    if (n === 0) return '$0';
    if (n < 0.01) return '<$0.01';
    if (n >= 1000) return '$' + Math.round(n).toLocaleString();
    return '$' + n.toFixed(2);
  },
  pct: (n) => (n || 0).toFixed(n >= 10 ? 0 : 1) + '%',
  until(iso) {
    if (!iso) return '';
    const ms = new Date(iso) - Date.now();
    if (ms <= 0) return 'resetting';
    const mins = Math.floor(ms / 60000);
    if (mins < 60) return mins + 'm left';
    const h = Math.floor(mins / 60);
    if (h < 24) return h + 'h ' + (mins % 60) + 'm left';
    return Math.floor(h / 24) + 'd left';
  },
  ago(iso) {
    if (!iso) return 'never';
    const s = Math.max(0, (Date.now() - new Date(iso)) / 1000);
    if (s < 60) return 'just now';
    if (s < 3600) return Math.floor(s / 60) + 'm ago';
    if (s < 86400) return Math.floor(s / 3600) + 'h ago';
    return Math.floor(s / 86400) + 'd ago';
  },
};

const round = (v) => String(Math.round(v * 100) / 100);
const plural = (n, w) => n + ' ' + w + (n === 1 ? '' : 's');
const heat = (p) => (p >= 90 ? 'hot' : p >= 75 ? 'warn' : '');

function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/** Drop the dated suffix and the redundant "claude-" prefix; keep any provider prefix. */
const shortModel = (m) => String(m).replace(/-20\d{6}$/, '').replace(/(^|\/)claude-/, '$1');

/* ---------------- pieces ---------------- */

const ICON = {
  refresh: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M21 12a9 9 0 1 1-2.6-6.4"/><path d="M21 3v6h-6"/></svg>',
  expand: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M15 3h6v6M9 21H3v-6M21 3l-8 8M3 21l8-8"/></svg>',
  // Sliders rather than a gear: a gear turns to mush at 13px, this stays readable.
  gear: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M3 7h11M18 7h3M3 17h3M10 17h11"/><circle cx="16" cy="7" r="2.2"/><circle cx="8" cy="17" r="2.2"/></svg>',
  close: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M18 6 6 18M6 6l12 12"/></svg>',
};

const coin = (cls) => `<svg class="${cls}" viewBox="0 0 100 100" aria-hidden="true"><use href="#coin"/></svg>`;

/**
 * How far through the window we are, as a percentage. Comparing that against how much has been
 * spent is the pace reading: 40% of the window gone and 70% of the budget spent means you run out
 * early. Only meaningful for a real window with both ends known.
 */
function elapsedPct(g) {
  if (!g.windowStartUtc || !g.resetsAtUtc) return null;
  const start = new Date(g.windowStartUtc).getTime();
  const end = new Date(g.resetsAtUtc).getTime();
  if (!(end > start)) return null;
  return Math.max(0, Math.min(100, ((Date.now() - start) / (end - start)) * 100));
}

function paceLabel(used, elapsed) {
  const drift = used - elapsed;
  if (drift > 8) return { text: 'ahead', over: true };
  if (drift < -8) return { text: 'behind', over: false };
  return { text: 'on pace', over: false };
}

function gauge(g) {
  const pct = Math.min(100, g.percent || 0);
  const left = fmt.until(g.resetsAtUtc);
  const elapsed = elapsedPct(g);
  const pace = elapsed === null ? null : paceLabel(pct, elapsed);

  return `
    <div class="g">
      <div class="g-top">
        <span class="k">${esc(g.label.toLowerCase())}</span>
        <span class="tag${g.authoritative ? ' live' : ''}"
              title="${g.authoritative ? 'Reported by the provider' : 'Measured against a budget you set'}"
        >${g.authoritative ? 'live' : 'budget'}</span>
        <span class="v">${fmt.pct(pct)}</span>
      </div>
      <div class="track">
        <i class="${heat(pct)}" style="width:${pct.toFixed(1)}%"></i>
        ${elapsed === null ? '' :
          `<u style="left:${elapsed.toFixed(1)}%" title="${elapsed.toFixed(0)}% of the window elapsed"></u>`}
      </div>
      <div class="g-foot">
        <span>${esc(g.sub || '')}</span>
        <span class="r">${
          elapsed === null ? '' : `${elapsed.toFixed(0)}% elapsed &middot; <b class="${pace.over ? 'over' : ''}">${pace.text}</b>`
        }${left ? (elapsed === null ? '' : ' &middot; ') + left : ''}</span>
      </div>
    </div>`;
}

/** Rows of label -> value. In a monospace grid this carries the numbers better than tiles do. */
function rows(items) {
  return `<div class="kv">${items.map((r) => `
    <div class="r">
      <span class="k${r.wide ? ' wide' : ''}"${r.title ? ` title="${esc(r.title)}"` : ''}>${esc(r.k)}</span>
      ${r.t ? `<span class="t">${esc(r.t)}</span>` : ''}
      <span class="v${r.strong ? ' fg' : ''}">${esc(r.v)}</span>
    </div>`).join('')}</div>`;
}

function section(label, body) {
  return body ? `<div class="sec">${label ? `<div class="sec-label">${esc(label)}</div>` : ''}${body}</div>` : '';
}

function barChart(buckets, mode, height) {
  if (!buckets || !buckets.length) return '';
  const h = height || 54;
  const w = 100;
  const pick = (b) => (mode === 'cost' ? b.cost : b.tokens) || 0;
  const max = Math.max(...buckets.map(pick), 1);
  const step = w / buckets.length;
  const bw = step * 0.72;

  const bars = buckets.map((b, i) => {
    const v = pick(b);
    const bh = v > 0 ? Math.max(1, (v / max) * h) : 1;
    const x = i * step + (step - bw) / 2;
    const label = mode === 'cost' ? fmt.money(v) : fmt.tokens(v);
    return `<rect class="${v > 0 ? '' : 'zero'}" x="${x.toFixed(2)}" y="${(h - bh).toFixed(2)}"
      width="${bw.toFixed(2)}" height="${bh.toFixed(2)}"><title>${esc(b.label)} · ${label}</title></rect>`;
  }).join('');

  return `
    <svg class="chart" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" height="${h}">${bars}</svg>
    <div class="chart-foot">
      <span>${esc(buckets[0].label)}</span>
      <span>peak ${mode === 'cost' ? fmt.money(max) : fmt.tokens(max)}</span>
      <span>${esc(buckets[buckets.length - 1].label)}</span>
    </div>`;
}

/** OpenCode and Antigravity bill per call, so their views are led by money, not by limits. */
const costLed = (p) => p.id === 'opencode' || p.id === 'antigravity';

function emptyState(p) {
  const head = p.state === 'notInstalled' ? 'not detected'
    : p.state === 'error' ? 'could not read usage'
    : 'nothing recorded yet';
  return `<div class="empty"><b>${head}</b>${esc(p.note || '')}</div>`;
}

/* ---------------- provider body ---------------- */

function providerBody(p, compact) {
  if (p.state !== 'ok') return section(null, emptyState(p));
  const mode = costLed(p) ? 'cost' : 'tokens';

  const stats = section(null, rows(p.stats.map((s) => ({
    k: s.label.toLowerCase(), t: s.sub, v: s.value, strong: true,
  }))));

  const models = p.models.length ? section('models, 7d', rows(
    p.models.slice(0, compact ? 4 : 8).map((m) => ({
      k: shortModel(m.model), wide: true, title: m.model, strong: true,
      v: mode === 'cost' ? fmt.money(m.cost) : fmt.tokens(m.tokens),
    })))) : '';

  const gauges = p.gauges.length ? section(null, p.gauges.map(gauge).join('')) : '';

  if (compact) return gauges + stats + models;

  const chart = section('14 days', barChart(p.daily, mode));
  const table = p.table.length ? section((p.tableTitle || '').toLowerCase(), rows(
    p.table.map((r) => ({
      k: r.name, wide: true, t: r.when, strong: true,
      v: mode === 'cost' ? fmt.money(r.cost) : fmt.tokens(r.tokens),
    })))) : '';

  return gauges + stats + chart + models + table;
}

/* ---------------- popup ---------------- */

/** A tool that is not installed is not news; it is just noise in a four-way tab strip. */
const available = (s) => (s.providers || []).filter((p) => p.state !== 'notInstalled');

function renderPopup(s) {
  const providers = available(s);
  const active = providers.find((p) => p.id === state.tab) || providers[0];
  const live = providers.filter((p) => p.state === 'ok').length;

  if (!providers.length) {
    return `
      <div class="bar">${coin('mark')}<span class="name">token meter</span></div>
      <div class="sec"><div class="empty"><b>no tools detected</b>Install Claude Code, Codex, OpenCode or Antigravity and they will appear here.</div></div>`;
  }

  return `
    <div class="bar">
      ${coin('mark')}
      <span class="name">token meter</span>
      <span class="spacer"></span>
      <button class="act ${state.refreshing ? 'spin' : ''}" data-act="refresh" title="Refresh">${ICON.refresh}</button>
      <button class="act" data-act="dashboard" title="Dashboard">${ICON.expand}</button>
    </div>

    <div class="hero">
      <div class="n">${fmt.tokens(s.tokensToday)}<small>tokens today</small></div>
      <div class="sub">${fmt.money(s.costToday)} across ${plural(live, 'tool')}${
        s.pressureLabel ? ` &middot; busiest <b class="${heat(s.pressure)}">${fmt.pct(s.pressure)}</b>` : ''}</div>
    </div>

    <div class="tabs">
      ${providers.map((p) => `
        <button class="tab ${p.id === active.id ? 'on' : ''} ${p.state === 'ok' ? '' : 'gone'}" data-tab="${p.id}"
        >${esc(p.name.replace(' Code', '').toLowerCase())}</button>`).join('')}
    </div>

    <div class="scroll">${active ? providerBody(active, true) : ''}</div>

    <div class="foot">
      <span>${fmt.ago(s.generatedAt)}</span>
      <span class="spacer"></span>
      <button data-act="dashboard">dashboard</button>
    </div>`;
}

/* ---------------- dashboard ---------------- */

function renderDashboard(s) {
  const providers = available(s);
  const live = providers.filter((p) => p.state === 'ok').length;

  return `
    <div class="bar">
      ${coin('mark')}
      <span class="name">token meter</span>
      <span class="spacer"></span>
      <button class="act ${state.refreshing ? 'spin' : ''}" data-act="refresh" title="Refresh">${ICON.refresh}</button>
      <button class="act" data-act="settings" title="Settings">${state.settingsOpen ? ICON.close : ICON.gear}</button>
    </div>

    <div class="wrap">
      <div class="head">
        <div>
          <div class="t">all token usage</div>
          <div class="s">${live} of ${providers.length} tools reporting &middot; ${fmt.ago(s.generatedAt)}</div>
        </div>
        <div class="totals">
          ${[['today', s.tokensToday, s.costToday],
             ['7 days', s.tokens7d, s.cost7d],
             ['all time', s.tokensTotal, s.costTotal]].map(([k, t, c]) => `
            <div>
              <div class="k">${k}</div>
              <div class="v">${fmt.tokens(t)}</div>
              <div class="c">${fmt.money(c)}</div>
            </div>`).join('')}
        </div>
      </div>

      ${state.settingsOpen ? `<div class="sec" style="border-top:1px solid var(--line)">${settingsBody()}</div>` : ''}

      ${s.daily && s.daily.length ? `
        <div class="sec" style="border-top:1px solid var(--line)">
          <div class="sec-label">combined, 14 days</div>
          ${barChart(s.daily, 'tokens', 72)}
        </div>` : ''}

      <div class="cols">
        ${providers.map(providerColumn).join('')}
      </div>
    </div>`;
}

function providerColumn(p) {
  const meta = p.state === 'ok'
    ? (p.lastActivityUtc ? fmt.ago(p.lastActivityUtc) : '')
    : p.state === 'notInstalled' ? 'not detected'
    : p.state === 'error' ? 'error' : 'idle';

  return `
    <div class="col ${p.state === 'ok' ? '' : 'off'}">
      <div class="col-head">
        <span class="sw" style="background:${p.state === 'ok' ? esc(p.accent) : 'var(--line)'}"></span>
        <span class="n">${esc(p.name.toLowerCase())}</span>
        ${p.account ? `<span class="m">${esc(p.account)}</span>` : ''}
        <span class="m">${esc(meta)}</span>
      </div>
      ${providerBody(p, false)}
    </div>`;
}

/* ---------------- settings ---------------- */

function weeklyCalibratable() {
  const claude = (state.snapshot?.providers || []).find((p) => p.id === 'claude');
  return !!claude?.gauges?.[1]?.calibratable;
}

function settingsBody() {
  const c = state.settings || {};
  const plans = [['pro', 'claude pro'], ['max5', 'claude max 5x'], ['max20', 'claude max 20x'], ['custom', 'custom']];

  const field = (label, hint, control) => `
    <div class="f"><label>${label}${hint ? `<i>${hint}</i>` : ''}</label>${control}</div>`;
  const num = (key, step) =>
    `<input type="number" step="${step}" data-set="${key}" value="${c[key] || 0}" />`;

  return `
    <div class="sec-label">settings &middot; %APPDATA%\\TokenMeter</div>
    <div class="set">
      ${field('calibrate 5-hour', 'type the % Claude Code is showing right now and the budget is solved for you',
        `<input type="number" step="1" min="0" max="100" placeholder="%" data-set="calibrateFiveHour" value="" />`)}
      ${weeklyCalibratable()
        ? field('calibrate weekly', 'same, for the weekly figure',
            `<input type="number" step="1" min="0" max="100" placeholder="%" data-set="calibrateWeekly" value="" />`)
        : field('calibrate weekly', 'needs a full week of local history first — until then the weekly figure only counts what this machine has recorded',
            `<input type="number" placeholder="—" disabled />`)}
      ${field('claude plan', 'starting points only — Anthropic publishes no limits',
        `<select data-set="claudePlan">${plans.map(([v, l]) =>
          `<option value="${v}" ${c.claudePlan === v ? 'selected' : ''}>${l}</option>`).join('')}</select>`)}
      ${field('claude 5-hour budget', 'equivalent API spend, the unit limits are consumed in', num('claudeFiveHourBudget', 1))}
      ${field('claude weekly budget', 'equivalent API spend', num('claudeWeeklyBudget', 10))}
      ${field('codex fallback budget', 'only until Codex reports a real rate limit', num('codexFiveHourBudget', 1))}
      ${field('opencode weekly spend', 'US dollars', num('openCodeWeeklyBudget', 5))}
      ${field('antigravity weekly spend', 'US dollars', num('antigravityWeeklyBudget', 5))}
      ${field('refresh interval', 'seconds', num('refreshSeconds', 15))}
      ${field('count cache reads', 'cached input is billed at a discount but still counts toward limits',
        `<button class="sw-toggle ${c.countCacheReads ? 'on' : ''}" data-toggle="countCacheReads"><i></i></button>`)}
      <div class="f"><span style="flex:1"></span><button class="btn" data-act="save">save</button></div>
    </div>`;
}

/* ---------------- render + events ---------------- */

function render() {
  if (!state.snapshot) return;
  app.innerHTML = route === 'dashboard' ? renderDashboard(state.snapshot) : renderPopup(state.snapshot);
}

document.addEventListener('click', (e) => {
  const tab = e.target.closest('[data-tab]');
  if (tab) {
    state.tab = tab.dataset.tab;
    localStorage.setItem('tm.tab', state.tab);
    render();
    return;
  }

  const toggle = e.target.closest('[data-toggle]');
  if (toggle) {
    const key = toggle.dataset.toggle;
    state.settings = state.settings || {};
    state.settings[key] = !state.settings[key];
    toggle.classList.toggle('on', state.settings[key]);
    return;
  }

  const btn = e.target.closest('[data-act]');
  if (!btn) return;

  switch (btn.dataset.act) {
    case 'refresh':
      state.refreshing = true;
      render();
      send('refresh');
      break;
    case 'dashboard':
      send('open-dashboard');
      break;
    case 'settings':
      state.settingsOpen = !state.settingsOpen;
      render();
      break;
    case 'save':
      saveSettings();
      break;
  }
});

function saveSettings() {
  const patch = Object.assign({}, state.settings);
  document.querySelectorAll('[data-set]').forEach((el) => {
    patch[el.dataset.set] = el.type === 'number' ? Number(el.value) : el.value;
  });
  state.settings = patch;
  state.settingsOpen = false;
  send('settings:save', patch);
  render();
}

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && route === 'popup') send('close-popup');
  if (e.key === 'r' && (e.ctrlKey || e.metaKey)) {
    e.preventDefault();
    send('refresh');
  }
});

// Countdowns and "x minutes ago" go stale on their own, so repaint on a slow tick.
setInterval(() => { if (state.snapshot) render(); }, 30000);

if (bridge) {
  send('ready');
} else if (window.__TM_PREVIEW__) {
  // Opened outside the app, for working on the design against a captured snapshot.
  state.snapshot = window.__TM_PREVIEW__.snapshot;
  state.settings = window.__TM_PREVIEW__.settings;
  render();
}
