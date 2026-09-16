/**
 * Parses a subset of the Adblock Plus / uBlock Origin filter syntax and turns
 * it into Chrome declarativeNetRequest rules plus cosmetic (CSS) selectors.
 *
 * Supported
 *   ! comment, [Adblock Plus 2.0]
 *   ||ads.example.com^            block a domain and its subdomains
 *   example.com / 0.0.0.0 host    hosts-file style, same as above
 *   /banner/*.gif                 URL pattern (| || ^ * anchors)
 *   /ads\d+\.js/                  regular expression
 *   @@||example.com^              exception
 *   $script,image,~font           resource types
 *   $third-party / $3p / $1p      party
 *   $domain=a.com|~b.com          initiator (page) domains
 *   $to=a.com|~b.com, $denyallow  request domains
 *   $method=get|~post, $match-case, $important, $all, $document
 *   ##.ad, example.com##.ad       element hiding
 *   example.com#@#.ad             element hiding exception
 *
 * Anything else (scriptlets, procedural selectors, $redirect, $csp, …) is
 * reported as an error for that line instead of being silently dropped.
 */

import { ALLOW_RULE_ID_BASE, RULE_PRIORITY } from './defaults.js';
import { isValidHostname } from './hosts.js';

const TYPE_OPTIONS = {
  script: ['script'],
  image: ['image'],
  stylesheet: ['stylesheet'],
  css: ['stylesheet'],
  object: ['object'],
  'object-subrequest': ['object'],
  xmlhttprequest: ['xmlhttprequest'],
  xhr: ['xmlhttprequest'],
  subdocument: ['sub_frame'],
  frame: ['sub_frame'],
  ping: ['ping'],
  beacon: ['ping'],
  media: ['media'],
  font: ['font'],
  websocket: ['websocket'],
  webtransport: ['webtransport'],
  other: ['other', 'csp_report', 'webbundle'],
  document: ['main_frame', 'sub_frame'],
  doc: ['main_frame', 'sub_frame'],
};

const ALL_TYPES = [...new Set(Object.values(TYPE_OPTIONS).flat())];
const FRAME_TYPES = ['main_frame', 'sub_frame'];
const HTTP_METHODS = new Set(['connect', 'delete', 'get', 'head', 'options', 'patch', 'post', 'put', 'other']);

const UNSUPPORTED_SELECTOR_RE =
  /:(-abp-contains|-abp-properties|has-text|contains|xpath|upward|matches-css(-before|-after)?|matches-attr|matches-path|min-text-length|watch-attr|remove|remove-attr|remove-class|style|others|if|if-not)\(/;

const FILE_EXTENSIONS = new Set(['js', 'mjs', 'css', 'gif', 'png', 'jpg', 'jpeg', 'webp', 'svg', 'swf', 'php', 'htm', 'html', 'json', 'xml', 'txt', 'mp4', 'webm']);

const OPTIONS_RE =/^~?[\w-]+(=[^,]*)?(,~?[\w-]+(=[^,]*)?)*$/;
const COSMETIC_RE = /^([a-z0-9.*~,_-]*)(#@?[?$%]?#)(.+)$/i;
const HOSTS_FILE_RE = /^(?:0\.0\.0\.0|127\.0\.0\.1|::1?)\s+(\S+)/;

class FilterError extends Error {}

const fail = (message) => {
  throw new FilterError(message);
};

/**
 * @param {string} text  Filter list source.
 * @returns {{
 *   network: Array<{ line: number, allow: boolean, rule: object }>,
 *   cosmetic: { generic: string[], byHost: Record<string, string[]>, exceptions: Record<string, string[]> },
 *   errors: Array<{ line: number, text: string, message: string }>,
 * }}
 */
export function parseFilterList(text) {
  const network = [];
  const cosmetic = { generic: new Set(), byHost: new Map(), exceptions: new Map() };
  const errors = [];

  const lines = String(text ?? '').split(/\r?\n/);
  lines.forEach((raw, index) => {
    const line = raw.trim();
    if (isComment(line)) return;
    try {
      const cos = COSMETIC_RE.exec(line);
      if (cos) {
        parseCosmetic(cos, cosmetic);
      } else {
        network.push({ line: index + 1, ...parseNetwork(line) });
      }
    } catch (err) {
      if (!(err instanceof FilterError)) throw err;
      errors.push({ line: index + 1, text: line, message: err.message });
    }
  });

  return {
    network,
    cosmetic: {
      generic: [...cosmetic.generic],
      byHost: Object.fromEntries([...cosmetic.byHost].map(([h, s]) => [h, [...s]])),
      exceptions: Object.fromEntries([...cosmetic.exceptions].map(([h, s]) => [h, [...s]])),
    },
    errors,
  };
}

// `!` and `[Adblock Plus]` headers are ABP comments; `#` is a hosts-file
// comment unless it starts a cosmetic separator such as `##` or `#@#`.
const isComment = (line) =>
  !line || line.startsWith('!') || line.startsWith('[') || (line.startsWith('#') && !/^#@?[?$%]?#/.test(line));

/* -------------------------------------------------------------------------- */
/* Cosmetic filters                                                           */
/* -------------------------------------------------------------------------- */

function parseCosmetic([, domainPart, separator, body], out) {
  if (separator !== '##' && separator !== '#@#') {
    fail(`"${separator}" filters (scriptlets / procedural) are not supported`);
  }
  const selector = normalizeSelector(body);
  const { include, exclude } = parseDomainList(domainPart, ',');
  const add = (map, host) => {
    if (!map.has(host)) map.set(host, new Set());
    map.get(host).add(selector);
  };

  if (separator === '#@#') {
    if (!include.length) fail('Element hiding exceptions need at least one domain');
    include.forEach((h) => add(out.exceptions, h));
    return;
  }

  if (include.length) {
    include.forEach((h) => add(out.byHost, h));
  } else {
    out.generic.add(selector);
  }
  // `~sub.example.com##.ad` means "everywhere the rule applies, except here".
  exclude.forEach((h) => add(out.exceptions, h));
}

function normalizeSelector(body) {
  let selector = body.trim();
  if (selector.startsWith('+js(')) fail('Scriptlet injection is not supported');
  selector = selector.replaceAll(':-abp-has(', ':has(');
  if (UNSUPPORTED_SELECTOR_RE.test(selector)) fail('Procedural cosmetic selectors are not supported');
  // Selectors are concatenated into a stylesheet, so anything that could end
  // the rule block or open a comment is rejected outright.
  if (/[{}]|\/\*/.test(selector)) fail('Selector contains characters that are not allowed ({, } or /*)');
  if (!selector) fail('Empty selector');
  return selector;
}

/* -------------------------------------------------------------------------- */
/* Network filters                                                            */
/* -------------------------------------------------------------------------- */

function parseNetwork(line) {
  const hosts = HOSTS_FILE_RE.exec(line);
  if (hosts) {
    const host = hosts[1].toLowerCase();
    if (!isValidHostname(host)) fail(`Invalid hostname "${hosts[1]}"`);
    if (host === 'localhost' || host === '0.0.0.0') fail('Refusing to block localhost');
    return buildRule({ allow: false, pattern: `||${host}^`, options: '' });
  }

  let rest = line;
  let allow = false;
  if (rest.startsWith('@@')) {
    allow = true;
    rest = rest.slice(2);
  }

  let pattern = rest;
  let options = '';
  const regexEnd = rest.startsWith('/') ? rest.lastIndexOf('/$') : -1;
  if (regexEnd > 0) {
    pattern = rest.slice(0, regexEnd + 1);
    options = rest.slice(regexEnd + 2);
  } else {
    const dollar = rest.lastIndexOf('$');
    if (dollar !== -1 && OPTIONS_RE.test(rest.slice(dollar + 1))) {
      pattern = rest.slice(0, dollar);
      options = rest.slice(dollar + 1);
    }
  }

  return buildRule({ allow, pattern, options });
}

function buildRule({ allow, pattern, options }) {
  const condition = {};
  const types = new Set();
  const excludedTypes = new Set();
  let important = false;

  for (const opt of options ? options.split(',') : []) {
    const [rawName, ...valueParts] = opt.split('=');
    const value = valueParts.join('=');
    const negated = rawName.startsWith('~');
    const name = (negated ? rawName.slice(1) : rawName).toLowerCase();

    if (TYPE_OPTIONS[name]) {
      TYPE_OPTIONS[name].forEach((t) => (negated ? excludedTypes : types).add(t));
      continue;
    }

    switch (name) {
      case 'all':
        if (negated) fail('"~all" is not a valid option');
        ALL_TYPES.forEach((t) => types.add(t));
        break;
      case 'third-party':
      case '3p':
        condition.domainType = negated ? 'firstParty' : 'thirdParty';
        break;
      case 'first-party':
      case '1p':
        condition.domainType = negated ? 'thirdParty' : 'firstParty';
        break;
      case 'domain':
      case 'from': {
        const { include, exclude } = parseDomainList(value, '|');
        if (include.length) condition.initiatorDomains = include;
        if (exclude.length) condition.excludedInitiatorDomains = exclude;
        break;
      }
      case 'to': {
        const { include, exclude } = parseDomainList(value, '|');
        if (include.length) condition.requestDomains = include;
        if (exclude.length) condition.excludedRequestDomains = exclude;
        break;
      }
      case 'denyallow': {
        const { include } = parseDomainList(value, '|');
        condition.excludedRequestDomains = include;
        break;
      }
      case 'method': {
        const include = [];
        const exclude = [];
        for (const m of value.toLowerCase().split('|')) {
          const neg = m.startsWith('~');
          const method = neg ? m.slice(1) : m;
          if (!HTTP_METHODS.has(method)) fail(`Unknown HTTP method "${method}"`);
          (neg ? exclude : include).push(method);
        }
        if (include.length) condition.requestMethods = include;
        if (exclude.length) condition.excludedRequestMethods = exclude;
        break;
      }
      case 'match-case':
        condition.isUrlFilterCaseSensitive = !negated;
        break;
      case 'important':
        important = true;
        break;
      default:
        fail(`Unsupported option "$${name}"`);
    }
  }

  applyPattern(pattern, condition);

  const isDocumentAllow = allow && types.has('main_frame');
  if (isDocumentAllow) {
    if ([...types].some((t) => !FRAME_TYPES.includes(t))) {
      fail('$document exceptions cannot be combined with other resource types');
    }
  } else if (types.size) {
    condition.resourceTypes = [...types].sort();
  } else if (excludedTypes.size) {
    condition.excludedResourceTypes = [...excludedTypes].sort();
  }
  if (isDocumentAllow) condition.resourceTypes = [...FRAME_TYPES];

  if (!allow && !condition.urlFilter && !condition.regexFilter && !condition.requestDomains && !condition.initiatorDomains) {
    fail('Filter is too broad: it would block every request');
  }

  let priority;
  if (allow) priority = important ? RULE_PRIORITY.importantAllow : RULE_PRIORITY.allow;
  else priority = important ? RULE_PRIORITY.importantBlock : RULE_PRIORITY.block;

  const type = isDocumentAllow ? 'allowAllRequests' : allow ? 'allow' : 'block';
  return { allow, rule: { priority, action: { type }, condition } };
}

function applyPattern(rawPattern, condition) {
  let pattern = rawPattern.trim();
  if (pattern === '' || pattern === '*' || pattern === '|' || pattern === '||') return;

  if (pattern.length > 2 && pattern.startsWith('/') && pattern.endsWith('/')) {
    const regex = pattern.slice(1, -1);
    try {
      new RegExp(regex);
    } catch {
      fail('Invalid regular expression');
    }
    condition.regexFilter = regex;
    return;
  }

  // A bare hostname is treated like `||hostname^` (uBlock Origin behaviour),
  // unless it is obviously a file name such as `ads.js`.
  const bareHost = /^[a-z0-9-]+(\.[a-z0-9-]+)+$/i.exec(pattern);
  const tld = pattern.slice(pattern.lastIndexOf('.') + 1).toLowerCase();
  if (bareHost && !FILE_EXTENSIONS.has(tld) && isValidHostname(pattern.toLowerCase())) {
    pattern = `||${pattern}^`;
  }

  const domainOnly = /^\|\|([a-z0-9.-]+)\^$/i.exec(pattern);
  if (domainOnly && isValidHostname(domainOnly[1].toLowerCase()) && !condition.requestDomains) {
    condition.requestDomains = [domainOnly[1].toLowerCase()];
    return;
  }

  // eslint-disable-next-line no-control-regex
  if (!/^[\x00-\x7f]*$/.test(pattern)) fail('URL patterns must be ASCII (use punycode for domains)');
  if (pattern.startsWith('||*')) pattern = pattern.slice(2);
  condition.urlFilter = pattern;
}

function parseDomainList(value, separator) {
  const include = [];
  const exclude = [];
  if (!value) return { include, exclude };
  for (const entry of value.split(separator)) {
    const neg = entry.startsWith('~');
    const domain = (neg ? entry.slice(1) : entry).trim().toLowerCase();
    if (!domain) continue;
    if (domain.endsWith('.*')) fail(`Wildcard TLD domains ("${domain}") are not supported`);
    if (!isValidHostname(domain)) fail(`Invalid domain "${domain}"`);
    (neg ? exclude : include).push(domain);
  }
  return { include, exclude };
}

/* -------------------------------------------------------------------------- */
/* Rule compilation                                                           */
/* -------------------------------------------------------------------------- */

/**
 * Merges rules that differ only in a single `requestDomains` entry into one
 * rule (Chrome counts rules, not domains, against its limits), then assigns
 * ids: block rules count up from 1, allow rules from ALLOW_RULE_ID_BASE.
 *
 * @returns {Array<{ rule: object, lines: number[] }>}
 */
export function compileNetworkRules(entries, { blockIdStart = 1, allowIdStart = ALLOW_RULE_ID_BASE } = {}) {
  const groups = new Map();
  const singles = [];

  for (const entry of entries) {
    const { condition } = entry.rule;
    if (condition.requestDomains?.length === 1) {
      const { requestDomains, ...rest } = condition;
      const key = JSON.stringify({ ...entry.rule, condition: sortKeys(rest) });
      if (!groups.has(key)) groups.set(key, { allow: entry.allow, rule: entry.rule, domains: new Set(), lines: [] });
      const g = groups.get(key);
      g.domains.add(requestDomains[0]);
      g.lines.push(entry.line);
    } else {
      singles.push({ allow: entry.allow, rule: entry.rule, lines: [entry.line] });
    }
  }

  const merged = [...groups.values()].map((g) => ({
    allow: g.allow,
    lines: g.lines,
    rule: { ...g.rule, condition: { ...g.rule.condition, requestDomains: [...g.domains].sort() } },
  }));

  let blockId = blockIdStart;
  let allowId = allowIdStart;
  return [...merged, ...singles].map(({ allow, rule, lines }) => ({
    lines,
    rule: { id: allow ? allowId++ : blockId++, ...rule },
  }));
}

const sortKeys = (obj) => Object.fromEntries(Object.entries(obj).sort(([a], [b]) => a.localeCompare(b)));
