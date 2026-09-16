/**
 * Single source of truth for settings, filter-list metadata and rule
 * conventions. Imported by the service worker, the UI pages and the build
 * script (which also embeds the defaults into the content-script data file).
 */

/**
 * Built-in filter lists. Each id maps to `filters/<id>.txt` and a DNR ruleset.
 * `androidDescription` overrides the text shown in the Android app, which can
 * only block whole domains.
 */
export const FILTER_LISTS = [
  {
    id: 'ads',
    title: 'Ads',
    description: 'Blocks ad networks and hides empty ad slots.',
    androidDescription: 'Blocks ad networks in apps and websites.',
    enabledByDefault: true,
  },
  {
    id: 'trackers',
    title: 'Trackers',
    description: 'Blocks analytics scripts, tracking pixels and data brokers.',
    enabledByDefault: true,
  },
  {
    id: 'social',
    title: 'Social widgets',
    description: 'Blocks share buttons and embedded social widgets. May break "Log in with…" buttons.',
    enabledByDefault: false,
  },
  {
    id: 'annoyances',
    title: 'Annoyances',
    description: 'Hides cookie banners and newsletter pop-ups. A few sites may stop scrolling.',
    enabledByDefault: false,
  },
];

export const DEFAULT_SETTINGS = Object.freeze({
  enabled: true,
  lists: Object.fromEntries(FILTER_LISTS.map((l) => [l.id, l.enabledByDefault])),
});

/**
 * Rule priorities. Higher wins inside this extension. The allowlist sits far
 * above everything so pausing a site always beats `$important` filters.
 */
export const RULE_PRIORITY = Object.freeze({
  block: 1,
  allow: 2,
  importantBlock: 3,
  importantAllow: 4,
  allowlist: 100,
});

/**
 * Allow-type rules get ids from this base upwards, block rules stay below it.
 * DNR match events only report rule ids, so this is how the stats code tells
 * a blocked request apart from an allowed one.
 */
export const ALLOW_RULE_ID_BASE = 1_000_000;

export const isBlockingRuleId = (id) => id < ALLOW_RULE_ID_BASE;

/** Tab-level stats are pushed to the popup at most this often. */
export const STATS_FLUSH_MS = 1000;

export function mergeSettings(stored) {
  return {
    ...DEFAULT_SETTINGS,
    ...stored,
    lists: { ...DEFAULT_SETTINGS.lists, ...stored?.lists },
  };
}
