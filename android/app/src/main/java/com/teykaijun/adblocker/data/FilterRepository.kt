package com.teykaijun.adblocker.data

import android.content.Context
import androidx.core.content.edit
import com.teykaijun.adblocker.BuildConfig
import com.teykaijun.adblocker.dns.DomainMatcher
import com.teykaijun.adblocker.dns.RuleParser
import com.teykaijun.adblocker.dns.Rules
import java.io.File
import java.io.IOException
import java.net.ConnectException
import java.net.HttpURLConnection
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.URL
import java.net.UnknownHostException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import javax.net.ssl.SSLException
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
 * lists, and turns the lists that are switched on into [Rules].
 */
class FilterRepository(
    private val context: Context,
    private val scope: CoroutineScope,
) {
    private val listDir = File(context.filesDir, "lists").apply { mkdirs() }
    private val prefs = context.getSharedPreferences("lists", Context.MODE_PRIVATE)
    private val jobs = mutableMapOf<String, Job>()

    /** When each list's latest download started; see [isDue]. */
    private val triedAt = ConcurrentHashMap<String, Long>()

    /** Lists the phone couldn't use, and until when they aren't downloaded again by themselves. */
    private val heldUntil = ConcurrentHashMap<String, Long>()

    val builtIn: List<BuiltInList> by lazy { readIndex() }

    private val statusState = MutableStateFlow(readStatus())
    val status: StateFlow<Map<String, ListStatus>> = statusState.asStateFlow()

    /** Bumped whenever a downloaded list file changes, so the VPN can reload. */
    private val revisionState = MutableStateFlow(0)
    val revision: StateFlow<Int> = revisionState.asStateFlow()

    /**
     * Reads the lists that are switched on. The user's own rules aren't part of this: they are
     * small and change often, so the VPN applies them on top (see [Settings.userRules]).
     */
    suspend fun buildRules(settings: Settings): Rules = withContext(Dispatchers.IO) {
        try {
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
            Rules(DomainMatcher(blocked, allowed), DomainMatcher(scamBlocked, scamAllowed))
        } catch (e: OutOfMemoryError) {
            // Stop protection with an explanation instead of crashing the whole app.
            throw IOException("Your filter lists are too big for this phone. Turn one off in Filters.")
        }
    }

    private fun readBuiltIn(id: String, blocked: MutableSet<String>, allowed: MutableSet<String>) {
        context.assets.open("$id.txt").bufferedReader().useLines { RuleParser.parseInto(it, blocked, allowed) }
    }

    /** Downloads `list` in the background, replacing the cached copy on success. */
    fun refresh(list: RemoteList) {
        synchronized(jobs) {
            if (jobs[list.id]?.isActive == true) return
            // Noted now, not when the download fails a little later, so the next hourly check
            // finds a failed try an hour old and tries again.
            triedAt[list.id] = System.currentTimeMillis()
            jobs[list.id] = scope.launch(Dispatchers.IO) { download(list) }
        }
    }

    /**
     * Refreshes enabled lists that were never downloaded or are over a week old. A list whose
     * download failed waits an hour from that try, so a broken address isn't fetched again on
     * every network change, or a week when the phone couldn't use the list (see [retryAfter]).
     * "Update now" still tries at once.
     */
    fun refreshStale(settings: Settings) {
        val now = System.currentTimeMillis()
        for (list in settings.remoteLists.filter { it.enabled }) {
            if (heldUntil[list.id]?.let { now < it } == true) continue
            if (isDue(statusState.value[list.id], triedAt[list.id], now)) refresh(list)
        }
    }

    fun delete(listId: String) {
        // download() checks for this under the same lock before it saves anything.
        synchronized(jobs) {
            jobs.remove(listId)?.cancel()
            fileFor(listId).delete()
        }
        triedAt.remove(listId)
        heldUntil.remove(listId)
        synchronized(prefs) {
            statusState.update { it - listId }
            saveStatus()
        }
        revisionState.update { it + 1 }
    }

    private suspend fun download(list: RemoteList) {
        val job = currentCoroutineContext().job
        synchronized(jobs) {
            job.ensureActive()
            setStatus(list.id) { it.copy(updating = true, error = null) }
        }
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
                            job.ensureActive() // the list was removed meanwhile
                            val read = input.read(buffer)
                            if (read < 0) break
                            total += read
                            if (total > MAX_BYTES) throw UnusableListException("The list is larger than 50 MB")
                            output.write(buffer, 0, read)
                        }
                    }
                }
            } finally {
                connection.disconnect()
            }

            val domains = temp.bufferedReader().useLines { countDomains(it) }
            if (domains == 0) throw UnusableListException("No domains found. Is this a blocklist?")

            synchronized(jobs) {
                job.ensureActive()
                val target = fileFor(list.id)
                if (!temp.renameTo(target)) {
                    target.delete()
                    if (!temp.renameTo(target)) throw IOException("Could not save the list")
                }
                setStatus(list.id) { ListStatus(updatedAt = System.currentTimeMillis(), domains = domains) }
                heldUntil.remove(list.id)
            }
            revisionState.update { it + 1 }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            failed(list.id, job, e.message ?: e.javaClass.simpleName, retryAfter(e))
        } catch (e: OutOfMemoryError) {
            failed(list.id, job, "This list is too big for this phone.", retryAfter(e))
        } finally {
            temp.delete() // already gone after a successful rename
        }
    }

    private fun failed(id: String, job: Job, message: String, retry: Retry) {
        synchronized(jobs) {
            if (!job.isActive) return // the list was removed
            setStatus(id) { it.copy(updating = false, error = message) }
            when (retry) {
                // At the next check, so at the latest when the phone gets online again.
                Retry.WhenOnline -> triedAt.remove(id)
                Retry.InAnHour -> Unit // counted from the start of this try
                Retry.InAWeek -> heldUntil[id] = System.currentTimeMillis() + MAX_AGE_MS
            }
        }
    }

    private fun setStatus(id: String, change: (ListStatus) -> ListStatus) {
        // Saving under the lock keeps an older snapshot from overwriting a newer one.
        synchronized(prefs) {
            statusState.update { it + (id to change(it[id] ?: ListStatus())) }
            saveStatus()
        }
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
        const val MAX_BYTES = 50L * 1024 * 1024
        const val TIMEOUT_MS = 30_000
    }
}

private val MAX_AGE_MS = TimeUnit.DAYS.toMillis(7)
private val RETRY_AFTER_MS = TimeUnit.HOURS.toMillis(1)

/**
 * Whether [FilterRepository.refreshStale] should download a list now: it was never downloaded or
 * is over a week old, and no try started in the last hour. (A stale list's latest try failed.)
 */
internal fun isDue(status: ListStatus?, triedAt: Long?, now: Long): Boolean {
    val stale = status == null || now - status.updatedAt > MAX_AGE_MS
    return stale && (triedAt == null || now - triedAt >= RETRY_AFTER_MS)
}

/** When a list whose download failed is tried again without the user asking. */
internal enum class Retry { WhenOnline, InAnHour, InAWeek }

/** A list that downloaded fine but that the phone can't use, so downloading it again soon won't help. */
internal class UnusableListException(message: String) : IOException(message)

/**
 * When to try again after a download failed with [e]. Without a connection (no network yet, a
 * hotel sign-in page) it's worth trying as soon as the phone gets online. A list too big for the
 * phone would only be downloaded in full again, so that waits a week.
 */
internal fun retryAfter(e: Throwable): Retry = when (e) {
    is UnusableListException, is OutOfMemoryError -> Retry.InAWeek
    is UnknownHostException, is ConnectException, is NoRouteToHostException, is SocketTimeoutException, is SSLException -> Retry.WhenOnline
    else -> Retry.InAnHour
}

/** More than a phone can hold in memory next to the other lists. */
internal const val MAX_LIST_DOMAINS = 1_000_000

/**
 * Counts the different domains a downloaded list blocks. Gives up with an [IOException] past
 * `limit`, before a huge list runs the app out of memory.
 */
internal fun countDomains(lines: Sequence<String>, limit: Int = MAX_LIST_DOMAINS): Int {
    val blocked = HashSet<String>()
    for (line in lines) {
        for (rule in RuleParser.parseLine(line)) {
            if (!rule.allow && blocked.add(rule.domain) && blocked.size > limit) {
                throw UnusableListException("This list has more domains than a phone can hold.")
            }
        }
    }
    return blocked.size
}
