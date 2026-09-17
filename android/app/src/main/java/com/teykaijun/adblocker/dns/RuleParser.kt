package com.teykaijun.adblocker.dns

/**
 * Reads the domain rules a DNS blocker can use from the common list formats:
 *
 *  - hosts files:          `0.0.0.0 ads.example.com tracker.example.net`
 *  - plain domain lists:   `ads.example.com`
 *  - Adblock-style lists:  `||ads.example.com^`, `@@||cdn.example.com^`, `||x.com^$important`
 *  - the app's own assets: `@@cdn.example.com` for exceptions
 *
 * Anything that needs more than a hostname to decide (paths, `$third-party`,
 * element hiding, regular expressions) is skipped.
 */
object RuleParser {

    data class Rule(val domain: String, val allow: Boolean)

    private val WHITESPACE = Regex("""\s+""")
    private val COSMETIC = Regex("""#[@?%$]?#""")
    private val IPV4 = Regex("""^\d{1,3}(\.\d{1,3}){3}$""")

    /** Hostnames that hosts files map for the local machine, never ad servers. */
    private val IGNORED = setOf(
        "localhost", "localhost.localdomain", "local", "broadcasthost",
        "ip6-localhost", "ip6-loopback", "ip6-localnet", "ip6-mcastprefix",
        "ip6-allnodes", "ip6-allrouters", "ip6-allhosts",
    )

    fun parseLine(raw: String): List<Rule> {
        val trimmed = raw.trim()
        if (trimmed.isEmpty() || trimmed[0] == '!' || trimmed[0] == '[') return emptyList()
        if (COSMETIC.containsMatchIn(trimmed)) return emptyList()
        val line = trimmed.substringBefore('#').trim()
        if (line.isEmpty()) return emptyList()

        val parts = line.split(WHITESPACE)
        if (parts.size > 1) {
            if (!isAddress(parts[0])) return emptyList()
            return parts.drop(1).mapNotNull { host -> normalizeDomain(host)?.let { Rule(it, allow = false) } }
        }

        var token = parts[0]
        val allow = token.startsWith("@@")
        if (allow) token = token.substring(2)

        val dollar = token.indexOf('$')
        if (dollar >= 0) {
            // `$important` is the only option that still means something for DNS.
            if (token.substring(dollar + 1).split(',').any { it != "important" }) return emptyList()
            token = token.substring(0, dollar)
        }

        if (token.startsWith("||")) {
            token = token.substring(2).removeSuffix("|")
            if (!token.endsWith('^')) return emptyList()
            token = token.dropLast(1)
        } else if (token.startsWith('|') || token.endsWith('^')) {
            return emptyList()
        }
        token = token.removePrefix("*.")

        return listOfNotNull(normalizeDomain(token)?.let { Rule(it, allow) })
    }

    fun parseInto(lines: Sequence<String>, blocked: MutableSet<String>, allowed: MutableSet<String>) {
        for (line in lines) {
            for (rule in parseLine(line)) {
                if (rule.allow) allowed += rule.domain else blocked += rule.domain
            }
        }
    }

    /** Lower-cased hostname with at least two labels, or null if `input` is not one. */
    fun normalizeDomain(input: String): String? {
        val domain = input.trim().trimEnd('.').lowercase()
        if (domain in IGNORED || !isHostname(domain) || IPV4.matches(domain)) return null
        return domain
    }

    // Hand-written instead of a regex: community lists have hundreds of
    // thousands of lines and this runs for each of them.
    private fun isHostname(name: String): Boolean {
        if (name.isEmpty() || name.length > 253) return false
        var labels = 0
        var start = 0
        for (i in 0..name.length) {
            if (i == name.length || name[i] == '.') {
                val length = i - start
                if (length == 0 || length > 63 || name[start] == '-' || name[i - 1] == '-') return false
                labels++
                start = i + 1
            } else {
                val c = name[i]
                if (c !in 'a'..'z' && c !in '0'..'9' && c != '-' && c != '_') return false
            }
        }
        return labels >= 2
    }

    private fun isAddress(token: String) = token == "0" || IPV4.matches(token) || ':' in token
}
