package com.teykaijun.adblocker.dns

/**
 * Decides whether a hostname is blocked. A rule for `example.com` also covers
 * every subdomain, and an allow rule anywhere up the chain wins over a block
 * rule, the same as `@@||example.com^` in Adblock Plus syntax.
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
