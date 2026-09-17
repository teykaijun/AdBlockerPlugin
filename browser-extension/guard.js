/**
 * Decides what to do when a page opens a tab or navigates somewhere that AdBlocker blocks.
 *
 * AdBlocker for Windows already stops those pages from loading, but the browser still
 * opens the tab (or leaves the current tab on an error page). The guard closes such
 * pop-up tabs and sends redirected tabs back to where they were.
 *
 * The browser APIs are passed in, so this file has no dependency on `chrome.*` and can
 * be tested with Node.
 */

/** Navigation errors that mean "the name could not be looked up", which is how a DNS blocker shows. */
export const DNS_ERRORS = new Set(['net::ERR_NAME_NOT_RESOLVED', 'net::ERR_NAME_RESOLUTION_FAILED']);

/** How long a tab opened by a page is treated as a possible pop-up. */
export const POPUP_WATCH_MS = 15_000;

/**
 * How many times in a row one tab may be sent back, counting go-backs less than `withinMs`
 * apart. A page that redirects again as soon as it loads would otherwise bounce forever;
 * after that the error page stays.
 */
export const BACK_LIMIT = { times: 3, withinMs: 10_000 };

/** The host of an http(s) URL worth asking about, or null (IP addresses, single-label names, other schemes). */
export function hostOf(url) {
  let parsed;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null;
  const host = parsed.hostname.toLowerCase().replace(/\.$/, '');
  if (!host.includes('.') || host.startsWith('[') || /^[\d.]+$/.test(host)) return null;
  return host;
}

export class Guard {
  /** Tabs opened by pages: tabId → time opened. */
  #popups = new Map();

  /** Tabs this guard closed. Browsers never reuse tab ids, so later events for them are stale. */
  #closed = new Set();

  /** Tabs this guard sent back: tabId → { last, times }. */
  #wentBack = new Map();

  /**
   * @param {object} deps
   * @param {(host: string) => Promise<boolean>} deps.isBlocked asks AdBlocker
   * @param {(tabId: number) => Promise<void>} deps.closeTab
   * @param {(tabId: number) => Promise<void>} deps.goBack rejects when there is no previous page
   * @param {(kind: 'closed' | 'redirect', host: string) => void} deps.report
   * @param {() => number} [deps.now]
   */
  constructor({ isBlocked, closeTab, goBack, report, now = () => Date.now() }) {
    this.isBlocked = isBlocked;
    this.closeTab = closeTab;
    this.goBack = goBack;
    this.report = report;
    this.now = now;
  }

  /** A page opened a new tab or window (webNavigation.onCreatedNavigationTarget). */
  async onTabOpened({ tabId, url }) {
    if (this.#closed.has(tabId)) return;
    this.#popups.set(tabId, this.now());
    await this.#closeIfBlocked(tabId, url);
  }

  /** A tab starts loading a new page (webNavigation.onBeforeNavigate). */
  async onBeforeNavigate({ tabId, frameId, url }) {
    // Pop-ups often open about:blank first, or pass through a redirector.
    if (frameId === 0 && this.#isPopup(tabId)) await this.#closeIfBlocked(tabId, url);
  }

  /** A page failed to load (webNavigation.onErrorOccurred). */
  async onNavigationError({ tabId, frameId, url, error }) {
    if (frameId !== 0 || !DNS_ERRORS.has(error) || this.#closed.has(tabId)) return;
    const host = hostOf(url);
    if (!host || !(await this.isBlocked(host)) || this.#closed.has(tabId)) return;

    if (this.#isPopup(tabId)) {
      await this.#close(tabId, host);
      return;
    }
    // The tab itself was sent to an ad. The page couldn't load anyway, so step back.
    if (!this.#mayGoBack(tabId)) return;
    try {
      await this.goBack(tabId);
      this.report('redirect', host);
    } catch {
      // Nothing to go back to (a new tab); leave the error page.
    }
  }

  /** A tab was closed (tabs.onRemoved). */
  onTabRemoved(tabId) {
    this.#popups.delete(tabId);
    this.#wentBack.delete(tabId);
  }

  #mayGoBack(tabId) {
    const now = this.now();
    const recent = this.#wentBack.get(tabId);
    const times = recent && now - recent.last <= BACK_LIMIT.withinMs ? recent.times + 1 : 1;
    if (times > BACK_LIMIT.times) return false;
    this.#wentBack.set(tabId, { last: now, times });
    return true;
  }

  #isPopup(tabId) {
    const opened = this.#popups.get(tabId);
    if (opened === undefined) return false;
    if (this.now() - opened <= POPUP_WATCH_MS) return true;
    this.#popups.delete(tabId);
    return false;
  }

  async #closeIfBlocked(tabId, url) {
    const host = hostOf(url);
    if (host && (await this.isBlocked(host))) await this.#close(tabId, host);
  }

  async #close(tabId, host) {
    if (!this.#popups.has(tabId)) return; // already handled
    this.#popups.delete(tabId);
    this.#closed.add(tabId);
    if (this.#closed.size > 1_000) this.#closed.delete(this.#closed.values().next().value);
    try {
      await this.closeTab(tabId);
      this.report('closed', host);
    } catch {
      // The tab is already gone.
    }
  }
}
