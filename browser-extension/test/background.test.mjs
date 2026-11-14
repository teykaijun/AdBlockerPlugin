import assert from 'node:assert/strict';
import { afterEach, beforeEach, describe, it, mock } from 'node:test';

const DNS_ERROR = 'net::ERR_NAME_NOT_RESOLVED';
const HOUR = 60 * 60_000;

/** A chrome.* event that keeps its listeners, so a test can fire it. */
function fakeEvent() {
  const listeners = [];
  return {
    listeners,
    addListener: (listener) => listeners.push(listener),
    fire: (...args) => Promise.all(listeners.map((listener) => listener(...args))),
  };
}

let loads = 0;

/**
 * Loads a fresh copy of background.js into a fake browser. The fake AdBlocker blocks the hosts in
 * `blocked` and `scam`, and calls the ones in `scam` suspected scam sites. Pass the `session` of an
 * earlier start to load the worker again within the same browser session.
 */
async function start({ blocked = ['ads.example.com'], scam = ['fake-shop.example', 'trap.example'], canGoBack = true, session = {} } = {}) {
  const adblocker = { running: true, blocked: new Set([...blocked, ...scam]), scam: new Set(scam) };
  const calls = { checks: [], removed: [], back: [], badge: [], colours: {}, notifications: [] };
  const chrome = {
    webNavigation: {
      onCreatedNavigationTarget: fakeEvent(),
      onBeforeNavigate: fakeEvent(),
      onErrorOccurred: fakeEvent(),
    },
    tabs: {
      onRemoved: fakeEvent(),
      remove: async (tabId) => {
        calls.removed.push(tabId);
      },
      goBack: async (tabId) => {
        if (!canGoBack) throw new Error('Cannot find a next page in history.');
        calls.back.push(tabId);
      },
    },
    runtime: {
      onInstalled: fakeEvent(),
      onStartup: fakeEvent(),
      getURL: (path) => `chrome-extension://companion/${path}`,
    },
    storage: {
      session: {
        get: async (key) => (key in session ? { [key]: structuredClone(session[key]) } : {}),
        set: async (items) => {
          Object.assign(session, structuredClone(items));
        },
      },
    },
    action: {
      setBadgeText: async ({ text }) => {
        calls.badge.push(text);
      },
      setBadgeBackgroundColor: async ({ color }) => {
        calls.colours.background = color;
      },
      setBadgeTextColor: async ({ color }) => {
        calls.colours.text = color;
      },
    },
    notifications: {
      create: async (id, options) => {
        calls.notifications.push({ id, ...options });
        return id;
      },
    },
  };
  globalThis.chrome = chrome;
  globalThis.fetch = async (url) => {
    if (!adblocker.running) throw new TypeError('Failed to fetch');
    const host = new URL(url).searchParams.get('host');
    calls.checks.push(host);
    const answer = { host, blocked: adblocker.blocked.has(host), scam: adblocker.scam.has(host) };
    return { ok: true, status: 200, json: async () => answer };
  };

  // Another URL is another copy of the module, with its own cache and listeners.
  await import(`../background.js?load=${++loads}`);

  const popUp = (tabId, host) =>
    chrome.webNavigation.onCreatedNavigationTarget.fire({ sourceTabId: 1, tabId, url: `https://${host}/offer` });
  const failToLoad = (tabId, host) =>
    chrome.webNavigation.onErrorOccurred.fire({ tabId, frameId: 0, url: `https://${host}/`, error: DNS_ERROR });
  return { chrome, adblocker, calls, session, popUp, failToLoad };
}

/** Lets queued work finish. The fakes answer straight away, so nothing waits on a timer. */
async function settle() {
  for (let i = 0; i < 3; i++) await new Promise((resolve) => setImmediate(resolve));
}

/**
 * Makes the next read of `key` from session storage slow: it sees the storage as it was when it
 * started, and only finishes when the returned function is called.
 */
function holdNextRead(chrome, key) {
  const read = chrome.storage.session.get;
  let release;
  const held = new Promise((resolve) => (release = resolve));
  chrome.storage.session.get = async (wanted) => {
    if (wanted !== key) return read(wanted);
    chrome.storage.session.get = read;
    const before = await read(wanted);
    await held;
    return before;
  };
  return release;
}

const summary = (stats) => stats.recent.map(({ kind, host }) => `${kind} ${host}`);

beforeEach(() => mock.timers.enable({ apis: ['Date'], now: Date.UTC(2026, 10, 14, 12) }));
afterEach(() => mock.timers.reset());

describe('background', () => {
  it('listens for every event it needs as soon as it loads', async () => {
    const { chrome } = await start();
    const events = {
      'webNavigation.onCreatedNavigationTarget': chrome.webNavigation.onCreatedNavigationTarget,
      'webNavigation.onBeforeNavigate': chrome.webNavigation.onBeforeNavigate,
      'webNavigation.onErrorOccurred': chrome.webNavigation.onErrorOccurred,
      'tabs.onRemoved': chrome.tabs.onRemoved,
      'runtime.onInstalled': chrome.runtime.onInstalled,
      'runtime.onStartup': chrome.runtime.onStartup,
    };
    for (const [name, event] of Object.entries(events)) assert.equal(event.listeners.length, 1, name);
  });

  it('closes a pop-up to a blocked host and counts it', async () => {
    const { calls, session, popUp } = await start();
    await popUp(7, 'ads.example.com');
    await popUp(8, 'news.example.org');
    await settle();
    assert.deepEqual(calls.removed, [7]);
    assert.equal(session.stats.closed, 1);
    assert.equal(session.stats.redirects, 0);
    assert.equal(session.stats.scams, 0);
    assert.deepEqual(summary(session.stats), ['closed ads.example.com']);
    assert.deepEqual(calls.badge, ['1']);
    assert.deepEqual(calls.notifications, []);
  });

  it('sends a tab back from a blocked redirect and counts it', async () => {
    const { calls, session, failToLoad } = await start();
    await failToLoad(3, 'ads.example.com');
    await settle();
    assert.deepEqual(calls.back, [3]);
    assert.equal(session.stats.redirects, 1);
    assert.deepEqual(calls.badge, ['1']);
  });

  it('warns about a scam site at most once an hour, and counts sites rather than tabs', async () => {
    const { calls, session, popUp } = await start();
    await popUp(7, 'fake-shop.example');
    await popUp(8, 'fake-shop.example');
    await settle();
    assert.deepEqual(calls.removed, [7, 8]);
    assert.equal(calls.notifications.length, 1);
    const [warning] = calls.notifications;
    assert.equal(warning.id, 'scam:fake-shop.example');
    assert.match(warning.message, /^fake-shop\.example is listed as a fake shop/);
    assert.equal(warning.contextMessage, 'If you trust it: adblocker allow fake-shop.example');
    assert.equal(session.stats.closed, 2);
    assert.equal(session.stats.scams, 1);
    assert.deepEqual(calls.badge, ['1', '2']);

    // Another scam site gets its own warning.
    await popUp(9, 'trap.example');
    await settle();
    assert.deepEqual(calls.notifications.map(({ id }) => id), ['scam:fake-shop.example', 'scam:trap.example']);
    assert.equal(session.stats.scams, 2);

    // An hour later the same site is worth a new warning, which replaces the old one.
    mock.timers.tick(HOUR);
    await popUp(10, 'fake-shop.example');
    await settle();
    assert.deepEqual(calls.notifications.map(({ id }) => id), ['scam:fake-shop.example', 'scam:trap.example', 'scam:fake-shop.example']);
    assert.equal(session.stats.scams, 2);
  });

  it('still knows it warned after the browser stops the worker and starts it again', async () => {
    const first = await start();
    await first.popUp(7, 'fake-shop.example');
    await settle();
    // The browser stops an idle worker after about 30 seconds. A new copy of it starts in the
    // same browser session, so with the same session storage.
    mock.timers.tick(60_000);
    const second = await start({ session: first.session });
    await second.popUp(8, 'fake-shop.example');
    await settle();
    assert.deepEqual(second.calls.removed, [8]);
    assert.equal(first.calls.notifications.length, 1);
    assert.deepEqual(second.calls.notifications, []);
  });

  it('warns once when a second report comes in before the first warning is saved', async () => {
    const { chrome, calls, popUp } = await start();
    const release = holdNextRead(chrome, 'warned');
    await popUp(7, 'fake-shop.example');
    await settle();
    await popUp(8, 'fake-shop.example');
    await settle();
    release();
    await settle();
    assert.deepEqual(calls.removed, [7, 8]);
    assert.equal(calls.notifications.length, 1);
  });

  it('names a scam site even when its error page has to stay', async () => {
    // Like a link from another app, which opens a new tab with nothing to go back to.
    const { calls, session, failToLoad } = await start({ canGoBack: false });
    await failToLoad(4, 'fake-shop.example');
    await settle();
    assert.deepEqual(calls.back, []);
    assert.deepEqual(calls.notifications.map(({ id }) => id), ['scam:fake-shop.example']);
    assert.deepEqual(summary(session.stats), ['shown fake-shop.example']);
    assert.equal(session.stats.redirects, 0);
    assert.equal(session.stats.scams, 1);
    // Nothing was closed or undone, so there is no count to show.
    assert.deepEqual(calls.badge, ['']);
  });

  it('keeps quiet about other blocked pages that have to stay', async () => {
    const { calls, session, failToLoad } = await start({ canGoBack: false });
    await failToLoad(4, 'ads.example.com');
    await settle();
    assert.equal(session.stats, undefined);
    assert.deepEqual(calls.badge, []);
    assert.deepEqual(calls.notifications, []);
  });

  it('asks again after a few seconds, so allowing a site works straight away', async () => {
    const { adblocker, calls, popUp } = await start();
    await popUp(7, 'ads.example.com');
    await popUp(8, 'ads.example.com');
    await settle();
    // One answer covers a burst of events.
    assert.deepEqual(calls.checks, ['ads.example.com']);
    assert.deepEqual(calls.removed, [7, 8]);

    adblocker.blocked.delete('ads.example.com'); // adblocker allow ads.example.com --for 10m
    mock.timers.tick(5_000);
    await popUp(9, 'ads.example.com');
    await settle();
    assert.deepEqual(calls.checks, ['ads.example.com', 'ads.example.com']);
    assert.deepEqual(calls.removed, [7, 8]);
  });

  it('leaves tabs alone while AdBlocker is not running, and asks again next time', async () => {
    const { adblocker, calls, popUp } = await start();
    adblocker.running = false;
    await popUp(7, 'ads.example.com');
    await settle();
    assert.deepEqual(calls.removed, []);

    adblocker.running = true;
    await popUp(8, 'ads.example.com');
    await settle();
    assert.deepEqual(calls.removed, [8]);
  });

  it('sets up the badge when the browser starts or the extension is installed', async () => {
    const { chrome, calls, popUp } = await start();
    await chrome.runtime.onStartup.fire();
    await settle();
    assert.deepEqual(calls.colours, { background: '#B21E2B', text: '#FFFFFF' });
    assert.deepEqual(calls.badge, ['']);

    await popUp(7, 'ads.example.com');
    await chrome.runtime.onInstalled.fire({ reason: 'install' });
    await settle();
    assert.deepEqual(calls.badge, ['', '1', '1']);
  });

  it('keeps a newer count when setting up the badge at start is slow', async () => {
    const { chrome, calls, popUp } = await start();
    // The set-up reads the counts before the pop-up is closed, and finishes after.
    const release = holdNextRead(chrome, 'stats');
    const starting = chrome.runtime.onStartup.fire();
    await settle();
    await popUp(7, 'ads.example.com');
    await settle();
    release();
    await starting;
    await settle();
    assert.deepEqual(calls.badge, ['', '1']);
  });
});
