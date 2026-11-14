package com.teykaijun.adblocker.dns

/**
 * The user's own rules, which win over the filter lists: domains they always block or always
 * allow, domains allowed for a while ("Allow for 10 minutes"), and a pause. They are small, so
 * the VPN swaps in a new set whenever they change instead of rebuilding the lists, and the times
 * are checked on every lookup, so an allowance or a pause runs out by itself.
 *
 * AdBlocker for Windows decides the same way.
 */
class UserRules(
    blocked: Set<String> = emptySet(),
    allowed: Set<String> = emptySet(),
    /** Domain → when its allowance runs out, in epoch milliseconds. */
    allowedUntil: Map<String, Long> = emptyMap(),
    /** When the pause ends, in epoch milliseconds; null or past means not paused. */
    private val pausedUntil: Long? = null,
) {
    /**
     * The rule of the user's that decides a name: the [domain] it was made for (the name itself
     * or a parent), whether it [allows] the name, and [until] when for an allowance that runs out.
     */
    data class Match(val domain: String, val allows: Boolean, val until: Long? = null)

    private val blocked = blocked.mapTo(HashSet(), ::normalize)
    private val allowed = allowed.mapTo(HashSet(), ::normalize)
    private val allowedUntil = allowedUntil.mapKeys { normalize(it.key) }

    fun isPaused(now: Long): Boolean = pausedUntil != null && pausedUntil > now

    /**
     * Whether `domain` is blocked at `now`, with [lists] holding the filter lists:
     *  1. While paused, nothing is.
     *  2. Otherwise the user's rule for the most specific level of the name that has one decides
     *     (see [match]).
     *  3. Without one, the lists decide.
     */
    fun isBlocked(domain: String, lists: DomainMatcher, now: Long): Boolean {
        if (isPaused(now)) return false
        val match = match(domain, now) ?: return lists.isBlocked(domain)
        return !match.allows
    }

    /**
     * The user's rule for the most specific of `domain` and its parents that has one, or null.
     * On the same level, allowing (always, or for a while that hasn't run out) wins over blocking.
     */
    fun match(domain: String, now: Long): Match? {
        if (blocked.isEmpty() && allowed.isEmpty() && allowedUntil.isEmpty()) return null
        var host = normalize(domain)
        while (host.isNotEmpty()) {
            if (host in allowed) return Match(host, allows = true)
            val until = allowedUntil[host]
            if (until != null && until > now) return Match(host, allows = true, until = until)
            if (host in blocked) return Match(host, allows = false)
            val dot = host.indexOf('.')
            if (dot < 0) break
            host = host.substring(dot + 1)
        }
        return null
    }

    /**
     * Until when allowances for a while let `domain` through, following on to a parent's
     * allowance that lasts longer once the name's own runs out. Null when no allowance decides
     * it now, or when an "always allow" takes over afterwards.
     */
    fun allowedUntil(domain: String, now: Long): Long? {
        var time = now
        var until: Long? = null
        while (true) {
            val match = match(domain, time)
            if (match == null || !match.allows) return until
            // Each step moves past an allowance that is still running, so this ends.
            time = match.until ?: return null
            until = time
        }
    }

    companion object {
        val NONE = UserRules()

        /** Names are compared in lower case and without a trailing dot, like DNS does. */
        private fun normalize(name: String) = name.trim().trimEnd('.').lowercase()
    }
}
