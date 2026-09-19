package com.teykaijun.adblocker.dns

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class RulesTest {
    private val rules = Rules(
        matcher = DomainMatcher(blocked = setOf("ads.example.com", "fake-shop.example"), allowed = setOf("real-shop.example")),
        scam = DomainMatcher(blocked = setOf("fake-shop.example", "real-shop.example"), allowed = setOf("real-shop.example")),
    )

    @Test
    fun `reports scam sites and their subdomains`() {
        assertTrue(rules.isScam("fake-shop.example"))
        assertTrue(rules.isScam("pay.fake-shop.example"))
    }

    @Test
    fun `leaves ordinary blocked domains unreported`() {
        assertTrue(rules.matcher.isBlocked("ads.example.com"))
        assertFalse(rules.isScam("ads.example.com"))
        assertFalse(rules.isScam("news.example.org"))
    }

    @Test
    fun `never reports a site the user allowed`() {
        assertFalse(rules.matcher.isBlocked("real-shop.example"))
        assertFalse(rules.isScam("real-shop.example"))
    }
}
