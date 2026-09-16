package com.teykaijun.adblocker.dns

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class PacketsTest {

    @Test
    fun `round-trips a UDP datagram`() {
        val payload = TestPackets.query("ads.example.com")
        val packet = Packets.buildIpv4Udp(TestPackets.CLIENT, 40_000, TestPackets.DNS_SERVER, 53, payload)!!

        val parsed = Packets.parseIpv4Udp(packet)!!
        assertArrayEquals(TestPackets.CLIENT, parsed.sourceAddress)
        assertArrayEquals(TestPackets.DNS_SERVER, parsed.destinationAddress)
        assertEquals(40_000, parsed.sourcePort)
        assertEquals(53, parsed.destinationPort)
        assertArrayEquals(payload, parsed.payload)
    }

    @Test
    fun `writes valid IPv4 and UDP checksums`() {
        val payload = byteArrayOf(1, 2, 3) // odd length exercises the padding byte
        val packet = Packets.buildIpv4Udp(TestPackets.DNS_SERVER, 53, TestPackets.CLIENT, 51_234, payload)!!

        // Summing a header that includes its own checksum yields zero.
        assertEquals(0, Packets.checksum(packet, 0, 20))
        val udpLength = packet.size - 20
        val pseudo = Packets.pseudoHeaderSum(TestPackets.DNS_SERVER, TestPackets.CLIENT, udpLength)
        assertEquals(0, Packets.checksum(packet, 20, udpLength, pseudo))
    }

    @Test
    fun `computes the RFC 1071 example checksum`() {
        val data = byteArrayOf(0x00, 0x01, 0xf2.toByte(), 0x03, 0xf4.toByte(), 0xf5.toByte(), 0xf6.toByte(), 0xf7.toByte())
        // The example's one's complement sum is 0xddf2, so the checksum is its complement.
        assertEquals(0xddf2.inv() and 0xFFFF, Packets.checksum(data, 0, data.size))
    }

    @Test
    fun `honours the length argument instead of the buffer size`() {
        val packet = TestPackets.udpQueryPacket("example.com")
        val buffer = packet.copyOf(4096)
        assertArrayEquals(TestPackets.query("example.com"), Packets.parseIpv4Udp(buffer, packet.size)!!.payload)
    }

    @Test
    fun `rejects packets it cannot handle`() {
        val packet = TestPackets.udpQueryPacket("example.com")

        assertNull("truncated", Packets.parseIpv4Udp(packet, 27))
        assertNull("shorter than total length", Packets.parseIpv4Udp(packet, packet.size - 1))

        val ipv6 = packet.copyOf().also { it[0] = 0x60 }
        assertNull("IPv6", Packets.parseIpv4Udp(ipv6))

        val tcp = packet.copyOf().also { it[9] = 6 }
        assertNull("TCP", Packets.parseIpv4Udp(tcp))

        val fragment = packet.copyOf().also { writeU16(it, 6, 0x2000) }
        assertNull("more fragments", Packets.parseIpv4Udp(fragment))

        val badUdpLength = packet.copyOf().also { writeU16(it, 24, packet.size) }
        assertNull("UDP length past the end", Packets.parseIpv4Udp(badUdpLength))
    }

    @Test
    fun `refuses payloads that do not fit in an IPv4 packet`() {
        assertNull(Packets.buildIpv4Udp(TestPackets.CLIENT, 1, TestPackets.DNS_SERVER, 2, ByteArray(65_508)))
        assertNotNull(Packets.buildIpv4Udp(TestPackets.CLIENT, 1, TestPackets.DNS_SERVER, 2, ByteArray(65_507)))
    }
}
