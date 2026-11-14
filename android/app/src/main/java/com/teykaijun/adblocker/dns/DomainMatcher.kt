package com.teykaijun.adblocker.dns

/**
 * Decides whether the filter lists block a hostname. A rule for `example.com`
 * also covers every subdomain, and an allow rule anywhere up the chain wins over
 * a block rule, the same as `@@||example.com^` in Adblock Plus syntax. The
 * user's own rules come first and aren't in here; see [UserRules].
 */
class DomainMatcher(private val blocked: Set<String>, private val allowed: Set<String>) {

    val blockedCount: Int get() = blocked.size

    fun isBlocked(domain: String): Boolean {
        var host = domain.trimEnd('.').lowercase()
        var blockHit = false
        while (host.isNotEmpty()) {
            if (host in allowed) return false
            if (!blockHit && host in blocked) blockHit = true
            val dot = host.indexOf('.')
            if (dot < 0) break
            host = host.substring(dot + 1)
        }
        return blockHit
    }

    companion object {
        val EMPTY = DomainMatcher(emptySet(), emptySet())
    }
}

/**
 * The filter lists the VPN answers with: every list rule in [matcher], and the subset from the
 * scam lists in [scam], so a blocked scam site can be reported instead of just failing to load.
 */
data class Rules(val matcher: DomainMatcher, val scam: DomainMatcher) {
    /** Whether the scam lists name `domain`. Only ask about lookups that were blocked. */
    fun isScam(domain: String) = scam.isBlocked(domain) && matcher.isBlocked(domain)

    companion object {
        val EMPTY = Rules(DomainMatcher.EMPTY, DomainMatcher.EMPTY)
    }
}
