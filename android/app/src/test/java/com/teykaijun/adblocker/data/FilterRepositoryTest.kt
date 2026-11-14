package com.teykaijun.adblocker.data

import java.io.IOException
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.util.concurrent.TimeUnit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class FilterRepositoryTest {
    @Test
    fun `counts the different domains a downloaded list blocks`() {
        val list = sequenceOf(
            "! A comment",
            "0.0.0.0 ads.example.com tracker.example.net",
            "||ads.example.com^",
            "@@||cdn.example.com^",
            "pixel.example.org",
        )
        assertEquals(3, countDomains(list))
        assertEquals(0, countDomains(sequenceOf("# nothing here", "")))
    }

    @Test
    fun `refuses a list too big for a phone before it runs out of memory`() {
        val huge = generateSequence(1) { it + 1 }.map { "ads$it.example.com" }
        val error = assertThrows(IOException::class.java) { countDomains(huge, limit = 1_000) }
        assertEquals("This list has more domains than a phone can hold.", error.message)
        assertEquals(1_000, countDomains(huge.take(1_000), limit = 1_000))
    }

    private val now = 1_800_000_000_000L
    private val minute = TimeUnit.MINUTES.toMillis(1)
    private val day = TimeUnit.DAYS.toMillis(1)

    @Test
    fun `downloads lists that are missing or over a week old`() {
        assertTrue("never downloaded", isDue(null, null, now))
        assertTrue("8 days old", isDue(ListStatus(updatedAt = now - 8 * day, domains = 10), null, now))
        assertFalse("1 day old", isDue(ListStatus(updatedAt = now - day, domains = 10), null, now))
        assertFalse("tried a minute ago and worked", isDue(ListStatus(updatedAt = now - minute, domains = 10), now - minute, now))
    }

    @Test
    fun `waits an hour after a failed download`() {
        // A failed try keeps the old copy and its date.
        val stale = ListStatus(updatedAt = now - 8 * day, domains = 10, error = "Server answered HTTP 503")
        assertFalse("tried 59 minutes ago", isDue(stale, now - 59 * minute, now))
        assertTrue("tried 61 minutes ago", isDue(stale, now - 61 * minute, now))
        // The hourly check that started a failed try tries it again at the next one.
        assertTrue("tried an hour ago", isDue(stale, now - 60 * minute, now))

        val neverDownloaded = ListStatus(error = "Unable to resolve host \"example.com\"")
        assertFalse("first try failed 5 minutes ago", isDue(neverDownloaded, now - 5 * minute, now))
        assertTrue("first try failed 2 hours ago", isDue(neverDownloaded, now - 120 * minute, now))
    }

    @Test
    fun `waits for a connection, an hour or a week depending on why a download failed`() {
        // No connection yet: try again as soon as the phone gets online.
        assertEquals(Retry.WhenOnline, retryAfter(UnknownHostException("Unable to resolve host")))
        assertEquals(Retry.WhenOnline, retryAfter(ConnectException("Failed to connect")))
        assertEquals(Retry.WhenOnline, retryAfter(SocketTimeoutException("timeout")))
        // The server said no: an hour.
        assertEquals(Retry.InAnHour, retryAfter(IOException("Server answered HTTP 404")))
        // The phone can't use the list: downloading it again every hour would only waste data.
        val tooBig = assertThrows(IOException::class.java) {
            countDomains(generateSequence(1) { it + 1 }.map { "ads$it.example.com" }, limit = 10)
        }
        assertEquals(Retry.InAWeek, retryAfter(tooBig))
        assertEquals(Retry.InAWeek, retryAfter(OutOfMemoryError()))
    }
}
