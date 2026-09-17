package com.teykaijun.adblocker.tabs

import android.accessibilityservice.AccessibilityService
import android.content.ComponentName
import android.content.Context
import android.provider.Settings
import java.lang.ref.WeakReference
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/** Blocked pages the tab closer left since its service started. */
data class TabCloserStats(val count: Int = 0, val latestHost: String? = null, val latestTime: Long = 0)

/** The tab closer's state for the UI. The work happens in [TabCloserService]. */
object TabCloser {
    private val runningState = MutableStateFlow(false)
    val running: StateFlow<Boolean> = runningState.asStateFlow()

    private val statsState = MutableStateFlow(TabCloserStats())
    val stats: StateFlow<TabCloserStats> = statsState.asStateFlow()

    private var service = WeakReference<AccessibilityService>(null)

    internal fun connected(connected: AccessibilityService) {
        service = WeakReference(connected)
        runningState.value = true
    }

    internal fun disconnected(disconnected: AccessibilityService) {
        if (service.get() !== disconnected) return
        service.clear()
        runningState.value = false
        statsState.value = TabCloserStats()
    }

    internal fun recordLeft(host: String) {
        statsState.update { TabCloserStats(it.count + 1, host, System.currentTimeMillis()) }
    }

    /** Whether the service is switched on in Android's accessibility settings. */
    fun isEnabled(context: Context): Boolean {
        val setting = Settings.Secure.getString(context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)
            ?: return false
        val ours = ComponentName(context, TabCloserService::class.java)
        return setting.split(':').any { ComponentName.unflattenFromString(it) == ours }
    }

    /** Switches the service off. Returns false if it isn't connected, so only Android's settings can. */
    fun turnOff(): Boolean {
        val connected = service.get() ?: return false
        connected.disableSelf()
        return true
    }
}
