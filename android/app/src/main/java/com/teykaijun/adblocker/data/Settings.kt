package com.teykaijun.adblocker.data

import android.content.Context
import androidx.core.content.edit
import org.json.JSONArray
import org.json.JSONObject
import java.net.InetAddress
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

enum class DnsProvider(val title: String, val detail: String, val addresses: List<String>) {
    NETWORK("Network default", "Whatever your Wi-Fi or mobile network provides", emptyList()),
    CLOUDFLARE("Cloudflare", "1.1.1.1", listOf("1.1.1.1", "1.0.0.1")),
    QUAD9("Quad9", "9.9.9.9 · also blocks malware", listOf("9.9.9.9", "149.112.112.112")),
    GOOGLE("Google", "8.8.8.8", listOf("8.8.8.8", "8.8.4.4")),
    CUSTOM("Custom", "An IP address you enter", emptyList()),
}

/** A community blocklist the app downloads on request. */
data class RemoteList(
    val id: String,
    val title: String,
    val url: String,
    val description: String,
    val enabled: Boolean = false,
    val custom: Boolean = false,
) {
    companion object {
        val PRESETS = listOf(
            RemoteList(
                id = "hagezi-normal",
                title = "HaGeZi Multi Normal",
                url = "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/multi.txt",
                description = "Recommended. About 180,000 ad, tracker and telemetry domains used by apps, games and websites.",
            ),
            RemoteList(
                id = "hagezi-popupads",
                title = "HaGeZi Pop-Up Ads",
                url = "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/popupads.txt",
                description = "Recommended. Blocks the scripts that open pop-up ads and redirect pages. About 50,000 domains.",
            ),
            RemoteList(
                id = "hagezi-light",
                title = "HaGeZi Multi Light",
                url = "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/light.txt",
                description = "A smaller list for older phones. Very few false positives.",
            ),
            RemoteList(
                id = "adguard-dns",
                title = "AdGuard DNS filter",
                url = "https://adguardteam.github.io/HostlistsRegistry/assets/filter_1.txt",
                description = "Combines EasyList, EasyPrivacy and AdGuard's mobile ads filter.",
            ),
            RemoteList(
                id = "stevenblack",
                title = "StevenBlack Unified hosts",
                url = "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts",
                description = "Popular hosts file covering ads and malware.",
            ),
        )

        /** Community lists that are on until the user turns them off. */
        val DEFAULT_ENABLED = setOf("hagezi-normal", "hagezi-popupads")
    }
}

data class Settings(
    /** Whether the user wants protection on; used to restart after a reboot. */
    val protectionOn: Boolean = false,
    /** Built-in list toggles the user changed; missing ids use the list default. */
    val builtInLists: Map<String, Boolean> = emptyMap(),
    val remoteLists: List<RemoteList> = RemoteList.PRESETS.map { it.copy(enabled = it.id in RemoteList.DEFAULT_ENABLED) },
    val blockedDomains: Set<String> = emptySet(),
    val allowedDomains: Set<String> = emptySet(),
    /** Packages that bypass the VPN entirely. */
    val bypassApps: Set<String> = emptySet(),
    val dnsProvider: DnsProvider = DnsProvider.NETWORK,
    val customDns: String = "",
    val startOnBoot: Boolean = true,
) {
    fun isBuiltInEnabled(list: BuiltInList) = builtInLists[list.id] ?: list.enabledByDefault

    /** Adds `domain` to one custom list and removes it from the other. */
    fun withDomainRule(domain: String, allow: Boolean) = copy(
        blockedDomains = if (allow) blockedDomains - domain else blockedDomains + domain,
        allowedDomains = if (allow) allowedDomains + domain else allowedDomains - domain,
    )

    fun withoutDomainRule(domain: String) = copy(blockedDomains = blockedDomains - domain, allowedDomains = allowedDomains - domain)
}

/** Settings persisted in SharedPreferences and exposed as a [StateFlow]. */
class SettingsStore(context: Context) {
    private val prefs = context.getSharedPreferences("settings", Context.MODE_PRIVATE)
    private val state = MutableStateFlow(read())

    val settings: StateFlow<Settings> = state.asStateFlow()

    fun update(transform: (Settings) -> Settings) {
        synchronized(this) {
            val next = transform(state.value)
            if (next != state.value) {
                write(next)
                state.value = next
            }
        }
    }

    private fun read(): Settings {
        migrate()
        val enabledRemote = prefs.getStringSet(KEY_REMOTE_ENABLED, null) ?: RemoteList.DEFAULT_ENABLED
        val custom = parseCustomLists(prefs.getString(KEY_CUSTOM_LISTS, null))
        val remote = (RemoteList.PRESETS + custom).map { it.copy(enabled = it.id in enabledRemote) }
        val builtIn = prefs.getStringSet(KEY_BUILTIN, emptySet()).orEmpty().mapNotNull { entry ->
            entry.split('=', limit = 2).takeIf { it.size == 2 }?.let { (id, on) -> id to on.toBoolean() }
        }.toMap()
        return Settings(
            protectionOn = prefs.getBoolean(KEY_PROTECTION_ON, false),
            builtInLists = builtIn,
            remoteLists = remote,
            blockedDomains = prefs.getStringSet(KEY_BLOCKED, emptySet()).orEmpty().toSet(),
            allowedDomains = prefs.getStringSet(KEY_ALLOWED, emptySet()).orEmpty().toSet(),
            bypassApps = prefs.getStringSet(KEY_BYPASS, emptySet()).orEmpty().toSet(),
            dnsProvider = runCatching { DnsProvider.valueOf(prefs.getString(KEY_DNS, null)!!) }.getOrDefault(DnsProvider.NETWORK),
            customDns = prefs.getString(KEY_CUSTOM_DNS, "").orEmpty(),
            startOnBoot = prefs.getBoolean(KEY_START_ON_BOOT, true),
        )
    }

    /** Brings settings saved by older versions up to date. */
    private fun migrate() {
        val version = prefs.getInt(KEY_VERSION, 1)
        if (version >= CURRENT_VERSION) return
        // Lists that became defaults in a later version are switched on for existing installs too.
        val added = buildSet {
            if (version < 2) add("hagezi-normal") // 1.1.0
            if (version < 3) add("hagezi-popupads") // 1.3.0
        }
        prefs.edit {
            prefs.getStringSet(KEY_REMOTE_ENABLED, null)?.let { putStringSet(KEY_REMOTE_ENABLED, it + added) }
            putInt(KEY_VERSION, CURRENT_VERSION)
        }
    }

    private fun write(s: Settings) {
        // SharedPreferences keeps a reference to string sets, so always hand it copies.
        prefs.edit {
            putBoolean(KEY_PROTECTION_ON, s.protectionOn)
            putStringSet(KEY_BUILTIN, s.builtInLists.map { (id, on) -> "$id=$on" }.toSet())
            putStringSet(KEY_REMOTE_ENABLED, s.remoteLists.filter { it.enabled }.map { it.id }.toSet())
            putString(KEY_CUSTOM_LISTS, customListsJson(s.remoteLists.filter { it.custom }))
            putStringSet(KEY_BLOCKED, s.blockedDomains.toSet())
            putStringSet(KEY_ALLOWED, s.allowedDomains.toSet())
            putStringSet(KEY_BYPASS, s.bypassApps.toSet())
            putString(KEY_DNS, s.dnsProvider.name)
            putString(KEY_CUSTOM_DNS, s.customDns)
            putBoolean(KEY_START_ON_BOOT, s.startOnBoot)
        }
    }

    private fun parseCustomLists(json: String?): List<RemoteList> = runCatching {
        val array = JSONArray(json ?: return emptyList())
        (0 until array.length()).map { i ->
            val o = array.getJSONObject(i)
            RemoteList(
                id = o.getString("id"),
                title = o.getString("title"),
                url = o.getString("url"),
                description = "Added by you",
                custom = true,
            )
        }
    }.getOrDefault(emptyList())

    private fun customListsJson(lists: List<RemoteList>): String = JSONArray(
        lists.map { JSONObject().put("id", it.id).put("title", it.title).put("url", it.url) },
    ).toString()

    private companion object {
        const val CURRENT_VERSION = 3
        const val KEY_VERSION = "version"
        const val KEY_PROTECTION_ON = "protection_on"
        const val KEY_BUILTIN = "builtin_lists"
        const val KEY_REMOTE_ENABLED = "remote_enabled"
        const val KEY_CUSTOM_LISTS = "custom_lists"
        const val KEY_BLOCKED = "blocked_domains"
        const val KEY_ALLOWED = "allowed_domains"
        const val KEY_BYPASS = "bypass_apps"
        const val KEY_DNS = "dns_provider"
        const val KEY_CUSTOM_DNS = "custom_dns"
        const val KEY_START_ON_BOOT = "start_on_boot"
    }
}

private val IPV4_LITERAL = Regex("""^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$""")
private val IPV6_LITERAL = Regex("""^[0-9a-fA-F:.]{2,45}$""")

/** Parses an IP address literal without ever falling back to a DNS lookup. */
fun parseIpLiteral(text: String): InetAddress? {
    val value = text.trim()
    val looksValid = IPV4_LITERAL.matches(value) || (':' in value && IPV6_LITERAL.matches(value))
    return if (looksValid) runCatching { InetAddress.getByName(value) }.getOrNull() else null
}
