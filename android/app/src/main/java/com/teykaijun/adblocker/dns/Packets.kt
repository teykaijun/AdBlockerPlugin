package com.teykaijun.adblocker.dns

/**
 * Just enough IPv4 + UDP to answer DNS queries arriving on the VPN interface.
 * The tunnel only routes the fake DNS server address, so nothing else ever
 * reaches this code.
 */
object Packets {
    private const val IPV4_HEADER = 20
    private const val UDP_HEADER = 8
    private const val PROTOCOL_UDP = 17
    private const val MAX_IPV4_PACKET = 65_535

    class Datagram(
        val sourceAddress: ByteArray,
        val sourcePort: Int,
        val destinationAddress: ByteArray,
        val destinationPort: Int,
        val payload: ByteArray,
    )

    /** Returns null for anything that is not a complete, unfragmented IPv4 UDP datagram. */
    fun parseIpv4Udp(packet: ByteArray, length: Int = packet.size): Datagram? {
        if (length < IPV4_HEADER + UDP_HEADER || length > packet.size) return null
        val versionAndLength = packet[0].toInt() and 0xFF
        if (versionAndLength ushr 4 != 4) return null
        val headerLength = (versionAndLength and 0x0F) * 4
        val totalLength = readU16(packet, 2)
        if (headerLength < IPV4_HEADER || totalLength < headerLength + UDP_HEADER || totalLength > length) return null
        if (readU16(packet, 6) and 0x3FFF != 0) return null // "more fragments" flag or a fragment offset
        if (packet[9].toInt() and 0xFF != PROTOCOL_UDP) return null
        val udpLength = readU16(packet, headerLength + 4)
        if (udpLength < UDP_HEADER || headerLength + udpLength > totalLength) return null
        return Datagram(
            sourceAddress = packet.copyOfRange(12, 16),
            sourcePort = readU16(packet, headerLength),
            destinationAddress = packet.copyOfRange(16, 20),
            destinationPort = readU16(packet, headerLength + 2),
            payload = packet.copyOfRange(headerLength + UDP_HEADER, headerLength + udpLength),
        )
    }

    /** Builds an IPv4/UDP packet with valid IP and UDP checksums, or null if `payload` is too big. */
    fun buildIpv4Udp(
        source: ByteArray,
        sourcePort: Int,
        destination: ByteArray,
        destinationPort: Int,
        payload: ByteArray,
    ): ByteArray? {
        require(source.size == 4 && destination.size == 4) { "IPv4 addresses must be 4 bytes" }
        val udpLength = UDP_HEADER + payload.size
        if (IPV4_HEADER + udpLength > MAX_IPV4_PACKET) return null

        val packet = ByteArray(IPV4_HEADER + udpLength)
        packet[0] = 0x45 // version 4, 20-byte header
        writeU16(packet, 2, packet.size)
        writeU16(packet, 6, 0x4000) // don't fragment
        packet[8] = 64 // TTL
        packet[9] = PROTOCOL_UDP.toByte()
        source.copyInto(packet, 12)
        destination.copyInto(packet, 16)
        writeU16(packet, 10, checksum(packet, 0, IPV4_HEADER))

        writeU16(packet, 20, sourcePort)
        writeU16(packet, 22, destinationPort)
        writeU16(packet, 24, udpLength)
        payload.copyInto(packet, IPV4_HEADER + UDP_HEADER)
        val udpChecksum = checksum(packet, IPV4_HEADER, udpLength, pseudoHeaderSum(source, destination, udpLength))
        // A computed zero is sent as all ones; zero means "no checksum" in UDP.
        writeU16(packet, 26, if (udpChecksum == 0) 0xFFFF else udpChecksum)
        return packet
    }

    /** Sum of the IPv4 pseudo-header that the UDP checksum covers. */
    fun pseudoHeaderSum(source: ByteArray, destination: ByteArray, udpLength: Int): Long =
        wordSum(source) + wordSum(destination) + PROTOCOL_UDP + udpLength

    /** Internet checksum (RFC 1071) of `length` bytes, starting from an `initial` sum. */
    fun checksum(data: ByteArray, offset: Int, length: Int, initial: Long = 0): Int {
        var sum = initial
        var i = offset
        val end = offset + length
        while (i + 1 < end) {
            sum += readU16(data, i)
            i += 2
        }
        if (i < end) sum += (data[i].toInt() and 0xFF) shl 8
        while (sum ushr 16 != 0L) sum = (sum and 0xFFFF) + (sum ushr 16)
        return sum.inv().toInt() and 0xFFFF
    }

    private fun wordSum(address: ByteArray): Long = readU16(address, 0).toLong() + readU16(address, 2)
}

internal fun readU16(data: ByteArray, offset: Int): Int =
    ((data[offset].toInt() and 0xFF) shl 8) or (data[offset + 1].toInt() and 0xFF)

internal fun writeU16(data: ByteArray, offset: Int, value: Int) {
    data[offset] = (value ushr 8).toByte()
    data[offset + 1] = value.toByte()
}
