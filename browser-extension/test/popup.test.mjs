import assert from 'node:assert/strict';
import { afterEach, beforeEach, describe, it, mock } from 'node:test';

/** A Saturday afternoon, in local time. */
const NOW = new Date(2026, 10, 14, 15, 0);
const TIME = { hour: '2-digit', minute: '2-digit' };

/** Just enough of an element for popup.js. */
function fakeElement(props = {}) {
  return {
    dataset: {},
    textContent: '',
    hidden: false,
    className: '',
    children: [],
    append(...nodes) {
      this.children.push(...nodes);
    },
    replaceChildren(...nodes) {
      this.children = nodes;
    },
    ...props,
  };
}

let loads = 0;

/**
 * Opens a fresh copy of popup.js. AdBlocker answers /v1/status with `status`, or can't be reached
 * when it is null.
 */
async function open({ status = { app: 'AdBlocker', version: '1.6.0' }, stats, mine = '1.6.0' } = {}) {
  const page = {
    status: fakeElement({ dataset: { state: 'checking' } }),
    closed: fakeElement({ textContent: '0' }),
    redirects: fakeElement({ textContent: '0' }),
    scams: fakeElement({ hidden: true }),
    newer: fakeElement({ hidden: true }),
    recent: fakeElement(),
  };
  globalThis.document = { getElementById: (id) => page[id], createElement: () => fakeElement() };
  globalThis.chrome = {
    runtime: { getManifest: () => ({ version: mine }) },
    storage: { session: { get: async () => (stats ? { stats } : {}) } },
  };
  globalThis.fetch = async () => {
    if (!status) throw new TypeError('Failed to fetch');
    return { ok: true, status: 200, json: async () => status };
  };
  await import(`../popup.js?open=${++loads}`);
  for (let i = 0; i < 3; i++) await new Promise((resolve) => setImmediate(resolve));
  return page;
}

/** The time as AdBlocker's /v1/status sends it. */
const utc = (date) => date.toISOString().replace('.000Z', 'Z');

beforeEach(() => mock.timers.enable({ apis: ['Date'], now: NOW.getTime() }));
afterEach(() => mock.timers.reset());

describe('popup status', () => {
  it('says when it is working with AdBlocker', async () => {
    const page = await open();
    assert.equal(page.status.dataset.state, 'on');
    assert.equal(page.status.textContent, 'Working with AdBlocker for Windows 1.6.0.');
    assert.equal(page.newer.hidden, true);
  });

  it('shows when a pause ends, with the day when that is not today', async () => {
    const later = new Date(2026, 10, 14, 16, 30);
    let page = await open({ status: { app: 'AdBlocker', version: '1.6.0', pausedUntil: utc(later) } });
    assert.equal(page.status.dataset.state, 'paused');
    assert.equal(
      page.status.textContent,
      `AdBlocker is paused until ${later.toLocaleString([], TIME)}, so nothing is blocked or closed.`,
    );

    const tomorrow = new Date(2026, 10, 15, 8, 0);
    page = await open({ status: { app: 'AdBlocker', version: '1.6.0', pausedUntil: utc(tomorrow) } });
    assert.equal(
      page.status.textContent,
      `AdBlocker is paused until ${tomorrow.toLocaleString([], { weekday: 'short', ...TIME })}, so nothing is blocked or closed.`,
    );
    assert.ok(page.status.textContent.includes(tomorrow.toLocaleDateString([], { weekday: 'short' })));
  });

  it('says how to look into it when AdBlocker can’t be reached', async () => {
    const page = await open({ status: null });
    assert.equal(page.status.dataset.state, 'off');
    assert.match(page.status.textContent, /^Can’t reach AdBlocker for Windows/);
    assert.match(page.status.textContent, /“adblocker status” shows whether it is running; if it is, “adblocker doctor” checks/);
  });

  it('doesn’t mistake another program on the same port for AdBlocker', async () => {
    const page = await open({ status: { version: '9.9.9' } });
    assert.equal(page.status.dataset.state, 'off');
    assert.equal(page.newer.hidden, true);
  });

  it('points at the newer companion when AdBlocker for Windows is newer', async () => {
    const page = await open({ status: { app: 'AdBlocker', version: '1.7.0' } });
    assert.equal(page.newer.hidden, false);
    assert.match(page.newer.textContent, /^This companion is 1\.6\.0\. .* release 1\.7\.0 /);
  });
});

describe('popup counts', () => {
  it('lists what happened, naming scam sites', async () => {
    const time = NOW.getTime();
    const stats = {
      closed: 1,
      redirects: 0,
      scams: 1,
      scamHosts: ['fake-shop.example'],
      recent: [
        { kind: 'shown', host: 'fake-shop.example', scam: true, time },
        { kind: 'closed', host: 'ads.example.com', scam: false, time },
        { kind: 'redirect', host: 'track.example.net', scam: false, time },
      ],
    };
    const page = await open({ stats });
    assert.equal(page.closed.textContent, '1');
    assert.equal(page.scams.hidden, false);
    assert.equal(page.scams.textContent, '1 suspected scam site blocked.');
    assert.deepEqual(
      page.recent.children.map((item) => item.children[1].textContent),
      ['Scam site: fake-shop.example', 'Closed ads.example.com', 'Went back from track.example.net'],
    );
    assert.equal(page.recent.children[0].className, 'scam');
  });
});
