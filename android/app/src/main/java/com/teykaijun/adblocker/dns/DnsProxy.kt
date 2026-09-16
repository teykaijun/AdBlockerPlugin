package com.teykaijun.adblocker.dns

import android.net.Network
import android.os.ParcelFileDescriptor
import android.os.SystemClock
import android.system.ErrnoException
import android.system.Os
import android.system.OsConstants
import android.system.StructPollfd
import android.util.Log
import java.io.FileDescriptor
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress

/**
 * Serves DNS for the VPN interface. Blocked names are answered with NXDOMAIN
 * immediately; everything else is relayed to the upstream resolver over a
 * socket that bypasses the VPN, and the answer is written back to the tunnel.
 *
 * [run] is a single-threaded event loop: [Os.poll] waits on the tunnel, every
 * in-flight upstream socket and a pipe that [stop] writes to.
 */
class DnsProxy(
    private val tunnel: FileDescriptor,
    private val protectSocket: (DatagramSocket) -> Boolean,
    private val onQuery: (domain: String, blocked: Boolean) -> Unit,
) {
    @Volatile var matcher: DomainMatcher = DomainMatcher.EMPTY

    @Volatile var upstreamServers: List<InetAddress> = emptyList()

    /** The underlying network upstream sockets are bound to, when known. */
    @Volatile var network: Network? = null

    @Volatile private var running = true

    private val wakeUp: Array<FileDescriptor> = Os.pipe()
    private val pending = ArrayDeque<PendingQuery>()
    private val writeQueue = ArrayDeque<ByteArray>()
    private val readBuffer = ByteArray(MAX_PACKET)
    private val answerBuffer = ByteArray(MAX_PACKET)
    private var serverIndex = 0

    private class PendingQuery(
        val socket: DatagramSocket,
        val descriptor: ParcelFileDescriptor,
        val request: Packets.Datagram,
        val server: InetAddress,
        val deadline: Long,
    ) {
        fun close() {
            runCatching { descriptor.close() }
            socket.close()
        }
    }

    /** Asks [run] to return. Safe to call from any thread. */
    fun stop() {
        running = false
        runCatching { Os.write(wakeUp[1], byteArrayOf(0), 0, 1) }
    }

    /** Serves queries until [stop] is called; throws if the tunnel fails. */
    fun run() {
        try {
            while (running) {
                val tunnelPoll = pollFor(tunnel, if (writeQueue.isEmpty()) POLLIN else POLLIN or POLLOUT)
                val wakePoll = pollFor(wakeUp[0], POLLIN)
                val socketPolls = pending.map { pollFor(it.descriptor.fileDescriptor, POLLIN) }
                val timeout = pending.firstOrNull()?.let { (it.deadline - now()).coerceIn(0, 1_000).toInt() } ?: -1

                poll(arrayOf(tunnelPoll, wakePoll, *socketPolls.toTypedArray()), timeout)
                if (!running) break

                val tunnelEvents = tunnelPoll.revents.toInt()
                if (tunnelEvents and (POLLERR or POLLHUP or POLLNVAL) != 0) {
                    throw IOException("The VPN interface was closed")
                }

                // `socketPolls[i]` belongs to `pending[i]`; collect the ready
                // queries first because handling them changes `pending`.
                val ready = pending.indices
                    .filter { socketPolls[it].revents.toInt() != 0 }
                    .map { pending[it] to socketPolls[it].revents.toInt() }
                for ((query, events) in ready) {
                    pending.remove(query)
                    if (events and POLLIN != 0) relayAnswer(query) else query.close()
                }
                expireQueries()

                if (tunnelEvents and POLLOUT != 0) flushWrites()
                if (tunnelEvents and POLLIN != 0) readTunnel()
            }
        } finally {
            pending.forEach { it.close() }
            pending.clear()
            runCatching { Os.close(wakeUp[0]) }
            runCatching { Os.close(wakeUp[1]) }
        }
    }

    private fun readTunnel() {
        repeat(MAX_READS_PER_WAKEUP) {
            val length = try {
                Os.read(tunnel, readBuffer, 0, readBuffer.size)
            } catch (e: ErrnoException) {
                if (e.errno == OsConstants.EAGAIN || e.errno == OsConstants.EINTR) return
                throw IOException("Cannot read from the VPN interface", e)
            }
            if (length <= 0) return
            handlePacket(length)
        }
    }

    private fun handlePacket(length: Int) {
        val request = Packets.parseIpv4Udp(readBuffer, length) ?: return
        if (request.destinationPort != DNS_PORT) return
        val question = DnsMessage.parseQuery(request.payload) ?: return
        val blocked = matcher.isBlocked(question.name)
        onQuery(question.name, blocked)
        if (blocked) {
            reply(request, DnsMessage.blockedResponse(request.payload, question))
        } else {
            forward(request)
        }
    }

    private fun forward(request: Packets.Datagram) {
        val servers = upstreamServers
        if (servers.isEmpty()) return
        val server = servers[serverIndex.mod(servers.size)]

        val socket = try {
            DatagramSocket()
        } catch (e: IOException) {
            Log.w(TAG, "Cannot open an upstream socket", e)
            return
        }
        try {
            protectSocket(socket)
            network?.let { net -> runCatching { net.bindSocket(socket) } }
            // Poll says when the answer is there; the timeout only guards
            // against a readiness notification without data.
            socket.soTimeout = 100
            socket.send(DatagramPacket(request.payload, request.payload.size, InetSocketAddress(server, DNS_PORT)))
            val descriptor = ParcelFileDescriptor.fromDatagramSocket(socket)
                ?: throw IOException("Upstream socket has no file descriptor")
            pending.addLast(PendingQuery(socket, descriptor, request, server, now() + QUERY_TIMEOUT_MS))
        } catch (e: IOException) {
            socket.close()
            serverIndex++
            return
        }
        while (pending.size > MAX_PENDING) pending.removeFirst().close()
    }

    private fun relayAnswer(query: PendingQuery) {
        try {
            val packet = DatagramPacket(answerBuffer, answerBuffer.size)
            query.socket.receive(packet)
            val answer = answerBuffer.copyOf(packet.length)
            val fromServer = packet.address == query.server && packet.port == DNS_PORT
            val sameId = answer.size >= 2 && readU16(answer, 0) == readU16(query.request.payload, 0)
            if (fromServer && sameId) reply(query.request, answer)
        } catch (e: IOException) {
            // Same as a timeout: the client will ask again.
        } finally {
            query.close()
        }
    }

    private fun expireQueries() {
        val now = now()
        while (pending.isNotEmpty() && pending.first().deadline <= now) {
            pending.removeFirst().close()
            serverIndex++ // try the next upstream server from now on
        }
    }

    private fun reply(request: Packets.Datagram, payload: ByteArray) {
        val packet = Packets.buildIpv4Udp(
            source = request.destinationAddress,
            sourcePort = request.destinationPort,
            destination = request.sourceAddress,
            destinationPort = request.sourcePort,
            payload = payload,
        ) ?: return
        writeQueue.addLast(packet)
        while (writeQueue.size > MAX_QUEUED_WRITES) writeQueue.removeFirst()
        flushWrites()
    }

    private fun flushWrites() {
        while (writeQueue.isNotEmpty()) {
            val packet = writeQueue.first()
            try {
                Os.write(tunnel, packet, 0, packet.size)
            } catch (e: ErrnoException) {
                if (e.errno == OsConstants.EAGAIN || e.errno == OsConstants.EINTR) return // wait for POLLOUT
                Log.w(TAG, "Dropping an answer the VPN interface refused", e)
            } catch (e: IOException) {
                Log.w(TAG, "Dropping an answer the VPN interface refused", e)
            }
            writeQueue.removeFirst()
        }
    }

    private fun poll(polls: Array<StructPollfd>, timeout: Int) {
        try {
            Os.poll(polls, timeout)
        } catch (e: ErrnoException) {
            if (e.errno != OsConstants.EINTR) throw IOException("poll() failed", e)
        }
    }

    private fun pollFor(descriptor: FileDescriptor, events: Int) = StructPollfd().apply {
        fd = descriptor
        this.events = events.toShort()
    }

    private fun now() = SystemClock.elapsedRealtime()

    private companion object {
        const val TAG = "DnsProxy"
        const val DNS_PORT = 53
        const val MAX_PACKET = 65_535
        const val MAX_PENDING = 512
        const val MAX_QUEUED_WRITES = 1_024
        const val MAX_READS_PER_WAKEUP = 64
        const val QUERY_TIMEOUT_MS = 10_000L

        val POLLIN = OsConstants.POLLIN
        val POLLOUT = OsConstants.POLLOUT
        val POLLERR = OsConstants.POLLERR
        val POLLHUP = OsConstants.POLLHUP
        val POLLNVAL = OsConstants.POLLNVAL
    }
}
