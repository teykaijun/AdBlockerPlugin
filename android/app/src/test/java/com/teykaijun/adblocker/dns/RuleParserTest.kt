package com.teykaijun.adblocker.dns

import com.teykaijun.adblocker.dns.RuleParser.Rule
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class RuleParserTest {
    private fun parse(line: String) = RuleParser.parseLine(line)

    private fun block(domain: String) = listOf(Rule(domain, allow = false))

    private fun allow(domain: String) = listOf(Rule(domain, allow = true))

    @Test
    fun `reads hosts files`() {
        assertEquals(block("ads.example.com"), parse("0.0.0.0 ads.example.com"))
        assertEquals(block("ads.example.com"), parse("127.0.0.1\tAds.Example.com # comment"))
        assertEquals(block("ads.example.com"), parse(":: ads.example.com"))
        assertEquals(
            listOf(Rule("a.example.com", false), Rule("b.example.com", false)),
            parse("0.0.0.0 a.example.com b.example.com"),
        )
        assertEquals(emptyList<Rule>(), parse("127.0.0.1 localhost"))
        assertEquals(emptyList<Rule>(), parse("::1 ip6-localhost ip6-loopback"))
        assertEquals(emptyList<Rule>(), parse("255.255.255.255 broadcasthost"))
        assertEquals(emptyList<Rule>(), parse("0.0.0.0 0.0.0.0"))
        assertEquals(emptyList<Rule>(), parse("example.com ads.example.com"))
    }

    @Test
    fun `reads plain domains and the app's own asset format`() {
        assertEquals(block("tracker.example.net"), parse("tracker.example.net"))
        assertEquals(block("tracker.example.net"), parse("  tracker.example.net.  "))
        assertEquals(allow("cdn.example.net"), parse("@@cdn.example.net"))
        assertEquals(block("sub.example.org"), parse("*.sub.example.org"))
    }

    @Test
    fun `reads Adblock-style domain rules`() {
        assertEquals(block("ads.example.com"), parse("||ads.example.com^"))
        assertEquals(allow("ads.example.com"), parse("@@||ads.example.com^"))
        assertEquals(allow("ads.example.com"), parse("@@||ads.example.com^|"))
        assertEquals(block("ads.example.com"), parse("||ads.example.com^\$important"))
        assertEquals(block("ads.example.com"), parse("||*.ads.example.com^"))
    }

    @Test
    fun `skips rules DNS cannot honour`() {
        val skipped = listOf(
            "||ads.example.com^\$third-party",
            "||ads.example.com^\$important,dnstype=AAAA",
            "||example.com/ads/",
            "||ads.example",
            "||ads.example.com",
            "|https://ads.example.com^",
            "/banner\\d+/",
            "ads.example.com^",
            "example.com##.ad-banner",
            "example.com#@#.ad-banner",
            "example.com#?#div:has-text(Ad)",
            "##.ad",
            "! comment",
            "# comment",
            "[Adblock Plus 2.0]",
            "",
            "com",
            "localhost",
            "192.168.1.1",
            "exa mple.com",
            "-bad.example.com",
            "||example.*^",
        )
        for (line in skipped) assertEquals("\"$line\" should be skipped", emptyList<Rule>(), parse(line))
    }

    @Test
    fun `normalizes domains`() {
        assertEquals("example.com", RuleParser.normalizeDomain("Example.COM."))
        assertEquals("_dmarc.example.com", RuleParser.normalizeDomain("_dmarc.example.com"))
        assertEquals("xn--bcher-kva.example", RuleParser.normalizeDomain("xn--bcher-kva.example"))
        assertNull(RuleParser.normalizeDomain("bücher.example"))
        assertNull(RuleParser.normalizeDomain("a".repeat(64) + ".com"))
        assertNull(RuleParser.normalizeDomain("1.2.3.4"))
    }

    @Test
    fun `collects rules into block and allow sets`() {
        val blocked = mutableSetOf<String>()
        val allowed = mutableSetOf<String>()
        RuleParser.parseInto(
            sequenceOf("# list", "||a.com^", "0.0.0.0 b.com", "@@||c.com^", "a.com", "||d.com^\$3p"),
            blocked,
            allowed,
        )
        assertEquals(setOf("a.com", "b.com"), blocked)
        assertEquals(setOf("c.com"), allowed)
    }

    @Test
    fun `reads every shared list without errors or duplicates`() {
        // The app packages the repository's filters/ folder as its assets.
        val dir = File("../../filters")
        val lists = dir.listFiles { f -> f.extension == "txt" }.orEmpty()
        assertTrue("no lists found in ${dir.absolutePath}", lists.isNotEmpty())
        for (file in lists) {
            val lines = file.readLines()
            val rules = lines.filter { it.isNotBlank() && !it.startsWith("!") }
            for (rule in rules) {
                assertTrue("${file.name}: \"$rule\" should be ||domain^ or @@||domain^", rule.matches(Regex("""(@@)?\|\|[a-z0-9.-]+\^""")))
            }
            val blocked = mutableSetOf<String>()
            val allowed = mutableSetOf<String>()
            RuleParser.parseInto(lines.asSequence(), blocked, allowed)
            assertEquals("${file.name}: every rule should parse once", rules.size, blocked.size + allowed.size)
        }
    }

    @Test
    fun `describes every shared list in lists json`() {
        val dir = File("../../filters")
        val index = dir.resolve("lists.json").readText()
        val ids = Regex(""""id"\s*:\s*"([^"]+)"""").findAll(index).map { it.groupValues[1] }.toSet()
        val files = dir.listFiles { f -> f.extension == "txt" }.orEmpty().map { it.nameWithoutExtension }.toSet()
        assertEquals(files, ids)
    }
}
