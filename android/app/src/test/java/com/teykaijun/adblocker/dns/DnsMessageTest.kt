package com.teykaijun.adblocker.dns

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class DnsMessageTest {

    @Test
    fun `reads the question in lower case`() {
        val query = TestPackets.query("Ads.Example.COM", type = 28, id = 0xBEEF)
        val question = DnsMessage.parseQuery(query)!!
        assertEquals("ads.example.com", question.name)
        assertEquals(28, question.type)
        assertEquals(0xBEEF, question.id)
        // 12-byte header + name (1+3 + 1+7 + 1+3 + 1) + type + class
        assertEquals(12 + 17 + 4, question.end)
    }

    @Test
    fun `reads the root name as empty`() {
        assertEquals("", DnsMessage.parseQuery(TestPackets.query(""))!!.name)
    }

    @Test
    fun `ignores responses, other opcodes and malformed names`() {
        val query = TestPackets.query("example.com")

        val response = query.copyOf().also { writeU16(it, 2, 0x8180) }
        assertNull(DnsMessage.parseQuery(response))

        val notify = query.copyOf().also { writeU16(it, 2, 4 shl 11) }
        assertNull(DnsMessage.parseQuery(notify))

        val noQuestion = query.copyOf().also { writeU16(it, 4, 0) }
        assertNull(DnsMessage.parseQuery(noQuestion))

        val pointer = query.copyOf().also { it[12] = 0xC0.toByte() }
        assertNull(DnsMessage.parseQuery(pointer))

        val labelPastEnd = query.copyOf(20)
        assertNull(DnsMessage.parseQuery(labelPastEnd))

        assertNull(DnsMessage.parseQuery(ByteArray(11)))
    }

    @Test
    fun `builds an NXDOMAIN that echoes only the question`() {
        val query = TestPackets.query("tracker.example.net", id = 0x0A0B)
        val question = DnsMessage.parseQuery(query)!!
        val response = DnsMessage.blockedResponse(query, question)

        assertEquals(question.end, response.size) // EDNS record dropped
        assertEquals(0x0A0B, readU16(response, 0))
        val flags = readU16(response, 2)
        assertEquals("QR", 0x8000, flags and 0x8000)
        assertEquals("RD copied", 0x0100, flags and 0x0100)
        assertEquals("RA", 0x0080, flags and 0x0080)
        assertEquals("RCODE", DnsMessage.RCODE_NXDOMAIN, flags and 0x000F)
        assertEquals(1, readU16(response, 4))
        assertEquals(0, readU16(response, 6))
        assertEquals(0, readU16(response, 8))
        assertEquals(0, readU16(response, 10))
        assertArrayEquals(query.copyOfRange(12, question.end), response.copyOfRange(12, question.end))
    }

    @Test
    fun `leaves RD clear when the query did not set it`() {
        val query = TestPackets.query("example.com").also { writeU16(it, 2, 0) }
        val response = DnsMessage.blockedResponse(query, DnsMessage.parseQuery(query)!!)
        assertEquals(0, readU16(response, 2) and 0x0100)
    }
}
