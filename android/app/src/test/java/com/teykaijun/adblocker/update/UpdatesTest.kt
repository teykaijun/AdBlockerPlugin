package com.teykaijun.adblocker.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class UpdatesTest {
    private fun release(
        tag: String = "v1.4.1",
        assets: String = """
            {"name": "AdBlocker.apk", "size": 1408045,
             "browser_download_url": "https://github.com/teykaijun/AdBlockerPlugin/releases/download/v1.4.1/AdBlocker.apk"},
            {"name": "SHA256SUMS.txt", "size": 250,
             "browser_download_url": "https://github.com/teykaijun/AdBlockerPlugin/releases/download/v1.4.1/SHA256SUMS.txt"}
        """,
        extra: String = "",
    ) = """
        {"tag_name": "$tag", "name": "AdBlocker 1.4.1", $extra
         "html_url": "https://github.com/teykaijun/AdBlockerPlugin/releases/tag/$tag",
         "assets": [$assets]}
    """.trimIndent()

    @Test
    fun `reads the version and the files of a release`() {
        val parsed = Updates.parseRelease(release())!!
        assertEquals("1.4.1", parsed.version)
        assertEquals("https://github.com/teykaijun/AdBlockerPlugin/releases/download/v1.4.1/AdBlocker.apk", parsed.apkUrl)
        assertEquals(1408045L, parsed.apkSize)
        assertTrue(parsed.sumsUrl!!.endsWith("SHA256SUMS.txt"))
        assertTrue(parsed.pageUrl.endsWith("/tag/v1.4.1"))
    }

    @Test
    fun `ignores releases it cannot install`() {
        assertNull("draft", Updates.parseRelease(release(extra = """"draft": true,""")))
        assertNull("prerelease", Updates.parseRelease(release(extra = """"prerelease": true,""")))
        assertNull("no apk", Updates.parseRelease(release(assets = """{"name": "adblocker.exe", "browser_download_url": "https://x/adblocker.exe"}""")))
        assertNull("no tag", Updates.parseRelease(release(tag = "")))
        assertNull(
            "not https",
            Updates.parseRelease(release(assets = """{"name": "AdBlocker.apk", "browser_download_url": "http://example.com/AdBlocker.apk"}""")),
        )
    }

    @Test
    fun `compares version numbers, not text`() {
        assertTrue(Updates.isNewer("1.4.1", "1.4.0"))
        assertTrue(Updates.isNewer("v1.10.0", "1.9.9"))
        assertTrue(Updates.isNewer("2.0", "1.9.9"))
        assertFalse(Updates.isNewer("1.4.0", "1.4.0"))
        assertFalse(Updates.isNewer("1.3.9", "1.4.0"))
        assertFalse(Updates.isNewer("1.4.0", "1.4.1"))
    }

    @Test
    fun `finds the checksum for the file it downloads`() {
        val hash = "dbd0c90244c4eff6ea973791f531231185d4e66a9e498965e0d636cda04f3cdd"
        val sums = """
            e12a704dcf3976e1070a458aab5816f3266d36e3d0e85eb095dccbacd22d6bde  adblocker.exe
            $hash  AdBlocker.apk
            f70ba4cbe4f5f152734cc4af1af89edd711129af9a1f3fa73d7e32e2a017af26  AdBlocker-Companion.zip
        """.trimIndent()
        assertEquals(hash, Updates.expectedHash(sums, "AdBlocker.apk"))
        // sha256sum writes a star in front of the name in binary mode.
        assertEquals(hash, Updates.expectedHash("$hash *AdBlocker.apk", "AdBlocker.apk"))
        assertNull(Updates.expectedHash(sums, "Missing.apk"))
        assertNull(Updates.expectedHash("not a checksum file", "AdBlocker.apk"))
    }
}
