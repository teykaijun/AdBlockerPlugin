package com.teykaijun.adblocker.tabs

import android.accessibilityservice.AccessibilityService
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.accessibility.AccessibilityEvent
import android.widget.Toast
import com.teykaijun.adblocker.app
import com.teykaijun.adblocker.vpn.VpnController

/**
 * Optional accessibility service that presses Back when a browser shows an address the VPN
 * blocks. Chrome and similar browsers then close a tab that a page opened, or return to the
 * page before a redirect.
 *
 * It only receives events from [Browsers], only reads their address bar, and does nothing
 * while protection is off.
 */
class TabCloserService : AccessibilityService() {
    private val guard = TabGuard(SystemClock::elapsedRealtime)
    private val handler = Handler(Looper.getMainLooper())

    override fun onServiceConnected() {
        super.onServiceConnected()
        TabCloser.connected(this)
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent) {
        val browser = event.packageName?.toString() ?: return
        val barId = Browsers.ADDRESS_BARS[browser] ?: return
        val matcher = VpnController.matcher ?: return
        // A bypassed browser uses the normal DNS, so its pages load; leave them.
        if (browser in app.settings.settings.value.bypassApps) return

        val root = rootInActiveWindow ?: return
        if (root.packageName?.toString() != browser) return
        val bar = root.findAccessibilityNodeInfosByViewId(barId).firstOrNull { it.isVisibleToUser } ?: return
        val reading = when {
            bar.isFocused -> AddressBar.Editing
            bar.isShowingHintText -> return
            else -> AddressBar.Showing(bar.text?.toString() ?: return)
        }

        val window = root.windowId
        val host = guard.onAddressBar(browser, window, reading, matcher::isBlocked) ?: return
        if (!performGlobalAction(GLOBAL_ACTION_BACK)) return
        TabCloser.recordLeft(host)
        Toast.makeText(this, "AdBlocker left a blocked page: $host", Toast.LENGTH_SHORT).show()
        handler.postDelayed({
            // No active window means a transition is still running; that tells us nothing.
            val active = rootInActiveWindow ?: return@postDelayed
            if (active.packageName?.toString() != browser) guard.onBrowserLeft(window, host)
        }, LEFT_CHECK_DELAY_MS)
    }

    override fun onInterrupt() = Unit

    override fun onUnbind(intent: Intent?): Boolean {
        TabCloser.disconnected(this)
        return super.onUnbind(intent)
    }

    override fun onDestroy() {
        handler.removeCallbacksAndMessages(null)
        TabCloser.disconnected(this)
        super.onDestroy()
    }

    private companion object {
        /** Long enough for the browser to close the tab or move to the background. */
        const val LEFT_CHECK_DELAY_MS = 700L
    }
}
