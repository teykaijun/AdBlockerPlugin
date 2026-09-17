import { get } from './api.js';
import { Guard } from './guard.js';

const CACHE_MS = 60_000;
const MAX_CACHED = 2_000;
const MAX_RECENT = 20;

/** host → { blocked: Promise<boolean>, until } */
const answers = new Map();

function isBlocked(host) {
  const cached = answers.get(host);
  if (cached && cached.until > Date.now()) return cached.blocked;

  const blocked = ask(host);
  answers.delete(host);
  if (answers.size >= MAX_CACHED) answers.delete(answers.keys().next().value);
  answers.set(host, { blocked, until: Date.now() + CACHE_MS });
  return blocked;
}

async function ask(host) {
  try {
    const answer = await get(`/check?host=${encodeURIComponent(host)}`);
    return answer.blocked === true;
  } catch {
    // AdBlocker isn't running: do nothing rather than guess, and ask again next time.
    answers.delete(host);
    return false;
  }
}

// Reports can arrive together; apply them one at a time.
let reporting = Promise.resolve();

function report(kind, host) {
  reporting = reporting
    .then(async () => {
      const { stats = { closed: 0, redirects: 0, recent: [] } } = await chrome.storage.session.get('stats');
      if (kind === 'closed') stats.closed += 1;
      else stats.redirects += 1;
      stats.recent = [{ kind, host, time: Date.now() }, ...stats.recent].slice(0, MAX_RECENT);
      await chrome.storage.session.set({ stats });
      await chrome.action.setBadgeText({ text: String(stats.closed + stats.redirects) });
    })
    .catch(() => {});
}

const guard = new Guard({
  isBlocked,
  closeTab: (tabId) => chrome.tabs.remove(tabId),
  goBack: (tabId) => chrome.tabs.goBack(tabId),
  report,
});

chrome.webNavigation.onCreatedNavigationTarget.addListener((details) => guard.onTabOpened(details));
chrome.webNavigation.onBeforeNavigate.addListener((details) => guard.onBeforeNavigate(details));
chrome.webNavigation.onErrorOccurred.addListener((details) => guard.onNavigationError(details));
chrome.tabs.onRemoved.addListener((tabId) => guard.onTabRemoved(tabId));

chrome.runtime.onInstalled.addListener(() => {
  chrome.action.setBadgeBackgroundColor({ color: '#B21E2B' });
  chrome.action.setBadgeTextColor({ color: '#FFFFFF' });
});
