package com.teykaijun.adblocker.data

import android.content.Context
import androidx.core.content.edit
import com.teykaijun.adblocker.BuildConfig
import com.teykaijun.adblocker.dns.DomainMatcher
import com.teykaijun.adblocker.dns.RuleParser
import com.teykaijun.adblocker.dns.Rules
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray

/** A list packaged into the app from the repository's `filters/` folder. */
data class BuiltInList(
    val id: String,
    val title: String,
    val description: String,
    val enabledByDefault: Boolean,
    val domains: Int,
)

data class ListStatus(
    val updatedAt: Long = 0,
    val domains: Int = 0,
    val error: String? = null,
    val updating: Boolean = false,
) {
    val downloaded: Boolean get() = updatedAt > 0
}

/**
 * Loads the built-in lists from assets, downloads the optional community
 * lists, and turns the current settings into a [DomainMatcher].
 */
class FilterRepository(
    private val context: Context,
    private val scope: CoroutineScope,
) {
    private val listDir = File(context.filesDir, "lists").apply { mkdirs() }
    private val prefs = context.getSharedPreferences("lists", Context.MODE_PRIVATE)
    private val jobs = mutableMapOf<String, Job>()

    val builtIn: List<BuiltInList> by lazy { readIndex() }

    private val statusState = MutableStateFlow(readStatus())
    val status: StateFlow<Map<String, ListStatus>> = statusState.asStateFlow()

    /** Bumped whenever a downloaded list file changes, so the VPN can reload. */
    private val revisionState = MutableStateFlow(0)
    val revision: StateFlow<Int> = revisionState.asStateFlow()

    suspend fun buildRules(settings: Settings): Rules = withContext(Dispatchers.IO) {
        val blocked = HashSet<String>(4_096)
        val allowed = HashSet<String>()
        val scamBlocked = HashSet<String>()
        val scamAllowed = HashSet<String>()
        for (list in builtIn) {
            if (settings.isBuiltInEnabled(list)) readBuiltIn(list.id, blocked, allowed)
        }
        for (list in settings.remoteLists) {
            val file = fileFor(list.id)
            if (!list.enabled || !file.exists()) continue
            // Scam lists are parsed into their own sets first; both sets share the same strings.
            val into = if (list.scam) scamBlocked to scamAllowed else blocked to allowed
            file.bufferedReader().useLines { RuleParser.parseInto(it, into.first, into.second) }
        }
        blocked += scamBlocked
        allowed += scamAllowed
        // Tells Firefox not to switch to its own DNS-over-HTTPS, which would skip the filter.
        blocked += FIREFOX_DOH_CANARY
        allowed -= FIREFOX_DOH_CANARY
        // The user's own rules win over everything else.
        blocked += settings.blockedDomains
        allowed -= settings.blockedDomains
        allowed += settings.allowedDomains
        Rules(DomainMatcher(blocked, allowed), DomainMatcher(scamBlocked, scamAllowed + settings.allowedDomains))
    }

    private fun readBuiltIn(id: String, blocked: MutableSet<String>, allowed: MutableSet<String>) {
        context.assets.open("$id.txt").bufferedReader().useLines { RuleParser.parseInto(it, blocked, allowed) }
    }

    /** Downloads `list` in the background, replacing the cached copy on success. */
    fun refresh(list: RemoteList) {
        synchronized(jobs) {
            if (jobs[list.id]?.isActive == true) return
            jobs[list.id] = scope.launch(Dispatchers.IO) { download(list) }
        }
    }

    /** Refreshes enabled lists that were never downloaded or are over a week old. */
    fun refreshStale(settings: Settings) {
        val now = System.currentTimeMillis()
        for (list in settings.remoteLists.filter { it.enabled }) {
            val status = statusState.value[list.id]
            if (status == null || now - status.updatedAt > MAX_AGE_MS) refresh(list)
        }
    }

    fun delete(listId: String) {
        fileFor(listId).delete()
        statusState.update { it - listId }
        saveStatus()
        revisionState.update { it + 1 }
    }

    private fun download(list: RemoteList) {
        setStatus(list.id) { it.copy(updating = true, error = null) }
        val temp = File(listDir, "${list.id}.download")
        try {
            val url = URL(list.url)
            if (url.protocol != "https") throw IOException("Only https:// addresses are allowed")
            val connection = url.openConnection() as HttpURLConnection
            try {
                connection.connectTimeout = TIMEOUT_MS
                connection.readTimeout = TIMEOUT_MS
                connection.setRequestProperty("User-Agent", "AdBlocker-Android/${BuildConfig.VERSION_NAME}")
                if (connection.responseCode !in 200..299) throw IOException("Server answered HTTP ${connection.responseCode}")
                connection.inputStream.use { input ->
                    temp.outputStream().use { output ->
                        val buffer = ByteArray(64 * 1024)
                        var total = 0L
                        while (true) {
                            val read = input.read(buffer)
                            if (read < 0) break
                            total += read
                            if (total > MAX_BYTES) throw IOException("The list is larger than 50 MB")
                            output.write(buffer, 0, read)
                        }
                    }
                }
            } finally {
                connection.disconnect()
            }

            val blocked = HashSet<String>()
            temp.bufferedReader().useLines { RuleParser.parseInto(it, blocked, HashSet()) }
            if (blocked.isEmpty()) throw IOException("No domains found. Is this a blocklist?")

            val target = fileFor(list.id)
            if (!temp.renameTo(target)) {
                target.delete()
                if (!temp.renameTo(target)) throw IOException("Could not save the list")
            }
            setStatus(list.id) { ListStatus(updatedAt = System.currentTimeMillis(), domains = blocked.size) }
            revisionState.update { it + 1 }
        } catch (e: Exception) {
            temp.delete()
            setStatus(list.id) { it.copy(updating = false, error = e.message ?: e.javaClass.simpleName) }
        }
    }

    private fun setStatus(id: String, change: (ListStatus) -> ListStatus) {
        statusState.update { it + (id to change(it[id] ?: ListStatus())) }
        saveStatus()
    }

    private fun fileFor(id: String) = File(listDir, "$id.txt")

    private fun readIndex(): List<BuiltInList> {
        val json = context.assets.open("lists.json").bufferedReader().use { it.readText() }
        val array = JSONArray(json)
        return (0 until array.length()).map { i ->
            val o = array.getJSONObject(i)
            val id = o.getString("id")
            val blocked = HashSet<String>()
            readBuiltIn(id, blocked, HashSet())
            BuiltInList(
                id = id,
                title = o.getString("title"),
                description = o.getString("description"),
                enabledByDefault = o.getBoolean("enabledByDefault"),
                domains = blocked.size,
            )
        }
    }

    private fun readStatus(): Map<String, ListStatus> =
        prefs.all.keys.filter { it.endsWith(".updated") }.associate { key ->
            val id = key.removeSuffix(".updated")
            id to ListStatus(
                updatedAt = prefs.getLong(key, 0),
                domains = prefs.getInt("$id.domains", 0),
                error = prefs.getString("$id.error", null),
            )
        }

    private fun saveStatus() {
        prefs.edit {
            clear()
            for ((id, s) in statusState.value) {
                putLong("$id.updated", s.updatedAt)
                putInt("$id.domains", s.domains)
                if (s.error != null) putString("$id.error", s.error)
            }
        }
    }

    private companion object {
        const val FIREFOX_DOH_CANARY = "use-application-dns.net"
        val MAX_AGE_MS = TimeUnit.DAYS.toMillis(7)
        const val MAX_BYTES = 50L * 1024 * 1024
        const val TIMEOUT_MS = 30_000
    }
}
