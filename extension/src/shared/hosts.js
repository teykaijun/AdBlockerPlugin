/** Hostname helpers shared by the service worker, UI pages and the parser. */

const HOSTNAME_RE = /^(?=.{1,253}$)(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))*$/;

export function isValidHostname(host) {
  return typeof host === 'string' && HOSTNAME_RE.test(host);
}

/** Lower-cases, trims a trailing dot and drops a leading `www.`. */
export function normalizeHost(host) {
  let h = String(host ?? '').trim().toLowerCase();
  if (h.endsWith('.')) h = h.slice(0, -1);
  if (h.startsWith('www.')) h = h.slice(4);
  return h;
}

/**
 * Accepts whatever a user pastes into the "allow a site" box
 * (`https://www.example.com/path`, `example.com`, ` Example.COM `)
 * and returns a normalized hostname, or null.
 */
export function parseHostInput(input) {
  const raw = String(input ?? '').trim();
  if (!raw) return null;
  let host = raw;
  try {
    host = new URL(raw.includes('://') ? raw : `http://${raw}`).hostname;
  } catch {
    return null;
  }
  host = normalizeHost(host);
  return isValidHostname(host) ? host : null;
}

/** `a.b.example.com` → [`a.b.example.com`, `b.example.com`, `example.com`, `com`] */
export function hostSuffixes(host) {
  const out = [];
  let h = host;
  while (h) {
    out.push(h);
    const dot = h.indexOf('.');
    if (dot === -1) break;
    h = h.slice(dot + 1);
  }
  return out;
}

/** True when `host` is `domain` or a subdomain of it. */
export function hostMatches(host, domain) {
  return host === domain || host.endsWith(`.${domain}`);
}

export function isAllowlisted(host, allowlist) {
  return !!host && allowlist.some((d) => hostMatches(host, d));
}

export function isWebUrl(url) {
  return url?.protocol === 'http:' || url?.protocol === 'https:';
}
