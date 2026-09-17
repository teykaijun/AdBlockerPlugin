<h1 align="center">AdBlocker</h1>

<p align="center">
  A small, free ad blocker for Windows and Android that I build in my spare time.<br />
  It tries to stop most ads and trackers in browsers, apps and games.
</p>

<p align="center">
  <a href="https://github.com/teykaijun/AdBlockerPlugin/releases/latest">Download</a> ·
  <a href="#windows">Windows</a> ·
  <a href="#browser-companion">Browser companion</a> ·
  <a href="#android">Android</a> ·
  <a href="#support">Support development ☕</a>
</p>

---

AdBlocker is a personal project, so please expect some rough edges. If an ad slips through, or
something stops working, I'd be grateful if you [opened an issue](https://github.com/teykaijun/AdBlockerPlugin/issues).

|  | Windows | Android |
| --- | --- | --- |
| **What it is** | A console app that runs quietly as a Windows service | An app with a local VPN |
| **Where it helps** | Chrome, Edge, Firefox and other apps on the PC | Apps, games and browsers on the phone |
| **How** | Filters DNS lookups on `127.0.0.1` | Filters DNS lookups through a VPN that carries nothing else |
| **Download** | [`adblocker.exe`](https://github.com/teykaijun/AdBlockerPlugin/releases/latest/download/adblocker.exe), plus the optional [`AdBlocker-Companion.zip`](https://github.com/teykaijun/AdBlockerPlugin/releases/latest/download/AdBlocker-Companion.zip) for Chrome and Edge | [`AdBlocker.apk`](https://github.com/teykaijun/AdBlockerPlugin/releases/latest/download/AdBlocker.apk) |

## How it works, and what it can't do

Before an app can load an ad, it usually has to look up the ad server's name, for example
`pagead2.googlesyndication.com`. AdBlocker answers those lookups itself:

```
 Chrome, apps, games ──lookup──► AdBlocker ──► on a blocklist?  ──► "no such domain": the ad doesn't load
                                           │
                                           └── otherwise ──► your normal DNS server ──► answer passed back
```

Everything else (web pages, downloads, video) uses your connection as usual, so it shouldn't slow
anything down.

This approach has real limits, and I'd rather be upfront about them:

- Ads served from the **same** servers as the content, such as YouTube video ads or sponsored posts
  inside Facebook and Instagram, can't be told apart from the content and will still show.
- It can't tidy up the empty space where a blocked ad would have been.
- It can't stop a page from opening a new tab or sending you somewhere else. The ad then fails to
  load ("This site can't be reached") but the tab is still there. The HaGeZi Pop-Up Ads list, on by
  default, blocks many of the scripts that do this, and on Windows the optional
  [browser companion](#browser-companion) closes those tabs in Chrome and Edge.
- Apps that use their own encrypted DNS can bypass it (the apps try to warn you about the common cases).
- Blocklists are never perfect; now and then something useful may get blocked. You can always allow it.

The blocking itself relies on excellent lists maintained by other people, especially
[HaGeZi](https://github.com/hagezi/dns-blocklists), [AdGuard](https://github.com/AdguardTeam/AdGuardSDNSFilter)
and [StevenBlack](https://github.com/StevenBlack/hosts). Many thanks to them.

## Windows

### Install

Works on Windows 10 (version 2004 or later) and Windows 11. You don't need to install .NET.

1. Download [`adblocker.exe`](https://github.com/teykaijun/AdBlockerPlugin/releases/latest/download/adblocker.exe).
   It isn't code-signed, so Windows SmartScreen may warn you; choose **More info → Run anyway** if you
   trust it. The source code is all here if you'd like to check or build it yourself.
2. Open **Terminal** (or Command Prompt) **as administrator** in the download folder and run:
   ```powershell
   .\adblocker.exe install
   ```
   This copies the program to `C:\Program Files\AdBlocker`, sets up the **AdBlocker** Windows service
   (it starts with Windows and restarts itself if it crashes) and starts blocking.
3. To see whether it's working:
   ```powershell
   & "C:\Program Files\AdBlocker\adblocker.exe" status
   ```

To remove it, run `adblocker uninstall` as administrator. It stops the service and puts your network
settings back the way they were.

If you'd rather not install a service, `adblocker run` (as administrator) blocks ads only while that
console window is open. Ctrl+C or closing the window puts everything back.

### Browser companion

Because AdBlocker works on DNS lookups, a page can still open an ad in a new tab, or send your tab to
an ad. The ad won't load, but you're left looking at "This site can't be reached". The optional
**AdBlocker Companion** extension for Chrome, Edge and other Chromium-based browsers tidies this up:

- a tab that a page opens to a blocked address is closed again, and
- when a page sends your tab to a blocked address, the tab goes back to where you were.

It asks AdBlocker on your PC whether an address is blocked, so it needs AdBlocker for Windows 1.3.0 or
later to be running, and it does nothing without it. It doesn't block anything itself and doesn't send
anything anywhere else.

It isn't on the Chrome Web Store, so it's installed as an unpacked extension:

1. Download [`AdBlocker-Companion.zip`](https://github.com/teykaijun/AdBlockerPlugin/releases/latest/download/AdBlocker-Companion.zip)
   and unzip it to a folder you'll keep (or use the [`browser-extension`](browser-extension) folder
   from this repository).
2. Open `chrome://extensions` (in Edge, `edge://extensions`) and turn on **Developer mode**.
3. Choose **Load unpacked** and pick that folder.

Its toolbar button shows how many ad tabs it has closed and redirects it has undone since the
browser started. A few things worth knowing:

- If you type a blocked address into a tab you've already used, it takes you back to the previous
  page. In a new tab you'll see the browser's error page instead, and `adblocker check <domain>`
  tells you which list blocks it.
- If a link opens a tab that closes straight away, the link went through a blocked address (often a
  click tracker). The **Recently** list in the extension's window shows which one, and
  `adblocker allow <domain>` lets it through.
- If a page keeps redirecting, it gives up after three tries and leaves the error page.
- Firefox isn't supported. Its own pop-up blocker, or uBlock Origin, helps there.

### Commands

Commands marked * need a terminal opened with **Run as administrator**.

| Command | What it does |
| --- | --- |
| `install` * / `uninstall [--purge]` * | Install and start the service / stop it, restore network settings and remove it (`--purge` also deletes settings and logs) |
| `start` * / `stop` * / `restart` * | Control the service. While it's stopped nothing is blocked and network settings are back to normal. |
| `run [--verbose]` * | Block ads in this console window until Ctrl+C, showing what gets blocked |
| `status` | Whether blocking is on, today's numbers, lists and network adapters |
| `check <domain>` | Whether a domain is blocked, and by which list |
| `block <domain>` * / `allow <domain>` * / `forget <domain>` * | Always block / never block a domain (and its subdomains) / remove your rule |
| `log [-n 30] [-f]` | Recently blocked domains; `-f` keeps following |
| `lists` | All lists and whether they're on |
| `lists enable <id>` * / `lists disable <id>` * | Turn a built-in or community list on or off |
| `lists add <url> [name]` * / `lists remove <id>` * | Add or remove a list of your own (hosts file or Adblock-style, `https://` only) |
| `update` * | Download the latest community lists now (they also update weekly on their own) |
| `dns [server...]` * | Show or choose where allowed lookups go: `auto` (your network's DNS, the default), `cloudflare`, `quad9`, `google` (these three use encrypted DNS over HTTPS), an IP address, or an `https://` DNS-over-HTTPS URL |
| `doctor` | Look for settings that might let ads slip past, and check that the browser companion can reach AdBlocker |
| `restore` * | Put the original network settings back if AdBlocker was killed and can't restart |
| `stats reset` * | Set the counters back to zero |
| `support` | Support development ☕ (opens my Buy Me a Coffee page) |

Changes made with these commands reach the running service within a few seconds.

### Settings and files

Everything is kept in `C:\ProgramData\AdBlocker`, which only administrators and the service can change:

| File | Contents |
| --- | --- |
| `config.json` | Lists, your rules, DNS servers, `excludedAdapters`, `logAllowedQueries`. You can edit it by hand; the service reloads it automatically. |
| `lists\` | Downloaded community lists |
| `logs\queries.log` | Blocked lookups (and allowed ones if `logAllowedQueries` is `true`), rotated at 10 MB |
| `logs\service.log` | What the service did, and any errors |
| `dns-backup.json` | Your network settings from before AdBlocker, used to restore them |

### If ads still get through on Windows

`adblocker doctor` looks for these:

- **Chrome, Edge or Brave set to a specific secure DNS provider.** The default setting (your current
  service provider) is fine. If you picked a provider such as Cloudflare, the browser skips Windows
  DNS: open **Settings → Privacy and security → Security → Use secure DNS** and choose your current
  service provider.
- **Firefox with DNS over HTTPS turned on by hand.** Firefox's default setting works with AdBlocker;
  a manually chosen provider doesn't.
- **VPN apps.** Adapters from Tailscale, WireGuard, OpenVPN and similar software are left alone so
  names on the VPN keep working. If a VPN sends all DNS through itself, ads won't be blocked while it's
  connected. You can remove it from `excludedAdapters` in `config.json` if you prefer.
- **Port 53 in use.** Windows Mobile hotspot / Internet Connection Sharing, or another DNS program,
  can stop AdBlocker from starting.

### How the Windows app works

- A small DNS server listens on `127.0.0.1:53` and `[::1]:53` (UDP and TCP), reachable only from the
  PC itself.
- Every connected network adapter (except VPN adapters) is pointed at it through the
  `SetInterfaceDnsSettings` API. All adapters are changed because Windows asks every adapter's DNS
  servers at once and takes the first answer. The original settings are saved to `dns-backup.json`
  first and restored when the service stops, the console closes, Windows shuts down, or after a crash
  when the service restarts.
- With `auto`, allowed lookups go to the DNS servers your network provides (so local names and
  captive portals keep working), with public resolvers as a fallback.
- New networks (another Wi-Fi, USB tethering) are picked up automatically.
- Lookups for `use-application-dns.net` are blocked, which asks Firefox to stay on the system DNS.
- A tiny HTTP endpoint on `127.0.0.1:45353` answers the browser companion's "is this address
  blocked?" questions (`GET /v1/check?host=…`). It only answers requests addressed to `127.0.0.1` or
  `localhost`, turns away requests from web pages, and can't change anything.

## Android

### Install

Needs Android 8.0 or later. Google Play doesn't allow apps that block ads in other apps, so it's
installed from an APK file:

1. Download [`AdBlocker.apk`](https://github.com/teykaijun/AdBlockerPlugin/releases/latest/download/AdBlocker.apk)
   on your phone (or copy it over) and open it. Android will ask you to allow installing apps from that
   source.
2. Open **AdBlocker**, tap the power button and accept Android's VPN request. The HaGeZi Multi Normal
   and Pop-Up Ads lists download in the background and start blocking a few seconds later.

A few tips that help:

- Turn on **Always-on VPN** for AdBlocker (there's a shortcut on the Home screen), but please leave
  **Block connections without VPN** off. AdBlocker only routes DNS, so that option would cut off
  everything else.
- Keep **Private DNS** on *Automatic* or *Off*. A named Private DNS server bypasses the app.
- In Chrome, keep **Settings → Privacy and security → Use secure DNS** on its default setting.

New versions from the Releases page install over older ones. If you built the app yourself, or used
a build from GitHub Actions, it's signed differently, so uninstall the old copy first.

### Pop-up ad tabs in Chrome

When a page in Chrome opens an ad in a new tab, AdBlocker stops the ad from loading, but the tab still
opens and shows "This site can't be reached". Chrome on Android doesn't support extensions, so
unfortunately the browser companion can't close these tabs there. These help:

- The **HaGeZi Pop-Up Ads** list blocks many of the scripts that open these tabs in the first place.
  It's on by default, and turned on for you when you update to 1.3.0.
- In Chrome, keep **Settings → Site settings → Pop-ups and redirects** and **Intrusive ads** set to
  block (the defaults).
- Pressing Back on a tab that a page opened usually closes it and takes you back to that page.
- Browsers with their own pop-up blocking, such as Firefox with uBlock Origin, can often stop these
  tabs from opening at all.

### Using it

| Screen | What you can do |
| --- | --- |
| **Home** | Turn protection on or off and see today's numbers |
| **Activity** | Browse and search recent lookups; tap one to always allow or block it |
| **Filters** | Turn lists on or off, add a list by URL, manage your blocked and allowed domains |
| **Apps** | Let apps skip AdBlocker entirely, handy if one misbehaves |
| **Settings** | Choose the DNS server, restart behaviour and Always-on VPN, and reset statistics |
| **⋮ menu** | Support development ☕, or open the source code |

There's also a Quick Settings tile and a status notification with a **Pause** button.

### How the Android app works

- The VPN routes a single address from a reserved documentation range (TEST-NET), and Android sends
  DNS lookups to it. Regular traffic never enters the VPN, and IPv6 traffic is left alone.
- `DnsProxy` is one thread running a `poll()` loop over the tunnel, the upstream queries in flight and
  a wake-up pipe. Blocked names are answered straight away; other queries time out after 10 seconds,
  after which the next DNS server is tried.
- The app excludes itself from its own VPN, so relayed queries and list downloads can't loop back in.

## Filter lists

[`filters/`](filters) holds the built-in lists, one `||domain^` rule per line (`@@||domain^` for
exceptions), plus `lists.json` with their names and defaults. Both apps include them as they are:

| List | Default | Contents |
| --- | --- | --- |
| `ads` | on | Ad networks, exchanges and in-app / in-game ad SDKs |
| `trackers` | on | Analytics, session recording, tracking pixels, app telemetry, data brokers |
| `social` | off | Share buttons and social widgets (breaks "Log in with Facebook") |

Community lists are downloaded when turned on and refreshed weekly:

| ID | List | Default |
| --- | --- | --- |
| `hagezi-normal` | [HaGeZi Multi Normal](https://github.com/hagezi/dns-blocklists) (about 180,000 domains) | on |
| `hagezi-popupads` | [HaGeZi Pop-Up Ads](https://github.com/hagezi/dns-blocklists): networks behind pop-up ads and redirects (about 50,000 domains) | on |
| `hagezi-light` | HaGeZi Multi Light | off |
| `adguard-dns` | [AdGuard DNS filter](https://github.com/AdguardTeam/AdGuardSDNSFilter) | off |
| `stevenblack` | [StevenBlack Unified hosts](https://github.com/StevenBlack/hosts) | off |

You can add any hosts file, plain domain list or Adblock-style domain list (`||domain^`). Rules that
need more than a domain name (paths, `$third-party`, element hiding) are skipped, because DNS never
sees that information. Your own blocked and allowed domains always win over the lists, and allowing
a domain also allows its subdomains.

Suggestions for domains to add to the built-in lists are very welcome.

## Support

AdBlocker is free and always will be. If it's useful to you and you'd like to support its development,
you can buy me a coffee ☕. It's entirely optional, and I really appreciate it:

**[buymeacoffee.com/casunoxd](https://buymeacoffee.com/casunoxd)**

Bug reports, ideas and pull requests help just as much. Thank you!

## Project layout

```
filters/                         Built-in blocklists shared by both apps
windows/                         Windows app (.NET 10)
  src/AdBlocker/
    Cli/                         Commands and console output
    Core/                        The blocker itself, statistics, logs
    Dns/                         DNS messages, the loopback server, upstream servers (UDP/TCP/HTTPS)
    Filtering/                   Rule parsing, domain matching, lists and downloads
    Platform/                    Adapter DNS settings, Windows service, browser checks
    Config/                      config.json and file locations
  tests/AdBlocker.Tests/         xUnit tests, including end-to-end runs on a spare port
browser-extension/               Browser companion for Chrome and Edge (Manifest V3)
  guard.js                       Decides which tabs to close or send back
  background.js, api.js          Wires it to the browser and to AdBlocker for Windows
  test/                          Node tests for guard.js
android/                         Android app (Kotlin, Jetpack Compose)
  app/src/main/java/com/teykaijun/adblocker/
    dns/  vpn/  data/  ui/
  app/src/test/                  JUnit tests for the DNS code
```

## Building it yourself

### Windows app

Needs the .NET 10 SDK.

```powershell
cd windows
dotnet test AdBlocker.slnx
dotnet publish src/AdBlocker/AdBlocker.csproj -c Release -r win-x64 -o artifacts/publish
```

This gives you a single self-contained `artifacts/publish/adblocker.exe` (about 12 MB). To try the DNS
server without touching any network settings, run `adblocker run --no-system-dns --port 5353` as
administrator and query it with `nslookup -port=5353 doubleclick.net 127.0.0.1`.

### Android app

Needs JDK 17 or later and the Android SDK with the API 37 platform (Android Studio installs both).
Open the `android` folder in Android Studio, or build from the command line:

```bash
cd android
./gradlew testDebugUnitTest lintDebug assembleRelease
```

The APK ends up in `android/app/build/outputs/apk/release/app-release.apk`. Without a keystore it's
signed with your local debug key. To use your own key, create `android/keystore.properties` (it's
git-ignored):

```properties
storeFile=/path/to/release.jks
storePassword=…
keyAlias=…
keyPassword=…
```

### Browser companion extension

There's nothing to build. With Node.js 22 or later, `npm test` in `browser-extension` runs its tests,
and **Load unpacked** on that folder lets you try changes. The release zip is the same folder without
`test/` and `package.json`.

### Automated builds and releases

GitHub Actions builds and tests everything on every push and keeps the apps as downloadable
artifacts. The files on the Releases page are built on my own PC, so that each new APK is signed with
the same key and can update the previous one.

## Permissions

**Windows:** administrator rights to install the service and change the network adapters' DNS
settings. The service runs as LocalSystem.

**Browser companion**

| Permission | Why |
| --- | --- |
| `webNavigation` (shown as "Read your browsing history") | Notice when a page opens a tab, or a page fails to load |
| `storage` | Keep the counts in its window until the browser closes |
| Access to `http://127.0.0.1` | Ask AdBlocker for Windows whether an address is blocked |

**Android**

| Permission | Why |
| --- | --- |
| VPN connection (asked when you first turn it on) | Receive DNS lookups on the phone |
| `INTERNET`, `ACCESS_NETWORK_STATE` | Pass on allowed lookups, download lists you turn on, notice network changes |
| `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_SPECIAL_USE`, `POST_NOTIFICATIONS` | Keep filtering running and show its status notification |
| `RECEIVE_BOOT_COMPLETED` | Turn protection back on after a restart or an update |

## Privacy

Both apps run entirely on your device and don't collect anything. Allowed lookups go to the DNS server
you chose (by default your network's own). Apart from that, they only go online to download the
community lists you turned on, from the addresses shown in the app. The browser companion only talks
to AdBlocker on your own PC and forgets its counts when the browser closes. The support links just
open a web page in your browser.

## License

[MIT](LICENSE). The community lists belong to their authors and keep their own licenses.
