package com.teykaijun.adblocker.tabs

import com.teykaijun.adblocker.dns.RuleParser
import java.net.IDN

/**
 * Browsers whose address bar the tab closer reads, with the view id of that bar. All of them
 * close a tab that a page opened when Back is pressed on it, and go back a page otherwise.
 * res/xml/tab_closer_service.xml lists the same packages.
 */
object Browsers {
    val ADDRESS_BARS: Map<String, String> = mapOf(
        "com.android.chrome" to "com.android.chrome:id/url_bar",
        "com.chrome.beta" to "com.chrome.beta:id/url_bar",
        "com.chrome.dev" to "com.chrome.dev:id/url_bar",
        "com.chrome.canary" to "com.chrome.canary:id/url_bar",
        "com.microsoft.emmx" to "com.microsoft.emmx:id/url_bar",
        "com.brave.browser" to "com.brave.browser:id/url_bar",
        "com.vivaldi.browser" to "com.vivaldi.browser:id/url_bar",
        "com.sec.android.app.sbrowser" to "com.sec.android.app.sbrowser:id/location_bar_edit_text",
    )
}

/** What a browser's address bar shows. */
sealed interface AddressBar {
    /** The user is typing in it or choosing a suggestion. */
    data object Editing : AddressBar

    /** The address of the current page, as displayed (often without `https://`). */
    data class Showing(val text: String) : AddressBar
}

/**
 * The web host in an address bar's text, or null for anything else: search terms,
 * `about:blank`, browser pages such as `chrome://settings`, IP addresses.
 */
fun hostInAddressBar(text: String): String? {
    val trimmed = text.trim()
    if (trimmed.isEmpty() || trimmed.any(Char::isWhitespace)) return null
    val scheme = trimmed.substringBefore("://", missingDelimiterValue = "").lowercase()
    if (scheme.isNotEmpty() && scheme != "http" && scheme != "https") return null
    val authority = trimmed.substringAfter("://").substringBefore('/').substringBefore('?').substringBefore('#')
    if ('@' in authority) return null
    // Browsers show international names in Unicode; blocklists use the xn-- form.
    val name = authority.substringBefore(':').trimEnd('.')
    val ascii = runCatching { IDN.toASCII(name, IDN.ALLOW_UNASSIGNED) }.getOrNull() ?: return null
    return RuleParser.normalizeDomain(ascii)
}

/**
 * Decides when to press Back because a browser shows a blocked address, so that a pop-up tab
 * closes, or a redirected tab returns to the page before.
 *
 * It acts once each time the address bar of a window changes to a blocked host. It leaves
 * alone addresses the user opened from the address bar, pages that Back could not leave (the
 * browser went to the background instead), and pages that keep redirecting.
 */
class TabGuard(private val now: () -> Long) {

    private class WindowState {
        var shownHost: String? = null

        /** Host → time until which it is left alone. */
        val leftAlone = HashMap<String, Long>()
        val backs = BurstLimit(BACK_LIMIT_TIMES, BACK_LIMIT_GAP_MS)
    }

    private val windows = object : LinkedHashMap<Int, WindowState>(16, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<Int, WindowState>) = size > MAX_WINDOWS
    }
    private val editing = HashSet<String>()
    private val editedAt = HashMap<String, Long>()

    /**
     * Called with what the address bar of `browser`'s `window` shows. Returns the blocked host
     * to press Back for, or null to do nothing.
     */
    fun onAddressBar(browser: String, window: Int, bar: AddressBar, isBlocked: (String) -> Boolean): String? {
        val time = now()
        if (bar is AddressBar.Editing) {
            editing += browser
            return null
        }
        // Pressing Enter or picking a suggestion ends the editing; the address follows right after.
        if (editing.remove(browser)) editedAt[browser] = time

        // Placeholders such as about:blank don't count as a change: pop-ups often start there.
        val host = hostInAddressBar((bar as AddressBar.Showing).text) ?: return null
        val state = windows.getOrPut(window) { WindowState() }
        if (host == state.shownHost) return null
        state.shownHost = host

        state.leftAlone.values.removeAll { it <= time }
        if (host in state.leftAlone || !isBlocked(host)) return null
        val edited = editedAt[browser]
        if (edited != null && time - edited <= TYPED_GRACE_MS) {
            // The user asked for this address, so show them the browser's error page.
            state.leftAlone[host] = time + LEAVE_ALONE_MS
            return null
        }
        return if (state.backs.tryAcquire(time)) host else null
    }

    /**
     * Back took the browser off the screen instead of closing the tab, so the page may still
     * be there when the user returns. Don't press Back for it again.
     */
    fun onBrowserLeft(window: Int, host: String) {
        windows[window]?.leftAlone?.put(host, now() + LEAVE_ALONE_MS)
    }

    companion object {
        /** How long after editing the address bar a blocked address counts as the user's own. */
        const val TYPED_GRACE_MS = 5_000L
        const val LEAVE_ALONE_MS = 30 * 60_000L

        /** A page that redirects straight back is left after this many quick Backs. */
        const val BACK_LIMIT_TIMES = 4
        const val BACK_LIMIT_GAP_MS = 3_000L
        private const val MAX_WINDOWS = 8
    }
}

/** Allows at most [times] events in a row that are each less than [gapMs] after the last allowed one. */
internal class BurstLimit(private val times: Int, private val gapMs: Long) {
    private var last: Long? = null
    private var count = 0

    fun tryAcquire(time: Long): Boolean {
        val previous = last
        count = if (previous != null && time - previous < gapMs) count + 1 else 1
        if (count > times) return false
        last = time
        return true
    }
}
