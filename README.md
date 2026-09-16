<p align="center">
  <img src="icons/icon-128.png" width="96" height="96" alt="" />
</p>

<h1 align="center">AdBlocker Plugin</h1>

<p align="center">
  A fast, private ad and tracker blocker for Google Chrome, built on Manifest V3.
</p>

---

## Features

- **Network blocking** with Chrome's native `declarativeNetRequest` engine. Ads and trackers are
  stopped before they download, and the extension never has to inspect your traffic.
- **Element hiding** removes leftover ad slots, sponsored widgets and (optionally) cookie banners.
  The CSS is injected as a *user-origin* stylesheet, so pages cannot override or remove it.
- **Per-site pause**: one click in the popup turns blocking off for the current site and its
  subdomains. The toolbar icon turns grey on paused sites.
- **Element picker**: point at anything on a page and hide it for good. The picker skips
  auto-generated class names so rules keep working after the site redeploys.
- **Your own filters** in Adblock Plus / uBlock Origin syntax, with per-line error reporting.
- **Four built-in lists** that can be switched on and off: Ads, Trackers, Social widgets and Annoyances.
- **Live statistics**: a badge count per page, plus requests blocked and elements hidden per page and
  since install.
- **Light and dark themes**, keyboard accessible, no remote code, no analytics.

## Install

The extension isn't on the Chrome Web Store, so you load it in developer mode:

1. Download or clone this repository.
   ```bash
   git clone https://github.com/teykaijun/AdBlockerPlugin.git
   ```
2. Open `chrome://extensions` in Chrome.
3. Turn on **Developer mode** (top-right corner).
4. Click **Load unpacked** and select the `AdBlockerPlugin` folder, the one that contains `manifest.json`.
5. Pin **AdBlocker** from the puzzle-piece menu so the icon stays visible.

No build step is needed: the compiled rulesets in `generated/` are committed. Requires Chrome 116 or later.

To update, `git pull` and click the reload icon on the extension's card in `chrome://extensions`.

## Using it

| Where | What you can do |
| --- | --- |
| **Toolbar badge** | Number of requests blocked on the current page. |
| **Popup → power button** | Pause or resume blocking on the current site. Reload the page to apply. |
| **Popup → switch** | Turn blocking off or on for every site. |
| **Popup → Hide an element** | Start the element picker. Click an element, adjust the selector if you want, then click **Hide element**. Press <kbd>Esc</kbd> to cancel and <kbd>↑</kbd> to select the parent element. |
| **Settings** | Turn filter lists on or off, manage allowed sites, edit your own filters, view or reset statistics. |

## Filter syntax

Custom filters (Settings → My filters) use a subset of the Adblock Plus syntax:

| Filter | Meaning |
| --- | --- |
| `\|\|ads.example.com^` | Block a domain and all of its subdomains |
| `ads.example.com` or `0.0.0.0 ads.example.com` | Same as above (hosts-file style) |
| `\|\|example.com/banners/*` | Block URLs matching a pattern (`*` wildcard, `^` separator, `\|` anchor) |
| `/track(ing)?\d+\.js/` | Block URLs matching a regular expression |
| `\|\|cdn.example.com^$script,third-party` | Only scripts, only when loaded by another site |
| `\|\|widgets.example^$domain=news.com\|~blog.news.com` | Only on news.com, but not on blog.news.com |
| `@@\|\|example.com/player.js` | Exception: never block this URL |
| `@@\|\|example.com^$document` | Exception: turn blocking off on a whole site |
| `##.sponsored` | Hide matching elements on every site |
| `example.com##.promo-box` | Hide matching elements on example.com only |
| `example.com#@#.ad-banner` | Stop a list from hiding `.ad-banner` on example.com |
| `! comment` | Ignored |

**Supported options:** resource types (`script`, `image`, `stylesheet`, `xhr`, `subdocument`,
`media`, `font`, `ping`, `websocket`, `object`, `other`, `document`, `all`, and `~type` to exclude),
`third-party` / `3p`, `first-party` / `1p`, `domain=` / `from=`, `to=`, `denyallow=`, `method=`,
`match-case` and `important`. `:has()` (and `:-abp-has()`) work in element-hiding selectors.

**Not supported:** scriptlets (`##+js(...)`), procedural selectors (`:has-text()`, `:xpath()`, …),
`$redirect`, `$removeparam`, `$csp` and `$popup`. These are reported as errors instead of being
silently ignored.

## How it works

```
Build time
  filters/*.txt ──► scripts/build.mjs ──┬──► generated/rulesets/*.json    static DNR rulesets
                    (filter-parser.js)  └──► generated/cosmetic-data.js   element-hiding data

Run time
  popup ──────────┐
  options page ───┼── messages ──► service worker ──────────► declarativeNetRequest
  picker.js ──────┘                 · only writer to storage      · static ruleset per list
                                    · counts blocked requests     · dynamic: paused sites
                                    · insertCSS (user origin)       + your own filters
                                          │         ▲
                               storage    │         │ "insert this CSS"
                               changes    ▼         │
                                    cosmetic.js (every frame, document_start)
```

- **One parser everywhere.** `src/shared/filter-parser.js` is used both at build time (built-in
  lists) and at run time in the service worker (your own filters), so both behave the same.
- **Rule compaction.** Plain domain filters with identical options are merged into a single DNR rule
  with a `requestDomains` list. The 88 domains in the ads list become one rule.
- **Priorities.** Block = 1, exception = 2, `$important` block = 3, `$important` exception = 4, and
  paused sites = 100, so pausing a site always wins.
- **Rule ids** below 1,000,000 are blocking rules and ids above are exceptions. The match events
  Chrome reports only contain rule ids, and this is how the statistics tell the two apart.
- **Element hiding** reads settings straight from `chrome.storage` (no service-worker round trip to
  decide what to hide), validates each selector, and swaps stylesheets without a flash when settings
  change.

## Project layout

```
manifest.json
filters/                 Source filter lists (edit these)
generated/               Build output, committed so the extension loads without Node
icons/                   Toolbar and store icons (generated by scripts/make-icons.mjs)
scripts/
  build.mjs              filters/*.txt → generated/*
  make-icons.mjs         Renders the PNG icons, no dependencies
src/
  background/service-worker.js
  content/cosmetic.js    Element hiding
  content/picker.js      Element picker
  popup/                 Toolbar popup
  options/               Settings page
  shared/                Parser, defaults, host helpers, messaging
  ui/base.css            Design tokens and shared components
tests/                   node:test suites
```

## Development

Requires Node.js 20 or later. There are no npm dependencies.

```bash
npm test          # parser, host helpers, build output and content-script tests
npm run build     # recompile filters/*.txt into generated/
npm run check     # fail if generated/ is out of date (used in CI)
npm run icons     # re-render icons/*.png
```

To add a filter to a built-in list, edit the file in `filters/`, run `npm run build`, then reload
the extension in `chrome://extensions`. The build fails on any line the parser can't handle.

To add a new list, add an entry to `FILTER_LISTS` in `src/shared/defaults.js`, create
`filters/<id>.txt` and add a matching `rule_resources` entry to `manifest.json`. The build checks
that these three agree.

**Debugging:** open `chrome://extensions`, then click **service worker** on the AdBlocker card to see
the background console. Blocked-request counting uses `onRuleMatchedDebug`, which Chrome only
provides to unpacked extensions. Packed builds fall back to `getMatchedRules`, which is rate
limited, so the popup refreshes that number at most every 30 seconds.

## Permissions

| Permission | Why |
| --- | --- |
| `declarativeNetRequest` | Block requests using the rulesets. |
| `declarativeNetRequestFeedback` | Count blocked requests for the popup and statistics. |
| `scripting` | Inject element-hiding CSS and the element picker. |
| `storage` | Save settings, allowed sites, your filters and statistics. |
| `webNavigation` | Reset per-page counters when a tab navigates. |
| Host access to all sites | Hide elements on any page and update the toolbar icon per tab. |

## Limitations

- **Video ads on YouTube** are served from the same servers as the videos, so network rules can't
  remove them without breaking playback. Blocking them needs script injection, which this extension
  doesn't do. Banner, feed and sidebar ads on YouTube *are* hidden.
- Manifest V3 has no equivalent of uBlock Origin's scriptlets or response filtering, so some
  anti-adblock walls can't be bypassed.
- Hiding a cookie banner (Annoyances list) doesn't answer it. A few sites keep the page locked
  until you do.

## Privacy

Everything runs locally. The extension makes no network requests of its own, collects no data, and
loads no remote code.

## License

[MIT](LICENSE)
