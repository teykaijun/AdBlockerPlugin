package com.teykaijun.adblocker.dns

/** Helpers to build DNS queries by hand for the tests. */
object TestPackets {
    val CLIENT = byteArrayOf(192.toByte(), 0, 2, 1)
    val DNS_SERVER = byteArrayOf(192.toByte(), 0, 2, 2)

    /** A standard query for `name` with recursion desired and an EDNS OPT record. */
    fun query(name: String, type: Int = 1, id: Int = 0x1234, withEdns: Boolean = true): ByteArray {
        val out = mutableListOf<Byte>()
        fun u16(v: Int) {
            out += (v ushr 8).toByte()
            out += v.toByte()
        }
        u16(id)
        u16(0x0100) // RD
        u16(1) // QDCOUNT
        u16(0) // ANCOUNT
        u16(0) // NSCOUNT
        u16(if (withEdns) 1 else 0) // ARCOUNT
        for (label in name.split('.').filter { it.isNotEmpty() }) {
            out += label.length.toByte()
            label.forEach { out += it.code.toByte() }
        }
        out += 0
        u16(type)
        u16(1) // class IN
        if (withEdns) {
            out += 0 // root name
            u16(41) // OPT
            u16(1232) // UDP payload size
            u16(0)
            u16(0)
            u16(0) // RDLENGTH
        }
        return out.toByteArray()
    }

    fun udpQueryPacket(name: String, sourcePort: Int = 40_000): ByteArray =
        Packets.buildIpv4Udp(CLIENT, sourcePort, DNS_SERVER, 53, query(name))!!
}
