package com.teykaijun.adblocker.vpn

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import android.system.OsConstants
import android.util.Log
import androidx.core.app.ServiceCompat
import com.teykaijun.adblocker.R
import com.teykaijun.adblocker.app
import com.teykaijun.adblocker.data.DnsProvider
import com.teykaijun.adblocker.data.Settings
import com.teykaijun.adblocker.data.parseIpLiteral
import com.teykaijun.adblocker.dns.DnsProxy
import com.teykaijun.adblocker.dns.DomainMatcher
import java.io.IOException
import java.net.InetAddress
import java.net.NetworkInterface
import kotlin.concurrent.thread
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch

/**
 * A local, DNS-only VPN. The tunnel routes nothing but one fake DNS server
 * address, so regular traffic never passes through the app; only name
 * lookups do, and [DnsProxy] answers or relays them.
 */
class AdBlockVpnService : VpnService() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var session: Job? = null
    private var tunnel: ParcelFileDescriptor? = null
    private var proxy: DnsProxy? = null
    private var proxyThread: Thread? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            app.settings.update { it.copy(protectionOn = false) }
            shutDown(VpnState.Stopped)
            return START_NOT_STICKY
        }

        // Started by the app, by Android's always-on VPN, or restarted after
        // the process was killed (null intent).
        enterForeground()
        Notifications.cancelProblem(this)
        app.settings.update { it.copy(protectionOn = true) }
        if (session?.isActive != true) session = scope.launch { runSession() }
        return START_STICKY
    }

    override fun onRevoke() {
        app.settings.update { it.copy(protectionOn = false) }
        shutDown(VpnState.Failed("Protection was turned off in Android's VPN settings, or another VPN app took over."))
    }

    override fun onDestroy() {
        releaseTunnel()
        scope.cancel()
        if (VpnController.state.value !is VpnState.Failed) VpnController.setState(VpnState.Stopped)
        super.onDestroy()
    }

    private suspend fun runSession() {
        VpnController.setState(VpnState.Starting)
        if (prepare(this) != null) {
            app.settings.update { it.copy(protectionOn = false) }
            shutDown(VpnState.Failed("Open AdBlocker and turn protection on to allow its VPN connection."), notify = true)
            return
        }
        try {
            val settings = app.settings.settings.value
            establish(settings, app.filters.buildMatcher(settings))
            VpnController.setState(VpnState.Running(System.currentTimeMillis()))
            app.filters.refreshStale(settings)
            coroutineScope {
                launch { reloadRulesOnChange() }
                launch { reconnectWhenBypassAppsChange() }
                launch { followUpstreamServers() }
                launch { publishStats() }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            Log.e(TAG, "VPN session failed", e)
            shutDown(VpnState.Failed(e.message ?: "The VPN connection could not be started."), notify = true)
        }
    }

    /** Creates (or replaces) the tunnel and starts a DNS proxy on it. */
    private fun establish(settings: Settings, matcher: DomainMatcher) {
        val subnet = pickSubnet()
        val dnsServer = "$subnet.2"
        val builder = Builder()
            .setSession(getString(R.string.app_name))
            .addAddress("$subnet.1", 24)
            .addRoute(dnsServer, 32)
            .addDnsServer(dnsServer)
            .setBlocking(false)
            .setConfigureIntent(Notifications.openAppIntent(this))
            // IPv6 traffic is not routed into the tunnel; let it use the real network.
            .allowFamily(OsConstants.AF_INET6)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) builder.setMetered(false)

        // The app's own traffic (relayed queries, list downloads) must not loop back in.
        builder.addDisallowedApplication(packageName)
        for (pkg in settings.bypassApps) {
            try {
                builder.addDisallowedApplication(pkg)
            } catch (e: PackageManager.NameNotFoundException) {
                // The app was uninstalled; ignore it.
            }
        }

        val newTunnel = builder.establish()
            ?: throw IllegalStateException("Android did not allow the VPN connection.")
        releaseTunnel()
        tunnel = newTunnel

        val newProxy = DnsProxy(newTunnel.fileDescriptor, { socket -> protect(socket) }, app.stats::record)
        newProxy.matcher = matcher
        VpnController.matcher = matcher
        newProxy.network = app.network.snapshot.value.network
        newProxy.upstreamServers = upstreamServers(settings)
        proxy = newProxy
        proxyThread = thread(name = "dns-proxy") {
            try {
                newProxy.run()
            } catch (e: IOException) {
                Log.e(TAG, "DNS proxy stopped", e)
                scope.launch {
                    if (proxy === newProxy) shutDown(VpnState.Failed("The VPN connection was lost."), notify = true)
                }
            }
        }
    }

    private suspend fun reloadRulesOnChange() {
        combine(app.settings.settings.map { ruleInputs(it) }.distinctUntilChanged(), app.filters.revision) { inputs, revision ->
            inputs to revision
        }
            .distinctUntilChanged()
            .drop(1)
            .collectLatest {
                val matcher = app.filters.buildMatcher(app.settings.settings.value)
                proxy?.let {
                    it.matcher = matcher
                    VpnController.matcher = matcher
                }
            }
    }

    private suspend fun reconnectWhenBypassAppsChange() {
        app.settings.settings.map { it.bypassApps }.distinctUntilChanged().drop(1).collect {
            establish(app.settings.settings.value, proxy?.matcher ?: DomainMatcher.EMPTY)
        }
    }

    private suspend fun followUpstreamServers() {
        combine(app.settings.settings, app.network.snapshot) { settings, network -> settings to network }
            .collect { (settings, network) ->
                proxy?.let {
                    it.network = network.network
                    it.upstreamServers = upstreamServers(settings)
                }
            }
    }

    private suspend fun publishStats() {
        var seconds = 0
        while (true) {
            delay(1_000)
            app.stats.publish()
            if (++seconds % 10 == 0) {
                app.stats.persist()
                Notifications.updateProtection(this, app.stats.totals.value)
            }
        }
    }

    private fun ruleInputs(s: Settings): List<Any> =
        listOf(s.builtInLists, s.remoteLists.filter { it.enabled }.map { it.id }, s.blockedDomains, s.allowedDomains)

    private fun upstreamServers(settings: Settings): List<InetAddress> {
        val chosen = when (settings.dnsProvider) {
            DnsProvider.NETWORK -> app.network.snapshot.value.dnsServers
            DnsProvider.CUSTOM -> listOfNotNull(parseIpLiteral(settings.customDns))
            else -> settings.dnsProvider.addresses.mapNotNull(::parseIpLiteral)
        }.filterNot(::isOwnAddress)
        return chosen.ifEmpty { DnsProvider.CLOUDFLARE.addresses.mapNotNull(::parseIpLiteral) }
    }

    /** A documentation-only (TEST-NET) range no real network uses. */
    private fun pickSubnet(): String {
        val inUse = runCatching {
            NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
                .filterNot { it.name.startsWith("tun") }
                .flatMap { it.inetAddresses.toList() }
                .mapNotNull { it.hostAddress }
        }.getOrDefault(emptyList())
        return SUBNETS.firstOrNull { subnet -> inUse.none { it.startsWith("$subnet.") } } ?: SUBNETS.first()
    }

    private fun isOwnAddress(address: InetAddress) =
        SUBNETS.any { subnet -> address.hostAddress?.startsWith("$subnet.") == true }

    private fun enterForeground() {
        val type = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
        } else {
            0
        }
        try {
            ServiceCompat.startForeground(this, Notifications.PROTECTION_ID, Notifications.protection(this, app.stats.totals.value), type)
        } catch (e: RuntimeException) {
            // Android can refuse this for background starts; a connected VPN is kept alive anyway.
            Log.w(TAG, "Could not show the foreground notification", e)
        }
    }

    private fun releaseTunnel() {
        // establish() sets it again when it replaces the tunnel.
        VpnController.matcher = null
        proxy?.stop()
        proxy = null
        proxyThread?.join(1_000)
        proxyThread = null
        tunnel?.let { runCatching { it.close() } }
        tunnel = null
    }

    private fun shutDown(state: VpnState, notify: Boolean = false) {
        session?.cancel()
        session = null
        releaseTunnel()
        app.stats.persist()
        app.stats.publish()
        VpnController.setState(state)
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        if (notify && state is VpnState.Failed) Notifications.showProblem(this, state.message)
        stopSelf()
    }

    companion object {
        const val ACTION_START = "com.teykaijun.adblocker.action.START"
        const val ACTION_STOP = "com.teykaijun.adblocker.action.STOP"
        private const val TAG = "AdBlockVpnService"
        private val SUBNETS = listOf("192.0.2", "198.51.100", "203.0.113")

        fun intent(context: Context, action: String): Intent =
            Intent(context, AdBlockVpnService::class.java).setAction(action)
    }
}
