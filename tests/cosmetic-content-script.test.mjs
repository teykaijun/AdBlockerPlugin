/**
 * Runs src/content/cosmetic.js inside a Node vm context with a minimal fake
 * DOM and chrome API, and checks which CSS it asks the worker to inject.
 */
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const DATA_SRC = readFileSync(path.join(ROOT, 'extension/generated/cosmetic-data.js'), 'utf8');
const SCRIPT_SRC = readFileSync(path.join(ROOT, 'extension/src/content/cosmetic.js'), 'utf8');

const tick = () => new Promise((resolve) => setImmediate(resolve));

function runContentScript({ hostname, stored = {}, ancestorOrigins = [] }) {
  const messages = [];
  const storageListeners = [];
  const store = { ...stored };

  const context = {
    URL,
    console,
    setTimeout: () => 0,
    clearTimeout: () => {},
    location: { hostname, ancestorOrigins },
    MutationObserver: class {
      observe() {}
    },
    document: {
      head: { append() {} },
      documentElement: { append() {} },
      createElement: () => ({ remove() {} }),
      createDocumentFragment: () => ({
        querySelector(selector) {
          if (selector.includes('!!')) throw new SyntaxError('invalid selector');
          return null;
        },
      }),
      querySelectorAll: () => [],
    },
    chrome: {
      storage: {
        local: { get: async (keys) => Object.fromEntries(keys.filter((k) => k in store).map((k) => [k, store[k]])) },
        onChanged: { addListener: (fn) => storageListeners.push(fn) },
      },
      runtime: {
        sendMessage: async (message) => {
          messages.push(message);
          return { ok: true };
        },
      },
    },
  };
  context.window = context;
  context.window.top = ancestorOrigins.length ? {} : context;

  vm.createContext(context);
  vm.runInContext(DATA_SRC, context);
  vm.runInContext(SCRIPT_SRC, context);

  return {
    messages,
    async change(patch) {
      Object.assign(store, patch);
      const changes = Object.fromEntries(Object.entries(patch).map(([k, v]) => [k, { newValue: v }]));
      storageListeners.forEach((fn) => fn(changes, 'local'));
      await tick();
      await tick();
    },
  };
}

const inserted = (messages) => messages.filter((m) => m.type === 'cosmetic:insert').map((m) => m.css);

describe('cosmetic content script', () => {
  it('injects generic and site-specific selectors for enabled lists', async () => {
    const { messages } = runContentScript({ hostname: 'www.youtube.com' });
    await tick();
    const [css] = inserted(messages);
    assert.ok(css, 'expected a stylesheet');
    assert.match(css, /\.adsbygoogle/);
    assert.match(css, /ytd-ad-slot-renderer/);
    assert.match(css, /display: none !important/);
    assert.doesNotMatch(css, /onetrust/, 'annoyances list is off by default');
  });

  it('does not use other sites’ selectors', async () => {
    const { messages } = runContentScript({ hostname: 'example.org' });
    await tick();
    const [css] = inserted(messages);
    assert.doesNotMatch(css, /ytd-ad-slot-renderer/);
  });

  it('injects nothing on paused sites, including their iframes', async () => {
    const top = runContentScript({ hostname: 'news.example.com', stored: { allowlist: ['example.com'] } });
    const frame = runContentScript({
      hostname: 'ads.thirdparty.net',
      stored: { allowlist: ['example.com'] },
      ancestorOrigins: ['https://news.example.com'],
    });
    await tick();
    assert.deepEqual(inserted(top.messages), []);
    assert.deepEqual(inserted(frame.messages), []);
  });

  it('injects nothing when blocking is off everywhere', async () => {
    const { messages } = runContentScript({ hostname: 'example.org', stored: { settings: { enabled: false } } });
    await tick();
    assert.deepEqual(inserted(messages), []);
  });

  it('applies user filters, exceptions and drops invalid selectors', async () => {
    const { messages } = runContentScript({
      hostname: 'shop.example.com',
      stored: {
        userCosmetic: {
          generic: ['.mine', 'div!!broken'],
          byHost: { 'example.com': ['.shop-promo'] },
          exceptions: { 'shop.example.com': ['.adsbygoogle'] },
        },
      },
    });
    await tick();
    const [css] = inserted(messages);
    assert.match(css, /\.mine/);
    assert.match(css, /\.shop-promo/);
    assert.doesNotMatch(css, /broken/);
    assert.doesNotMatch(css, /\.adsbygoogle/);
  });

  it('swaps stylesheets when settings change', async () => {
    const page = runContentScript({ hostname: 'example.org' });
    await tick();
    const [first] = inserted(page.messages);

    await page.change({ settings: { enabled: true, lists: { annoyances: true } } });
    const second = inserted(page.messages)[1];
    assert.match(second, /onetrust/);
    const removal = page.messages.find((m) => m.type === 'cosmetic:remove');
    assert.equal(removal.css, first);

    await page.change({ allowlist: ['example.org'] });
    const lastRemoval = page.messages.filter((m) => m.type === 'cosmetic:remove').at(-1);
    assert.equal(lastRemoval.css, second);
    assert.equal(inserted(page.messages).length, 2);
  });
});
