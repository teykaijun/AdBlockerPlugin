package com.teykaijun.adblocker.dns

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DomainMatcherTest {
    private val matcher = DomainMatcher(
        blocked = setOf("doubleclick.net", "ads.example.com", "tracker.io"),
        allowed = setOf("safe.ads.example.com", "tracker.io"),
    )

    @Test
    fun `blocks listed domains and their subdomains`() {
        assertTrue(matcher.isBlocked("doubleclick.net"))
        assertTrue(matcher.isBlocked("googleads.g.doubleclick.net"))
        assertTrue(matcher.isBlocked("ads.example.com"))
        assertTrue(matcher.isBlocked("cdn.ads.example.com"))
    }

    @Test
    fun `leaves parents and lookalikes alone`() {
        assertFalse(matcher.isBlocked("example.com"))
        assertFalse(matcher.isBlocked("www.example.com"))
        assertFalse(matcher.isBlocked("notdoubleclick.net"))
        assertFalse(matcher.isBlocked("doubleclick.net.example.org"))
        assertFalse(matcher.isBlocked(""))
        assertFalse(matcher.isBlocked("net"))
    }

    @Test
    fun `lets exceptions win`() {
        assertFalse(matcher.isBlocked("safe.ads.example.com"))
        assertFalse(matcher.isBlocked("img.safe.ads.example.com"))
        assertFalse("allowed and blocked at once", matcher.isBlocked("tracker.io"))
        assertFalse(matcher.isBlocked("pixel.tracker.io"))
    }

    @Test
    fun `normalises case and a trailing dot`() {
        assertTrue(matcher.isBlocked("DoubleClick.NET."))
    }

    @Test
    fun `counts blocked domains`() {
        assertEquals(3, matcher.blockedCount)
        assertFalse(DomainMatcher.EMPTY.isBlocked("doubleclick.net"))
    }
}
