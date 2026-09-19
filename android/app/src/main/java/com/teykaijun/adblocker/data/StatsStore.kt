package com.teykaijun.adblocker.data

import android.content.Context
import androidx.core.content.edit
import java.time.LocalDate
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

data class QueryEvent(val id: Long, val time: Long, val domain: String, val blocked: Boolean, val scam: Boolean = false)

data class Totals(
    val blockedToday: Long = 0,
    val queriesToday: Long = 0,
    val blockedTotal: Long = 0,
    val queriesTotal: Long = 0,
    val since: Long = 0,
)

/**
 * Counters and a short history of recent queries. [record] is called from
 * the DNS thread for every query, so it only touches memory; the VPN service
 * calls [publish] and [persist] on a timer.
 */
class StatsStore(context: Context) {
    private val prefs = context.getSharedPreferences("stats", Context.MODE_PRIVATE)
    private val lock = Any()

    private var day = prefs.getLong(KEY_DAY, today())
    private var blockedToday = prefs.getLong(KEY_BLOCKED_TODAY, 0)
    private var queriesToday = prefs.getLong(KEY_QUERIES_TODAY, 0)
    private var blockedTotal = prefs.getLong(KEY_BLOCKED_TOTAL, 0)
    private var queriesTotal = prefs.getLong(KEY_QUERIES_TOTAL, 0)
    private var since = prefs.getLong(KEY_SINCE, System.currentTimeMillis())
    private val recentEvents = ArrayDeque<QueryEvent>(RECENT_LIMIT)
    private var nextId = 0L
    private var dirty = false

    private val totalsState = MutableStateFlow(snapshot())
    val totals: StateFlow<Totals> = totalsState.asStateFlow()

    private val recentState = MutableStateFlow<List<QueryEvent>>(emptyList())
    /** Newest first. */
    val recent: StateFlow<List<QueryEvent>> = recentState.asStateFlow()

    fun record(domain: String, blocked: Boolean, scam: Boolean = false) {
        synchronized(lock) {
            rollOverIfNewDay()
            queriesToday++
            queriesTotal++
            if (blocked) {
                blockedToday++
                blockedTotal++
            }
            recentEvents.addFirst(QueryEvent(nextId++, System.currentTimeMillis(), domain, blocked, scam))
            if (recentEvents.size > RECENT_LIMIT) recentEvents.removeLast()
            dirty = true
        }
    }

    /** Pushes the latest numbers to the UI. */
    fun publish() {
        synchronized(lock) {
            rollOverIfNewDay()
            totalsState.value = snapshot()
            if (recentState.value.firstOrNull()?.id != recentEvents.firstOrNull()?.id) {
                recentState.value = recentEvents.toList()
            }
        }
    }

    fun persist() {
        synchronized(lock) {
            if (!dirty) return
            dirty = false
            prefs.edit {
                putLong(KEY_DAY, day)
                putLong(KEY_BLOCKED_TODAY, blockedToday)
                putLong(KEY_QUERIES_TODAY, queriesToday)
                putLong(KEY_BLOCKED_TOTAL, blockedTotal)
                putLong(KEY_QUERIES_TOTAL, queriesTotal)
                putLong(KEY_SINCE, since)
            }
        }
    }

    fun reset() {
        synchronized(lock) {
            blockedToday = 0
            queriesToday = 0
            blockedTotal = 0
            queriesTotal = 0
            since = System.currentTimeMillis()
            dirty = true
        }
        persist()
        publish()
    }

    fun clearRecent() {
        synchronized(lock) {
            recentEvents.clear()
            recentState.value = emptyList()
        }
    }

    private fun rollOverIfNewDay() {
        val now = today()
        if (now != day) {
            day = now
            blockedToday = 0
            queriesToday = 0
            dirty = true
        }
    }

    private fun snapshot() = Totals(blockedToday, queriesToday, blockedTotal, queriesTotal, since)

    private fun today() = LocalDate.now().toEpochDay()

    private companion object {
        const val RECENT_LIMIT = 500
        const val KEY_DAY = "day"
        const val KEY_BLOCKED_TODAY = "blocked_today"
        const val KEY_QUERIES_TODAY = "queries_today"
        const val KEY_BLOCKED_TOTAL = "blocked_total"
        const val KEY_QUERIES_TOTAL = "queries_total"
        const val KEY_SINCE = "since"
    }
}
