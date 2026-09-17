import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { BACK_LIMIT, Guard, hostOf, POPUP_WATCH_MS } from '../guard.js';

/** A guard wired to fakes; `blocked` lists the hosts AdBlocker would block. */
function setup({ blocked = ['ads.example.com'], canGoBack = true } = {}) {
  const calls = { closed: [], back: [], reports: [], asked: [] };
  let clock = 1_000;
  const guard = new Guard({
    isBlocked: async (host) => {
      calls.asked.push(host);
      return blocked.includes(host);
    },
    closeTab: async (tabId) => {
      calls.closed.push(tabId);
    },
    goBack: async (tabId) => {
      if (!canGoBack) throw new Error('Cannot find a next page in history.');
      calls.back.push(tabId);
    },
    report: (kind, host) => calls.reports.push(`${kind} ${host}`),
    now: () => clock,
  });
  return { guard, calls, advance: (ms) => (clock += ms) };
}

const DNS_ERROR = 'net::ERR_NAME_NOT_RESOLVED';

describe('pop-up tabs', () => {
  it('closes a tab a page opens straight to a blocked host', async () => {
    const { guard, calls } = setup();
    await guard.onTabOpened({ tabId: 7, url: 'https://ads.example.com/landing?id=1' });
    assert.deepEqual(calls.closed, [7]);
    assert.deepEqual(calls.reports, ['closed ads.example.com']);
  });

  it('leaves pop-ups to allowed sites alone', async () => {
    const { guard, calls } = setup();
    await guard.onTabOpened({ tabId: 7, url: 'https://accounts.example.org/login' });
    await guard.onNavigationError({ tabId: 7, frameId: 0, url: 'https://accounts.example.org/login', error: 'net::ERR_CONNECTION_RESET' });
    assert.deepEqual(calls.closed, []);
    assert.deepEqual(calls.back, []);
  });

  it('waits for about:blank pop-ups to go somewhere', async () => {
    const { guard, calls } = setup();
    await guard.onTabOpened({ tabId: 8, url: 'about:blank' });
    assert.deepEqual(calls.asked, []);
    await guard.onBeforeNavigate({ tabId: 8, frameId: 0, url: 'https://ads.example.com/' });
    assert.deepEqual(calls.closed, [8]);
  });

  it('closes a pop-up that reaches a blocked host through a redirect', async () => {
    const { guard, calls } = setup();
    await guard.onTabOpened({ tabId: 9, url: 'https://click.example.net/r?to=ads' });
    assert.deepEqual(calls.closed, []);
    await guard.onNavigationError({ tabId: 9, frameId: 0, url: 'https://ads.example.com/offer', error: DNS_ERROR });
    assert.deepEqual(calls.closed, [9]);
    assert.deepEqual(calls.back, []);
  });

  it('closes each tab only once', async () => {
    const { guard, calls } = setup();
    await guard.onTabOpened({ tabId: 7, url: 'https://ads.example.com/' });
    await guard.onBeforeNavigate({ tabId: 7, frameId: 0, url: 'https://ads.example.com/' });
    await guard.onNavigationError({ tabId: 7, frameId: 0, url: 'https://ads.example.com/', error: DNS_ERROR });
    assert.deepEqual(calls.closed, [7]);
    assert.equal(calls.reports.length, 1);
  });

  it('stops treating a tab as a pop-up after a while', async () => {
    const { guard, calls, advance } = setup();
    await guard.onTabOpened({ tabId: 10, url: 'https://news.example.org/' });
    advance(POPUP_WATCH_MS + 1);
    await guard.onBeforeNavigate({ tabId: 10, frameId: 0, url: 'https://ads.example.com/' });
    assert.deepEqual(calls.closed, []);
    // The later failure is then handled like a redirect in an ordinary tab.
    await guard.onNavigationError({ tabId: 10, frameId: 0, url: 'https://ads.example.com/', error: DNS_ERROR });
    assert.deepEqual(calls.back, [10]);
  });

  it('forgets closed tabs', async () => {
    const { guard, calls } = setup();
    await guard.onTabOpened({ tabId: 11, url: 'about:blank' });
    guard.onTabRemoved(11);
    await guard.onBeforeNavigate({ tabId: 11, frameId: 0, url: 'https://ads.example.com/' });
    assert.deepEqual(calls.closed, []);
  });
});

describe('redirects in an existing tab', () => {
  it('goes back when a page sends the tab to a blocked host', async () => {
    const { guard, calls } = setup();
    await guard.onNavigationError({ tabId: 3, frameId: 0, url: 'https://ads.example.com/click', error: DNS_ERROR });
    assert.deepEqual(calls.back, [3]);
    assert.deepEqual(calls.closed, []);
    assert.deepEqual(calls.reports, ['redirect ads.example.com']);
  });

  it('does nothing when there is no page to go back to', async () => {
    const { guard, calls } = setup({ canGoBack: false });
    await guard.onNavigationError({ tabId: 3, frameId: 0, url: 'https://ads.example.com/', error: DNS_ERROR });
    assert.deepEqual(calls.back, []);
    assert.deepEqual(calls.reports, []);
  });

  it('only reacts to lookup failures of blocked hosts in the main frame', async () => {
    const { guard, calls } = setup();
    await guard.onNavigationError({ tabId: 3, frameId: 0, url: 'https://unknown.example.org/', error: DNS_ERROR });
    await guard.onNavigationError({ tabId: 3, frameId: 0, url: 'https://ads.example.com/', error: 'net::ERR_ABORTED' });
    await guard.onNavigationError({ tabId: 3, frameId: 5, url: 'https://ads.example.com/', error: DNS_ERROR });
    await guard.onBeforeNavigate({ tabId: 3, frameId: 0, url: 'https://ads.example.com/' });
    assert.deepEqual(calls.back, []);
    assert.deepEqual(calls.closed, []);
  });

  it('also reacts to failed lookups reported as resolution failures', async () => {
    const { guard, calls } = setup();
    await guard.onNavigationError({ tabId: 4, frameId: 0, url: 'http://ads.example.com/', error: 'net::ERR_NAME_RESOLUTION_FAILED' });
    assert.deepEqual(calls.back, [4]);
  });

  it('stops going back when a page keeps redirecting', async () => {
    const { guard, calls, advance } = setup();
    const redirect = { tabId: 5, frameId: 0, url: 'https://ads.example.com/', error: DNS_ERROR };
    for (let i = 0; i < BACK_LIMIT.times + 2; i++) {
      await guard.onNavigationError(redirect);
      advance(2_000);
    }
    assert.equal(calls.back.length, BACK_LIMIT.times);

    // Other tabs are unaffected, and the tab is sent back again after a quiet spell.
    await guard.onNavigationError({ ...redirect, tabId: 6 });
    advance(BACK_LIMIT.withinMs);
    await guard.onNavigationError(redirect);
    assert.deepEqual(calls.back.slice(BACK_LIMIT.times), [6, 5]);
  });

  it('keeps going back for redirects that are far apart', async () => {
    const { guard, calls, advance } = setup();
    for (let i = 0; i < BACK_LIMIT.times + 2; i++) {
      await guard.onNavigationError({ tabId: 5, frameId: 0, url: 'https://ads.example.com/', error: DNS_ERROR });
      advance(BACK_LIMIT.withinMs + 1);
    }
    assert.equal(calls.back.length, BACK_LIMIT.times + 2);
  });

  it('forgets the count when the tab closes', async () => {
    const { guard, calls } = setup();
    const redirect = { tabId: 5, frameId: 0, url: 'https://ads.example.com/', error: DNS_ERROR };
    for (let i = 0; i < BACK_LIMIT.times; i++) await guard.onNavigationError(redirect);
    guard.onTabRemoved(5);
    await guard.onNavigationError(redirect);
    assert.equal(calls.back.length, BACK_LIMIT.times + 1);
  });
});

describe('hostOf', () => {
  it('returns the lower-case host of http and https URLs', () => {
    assert.equal(hostOf('https://Ads.Example.COM./path'), 'ads.example.com');
    assert.equal(hostOf('http://ads.example.com:8080/'), 'ads.example.com');
  });

  it('ignores everything that is not a named web host', () => {
    for (const url of [
      'about:blank',
      'chrome://newtab/',
      'file:///C:/page.html',
      'https://192.168.1.1/',
      'http://[::1]/',
      'http://localhost:3000/',
      'not a url',
      '',
    ]) {
      assert.equal(hostOf(url), null, url);
    }
  });
});
