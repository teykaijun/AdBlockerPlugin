import { FILTER_LISTS } from '../shared/defaults.js';
import { parseHostInput } from '../shared/hosts.js';
import { call } from '../shared/messaging.js';

const $ = (id) => document.getElementById(id);
const number = new Intl.NumberFormat();

let state = await call('state:get');
let savedFilters = state.userFilters;

const listStats = await fetch(chrome.runtime.getURL('generated/list-stats.json'))
  .then((r) => r.json())
  .catch(() => ({}));

function h(tag, props = {}, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key.startsWith('on')) el.addEventListener(key.slice(2), value);
    else if (key in el) el[key] = value;
    else el.setAttribute(key, value);
  }
  el.append(...children);
  return el;
}

let toastTimer = 0;
function toast(message) {
  const el = $('toast');
  el.textContent = message;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (el.hidden = true), 2500);
}

async function update(type, payload, message) {
  try {
    state = await call(type, payload);
    renderAll();
    if (message) toast(message);
  } catch (err) {
    toast(err.message);
    renderAll();
  }
}

/* Header ------------------------------------------------------------------ */

function renderHeader() {
  $('global-toggle').checked = state.settings.enabled;
  $('off-banner').hidden = state.settings.enabled;
}

$('global-toggle').addEventListener('change', (e) =>
  update('settings:update', { patch: { enabled: e.target.checked } }, e.target.checked ? 'Blocking turned on' : 'Blocking turned off'),
);

/* Filter lists ------------------------------------------------------------ */

function renderLists() {
  // Built once and then only updated, so keyboard focus survives a toggle.
  if ($('list-rows').childElementCount) {
    FILTER_LISTS.forEach((list) => ($(`list-${list.id}`).checked = !!state.settings.lists[list.id]));
    return;
  }
  const rows = FILTER_LISTS.map((list) => {
    const stats = listStats[list.id];
    const meta = [];
    if (stats?.networkFilters) meta.push(`${number.format(stats.networkFilters)} blocking filters`);
    if (stats?.cosmeticFilters) meta.push(`${number.format(stats.cosmeticFilters)} hiding filters`);
    const id = `list-${list.id}`;

    return h(
      'li',
      { className: 'row' },
      h(
        'div',
        {},
        h('label', { className: 'row-title', htmlFor: id }, list.title),
        h('p', { className: 'row-desc' }, list.description),
        meta.length ? h('p', { className: 'row-meta' }, meta.join(' · ')) : '',
      ),
      h(
        'span',
        { className: 'switch' },
        h('input', {
          type: 'checkbox',
          id,
          role: 'switch',
          checked: !!state.settings.lists[list.id],
          onchange: (e) =>
            update(
              'settings:update',
              { patch: { lists: { [list.id]: e.target.checked } } },
              `${list.title} ${e.target.checked ? 'enabled' : 'disabled'}`,
            ),
        }),
        h('span', { className: 'track', 'aria-hidden': 'true' }),
      ),
    );
  });
  $('list-rows').replaceChildren(...rows);
}

/* Allowed sites ----------------------------------------------------------- */

function renderAllowlist() {
  const rows = state.allowlist.map((host) =>
    h(
      'li',
      { className: 'row' },
      h('span', { className: 'row-host' }, host),
      h(
        'button',
        {
          type: 'button',
          className: 'btn btn-ghost',
          'aria-label': `Remove ${host}`,
          onclick: () => update('site:setEnabled', { host, enabled: true }, `Blocking resumed on ${host}`),
        },
        'Remove',
      ),
    ),
  );
  $('allow-rows').replaceChildren(...rows);
  $('allow-empty').hidden = rows.length > 0;
}

function showAllowError(message) {
  $('allow-error').textContent = message;
  $('allow-error').hidden = !message;
  $('allow-input').setAttribute('aria-invalid', String(!!message));
}

$('allow-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const host = parseHostInput($('allow-input').value);
  if (!host) {
    showAllowError('Enter a domain such as example.com');
    return;
  }
  showAllowError('');
  await update('site:setEnabled', { host, enabled: false }, `Blocking paused on ${host}`);
  $('allow-input').value = '';
});

$('allow-input').addEventListener('input', () => showAllowError(''));

/* My filters -------------------------------------------------------------- */

const editor = $('filters-text');

function isDirty() {
  return editor.value !== savedFilters;
}

function renderFilters() {
  const status = state.filterStatus;
  const parts = [];
  if (status) {
    parts.push(`${number.format(status.networkFilters)} blocking`, `${number.format(status.cosmeticFilters)} hiding`);
    if (status.errors.length) parts.push(`${status.errors.length} skipped`);
  }
  $('filters-summary').textContent = parts.length ? `Active: ${parts.join(' · ')}` : '';
  $('filters-dirty').hidden = !isDirty();

  const errors = status?.errors ?? [];
  $('filters-errors').replaceChildren(
    ...errors.map((err) =>
      h(
        'li',
        {},
        h(
          'button',
          { type: 'button', className: 'error-link', onclick: () => selectLine(err.line) },
          h('b', {}, `Line ${err.line}`),
          h('span', {}, err.message),
        ),
      ),
    ),
  );
}

function selectLine(lineNumber) {
  const lines = editor.value.split('\n');
  const start = lines.slice(0, lineNumber - 1).reduce((n, l) => n + l.length + 1, 0);
  const end = start + (lines[lineNumber - 1]?.length ?? 0);
  editor.focus();
  editor.setSelectionRange(start, end);
  const lineHeight = parseFloat(getComputedStyle(editor).lineHeight) || 20;
  editor.scrollTop = Math.max(0, (lineNumber - 3) * lineHeight);
}

async function saveFilters() {
  const text = editor.value;
  $('filters-save').disabled = true;
  $('filters-summary').textContent = 'Saving…';
  try {
    state = await call('filters:save', { text });
    savedFilters = text;
    const skipped = state.filterStatus?.errors.length ?? 0;
    toast(skipped ? `Saved, ${skipped} line${skipped === 1 ? '' : 's'} skipped` : 'Filters saved');
  } catch (err) {
    toast(err.message);
  } finally {
    $('filters-save').disabled = false;
    renderFilters();
  }
}

editor.value = savedFilters;
editor.addEventListener('input', () => ($('filters-dirty').hidden = !isDirty()));
editor.addEventListener('keydown', (e) => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
    e.preventDefault();
    saveFilters();
  }
});
$('filters-save').addEventListener('click', saveFilters);

window.addEventListener('beforeunload', (e) => {
  if (isDirty()) e.preventDefault();
});

/* Statistics -------------------------------------------------------------- */

function renderStats() {
  const { blocked, hidden, since } = state.stats;
  $('stat-blocked').textContent = state.liveCounting ? number.format(blocked) : '—';
  $('stat-hidden').textContent = number.format(hidden);
  $('stat-since').textContent = new Date(since).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
  $('stats-note').hidden = state.liveCounting;
}

$('stats-reset').addEventListener('click', () => {
  if (confirm('Reset all statistics to zero?')) update('stats:reset', {}, 'Statistics reset');
});

/* About & navigation ------------------------------------------------------ */

const { version } = chrome.runtime.getManifest();
$('version').textContent = `Version ${version}`;
$('about-version').textContent = version;

const navLinks = [...document.querySelectorAll('.side-nav a')];
const observer = new IntersectionObserver(
  (entries) => {
    const visible = entries.filter((e) => e.isIntersecting).sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top);
    if (!visible.length) return;
    const id = visible[0].target.id;
    navLinks.forEach((a) => a.setAttribute('aria-current', String(a.hash === `#${id}`)));
  },
  { rootMargin: '-80px 0px -60% 0px' },
);
document.querySelectorAll('main > section').forEach((s) => observer.observe(s));

/* Rendering --------------------------------------------------------------- */

function renderAll() {
  renderHeader();
  renderLists();
  renderAllowlist();
  renderFilters();
  renderStats();
}

// Keep in sync with changes made elsewhere (popup, element picker).
let refreshQueued = false;
chrome.storage.local.onChanged.addListener((changes) => {
  if (!['settings', 'allowlist', 'userFilters', 'filterStatus', 'stats'].some((k) => k in changes) || refreshQueued) return;
  refreshQueued = true;
  queueMicrotask(async () => {
    refreshQueued = false;
    const wasDirty = isDirty();
    state = await call('state:get');
    if (!wasDirty && state.userFilters !== savedFilters) editor.value = state.userFilters;
    savedFilters = state.userFilters;
    renderAll();
  });
});

renderAll();
