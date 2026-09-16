/**
 * Background service worker.
 *
 * - Owns all persistent state (it is the only writer to chrome.storage.local)
 *   and keeps Chrome's declarativeNetRequest state in sync with it.
 * - Injects element-hiding CSS on behalf of content scripts as *user-origin*
 *   stylesheets, which pages cannot override or remove.
 * - Tracks per-tab and lifetime statistics.
 */
import { ALLOW_RULE_ID_BASE, FILTER_LISTS, RULE_PRIORITY, STATS_FLUSH_MS, isBlockingRuleId, mergeSettings } from '../shared/defaults.js';
import { compileNetworkRules, parseFilterList } from '../shared/filter-parser.js';
import { hostMatches, isAllowlisted, isValidHostname, isWebUrl, normalizeHost } from '../shared/hosts.js';

const dnr = chrome.declarativeNetRequest;

// onRuleMatchedDebug only exists for unpacked extensions. Packed builds fall
// back to getMatchedRules(), which is rate limited, so counts are less live.
const LIVE_COUNTING = typeof dnr.onRuleMatchedDebug?.addListener === 'function';

const ICONS = {
  on: { 16: '/icons/icon-16.png', 32: '/icons/icon-32.png' },
  off: { 16: '/icons/icon-off-16.png', 32: '/icons/icon-off-32.png' },
};

/* -------------------------------------------------------------------------- */
/* State                                                                      */
/* -------------------------------------------------------------------------- */

async function loadState() {
  const stored = await chrome.storage.local.get(['settings', 'allowlist', 'userFilters', 'filterStatus', 'stats']);
  return {
    settings: mergeSettings(stored.settings),
    allowlist: stored.allowlist ?? [],
    userFilters: stored.userFilters ?? '',
    filterStatus: stored.filterStatus ?? null,
    stats: stored.stats ?? { blocked: 0, hidden: 0, since: Date.now() },
    liveCounting: LIVE_COUNTING,
  };
}

// Every mutation runs through this queue so DNR updates never interleave
// (two overlapping updateDynamicRules calls would fight over rule ids).
let queue = Promise.resolve();
function serialized(task) {
  const run = queue.then(task);
  queue = run.catch((err) => console.error(err));
  return run;
}

/** Applies `change(state)` → storage patch, re-syncs Chrome, returns fresh state. */
function mutate(change) {
  return serialized(async () => {
    const patch = await change(await loadState());
    if (patch) await chrome.storage.local.set(patch);
    const state = await loadState();
    await syncRulesets(state.settings);
    state.filterStatus = await syncDynamicRules(state);
    await refreshIcons(state);
    return state;
  });
}

const syncAll = () => mutate(({ settings }) => ({ settings }));

/* -------------------------------------------------------------------------- */
/* declarativeNetRequest                                                      */
/* -------------------------------------------------------------------------- */

async function syncRulesets(settings) {
  const current = new Set(await dnr.getEnabledRulesets());
  const wanted = new Set(settings.enabled ? FILTER_LISTS.filter((l) => settings.lists[l.id]).map((l) => l.id) : []);
  const enableRulesetIds = [...wanted].filter((id) => !current.has(id));
  const disableRulesetIds = [...current].filter((id) => !wanted.has(id));
  if (enableRulesetIds.length || disableRulesetIds.length) {
    await dnr.updateEnabledRulesets({ enableRulesetIds, disableRulesetIds });
  }
}

/**
 * Dynamic rules = one allowAllRequests rule for paused sites + the user's own
 * network filters. Also compiles the user's cosmetic filters for the content
 * script and records a status report for the options page.
 */
async function syncDynamicRules({ settings, allowlist, userFilters }) {
  const parsed = parseFilterList(userFilters);
  const errors = parsed.errors.map(({ line, message }) => ({ line, message }));

  const userRules = [];
  for (const entry of compileNetworkRules(parsed.network, { allowIdStart: ALLOW_RULE_ID_BASE + 1 })) {
    const { regexFilter, isUrlFilterCaseSensitive } = entry.rule.condition;
    if (regexFilter) {
      const { isSupported, reason } = await dnr.isRegexSupported({ regex: regexFilter, isCaseSensitive: !!isUrlFilterCaseSensitive });
      if (!isSupported) {
        errors.push({ line: entry.lines[0], message: `Chrome cannot use this regular expression (${reason})` });
        continue;
      }
    }
    userRules.push(entry);
  }

  const base = [];
  if (settings.enabled && allowlist.length) {
    base.push({
      id: ALLOW_RULE_ID_BASE,
      priority: RULE_PRIORITY.allowlist,
      action: { type: 'allowAllRequests' },
      condition: { requestDomains: allowlist, resourceTypes: ['main_frame', 'sub_frame'] },
    });
  }
  const wanted = settings.enabled ? userRules : [];
  const removeRuleIds = (await dnr.getDynamicRules()).map((r) => r.id);

  try {
    await dnr.updateDynamicRules({ removeRuleIds, addRules: [...base, ...wanted.map((e) => e.rule)] });
  } catch {
    // Chrome rejects the whole batch if one rule is bad. Add them one at a
    // time instead so the good ones still apply and the bad one gets a line.
    await dnr.updateDynamicRules({ removeRuleIds, addRules: base });
    for (const { rule, lines } of wanted) {
      try {
        await dnr.updateDynamicRules({ addRules: [rule] });
      } catch (err) {
        errors.push({ line: lines[0], message: err.message });
      }
    }
  }

  const cosmeticCount =
    parsed.cosmetic.generic.length +
    Object.values(parsed.cosmetic.byHost).flat().length +
    Object.values(parsed.cosmetic.exceptions).flat().length;
  const filterStatus = {
    networkRules: userRules.length,
    networkFilters: parsed.network.length,
    cosmeticFilters: cosmeticCount,
    errors: errors.sort((a, b) => a.line - b.line),
  };
  await chrome.storage.local.set({ userCosmetic: parsed.cosmetic, filterStatus });
  return filterStatus;
}

/* -------------------------------------------------------------------------- */
/* Toolbar icon                                                               */
/* -------------------------------------------------------------------------- */

function hostOf(url) {
  try {
    const u = new URL(url);
    return isWebUrl(u) ? normalizeHost(u.hostname) : null;
  } catch {
    return null;
  }
}

async function updateTabIcon(tabId, url, { settings, allowlist }) {
  const host = hostOf(url);
  const active = settings.enabled && !(host && isAllowlisted(host, allowlist));
  try {
    await chrome.action.setIcon({ tabId, path: active ? ICONS.on : ICONS.off });
  } catch {
    // The tab went away while we were working.
  }
}

async function refreshIcons(state) {
  await chrome.action.setIcon({ path: state.settings.enabled ? ICONS.on : ICONS.off });
  const tabs = await chrome.tabs.query({});
  await Promise.all(tabs.map((tab) => updateTabIcon(tab.id, tab.url, state)));
}

async function setupBadge() {
  await dnr.setExtensionActionOptions({ displayActionCountAsBadgeText: true });
  await chrome.action.setBadgeBackgroundColor({ color: '#B21E2B' });
  await chrome.action.setBadgeTextColor({ color: '#FFFFFF' });
}

/* -------------------------------------------------------------------------- */
/* Statistics                                                                 */
/* -------------------------------------------------------------------------- */

/** tabId → { blocked: number, hidden: { [frameId]: number } } */
const tabStats = new Map();
const matchedRulesCache = new Map();
let lifetimeDelta = { blocked: 0, hidden: 0 };
let flushTimer = 0;

// The worker can be stopped at any time; tab counts survive in session storage.
const statsReady = chrome.storage.session.get('tabStats').then(({ tabStats: saved }) => {
  for (const [id, entry] of Object.entries(saved ?? {})) tabStats.set(Number(id), entry);
});

function tabEntry(tabId) {
  let entry = tabStats.get(tabId);
  if (!entry) tabStats.set(tabId, (entry = { blocked: 0, hidden: {} }));
  return entry;
}

async function withStats(update) {
  await statsReady;
  update();
  flushTimer ||= setTimeout(flushStats, STATS_FLUSH_MS);
}

function flushStats() {
  flushTimer = 0;
  const delta = lifetimeDelta;
  lifetimeDelta = { blocked: 0, hidden: 0 };
  return serialized(async () => {
    const { stats } = await loadState();
    await chrome.storage.local.set({
      stats: { ...stats, blocked: stats.blocked + delta.blocked, hidden: stats.hidden + delta.hidden },
    });
    await chrome.storage.session.set({ tabStats: Object.fromEntries(tabStats) });
  });
}

async function getTabStats(tabId) {
  await statsReady;
  const entry = tabStats.get(tabId);
  const hidden = Object.values(entry?.hidden ?? {}).reduce((a, b) => a + b, 0);
  if (LIVE_COUNTING) return { blocked: entry?.blocked ?? 0, hidden, live: true };

  const cached = matchedRulesCache.get(tabId);
  if (cached && Date.now() - cached.at < 30_000) return { blocked: cached.blocked, hidden, live: false };
  try {
    const { rulesMatchedInfo } = await dnr.getMatchedRules({ tabId });
    const blocked = rulesMatchedInfo.filter((info) => isBlockingRuleId(info.rule.ruleId)).length;
    matchedRulesCache.set(tabId, { at: Date.now(), blocked });
    return { blocked, hidden, live: false };
  } catch {
    return { blocked: null, hidden, live: false }; // quota exceeded
  }
}

if (LIVE_COUNTING) {
  dnr.onRuleMatchedDebug.addListener(({ request, rule }) => {
    if (request.tabId < 0 || !isBlockingRuleId(rule.ruleId)) return;
    withStats(() => {
      tabEntry(request.tabId).blocked++;
      lifetimeDelta.blocked++;
    });
  });
}

chrome.webNavigation.onCommitted.addListener(({ tabId, frameId, url, documentLifecycle }) => {
  if (documentLifecycle === 'prerender') return;
  withStats(() => {
    if (frameId === 0) {
      tabStats.set(tabId, { blocked: 0, hidden: {} });
      matchedRulesCache.delete(tabId);
    } else {
      delete tabEntry(tabId).hidden[frameId];
    }
  });
  if (frameId === 0) loadState().then((state) => updateTabIcon(tabId, url, state));
});

chrome.tabs.onRemoved.addListener((tabId) => {
  matchedRulesCache.delete(tabId);
  withStats(() => tabStats.delete(tabId));
});

/* -------------------------------------------------------------------------- */
/* Messaging                                                                  */
/* -------------------------------------------------------------------------- */

const EXTENSION_ORIGIN = chrome.runtime.getURL('');

/** Messages accepted from the popup and options page. */
const pageHandlers = {
  'state:get': () => loadState(),

  'settings:update': ({ patch }) =>
    mutate(({ settings }) => ({
      settings: mergeSettings({
        ...settings,
        ...(typeof patch?.enabled === 'boolean' && { enabled: patch.enabled }),
        lists: { ...settings.lists, ...pickBooleans(patch?.lists) },
      }),
    })),

  'site:setEnabled': ({ host, enabled }) =>
    mutate(({ allowlist }) => {
      const site = normalizeHost(host);
      if (!isValidHostname(site)) throw new Error(`"${host}" is not a valid hostname`);
      // Resuming must also drop parent entries (pausing example.com pauses
      // www.example.com, so resuming on www.example.com removes example.com).
      const next = enabled ? allowlist.filter((d) => !hostMatches(site, d)) : [...new Set([...allowlist, site])];
      return { allowlist: next.sort() };
    }),

  'filters:save': ({ text }) => mutate(() => ({ userFilters: String(text ?? '') })),

  'stats:tab': ({ tabId }) => getTabStats(tabId),

  'stats:reset': () =>
    serialized(async () => {
      lifetimeDelta = { blocked: 0, hidden: 0 };
      await chrome.storage.local.set({ stats: { blocked: 0, hidden: 0, since: Date.now() } });
      return loadState();
    }),
};

/** Messages accepted from content scripts (cosmetic.js, picker.js). */
const contentHandlers = {
  'cosmetic:insert': ({ css }, sender) =>
    chrome.scripting.insertCSS({ target: targetOf(sender), css: String(css), origin: 'USER' }),

  'cosmetic:remove': ({ css }, sender) =>
    chrome.scripting.removeCSS({ target: targetOf(sender), css: String(css), origin: 'USER' }),

  'cosmetic:count': ({ count }, sender) =>
    withStats(() => {
      const hidden = tabEntry(sender.tab.id).hidden;
      const next = Math.max(0, Number(count) || 0);
      lifetimeDelta.hidden += Math.max(0, next - (hidden[sender.frameId] ?? 0));
      hidden[sender.frameId] = next;
    }),

  // The host always comes from the sender, so a page can only ever add
  // hiding rules for itself.
  'picker:addRule': ({ selector }, sender) =>
    mutate(({ userFilters }) => {
      const host = hostOf(sender.url);
      if (!host) throw new Error('Cannot add rules for this page');
      const line = `${host}##${String(selector).trim()}`;
      const { errors } = parseFilterList(line);
      if (errors.length) throw new Error(errors[0].message);
      const text = userFilters.replace(/\s*$/, '');
      return { userFilters: `${text}${text ? '\n' : ''}${line}\n` };
    }).then(() => undefined),
};

function targetOf(sender) {
  return sender.documentId
    ? { tabId: sender.tab.id, documentIds: [sender.documentId] }
    : { tabId: sender.tab.id, frameIds: [sender.frameId] };
}

function pickBooleans(obj) {
  return Object.fromEntries(Object.entries(obj ?? {}).filter(([k, v]) => typeof v === 'boolean' && FILTER_LISTS.some((l) => l.id === k)));
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id) return false;
  const isPage = sender.url?.startsWith(EXTENSION_ORIGIN);
  const table = isPage ? pageHandlers : sender.tab ? contentHandlers : null;
  const handler = table && Object.hasOwn(table, message?.type) ? table[message.type] : null;
  if (!handler) return false;

  Promise.resolve()
    .then(() => handler(message, sender))
    .then(
      (result) => sendResponse({ ok: true, result }),
      (error) => sendResponse({ ok: false, error: String(error?.message ?? error) }),
    );
  return true; // keep the channel open for the async response
});

/* -------------------------------------------------------------------------- */
/* Lifecycle                                                                  */
/* -------------------------------------------------------------------------- */

chrome.runtime.onInstalled.addListener(async ({ reason }) => {
  await setupBadge();
  await syncAll();
  if (reason === 'install') await injectIntoOpenTabs();
});

chrome.runtime.onStartup.addListener(async () => {
  await setupBadge();
  await syncAll();
});

/** Content scripts only run on pages loaded after install; cover open tabs too. */
async function injectIntoOpenTabs() {
  const files = chrome.runtime.getManifest().content_scripts[0].js;
  const tabs = await chrome.tabs.query({ url: ['http://*/*', 'https://*/*'] });
  await Promise.allSettled(
    tabs.map((tab) => chrome.scripting.executeScript({ target: { tabId: tab.id, allFrames: true }, files })),
  );
}
