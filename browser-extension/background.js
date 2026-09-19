import { get } from './api.js';
import { Guard } from './guard.js';

const CACHE_MS = 60_000;
const MAX_CACHED = 2_000;
const MAX_RECENT = 20;

/** host → { answer: Promise<{ blocked, scam }>, until } */
const answers = new Map();

function answerFor(host) {
  const cached = answers.get(host);
  if (cached && cached.until > Date.now()) return cached.answer;

  const answer = ask(host);
  answers.delete(host);
  if (answers.size >= MAX_CACHED) answers.delete(answers.keys().next().value);
  answers.set(host, { answer, until: Date.now() + CACHE_MS });
  return answer;
}

async function ask(host) {
  try {
    const answer = await get(`/check?host=${encodeURIComponent(host)}`);
    return { blocked: answer.blocked === true, scam: answer.scam === true };
  } catch {
    // AdBlocker isn't running: do nothing rather than guess, and ask again next time.
    answers.delete(host);
    return { blocked: false, scam: false };
  }
}

async function isBlocked(host) {
  return (await answerFor(host)).blocked;
}

/** Scam sites look like broken websites, so say what happened. */
function warnAboutScam(host) {
  chrome.notifications.create(`scam:${host}:${Date.now()}`, {
    type: 'basic',
    iconUrl: chrome.runtime.getURL('icons/icon-128.png'),
    title: 'AdBlocker blocked a suspected scam site',
    message: `${host} is listed as a fake shop, subscription trap or similar scam.`,
    contextMessage: 'If you trust it, run: adblocker allow ' + host,
    priority: 2,
  });
}

// Reports can arrive together; apply them one at a time.
let reporting = Promise.resolve();

function report(kind, host) {
  reporting = reporting
    .then(async () => {
      const { scam } = await answerFor(host);
      const { stats = { closed: 0, redirects: 0, scams: 0, recent: [] } } = await chrome.storage.session.get('stats');
      if (kind === 'closed') stats.closed += 1;
      else stats.redirects += 1;
      if (scam) stats.scams = (stats.scams ?? 0) + 1;
      stats.recent = [{ kind, host, scam, time: Date.now() }, ...stats.recent].slice(0, MAX_RECENT);
      await chrome.storage.session.set({ stats });
      await chrome.action.setBadgeText({ text: String(stats.closed + stats.redirects) });
      if (scam) warnAboutScam(host);
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
