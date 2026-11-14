package com.teykaijun.adblocker.dns

import com.teykaijun.adblocker.data.Settings
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The order both apps decide in: a pause, then the user's most specific rule, then the lists. */
class UserRulesTest {
    private val now = 1_000_000_000L
    private val minute = 60_000L

    private val lists = DomainMatcher(
        blocked = setOf("ads.example.com", "shop.example", "tracker.example.net"),
        allowed = setOf("example.com"),
    )

    private fun blocked(domain: String, user: UserRules, at: Long = now) = user.isBlocked(domain, lists, at)

    @Test
    fun `the lists decide when the user has no rule for a name`() {
        assertTrue(blocked("cdn.shop.example", UserRules.NONE))
        assertTrue(blocked("tracker.example.net", UserRules.NONE))
        assertFalse("a list exception wins over a list block", blocked("ads.example.com", UserRules.NONE))
        assertFalse(blocked("news.example.org", UserRules.NONE))
    }

    @Test
    fun `a user block of a subdomain beats a list exception for its parent`() {
        val user = UserRules(blocked = setOf("ads.example.com"))
        assertTrue(blocked("ads.example.com", user))
        assertTrue(blocked("img.ads.example.com", user))
        assertFalse(blocked("www.example.com", user))
    }

    @Test
    fun `the most specific of the user's rules wins`() {
        val user = UserRules(blocked = setOf("ads.example.com"), allowed = setOf("example.com"))
        assertTrue(blocked("ads.example.com", user))
        assertFalse(blocked("www.example.com", user))
        assertFalse(blocked("example.com", user))

        val other = UserRules(blocked = setOf("example.com"), allowed = setOf("ads.example.com"))
        assertFalse(blocked("ads.example.com", other))
        assertTrue(blocked("www.example.com", other))
    }

    @Test
    fun `allowing wins over blocking on the same level`() {
        assertFalse(blocked("tracker.example.net", UserRules(blocked = setOf("tracker.example.net"), allowed = setOf("tracker.example.net"))))
        val forAWhile = UserRules(blocked = setOf("tracker.example.net"), allowedUntil = mapOf("tracker.example.net" to now + minute))
        assertFalse(blocked("tracker.example.net", forAWhile))
    }

    @Test
    fun `a temporary allow lets a domain and its subdomains through despite a list block`() {
        val user = UserRules(allowedUntil = mapOf("shop.example" to now + 10 * minute))
        assertFalse(blocked("shop.example", user))
        assertFalse(blocked("cdn.shop.example", user))
        assertTrue("another domain", blocked("tracker.example.net", user))
        assertEquals(UserRules.Match("shop.example", allows = true, until = now + 10 * minute), user.match("cdn.shop.example", now))
    }

    @Test
    fun `a user block of a subdomain beats a temporary allow of its parent`() {
        val user = UserRules(blocked = setOf("cdn.shop.example"), allowedUntil = mapOf("shop.example" to now + 10 * minute))
        assertTrue(blocked("cdn.shop.example", user))
        assertFalse(blocked("www.shop.example", user))
    }

    @Test
    fun `a temporary allow of a subdomain beats a user block of its parent`() {
        val user = UserRules(blocked = setOf("shop.example"), allowedUntil = mapOf("cdn.shop.example" to now + 10 * minute))
        assertFalse(blocked("cdn.shop.example", user))
        assertTrue(blocked("www.shop.example", user))
    }

    @Test
    fun `allowances that ran out do nothing`() {
        val user = UserRules(
            blocked = setOf("shop.example"),
            allowedUntil = mapOf("cdn.shop.example" to now - minute, "tracker.example.net" to now),
        )
        assertTrue(blocked("cdn.shop.example", user))
        assertTrue("ends at the time itself", blocked("tracker.example.net", user))
        assertNull(user.match("tracker.example.net", now))
        assertTrue(blocked("shop.example", UserRules(allowedUntil = mapOf("shop.example" to now + minute)), at = now + minute))
    }

    @Test
    fun `blocking a domain right after allowing it for a while blocks it at once`() {
        val allowed = Settings().withTemporaryAllow("tracker.example.net", minutes = 10, now = now)
        assertFalse(blocked("tracker.example.net", allowed.userRules()))
        assertTrue(blocked("tracker.example.net", allowed.withDomainRule("tracker.example.net", allow = false).userRules()))
        assertTrue(blocked("cdn.shop.example", Settings().withDomainRule("cdn.shop.example", allow = false).userRules()))
    }

    @Test
    fun `nothing is blocked while paused, and blocking comes back on its own`() {
        val user = UserRules(blocked = setOf("news.example.org"), pausedUntil = now + 10 * minute)
        assertTrue(user.isPaused(now))
        assertFalse(blocked("cdn.shop.example", user))
        assertFalse("not even the user's own blocks", blocked("news.example.org", user))
        assertFalse(user.isPaused(now + 10 * minute))
        assertTrue(blocked("cdn.shop.example", user, at = now + 10 * minute))
        assertTrue(blocked("news.example.org", user, at = now + 10 * minute))
        assertFalse(UserRules(pausedUntil = null).isPaused(now))
    }

    @Test
    fun `compares names in lower case and without a trailing dot`() {
        val user = UserRules(blocked = setOf("News.Example.org."), allowedUntil = mapOf("Shop.Example." to now + minute))
        assertTrue(blocked("NEWS.example.ORG.", user))
        assertTrue(blocked("www.news.example.org", user))
        assertFalse(blocked("Cdn.Shop.Example.", user))
        assertFalse("a lookalike isn't a subdomain", blocked("notnews.example.org", user))
    }

    @Test
    fun `reports which of the user's rules decides`() {
        val user = UserRules(blocked = setOf("example.com"), allowed = setOf("www.example.com"), allowedUntil = mapOf("shop.example" to now + minute))
        assertEquals(UserRules.Match("example.com", allows = false), user.match("ads.example.com", now))
        assertEquals(UserRules.Match("www.example.com", allows = true), user.match("img.www.example.com", now))
        assertNull(user.match("news.example.org", now))
        assertNull(UserRules.NONE.match("example.com", now))
    }

    @Test
    fun `follows allowances for a while on to a parent's that lasts longer`() {
        val nested = UserRules(allowedUntil = mapOf("cdn.shop.example" to now + 10 * minute, "shop.example" to now + 15 * minute))
        assertEquals(now + 15 * minute, nested.allowedUntil("cdn.shop.example", now))
        assertEquals(now + 15 * minute, nested.allowedUntil("img.cdn.shop.example", now))
        assertEquals(now + 15 * minute, nested.allowedUntil("shop.example", now))

        // A parent's allowance that ends first changes nothing.
        val shorter = UserRules(allowedUntil = mapOf("cdn.shop.example" to now + 10 * minute, "shop.example" to now + 5 * minute))
        assertEquals(now + 10 * minute, shorter.allowedUntil("cdn.shop.example", now))

        // A block in between ends it, and an "always allow" takes over for good.
        val blockedBetween = UserRules(blocked = setOf("shop.example"), allowedUntil = mapOf("cdn.shop.example" to now + 10 * minute))
        assertEquals(now + 10 * minute, blockedBetween.allowedUntil("cdn.shop.example", now))
        val allowedAfter = UserRules(allowed = setOf("shop.example"), allowedUntil = mapOf("cdn.shop.example" to now + 10 * minute))
        assertNull(allowedAfter.allowedUntil("cdn.shop.example", now))

        // No allowance decides these.
        assertNull(nested.allowedUntil("news.example.org", now))
        assertNull(UserRules(allowed = setOf("shop.example")).allowedUntil("cdn.shop.example", now))
        assertNull(nested.allowedUntil("cdn.shop.example", now + 20 * minute))
    }
}
