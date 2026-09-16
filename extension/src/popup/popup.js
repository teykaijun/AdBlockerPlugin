import { isAllowlisted, isWebUrl, normalizeHost } from '../shared/hosts.js';
import { call } from '../shared/messaging.js';

const $ = (id) => document.getElementById(id);
const number = new Intl.NumberFormat();

// Chrome does not let extensions touch its own store pages.
const RESTRICTED_HOSTS = ['chromewebstore.google.com', 'chrome.google.com'];

const STATUS_TEXT = {
  on: 'Blocking ads and trackers',
  paused: 'Paused on this site',
  off: 'Blocking is off on all sites',
  unsupported: 'AdBlocker can’t run on this page',
};

const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
const pageUrl = parseUrl(tab?.url);
const host =
  pageUrl && isWebUrl(pageUrl) && !RESTRICTED_HOSTS.includes(pageUrl.hostname) ? normalizeHost(pageUrl.hostname) : null;

let state;
let needsReload = false;

function parseUrl(url) {
  try {
    return url ? new URL(url) : null;
  } catch {
    return null;
  }
}

function pageLabel() {
  if (!pageUrl) return 'This page';
  if (pageUrl.protocol === 'chrome:' || pageUrl.protocol === 'edge:') return 'Browser page';
  if (pageUrl.protocol === 'file:') return 'Local file';
  return pageUrl.hostname || 'This page';
}

function siteState() {
  if (!host) return 'unsupported';
  if (!state.settings.enabled) return 'off';
  return isAllowlisted(host, state.allowlist) ? 'paused' : 'on';
}

function render() {
  const current = siteState();
  document.body.dataset.state = current;

  $('global-toggle').checked = state.settings.enabled;
  $('site-host').textContent = host ?? pageLabel();
  $('site-status').textContent = STATUS_TEXT[current];

  const toggle = $('site-toggle');
  toggle.disabled = current === 'off' || current === 'unsupported';
  toggle.setAttribute('aria-pressed', String(current === 'on'));
  toggle.title = current === 'on' ? 'Pause on this site' : current === 'paused' ? 'Resume blocking on this site' : '';
  $('site-toggle-label').textContent = toggle.title || 'Blocking unavailable';

  $('pick').disabled = current !== 'on';
  $('reload-bar').hidden = !needsReload || current === 'unsupported';
  renderLifetime();
}

function renderLifetime() {
  const { blocked, hidden, since } = state.stats;
  const date = new Date(since).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
  $('lifetime').textContent = state.liveCounting
    ? `${number.format(blocked)} blocked · ${number.format(hidden)} hidden since ${date}`
    : `${number.format(hidden)} elements hidden since ${date}`;
}

async function renderStats() {
  if (!host || !tab) return;
  const { blocked, hidden, live } = await call('stats:tab', { tabId: tab.id });
  $('blocked').textContent = blocked == null ? '—' : number.format(blocked);
  $('blocked').title = live || blocked == null ? '' : 'Updated every 30 seconds';
  $('hidden').textContent = number.format(hidden);
}

async function run(action) {
  $('error').hidden = true;
  try {
    await action();
  } catch (err) {
    $('error').textContent = err.message;
    $('error').hidden = false;
  }
}

/* Events ------------------------------------------------------------------ */

$('global-toggle').addEventListener('change', (event) =>
  run(async () => {
    state = await call('settings:update', { patch: { enabled: event.target.checked } });
    needsReload = true;
    render();
  }),
);

$('site-toggle').addEventListener('click', () =>
  run(async () => {
    state = await call('site:setEnabled', { host, enabled: siteState() !== 'on' });
    needsReload = true;
    render();
  }),
);

$('reload').addEventListener('click', () => {
  chrome.tabs.reload(tab.id);
  window.close();
});

$('pick').addEventListener('click', () =>
  run(async () => {
    await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ['src/content/picker.js'] });
    window.close();
  }),
);

$('open-options').addEventListener('click', () => {
  chrome.runtime.openOptionsPage();
  window.close();
});

chrome.storage.session.onChanged.addListener((changes) => {
  if (changes.tabStats) renderStats();
});

chrome.storage.local.onChanged.addListener((changes) => {
  if (changes.stats?.newValue && state) {
    state.stats = changes.stats.newValue;
    renderLifetime();
  }
});

/* Init -------------------------------------------------------------------- */

await run(async () => {
  state = await call('state:get');
  render();
  await renderStats();
});
