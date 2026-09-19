package com.teykaijun.adblocker.update

import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import org.json.JSONObject

/** A release on the project's GitHub page. */
data class Release(
    val version: String,
    val apkUrl: String,
    val apkSize: Long,
    val sumsUrl: String?,
    val pageUrl: String,
)

/**
 * Looks for a newer AdBlocker on the project's GitHub releases page and downloads it.
 *
 * Android only replaces an app with a build signed by the same key, and checks that itself,
 * so the worst a wrong download can do is fail to install. The SHA-256 from the release is
 * checked first anyway, to catch a truncated or corrupted file early.
 */
object Updates {
    const val LATEST_RELEASE_API = "https://api.github.com/repos/teykaijun/AdBlockerPlugin/releases/latest"
    const val APK_NAME = "AdBlocker.apk"
    const val SUMS_NAME = "SHA256SUMS.txt"
    private const val TIMEOUT_MS = 20_000
    private const val MAX_APK_BYTES = 100L * 1024 * 1024

    /** Reads GitHub's "latest release" answer. Null if it has no APK to install. */
    fun parseRelease(json: String): Release? {
        val release = JSONObject(json)
        if (release.optBoolean("draft") || release.optBoolean("prerelease")) return null
        val version = release.optString("tag_name").removePrefix("v").trim()
        if (version.isEmpty()) return null

        val assets = release.optJSONArray("assets") ?: return null
        var apk: JSONObject? = null
        var sums: JSONObject? = null
        for (i in 0 until assets.length()) {
            val asset = assets.getJSONObject(i)
            when (asset.optString("name")) {
                APK_NAME -> apk = asset
                SUMS_NAME -> sums = asset
            }
        }
        val apkUrl = apk?.optString("browser_download_url").orEmpty()
        if (!apkUrl.startsWith("https://")) return null
        return Release(
            version = version,
            apkUrl = apkUrl,
            apkSize = apk?.optLong("size") ?: 0,
            sumsUrl = sums?.optString("browser_download_url")?.takeIf { it.startsWith("https://") },
            pageUrl = release.optString("html_url"),
        )
    }

    /** Whether `latest` ("1.4.1" or "v1.4.1") is a higher version number than `current`. */
    fun isNewer(latest: String, current: String): Boolean {
        val a = parts(latest)
        val b = parts(current)
        for (i in 0 until maxOf(a.size, b.size)) {
            val left = a.getOrElse(i) { 0 }
            val right = b.getOrElse(i) { 0 }
            if (left != right) return left > right
        }
        return false
    }

    /** The SHA-256 recorded for `fileName` in a SHA256SUMS.txt, or null if it isn't listed. */
    fun expectedHash(sums: String, fileName: String): String? = sums.lineSequence()
        .mapNotNull { line ->
            val parts = line.trim().split(Regex("""\s+"""), limit = 2)
            if (parts.size == 2 && parts[1].removePrefix("*").trim() == fileName) parts[0].lowercase() else null
        }
        .firstOrNull { it.length == 64 }

    fun sha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buffer = ByteArray(64 * 1024)
            while (true) {
                val read = input.read(buffer)
                if (read < 0) break
                digest.update(buffer, 0, read)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    /** The latest release, or null if this build is already up to date. */
    fun check(currentVersion: String): Release? {
        val release = parseRelease(fetch(LATEST_RELEASE_API)) ?: return null
        return release.takeIf { isNewer(it.version, currentVersion) }
    }

    /**
     * Downloads the release's APK into `directory` and checks its SHA-256 against the release.
     * Throws [IOException] if anything does not add up.
     */
    fun download(release: Release, directory: File, onProgress: (Int) -> Unit): File {
        directory.mkdirs()
        directory.listFiles()?.forEach { it.delete() }
        val apk = File(directory, "AdBlocker-${release.version}.apk")
        val expected = release.sumsUrl?.let { expectedHash(fetch(it), APK_NAME) }
            ?: throw IOException("The release has no checksum to verify the download against.")

        open(release.apkUrl) { connection ->
            val total = if (release.apkSize > 0) release.apkSize else connection.contentLengthLong
            connection.inputStream.use { input ->
                apk.outputStream().use { output ->
                    val buffer = ByteArray(64 * 1024)
                    var written = 0L
                    var reported = -1
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        written += read
                        if (written > MAX_APK_BYTES) throw IOException("The download is unexpectedly large.")
                        output.write(buffer, 0, read)
                        val percent = if (total > 0) ((written * 100) / total).toInt().coerceIn(0, 100) else 0
                        if (percent != reported) {
                            reported = percent
                            onProgress(percent)
                        }
                    }
                }
            }
        }

        val actual = sha256(apk)
        if (!actual.equals(expected, ignoreCase = true)) {
            apk.delete()
            throw IOException("The download does not match the checksum published with the release.")
        }
        return apk
    }

    private fun parts(version: String) =
        version.trim().removePrefix("v").split('.', '-', '+').map { it.toIntOrNull() ?: 0 }

    private fun fetch(url: String): String {
        var text = ""
        open(url) { text = it.inputStream.bufferedReader().use { reader -> reader.readText() } }
        return text
    }

    private fun open(url: String, use: (HttpURLConnection) -> Unit) {
        val parsed = URL(url)
        if (parsed.protocol != "https") throw IOException("Only https:// addresses are allowed")
        val connection = parsed.openConnection() as HttpURLConnection
        try {
            connection.connectTimeout = TIMEOUT_MS
            connection.readTimeout = TIMEOUT_MS
            connection.setRequestProperty("Accept", "application/vnd.github+json")
            connection.setRequestProperty("User-Agent", "AdBlocker-Android")
            if (connection.responseCode !in 200..299) throw IOException("GitHub answered HTTP ${connection.responseCode}")
            use(connection)
        } finally {
            connection.disconnect()
        }
    }
}
