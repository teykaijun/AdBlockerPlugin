<p align="center">
  <img src="extension/icons/icon-128.png" width="96" height="96" alt="" />
</p>

<h1 align="center">AdBlocker Plugin</h1>

<p align="center">
  A fast, private ad and tracker blocker for Google Chrome, plus an Android app that blocks ads across the whole phone.
</p>

---

|  | Chrome extension | Android app |
| --- | --- | --- |
| **Blocks** | Ad and tracker requests, leftover ad slots, cookie banners (optional) | Ad and tracker domains in every app and browser |
| **How** | Chrome's `declarativeNetRequest` engine plus element hiding | A local VPN that only carries DNS lookups |
| **Filter lists** | Built-in lists and your own filters | The same built-in lists, optional community lists and your own domains |
| **Install** | *Load unpacked* from [`extension/`](extension) | Sideload the APK |

Chrome for Android doesn't run extensions, which is why the phone version is a separate app.

## Chrome extension

### Features

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

### Install

The extension isn't on the Chrome Web Store, so you load it in developer mode:

1. Download or clone this repository.
   ```bash
   git clone https://github.com/teykaijun/AdBlockerPlugin.git
   ```
2. Open `chrome://extensions` in Chrome.
3. Turn on **Developer mode** (top-right corner).
4. Click **Load unpacked** and select the **`extension`** folder inside the repository (the one that
   contains `manifest.json`).
5. Pin **AdBlocker** from the puzzle-piece menu so the icon stays visible.

No build step is needed: the compiled rulesets in `extension/generated/` are committed. Requires
Chrome 116 or later.

To update, `git pull` and click the reload icon on the extension's card in `chrome://extensions`.

### Using it

| Where | What you can do |
| --- | --- |
| **Toolbar badge** | Number of requests blocked on the current page. |
| **Popup → power button** | Pause or resume blocking on the current site. Reload the page to apply. |
| **Popup → switch** | Turn blocking off or on for every site. |
| **Popup → Hide an element** | Start the element picker. Click an element, adjust the selector if you want, then click **Hide element**. Press <kbd>Esc</kbd> to cancel and <kbd>↑</kbd> to select the parent element. |
| **Settings** | Turn filter lists on or off, manage allowed sites, edit your own filters, view or reset statistics. |

## Android app

### Features

- **Blocks ads everywhere on the phone**: in apps, games and every browser, including Chrome.
- **Activity log** of recent lookups, each marked Blocked or Allowed. Tap one to always allow or
  always block that domain.
- **Filter lists**: the same built-in Ads, Trackers and Social lists as the extension, plus optional
  community lists downloaded when you turn them on and refreshed weekly (HaGeZi Multi Light and Normal,
  AdGuard DNS filter, StevenBlack hosts). You can also add any hosts file or Adblock-style list by URL.
- **Your own blocked and allowed domains**, which always win over the lists.
- **Per-app bypass** for apps that misbehave while filtering is on.
- **DNS server of your choice**: your network's own, Cloudflare, Quad9, Google or any IP address.
- **Quick Settings tile**, a status notification with a **Pause** button, restart after reboot and
  support for Android's Always-on VPN.
- **Warns you** when Private DNS is set up in a way that would bypass the app.
- Material 3 design with light, dark and themed-icon support. No ads, no analytics, no account.

### Install

Requires Android 8.0 or later. Google Play doesn't allow apps that block ads in other apps, so the
app is installed from an APK file:

1. Get `app-release.apk`: open the repository's **Actions** tab → latest **CI** run → **Artifacts** →
   `AdBlocker-android`, or [build it yourself](#android-app-1).
2. Copy it to the phone and open it. Allow installing apps from that source when Android asks.
3. Open **AdBlocker**, tap the power button, and accept Android's VPN connection request.

For best results:

- Turn on **Always-on VPN** for AdBlocker (the Home screen has a shortcut), but leave **Block
  connections without VPN** off. AdBlocker only routes DNS, so that option would cut off all other traffic.
- Keep **Private DNS** on *Automatic* or *Off*. A named Private DNS server bypasses the app.
- In Chrome, keep **Settings → Privacy → Use secure DNS** on the default setting (your current service
  provider). A specific secure DNS provider bypasses the app.

An APK can only update an installed copy that was signed with the same key. APKs built on different
machines, or by CI without the signing secrets below, use different debug keys. To switch between
them, uninstall the app first.

### Using it

| Tab | What you can do |
| --- | --- |
| **Home** | Turn protection on or off and see today's numbers. |
| **Activity** | Browse and search recent lookups. Tap one to always allow or block it. |
| **Filters** | Turn lists on or off, add a list by URL, manage your blocked and allowed domains. |
| **Apps** | Choose apps that skip AdBlocker entirely. |
| **Settings** | Pick the DNS server, restart behaviour and Always-on VPN, and reset statistics. |

### How it works

```
 any app ──DNS lookup──► Android ──► VPN interface (only 192.0.2.2 is routed into it)
                                           │
                                           ▼
                                      DnsProxy ── blocked?  ──► "no such domain" (NXDOMAIN)
                                           │
                                           └── otherwise ──► your DNS server, over a socket that
                                                            bypasses the VPN ──► answer relayed back
 all other traffic ────────────────────────────────────────► normal network, untouched
```

- The VPN routes a single address from the reserved documentation range (TEST-NET), so the app never
  sees or slows down your regular traffic. IPv6 traffic is left alone.
- `DnsProxy` is one thread running a `poll()` loop over the tunnel, every in-flight upstream query and
  a wake-up pipe. Blocked names are answered immediately; queries time out after 10 seconds, after
  which the next DNS server is tried.
- A rule for `example.com` also covers its subdomains, and an allowed domain wins over a blocked one,
  just like `@@||example.com^` in the extension.
- The app excludes itself from its own VPN, so relayed queries and list downloads can't loop back in.

### Limitations

- DNS blocking can't hide the empty space an ad leaves behind, and can't block ads served from the
  same domain as the content, such as YouTube, Facebook or Instagram ads.
- Apps that use their own encrypted DNS (DNS over HTTPS), or hard-code a DNS server, skip the filter.
- Only one VPN app can be active at a time.
- Blocking an analytics domain also blocks its website and dashboard. Allow the domain in
  **Activity** if you need it.

## Filter lists and syntax

Both apps are built from the same source lists in [`filters/`](filters). `npm run build` compiles them
into the extension's rulesets and into one-domain-per-line assets for the Android app. Only rules that
match a whole domain with no other conditions (for example `||ads.example.com^`) carry over to
Android, because DNS never sees paths or page context.

Custom filters in the extension (Settings → My filters) use a subset of the Adblock Plus syntax:

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

The Android app reads hosts files (`0.0.0.0 ads.example.com`), plain domain lists and Adblock-style
domain rules (`||ads.example.com^`, `@@||ads.example.com^`, `$important`). It skips everything else.

## How the extension works

```
Build time
  filters/*.txt ──► scripts/build.mjs ──┬──► extension/generated/rulesets/*.json   static DNR rulesets
                    (filter-parser.js)  ├──► extension/generated/cosmetic-data.js  element-hiding data
                                        └──► android/app/src/main/assets/filters/  domain lists for Android

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

- **One parser everywhere.** `extension/src/shared/filter-parser.js` is used both at build time
  (built-in lists) and at run time in the service worker (your own filters), so both behave the same.
- **Rule compaction.** Plain domain filters with identical options are merged into a single DNR rule
  with a `requestDomains` list. The 114 domains in the ads list become one rule.
- **Priorities.** Block = 1, exception = 2, `$important` block = 3, `$important` exception = 4, and
  paused sites = 100, so pausing a site always wins.
- **Rule ids** below 1,000,000 are blocking rules and ids above are exceptions. The match events
  Chrome reports only contain rule ids, and this is how the statistics tell the two apart.
- **Element hiding** reads settings straight from `chrome.storage` (no service-worker round trip to
  decide what to hide), validates each selector, and swaps stylesheets without a flash when settings
  change.

## Project layout

```
filters/                     Source filter lists shared by both apps (edit these)
scripts/
  build.mjs                  filters/*.txt → extension/generated/* and Android assets
  make-icons.mjs             Renders the extension's PNG icons, no dependencies
tests/                       node:test suites for the parser, build output and content script
extension/                   Chrome extension (load this folder)
  manifest.json
  generated/                 Build output, committed so the extension loads without Node
  icons/
  src/
    background/              Service worker
    content/                 Element hiding and the element picker
    popup/  options/         UI pages
    shared/                  Parser, defaults, host helpers, messaging
android/                     Android app (Gradle project)
  app/src/main/java/com/teykaijun/adblocker/
    dns/                     Packet and DNS parsing, rule parsing, matching, the proxy loop
    vpn/                     VpnService, notifications, Quick Settings tile, boot receiver
    data/                    Settings, filter lists and downloads, statistics, network monitor
    ui/                      Jetpack Compose screens
  app/src/main/assets/filters/   Generated from filters/ by npm run build
  app/src/test/              JUnit tests for the DNS code
```

## Development

### Extension and filter lists

Requires Node.js 22 or later. There are no npm dependencies.

```bash
npm test          # parser, host helpers, build output and content-script tests
npm run build     # recompile filters/*.txt for the extension and the Android app
npm run check     # fail if the generated files are out of date
npm run icons     # re-render extension/icons/*.png
```

To add a filter to a built-in list, edit the file in `filters/`, run `npm run build`, then reload
the extension in `chrome://extensions` and rebuild the Android app. The build fails on any line the
parser can't handle.

To add a new list, add an entry to `FILTER_LISTS` in `extension/src/shared/defaults.js`, create
`filters/<id>.txt` and add a matching `rule_resources` entry to `extension/manifest.json`. The build
checks that these three agree.

**Debugging:** open `chrome://extensions`, then click **service worker** on the AdBlocker card to see
the background console. Blocked-request counting uses `onRuleMatchedDebug`, which Chrome only
provides to unpacked extensions. Packed builds fall back to `getMatchedRules`, which is rate
limited, so the popup refreshes that number at most every 30 seconds.

### Android app

Requires JDK 17 or later and the Android SDK with the API 37 platform. Android Studio installs both.
Open the `android` folder in Android Studio, or build from the command line:

```bash
cd android
./gradlew testDebugUnitTest lintDebug assembleRelease
```

The APK is written to `android/app/build/outputs/apk/release/app-release.apk`.

**Release signing.** Without a keystore, the release build is signed with the local debug key so it
can still be installed. To use your own key, create `android/keystore.properties` (it is git-ignored):

```properties
storeFile=/path/to/release.jks
storePassword=…
keyAlias=…
keyPassword=…
```

For CI, add the repository secrets `ADBLOCKER_KEYSTORE_BASE64` (the `.jks` file, base64-encoded),
`ADBLOCKER_KEYSTORE_PASSWORD`, `ADBLOCKER_KEY_ALIAS` and `ADBLOCKER_KEY_PASSWORD`. Pushing a tag like
`v1.0.0` also attaches the APK to a GitHub release.

## Permissions

**Chrome extension**

| Permission | Why |
| --- | --- |
| `declarativeNetRequest` | Block requests using the rulesets. |
| `declarativeNetRequestFeedback` | Count blocked requests for the popup and statistics. |
| `scripting` | Inject element-hiding CSS and the element picker. |
| `storage` | Save settings, allowed sites, your filters and statistics. |
| `webNavigation` | Reset per-page counters when a tab navigates. |
| Host access to all sites | Hide elements on any page and update the toolbar icon per tab. |

**Android app**

| Permission | Why |
| --- | --- |
| VPN connection (asked when you first turn it on) | Receive DNS lookups on the phone. |
| `INTERNET`, `ACCESS_NETWORK_STATE` | Relay allowed lookups, download lists you turn on, follow network changes. |
| `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_SPECIAL_USE`, `POST_NOTIFICATIONS` | Keep filtering running and show its status notification. |
| `RECEIVE_BOOT_COMPLETED` | Turn protection back on after a restart or an update. |

## Limitations of the extension

- **Video ads on YouTube** are served from the same servers as the videos, so network rules can't
  remove them without breaking playback. Blocking them needs script injection, which this extension
  doesn't do. Banner, feed and sidebar ads on YouTube *are* hidden.
- Manifest V3 has no equivalent of uBlock Origin's scriptlets or response filtering, so some
  anti-adblock walls can't be bypassed.
- Hiding a cookie banner (Annoyances list) doesn't answer it. A few sites keep the page locked
  until you do.

## Privacy

Everything runs on your device, and neither app collects any data.

- **Extension:** makes no network requests of its own and loads no remote code.
- **Android app:** sends allowed DNS lookups to the DNS server you chose (by default your network's
  own). It only goes online otherwise to download community lists you turned on, from the address
  shown in the app.

## License

[MIT](LICENSE). Community lists that the Android app downloads come from their authors and keep their
own licenses.
