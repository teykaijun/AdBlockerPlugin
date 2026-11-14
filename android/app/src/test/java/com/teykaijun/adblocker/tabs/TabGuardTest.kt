package com.teykaijun.adblocker.tabs

import com.teykaijun.adblocker.dns.DomainMatcher
import com.teykaijun.adblocker.dns.UserRules
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TabGuardTest {
    private var clock = 1_000_000L
    private val guard = TabGuard { clock }
    private val matcher = DomainMatcher(blocked = setOf("ads.example.com", "popunder.example.net"), allowed = emptySet())

    private fun show(text: String, window: Int = 1, browser: String = CHROME) =
        guard.onAddressBar(browser, window, AddressBar.Showing(text), matcher::isBlocked)

    private fun edit(window: Int = 1, browser: String = CHROME) =
        guard.onAddressBar(browser, window, AddressBar.Editing, matcher::isBlocked)

    @Test
    fun `presses Back when a page sends the tab to a blocked host`() {
        assertNull(show("news.example.org/story"))
        assertEquals("ads.example.com", show("ads.example.com/offer?id=1"))
    }

    @Test
    fun `waits for about-blank pop-ups to go somewhere`() {
        assertNull(show("news.example.org"))
        assertNull(show("about:blank"))
        assertEquals("popunder.example.net", show("https://popunder.example.net/x"))
    }

    @Test
    fun `acts once while the bar keeps showing the same host`() {
        show("news.example.org")
        assertEquals("ads.example.com", show("ads.example.com"))
        assertNull(show("ads.example.com"))
        assertNull(show("ads.example.com/other-path"))
        assertNull(show("about:blank"))
        assertNull(show("ads.example.com"))
    }

    @Test
    fun `acts again when the blocked host comes back after another page`() {
        show("news.example.org")
        assertEquals("ads.example.com", show("ads.example.com"))
        clock += 5_000
        assertNull(show("news.example.org"))
        assertEquals("ads.example.com", show("ads.example.com"))
    }

    @Test
    fun `leaves allowed hosts alone`() {
        assertNull(show("news.example.org"))
        assertNull(show("example.com"))
        assertNull(show("safe.example.net"))
    }

    @Test
    fun `leaves addresses the user typed alone`() {
        show("news.example.org")
        edit()
        clock += 20_000 // thinking before pressing Enter
        edit()
        clock += 300
        assertNull(show("ads.example.com"))

        // Switching to another tab and back doesn't change that.
        clock += 60_000
        show("news.example.org")
        assertNull(show("ads.example.com"))
    }

    @Test
    fun `acts on pop-ups that come well after editing`() {
        show("news.example.org")
        edit()
        clock += 100
        show("news.example.org/search-result")
        clock += TabGuard.TYPED_GRACE_MS + 1
        assertEquals("ads.example.com", show("ads.example.com"))
    }

    @Test
    fun `counts editing per browser and pop-ups per window`() {
        show("news.example.org", window = 1)
        edit(window = 1, browser = EDGE)
        show("news.example.org", window = 2, browser = EDGE)
        // Chrome was not edited, and a new custom tab window starts fresh.
        assertEquals("ads.example.com", show("ads.example.com", window = 1))
        assertEquals("ads.example.com", show("ads.example.com", window = 3))
    }

    @Test
    fun `leaves a page alone once Back sent the browser away from it`() {
        show("news.example.org")
        assertEquals("ads.example.com", show("ads.example.com"))
        guard.onBrowserLeft(window = 1, host = "ads.example.com")
        clock += 10_000
        show("news.example.org")
        assertNull(show("ads.example.com"))

        clock += TabGuard.LEAVE_ALONE_MS
        show("news.example.org")
        assertEquals("ads.example.com", show("ads.example.com"))
    }

    @Test
    fun `stops when a page keeps redirecting`() {
        repeat(TabGuard.BACK_LIMIT_TIMES) {
            assertNull(show("bouncer.example.org"))
            assertEquals("ads.example.com", show("ads.example.com"))
            clock += 1_000
        }
        assertNull(show("bouncer.example.org"))
        assertNull("the error page stays", show("ads.example.com"))

        // A while later it works again.
        clock += TabGuard.BACK_LIMIT_GAP_MS
        show("bouncer.example.org")
        assertEquals("ads.example.com", show("ads.example.com"))
    }

    @Test
    fun `stops after many quick Backs across a browser's windows`() {
        // An app that keeps opening a custom tab to a blocked address gets a new window each time.
        repeat(TabGuard.BROWSER_BACK_LIMIT_TIMES) { i ->
            assertEquals("ads.example.com", show("ads.example.com", window = i + 1))
            clock += 1_000
        }
        assertNull(show("ads.example.com", window = TabGuard.BROWSER_BACK_LIMIT_TIMES + 1))

        // Other browsers aren't affected, and a while later it works again.
        assertEquals("ads.example.com", show("ads.example.com", window = 50, browser = EDGE))
        clock += TabGuard.BACK_LIMIT_GAP_MS
        assertEquals("ads.example.com", show("ads.example.com", window = 51))
    }

    @Test
    fun `counts an allow for the www name that the browser hides`() {
        val lists = DomainMatcher(blocked = setOf("shop.example"), allowed = emptySet())
        assertTrue(blockedAsShown("shop.example", lists::isBlocked))
        assertTrue(blockedAsShown("www.shop.example", lists::isBlocked))
        assertFalse(blockedAsShown("shop.example", DomainMatcher(blocked = setOf("shop.example"), allowed = setOf("www.shop.example"))::isBlocked))

        // The Activity screen allows the name that was looked up, www included.
        val now = 1_000_000_000L
        val user = UserRules(allowedUntil = mapOf("www.shop.example" to now + 60_000))
        val blocks = { host: String -> user.isBlocked(host, lists, now) }
        assertFalse(blockedAsShown("shop.example", blocks))
        assertNull(guard.onAddressBar(CHROME, 1, AddressBar.Showing("https://shop.example/basket")) { blockedAsShown(it, blocks) })
        assertTrue(blockedAsShown("other.shop.example", blocks))
    }

    @Test
    fun `keeps acting for pop-ups that are a few seconds apart`() {
        repeat(TabGuard.BACK_LIMIT_TIMES * 2) {
            show("streams.example.org")
            assertEquals("ads.example.com", show("ads.example.com"))
            clock += TabGuard.BACK_LIMIT_GAP_MS
        }
    }

    @Test
    fun `reads hosts from what address bars display`() {
        assertEquals("ads.example.com", hostInAddressBar("ads.example.com"))
        assertEquals("ads.example.com", hostInAddressBar(" https://Ads.Example.com./landing?x=1#top "))
        assertEquals("ads.example.com", hostInAddressBar("http://ads.example.com:8080"))
        assertEquals("ads.example.com", hostInAddressBar("ads.example.com/path/with:colon"))
        assertEquals("xn--mnchen-3ya.de", hostInAddressBar("münchen.de"))
    }

    @Test
    fun `ignores everything that is not a web address`() {
        for (text in listOf(
            "",
            "   ",
            "about:blank",
            "chrome://settings",
            "chrome-native://newtab/",
            "file:///sdcard/page.html",
            "content://downloads/1",
            "javascript:void(0)",
            "mailto:someone@example.com",
            "user@ads.example.com",
            "best pizza near me",
            "weather",
            "192.168.1.1",
            "http://[::1]/",
            "localhost:3000",
        )) {
            assertNull(text, hostInAddressBar(text))
        }
    }

    @Test
    fun `the service listens to exactly the listed browsers`() {
        val xml = File("src/main/res/xml/tab_closer_service.xml").readText()
        val packages = Regex("""android:packageNames="([^"]*)"""").find(xml)!!.groupValues[1].split(',').toSet()
        assertEquals(Browsers.ADDRESS_BARS.keys, packages)
        for ((pkg, id) in Browsers.ADDRESS_BARS) assertTrue(id, id.startsWith("$pkg:id/"))
    }

    private companion object {
        const val CHROME = "com.android.chrome"
        const val EDGE = "com.microsoft.emmx"
    }
}
