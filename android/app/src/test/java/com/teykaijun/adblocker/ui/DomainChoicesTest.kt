package com.teykaijun.adblocker.ui

import com.teykaijun.adblocker.data.Settings
import com.teykaijun.adblocker.ui.DomainChoices.Extra
import org.junit.Assert.assertEquals
import org.junit.Test

/** What the Activity dialog offers, worked out from the rules as they are now. */
class DomainChoicesTest {
    private val now = 1_000_000_000L

    private fun choices(domain: String, settings: Settings, listed: Boolean) = domainChoices(
        domain,
        match = settings.userRules().match(domain, now),
        listed = listed,
        blockedAgain = settings.blockedOnceAllowancesEnd(domain, listed, now),
    )

    @Test
    fun `a listed domain can be allowed always or for a while`() {
        assertEquals(DomainChoices(rule = true, extra = Extra.AllowForAWhile), choices("ads.example.com", Settings(), listed = true))
    }

    @Test
    fun `an unlisted domain can be blocked`() {
        assertEquals(DomainChoices(rule = false, extra = null), choices("news.example.org", Settings(), listed = false))
    }

    @Test
    fun `the user's own rule for the name can be removed`() {
        val settings = Settings().withDomainRule("ads.example.com", allow = true)
        assertEquals(DomainChoices(rule = null, extra = null), choices("ads.example.com", settings, listed = true))
        val blocked = Settings().withDomainRule("news.example.org", allow = false)
        assertEquals(DomainChoices(rule = null, extra = null), choices("news.example.org", blocked, listed = false))
    }

    @Test
    fun `a name allowed for a while can be blocked again now, or allowed for good`() {
        val settings = Settings().withTemporaryAllow("shop.example", minutes = 10, now = now)
        assertEquals(DomainChoices(rule = true, extra = Extra.BlockAgain), choices("shop.example", settings, listed = true))
        assertEquals("covered by its parent", DomainChoices(rule = true, extra = Extra.BlockAgain), choices("cdn.shop.example", settings, listed = true))
        // Without the allowance it isn't blocked anyway, so ending it would change nothing.
        assertEquals(DomainChoices(rule = false, extra = null), choices("cdn.shop.example", settings, listed = false))
    }

    @Test
    fun `an allowance that ran out offers the choices of a blocked name again`() {
        val settings = Settings(temporaryAllowed = mapOf("shop.example" to now - 1))
        assertEquals(DomainChoices(rule = true, extra = Extra.AllowForAWhile), choices("shop.example", settings, listed = true))
    }

    @Test
    fun `the user's rules for a parent count`() {
        val blockedParent = Settings().withDomainRule("example.com", allow = false)
        assertEquals(DomainChoices(rule = true, extra = Extra.AllowForAWhile), choices("ads.example.com", blockedParent, listed = false))
        val allowedParent = Settings().withDomainRule("example.com", allow = true)
        assertEquals(DomainChoices(rule = false, extra = null), choices("ads.example.com", allowedParent, listed = true))
        // A user block of the parent stays once the allowance of the subdomain ends.
        val both = blockedParent.withTemporaryAllow("ads.example.com", minutes = 10, now = now)
        assertEquals(DomainChoices(rule = true, extra = Extra.BlockAgain), choices("ads.example.com", both, listed = false))
    }
}
