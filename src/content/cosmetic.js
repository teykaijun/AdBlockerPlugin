/**
 * Element hiding. Runs at document_start in every frame.
 *
 * Works out which selectors apply to this page (built-in lists from
 * generated/cosmetic-data.js plus the user's own filters), then asks the
 * service worker to inject them as a user-origin stylesheet. Re-applies live
 * when settings change, and reports how many elements are hidden.
 *
 * Content scripts cannot import ES modules, so the few host helpers used here
 * are small copies of the ones in src/shared/hosts.js.
 */
(() => {
  if (globalThis.__adblockerCosmetic) return;
  globalThis.__adblockerCosmetic = true;

  const DATA = globalThis.ADBLOCKER_DATA;
  if (!DATA) return;

  const COUNT_DELAY_MS = 1500;
  const SELECTORS_PER_RULE = 50;

  const normalize = (host) => String(host || '').toLowerCase();
  const matches = (host, domain) => host === domain || host.endsWith(`.${domain}`);

  const ancestorHost = (pick) => {
    const origins = location.ancestorOrigins;
    if (!origins?.length) return '';
    try {
      return new URL(pick(origins)).hostname;
    } catch {
      return ''; // sandboxed frames report "null"
    }
  };

  // about:blank / srcdoc frames inherit the host of their parent.
  const frameHost = normalize(location.hostname || ancestorHost((o) => o[0]));
  // Pausing a site applies to everything embedded in it, like the DNR rule.
  const topHost = window === window.top ? frameHost : normalize(ancestorHost((o) => o[o.length - 1])) || frameHost;
  if (!frameHost) return;

  const suffixes = [];
  for (let h = frameHost; h; h = h.includes('.') ? h.slice(h.indexOf('.') + 1) : '') suffixes.push(h);

  const probe = document.createDocumentFragment();
  const isValidSelector = (selector) => {
    try {
      probe.querySelector(selector);
      return true;
    } catch {
      return false;
    }
  };

  function collectSelectors(settings, userCosmetic) {
    const sources = Object.entries(DATA.lists)
      .filter(([id]) => settings.lists[id])
      .map(([, list]) => list);
    if (userCosmetic) sources.push(userCosmetic);

    const selected = new Set();
    const excluded = new Set();
    for (const { generic = [], byHost = {}, exceptions = {} } of sources) {
      generic.forEach((s) => selected.add(s));
      for (const h of suffixes) {
        byHost[h]?.forEach((s) => selected.add(s));
        exceptions[h]?.forEach((s) => excluded.add(s));
      }
    }
    return [...selected].filter((s) => !excluded.has(s) && isValidSelector(s));
  }

  function buildCss(selectors) {
    const rules = [];
    for (let i = 0; i < selectors.length; i += SELECTORS_PER_RULE) {
      rules.push(`${selectors.slice(i, i + SELECTORS_PER_RULE).join(',\n')} { display: none !important; }`);
    }
    return rules.join('\n');
  }

  async function send(message) {
    try {
      const response = await chrome.runtime.sendMessage(message);
      return !!response?.ok;
    } catch {
      return false; // extension was reloaded or the worker is unavailable
    }
  }

  /* ------------------------------ Styles -------------------------------- */

  let appliedCss = '';
  let activeSelector = '';
  let fallbackStyle = null;

  async function setCss(css) {
    if (css === appliedCss) return;
    const previous = appliedCss;
    appliedCss = css;

    // Insert the new sheet before removing the old one so ads never flash.
    const inserted = !css || (await send({ type: 'cosmetic:insert', css }));
    if (inserted) {
      fallbackStyle?.remove();
      fallbackStyle = null;
    } else {
      fallbackStyle ??= document.createElement('style');
      fallbackStyle.textContent = css;
      (document.head ?? document.documentElement).append(fallbackStyle);
    }
    if (previous) await send({ type: 'cosmetic:remove', css: previous });
  }

  async function refresh() {
    let stored;
    try {
      stored = await chrome.storage.local.get(['settings', 'allowlist', 'userCosmetic']);
    } catch {
      return;
    }
    const settings = {
      ...DATA.defaults,
      ...stored.settings,
      lists: { ...DATA.defaults.lists, ...stored.settings?.lists },
    };
    const paused = (stored.allowlist ?? []).some((d) => matches(topHost, d));
    const selectors = settings.enabled && !paused ? collectSelectors(settings, stored.userCosmetic) : [];

    activeSelector = selectors.join(',');
    await setCss(buildCss(selectors));
    scheduleCount();
  }

  let pending = Promise.resolve();
  const queueRefresh = () => (pending = pending.then(refresh, refresh));

  /* ------------------------------ Counting ------------------------------ */

  let reported = 0;
  let countTimer = 0;

  function scheduleCount() {
    countTimer ||= setTimeout(countHidden, COUNT_DELAY_MS);
  }

  function countHidden() {
    countTimer = 0;
    let count = 0;
    if (activeSelector) {
      try {
        count = document.querySelectorAll(activeSelector).length;
      } catch {
        // A selector valid on its own can still fail in a list; skip counting.
      }
    }
    if (count !== reported) {
      reported = count;
      send({ type: 'cosmetic:count', count });
    }
  }

  new MutationObserver(scheduleCount).observe(document, { childList: true, subtree: true });

  chrome.storage.onChanged.addListener((changes, area) => {
    if (area === 'local' && ('settings' in changes || 'allowlist' in changes || 'userCosmetic' in changes)) {
      queueRefresh();
    }
  });

  queueRefresh();
})();
