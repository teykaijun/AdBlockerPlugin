package com.teykaijun.adblocker.dns

/**
 * Picks the upstream DNS server to relay a lookup to. It stays with one server until a query
 * sent to that server fails, then moves on to the next. A failure only counts while its server
 * is still the current one, so a burst of timeouts from a dead server moves on once instead of
 * flipping back to it.
 */
class UpstreamChooser<T> {
    private var index = 0

    fun pick(servers: List<T>): T? = if (servers.isEmpty()) null else servers[index.mod(servers.size)]

    fun failed(server: T, servers: List<T>) {
        if (pick(servers) == server) index++
    }
}
