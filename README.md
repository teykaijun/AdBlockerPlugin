<h1 align="center">AdBlocker</h1>

<p align="center">
  Blocks ads and trackers everywhere on your Windows PC and your Android phone:
  in Chrome and every other browser, in apps and in games.
</p>

---

|  | Windows | Android |
| --- | --- | --- |
| **What it is** | A headless console app that runs as a Windows service | An app with a local VPN |
| **Blocks ads in** | Chrome, Edge, Firefox and every other app | Every app, game and browser |
| **How** | A filtering DNS server on `127.0.0.1` that Windows is pointed at | A VPN that only carries DNS lookups |
| **Install** | `adblocker install` from an administrator terminal | Sideload the APK |

Both apps use the same built-in blocklists from [`filters/`](filters) and can add the same community
lists, such as HaGeZi Multi Normal with about 180,000 ad and tracker domains, which is on by default.

## How it works

Before an app or a browser can load an ad, it has to look up the ad server's name, for example
`pagead2.googlesyndication.com`. AdBlocker answers those lookups itself:

```
 Chrome, apps, games ──lookup──► AdBlocker ──► on a blocklist?  ──► "no such domain": the ad never loads
                                           │
                                           └── otherwise ──► your normal DNS server ──► answer passed back
```

Nothing else goes through AdBlocker. Web pages, downloads and video streams use your connection
exactly as before, so there is no slowdown. Blocking by name has one limit: ads served from the
**same** servers as the content, like YouTube video ads or sponsored posts inside Facebook and
Instagram, cannot be separated from the content and still show.

## Windows

### Install

Requires Windows 10 version 2004 or later, or Windows 11. No .NET installation is needed.

1. Download `adblocker.exe`: open the repository's **Actions** tab → latest **CI** run →
   **Artifacts** → `AdBlocker-windows`, or [build it](#windows-app). Windows SmartScreen may warn
   because the file is not signed; choose **More info → Run anyway**.
2. Open **Terminal** (or Command Prompt) **as administrator**, go to the download folder and run:
   ```powershell
   .\adblocker.exe install
   ```
   This copies the program to `C:\Program Files\AdBlocker`, installs the **AdBlocker** Windows
   service (starts with Windows, restarts itself if it ever crashes) and starts blocking.
3. Check that it works:
   ```powershell
   & "C:\Program Files\AdBlocker\adblocker.exe" status
   ```

To remove it again, run `adblocker uninstall` as administrator. That stops the service and puts your
network settings back exactly as they were.

Prefer not to install a service? `adblocker run` (as administrator) blocks ads only while that console
window stays open. Ctrl+C or closing the window restores everything.

### Commands

Commands marked * need a terminal opened with **Run as administrator**.

| Command | What it does |
| --- | --- |
| `install` * / `uninstall [--purge]` * | Install and start the service / stop it, restore network settings and remove it (`--purge` also deletes settings and logs) |
| `start` * / `stop` * / `restart` * | Control the service. While stopped, nothing is blocked and network settings are back to normal. |
| `run [--verbose]` * | Block ads in this console window until Ctrl+C, printing what gets blocked |
| `status` | Whether blocking is on, today's numbers, lists and network adapters |
| `check <domain>` | Whether a domain is blocked, and by which list |
| `block <domain>` * / `allow <domain>` * / `forget <domain>` * | Always block / never block a domain (and its subdomains) / remove your rule |
| `log [-n 30] [-f]` | Recently blocked domains; `-f` keeps following |
| `lists` | All lists with their state |
| `lists enable <id>` * / `lists disable <id>` * | Turn a built-in or community list on or off |
| `lists add <url> [name]` * / `lists remove <id>` * | Add or remove your own list (hosts file or Adblock-style, `https://` only) |
| `update` * | Download the latest community lists now (they also update weekly by themselves) |
| `dns [server...]` * | Show or choose where allowed lookups go: `auto` (your network's DNS, the default), `cloudflare`, `quad9`, `google` (these three use encrypted DNS over HTTPS), an IP address, or an `https://` DNS-over-HTTPS URL |
| `doctor` | Look for settings that let ads slip past AdBlocker |
| `restore` * | Put the original network settings back if AdBlocker was killed and cannot restart |
| `stats reset` * | Set the counters back to zero |

Changes made with these commands reach the running service within a few seconds.

### Settings and files

Everything lives in `C:\ProgramData\AdBlocker`, which only administrators and the service can change:

| File | Contents |
| --- | --- |
| `config.json` | Lists, your rules, DNS servers, `excludedAdapters`, `logAllowedQueries`. Editable by hand; the service reloads it automatically. |
| `lists\` | Downloaded community lists |
| `logs\queries.log` | Blocked lookups (and allowed ones if `logAllowedQueries` is `true`), rotated at 10 MB |
| `logs\service.log` | What the service did, and any errors |
| `dns-backup.json` | The network settings from before AdBlocker, used to restore them |

### Things that bypass AdBlocker on Windows

`adblocker doctor` checks for these:

- **Chrome, Edge or Brave with a custom secure DNS provider.** The default setting ("OS default" /
  your current service provider) is fine. A specific provider such as Cloudflare makes the browser
  skip Windows DNS: in the browser open **Settings → Privacy and security → Security → Use secure DNS**
  and choose your current service provider.
- **Firefox with DNS over HTTPS switched on by hand.** Firefox's automatic setting respects
  AdBlocker; a manually chosen provider does not.
- **VPN apps.** Adapters of Tailscale, WireGuard, OpenVPN and similar VPN software are left alone so
  names on the VPN keep working. When a VPN sends all DNS through itself, ads are not blocked while
  it is connected. Remove its name from `excludedAdapters` in `config.json` to change that.
- **Port 53 in use.** Windows Mobile hotspot / Internet Connection Sharing, or another local DNS
  program, can stop AdBlocker from starting.

### How the Windows app works

- A DNS server listens on `127.0.0.1:53` and `[::1]:53` (UDP and TCP) and is reachable only from
  the PC itself.
- Every connected network adapter (except excluded VPN adapters) is pointed at it through the
  `SetInterfaceDnsSettings` API. All adapters are changed, because Windows asks every adapter's
  DNS servers at the same time and takes the first answer. The original settings are saved to
  `dns-backup.json` before anything changes, and are restored when the service stops, the console
  closes, Windows shuts down, or after a crash when the service restarts.
- With `auto`, allowed lookups go to the DNS servers your network handed out (so local names and
  captive portals keep working), with public resolvers as a fallback.
- New networks (another Wi-Fi, USB tethering) are picked up automatically.
- Lookups for `use-application-dns.net` are blocked, which tells Firefox not to switch to its own DNS.

## Android

### Install

Requires Android 8.0 or later. Google Play doesn't allow apps that block ads in other apps, so the
app is installed from an APK file:

1. Get `app-release.apk`: open the repository's **Actions** tab → latest **CI** run → **Artifacts** →
   `AdBlocker-android`, or [build it](#android-app).
2. Copy it to the phone and open it. Allow installing apps from that source when Android asks.
3. Open **AdBlocker**, tap the power button, and accept Android's VPN connection request. HaGeZi Multi
   Normal downloads in the background and starts blocking a few seconds later.

For best results:

- Turn on **Always-on VPN** for AdBlocker (the Home screen has a shortcut), but leave **Block
  connections without VPN** off. AdBlocker only routes DNS, so that option would cut off all other traffic.
- Keep **Private DNS** on *Automatic* or *Off*. A named Private DNS server bypasses the app.
- In Chrome, keep **Settings → Privacy and security → Use secure DNS** on the default setting.

An APK can only update an installed copy that was signed with the same key. APKs built on different
machines, or by CI without the signing secrets below, use different debug keys. To switch between
them, uninstall the app first.

### Using it

| Tab | What you can do |
| --- | --- |
| **Home** | Turn protection on or off and see today's numbers. |
| **Activity** | Browse and search recent lookups. Tap one to always allow or block it. |
| **Filters** | Turn lists on or off, add a list by URL, manage your blocked and allowed domains. |
| **Apps** | Choose apps that skip AdBlocker entirely, for any app that misbehaves. |
| **Settings** | Pick the DNS server, restart behaviour and Always-on VPN, and reset statistics. |

There is also a Quick Settings tile and a status notification with a **Pause** button.

### How the Android app works

- The VPN routes a single address from a reserved documentation range (TEST-NET), and Android sends
  DNS lookups to it. Regular traffic never enters the VPN; IPv6 traffic is left alone.
- `DnsProxy` is one thread running a `poll()` loop over the tunnel, every in-flight upstream query and
  a wake-up pipe. Blocked names are answered immediately; queries time out after 10 seconds, after
  which the next DNS server is tried.
- The app excludes itself from its own VPN, so relayed queries and list downloads can't loop back in.

## Filter lists

[`filters/`](filters) holds the built-in lists, one `||domain^` rule per line (`@@||domain^` for
exceptions), plus `lists.json` with their names and defaults. Both apps package them as they are:

| List | Default | Contents |
| --- | --- | --- |
| `ads` | on | Ad networks, exchanges and in-app / in-game ad SDKs |
| `trackers` | on | Analytics, session recording, tracking pixels, app telemetry, data brokers |
| `social` | off | Share buttons and social widgets (breaks "Log in with Facebook") |

Community lists, downloaded when turned on and refreshed weekly:

| ID | List | Default |
| --- | --- | --- |
| `hagezi-normal` | [HaGeZi Multi Normal](https://github.com/hagezi/dns-blocklists) | on |
| `hagezi-light` | HaGeZi Multi Light | off |
| `adguard-dns` | [AdGuard DNS filter](https://github.com/AdguardTeam/AdGuardSDNSFilter) | off |
| `stevenblack` | [StevenBlack Unified hosts](https://github.com/StevenBlack/hosts) | off |

You can add any list that is a hosts file, a plain domain list or an Adblock-style domain list
(`||domain^`). Rules that need more than a domain name (paths, `$third-party`, element hiding) are
skipped, because DNS never sees them. Your own blocked and allowed domains always win over the lists,
and an allowed domain also allows its subdomains.

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
android/                         Android app (Kotlin, Jetpack Compose)
  app/src/main/java/com/teykaijun/adblocker/
    dns/  vpn/  data/  ui/
  app/src/test/                  JUnit tests for the DNS code
```

## Development

### Windows app

Requires the .NET 10 SDK.

```powershell
cd windows
dotnet test AdBlocker.slnx
dotnet publish src/AdBlocker/AdBlocker.csproj -c Release -r win-x64 -o artifacts/publish
```

This produces one self-contained `artifacts/publish/adblocker.exe` (about 12 MB). To try the DNS server
without touching any network settings, run `adblocker run --no-system-dns --port 5353` as administrator
and query it with `nslookup -port=5353 doubleclick.net 127.0.0.1`.

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
`ADBLOCKER_KEYSTORE_PASSWORD`, `ADBLOCKER_KEY_ALIAS` and `ADBLOCKER_KEY_PASSWORD`.

Pushing a tag like `v1.1.0` attaches both `adblocker.exe` and the APK to a GitHub release.

## Permissions

**Windows:** administrator rights to install the service and change the network adapters' DNS
settings. The service runs as LocalSystem.

**Android**

| Permission | Why |
| --- | --- |
| VPN connection (asked when you first turn it on) | Receive DNS lookups on the phone. |
| `INTERNET`, `ACCESS_NETWORK_STATE` | Relay allowed lookups, download lists you turn on, follow network changes. |
| `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_SPECIAL_USE`, `POST_NOTIFICATIONS` | Keep filtering running and show its status notification. |
| `RECEIVE_BOOT_COMPLETED` | Turn protection back on after a restart or an update. |

## Privacy

Both apps run entirely on your device and collect nothing. Allowed lookups go to the DNS server you
chose (by default your network's own). The only other network access is downloading the community
lists you turned on, from the addresses shown in the app.

## License

[MIT](LICENSE). Community lists come from their authors and keep their own licenses.
