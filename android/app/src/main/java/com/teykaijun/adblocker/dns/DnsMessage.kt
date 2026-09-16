package com.teykaijun.adblocker.dns

/** Reads the question out of a DNS query and builds the "blocked" answer. */
object DnsMessage {
    private const val HEADER = 12
    private const val MAX_NAME_LENGTH = 253
    const val RCODE_NXDOMAIN = 3

    /**
     * @property name lower-cased query name without the trailing dot
     * @property end offset of the first byte after the question section
     */
    class Question(val id: Int, val name: String, val type: Int, val end: Int)

    /** Returns the first question of a standard query, or null for anything else. */
    fun parseQuery(message: ByteArray): Question? {
        if (message.size < HEADER) return null
        val flags = readU16(message, 2)
        if (flags and 0x8000 != 0) return null // a response, not a query
        if ((flags ushr 11) and 0x0F != 0) return null // not a standard query (opcode 0)
        if (readU16(message, 4) == 0) return null // no questions

        val name = StringBuilder()
        var pos = HEADER
        while (true) {
            if (pos >= message.size) return null
            val labelLength = message[pos].toInt() and 0xFF
            pos++
            if (labelLength == 0) break
            // Longer values are compression pointers, which never appear in
            // the first name of a well-formed query.
            if (labelLength > 63 || pos + labelLength > message.size) return null
            if (name.isNotEmpty()) name.append('.')
            for (i in pos until pos + labelLength) {
                name.append((message[i].toInt() and 0xFF).toChar().lowercaseChar())
            }
            pos += labelLength
            if (name.length > MAX_NAME_LENGTH) return null
        }
        if (pos + 4 > message.size) return null
        return Question(id = readU16(message, 0), name = name.toString(), type = readU16(message, pos), end = pos + 4)
    }

    /**
     * NXDOMAIN answer to [query]: the header and first question are echoed,
     * everything after them (other questions, EDNS options) is dropped.
     */
    fun blockedResponse(query: ByteArray, question: Question): ByteArray {
        val response = query.copyOf(question.end)
        val recursionDesired = readU16(query, 2) and 0x0100
        // QR (response) | RD (copied) | RA (recursion available) | RCODE
        writeU16(response, 2, 0x8000 or recursionDesired or 0x0080 or RCODE_NXDOMAIN)
        writeU16(response, 4, 1) // QDCOUNT
        writeU16(response, 6, 0) // ANCOUNT
        writeU16(response, 8, 0) // NSCOUNT
        writeU16(response, 10, 0) // ARCOUNT
        return response
    }
}
