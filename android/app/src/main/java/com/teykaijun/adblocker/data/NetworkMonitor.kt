package com.teykaijun.adblocker.data

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Build
import java.net.InetAddress
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Tracks this app's default network. The app excludes itself from its own
 * VPN, so this is always the real Wi-Fi or mobile network: the one whose DNS
 * servers queries are relayed to, and whose Private DNS setting matters.
 */
class NetworkMonitor(context: Context) {

    data class Snapshot(
        val network: Network? = null,
        val dnsServers: List<InetAddress> = emptyList(),
        /** Hostname of a Private DNS (DNS-over-TLS) server in strict mode, which bypasses the app. */
        val privateDnsServer: String? = null,
    )

    private val state = MutableStateFlow(Snapshot())
    val snapshot: StateFlow<Snapshot> = state.asStateFlow()

    private val connectivity = context.getSystemService(ConnectivityManager::class.java)
    private var isVpn = false

    init {
        connectivity.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
            override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) {
                isVpn = capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
            }

            override fun onLinkPropertiesChanged(network: Network, properties: LinkProperties) {
                // Never relay to a VPN's DNS server, which could be our own.
                if (isVpn) return
                val strictPrivateDns = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P && properties.isPrivateDnsActive) {
                    properties.privateDnsServerName
                } else {
                    null
                }
                state.value = Snapshot(network, properties.dnsServers.toList(), strictPrivateDns)
            }

            override fun onLost(network: Network) {
                if (state.value.network == network) state.value = Snapshot()
            }
        })
    }
}
