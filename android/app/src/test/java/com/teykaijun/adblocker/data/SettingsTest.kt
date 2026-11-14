package com.teykaijun.adblocker.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SettingsTest {
    private val now = 1_000_000_000L
    private val minute = 60_000L

    @Test
    fun `allowing for a while keeps only entries that have not run out`() {
        val settings = Settings(temporaryAllowed = mapOf("old.example.org" to now - minute))
            .withTemporaryAllow("shop.example.com", minutes = 10, now = now)
        assertEquals(mapOf("shop.example.com" to now + 10 * minute), settings.temporaryAllowed)
        assertEquals(now + 10 * minute, settings.userRules().match("cdn.shop.example.com", now)?.until)
        assertNull(settings.userRules().match("shop.example.com", now + 11 * minute))
    }

    @Test
    fun `a permanent rule ends the allowance for exactly that domain`() {
        val allowed = Settings()
            .withTemporaryAllow("shop.example.com", minutes = 10, now = now)
            .withTemporaryAllow("x.com", minutes = 10, now = now)
        assertEquals(setOf("shop.example.com"), allowed.withDomainRule("x.com", allow = false).temporaryAllowed.keys)
        assertEquals(setOf("shop.example.com"), allowed.withDomainRule("x.com", allow = true).temporaryAllowed.keys)
        assertEquals(setOf("shop.example.com"), allowed.withoutDomainRule("x.com").temporaryAllowed.keys)
        // A parent's allowance stays, and the user's block of the subdomain wins over it.
        val blockedSub = allowed.withDomainRule("cdn.shop.example.com", allow = false)
        assertEquals(setOf("shop.example.com", "x.com"), blockedSub.temporaryAllowed.keys)
        assertEquals(setOf("cdn.shop.example.com"), blockedSub.blockedDomains)
    }

    @Test
    fun `block again now ends the allowances that cover a name, and nothing else`() {
        val settings = Settings(
            allowedDomains = setOf("cdn.shop.example.com"),
            temporaryAllowed = mapOf(
                "shop.example.com" to now + minute,
                "cdn.shop.example.com" to now + minute,
                "img.cdn.shop.example.com" to now + minute,
                "notshop.example.com" to now + minute,
            ),
        ).withoutAllowancesFor("cdn.shop.example.com")
        assertEquals(setOf("img.cdn.shop.example.com", "notshop.example.com"), settings.temporaryAllowed.keys)
        assertEquals("permanent rules stay", setOf("cdn.shop.example.com"), settings.allowedDomains)
    }

    @Test
    fun `tells whether a name is blocked once its allowances end`() {
        val allowed = Settings().withTemporaryAllow("ads.example.com", minutes = 10, now = now)
        assertTrue(allowed.blockedOnceAllowancesEnd("cdn.ads.example.com", listed = true, now = now))
        assertFalse(allowed.blockedOnceAllowancesEnd("cdn.ads.example.com", listed = false, now = now))
        // Then the user's own rules for a parent decide before the lists.
        assertTrue(allowed.withDomainRule("example.com", allow = false).blockedOnceAllowancesEnd("ads.example.com", listed = false, now = now))
        assertFalse(allowed.withDomainRule("example.com", allow = true).blockedOnceAllowancesEnd("ads.example.com", listed = true, now = now))
    }

    @Test
    fun `a pause lasts until its time`() {
        val paused = Settings().pausedFor(10, now = now)
        assertEquals(now + 10 * minute, paused.pausedUntil)
        assertTrue(paused.isPaused(now))
        assertFalse(paused.isPaused(now + 10 * minute))
        assertFalse(Settings().isPaused(now))
        assertTrue(paused.userRules().isPaused(now))
    }

    @Test
    fun `forgets pauses and allowances once they are over`() {
        val settings = Settings(
            pausedUntil = now + 5 * minute,
            temporaryAllowed = mapOf("a.example" to now + minute, "b.example" to now + 20 * minute),
        )
        assertEquals(now + minute, settings.nextExpiry())

        val later = settings.withoutExpired(now + 5 * minute)
        assertNull(later.pausedUntil)
        assertEquals(mapOf("b.example" to now + 20 * minute), later.temporaryAllowed)
        assertEquals(now + 20 * minute, later.nextExpiry())
        assertNull(Settings().nextExpiry())
        assertEquals(settings, settings.withoutExpired(now))
    }

    @Test
    fun `saves allowances and reads them back, skipping broken and old entries`() {
        val allowed = mapOf("shop.example.com" to now + minute, "x.com" to now + 2 * minute)
        assertEquals(allowed, decodeAllowances(encodeAllowances(allowed), now))
        val saved = setOf(
            "shop.example.com=${now + minute}",
            "old.example.org=${now - minute}",
            "ends.example.org=$now",
            "no-time.example.org",
            "bad-time.example.org=soon",
            "=${now + minute}",
            "two=equals=${now + minute}",
        )
        assertEquals(mapOf("shop.example.com" to now + minute), decodeAllowances(saved, now))
        assertTrue(decodeAllowances(emptySet(), now).isEmpty())
    }

    @Test
    fun `reads a saved pause only while it lasts`() {
        assertEquals(now + minute, decodePausedUntil(now + minute, now))
        assertNull(decodePausedUntil(now, now))
        assertNull("nothing saved", decodePausedUntil(0, now))
    }
}
