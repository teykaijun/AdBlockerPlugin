import { get } from './api.js';
import { Guard } from './guard.js';

// Short, so that `adblocker allow`, `allow --for` and `pause` work straight away. It only has to
// cover the few events of one page load, and asking AdBlocker on this PC takes about a millisecond.
const CACHE_MS = 5_000;
const MAX_CACHED = 2_000;
const MAX_RECENT = 20;
/** Warn about each scam site at most once an hour, as the Android app does. */
const WARN_EVERY_MS = 60 * 60_000;

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
async function warnAboutScam(host) {
  const now = Date.now();
  // In session storage rather than memory, because the browser stops this worker when it's idle.
  const { warned = {} } = await chrome.storage.session.get('warned');
  if (warned[host] !== undefined && now - warned[host] < WARN_EVERY_MS) return;
  warned[host] = now;
  await chrome.storage.session.set({ warned });
  // One id per site, so a new warning replaces the old one instead of piling up.
  await chrome.notifications.create(`scam:${host}`, {
    type: 'basic',
    iconUrl: chrome.runtime.getURL('icons/icon-128.png'),
    title: 'AdBlocker blocked a suspected scam site',
    message: `${host} is listed as a fake shop, subscription trap or similar scam.`,
    // One short line in Windows notifications. "--for" would also fail on AdBlocker before 1.6.0.
    contextMessage: `If you trust it: adblocker allow ${host}`,
    priority: 2,
  });
}

/** The toolbar count: ad tabs closed and redirects undone since the browser started. */
function badgeText(stats) {
  const count = stats ? stats.closed + stats.redirects : 0;
  return count > 0 ? String(count) : '';
}

// Reports and badge updates can arrive together; apply them one at a time.
let reporting = Promise.resolve();

function inTurn(task) {
  reporting = reporting.then(task).catch(() => {});
}

function report(kind, host) {
  inTurn(async () => {
    const { scam } = await answerFor(host);
    // An error page that stayed is only worth a mention when it hides a scam site.
    if (kind === 'shown' && !scam) return;
    const { stats = { closed: 0, redirects: 0, scams: 0, scamHosts: [], recent: [] } } =
      await chrome.storage.session.get('stats');
    if (kind === 'closed') stats.closed += 1;
    else if (kind === 'redirect') stats.redirects += 1;
    if (scam) {
      // Count sites, not events: one fake shop can open several tabs or keep redirecting.
      const sites = new Set(stats.scamHosts);
      sites.add(host);
      stats.scamHosts = [...sites];
      stats.scams = sites.size;
    }
    stats.recent = [{ kind, host, scam, time: Date.now() }, ...stats.recent].slice(0, MAX_RECENT);
    await chrome.storage.session.set({ stats });
    await chrome.action.setBadgeText({ text: badgeText(stats) });
    if (scam) await warnAboutScam(host);
  });
}

/** The browser may keep the last badge, but the counts start again with each browser session. */
async function syncBadge() {
  await chrome.action.setBadgeBackgroundColor({ color: '#B21E2B' });
  await chrome.action.setBadgeTextColor({ color: '#FFFFFF' });
  const { stats } = await chrome.storage.session.get('stats');
  await chrome.action.setBadgeText({ text: badgeText(stats) });
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

chrome.runtime.onInstalled.addListener(() => inTurn(syncBadge));
chrome.runtime.onStartup.addListener(() => inTurn(syncBadge));
