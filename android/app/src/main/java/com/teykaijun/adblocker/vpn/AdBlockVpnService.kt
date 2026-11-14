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
import com.teykaijun.adblocker.dns.Rules
import java.io.IOException
import java.net.InetAddress
import java.net.NetworkInterface
import java.util.concurrent.TimeUnit
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
import kotlinx.coroutines.flow.dropWhile
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch

/**
 * A local, DNS-only VPN. The tunnel routes nothing but one fake DNS server
 * address, so regular traffic never passes through the app; only name
 * lookups do, and [DnsProxy] answers or relays them.
 */
class AdBlockVpnService : VpnService() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    /** Scam sites already warned about: domain → when the warning may be repeated. */
    private val warnedAbout = LinkedHashMap<String, Long>()
    private var session: Job? = null
    private var tunnel: ParcelFileDescriptor? = null
    private var proxy: DnsProxy? = null
    private var proxyThread: Thread? = null
    /** What the status notification shows now, so it is only posted again when that changes. */
    private var shownStatus: ProtectionStatus? = null

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
            val builtFrom = ruleInputs(settings) to app.filters.revision.value
            val rules = app.filters.buildRules(settings)
            // Reading the lists takes a while; start with whatever the user changed meanwhile.
            val current = app.settings.settings.value
            VpnController.userRules = current.userRules()
            establish(current, rules)
            VpnController.setState(running())
            coroutineScope {
                launch { reloadRulesOnChange(builtFrom) }
                launch { followUserRules() }
                launch { reconnectWhenBypassAppsChange(current.bypassApps) }
                launch { followUpstreamServers() }
                launch { refreshListsWhenDue() }
                launch { forgetPausesAndAllowancesWhenOver() }
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
    private fun establish(settings: Settings, rules: Rules) {
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

        val newProxy = DnsProxy(newTunnel.fileDescriptor, { socket -> protect(socket) }, ::onQuery)
        newProxy.matcher = rules.matcher
        newProxy.userRules = VpnController.userRules
        VpnController.rules = rules
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

    /**
     * Every lookup, straight from the DNS thread. Scam sites get a warning, because a blocked
     * shop or "free trial" otherwise just looks like a website that is down.
     */
    private fun onQuery(domain: String, blocked: Boolean) {
        val scam = blocked && VpnController.rules?.isScam(domain) == true
        app.stats.record(domain, blocked, scam)
        if (scam && shouldWarnAbout(domain)) Notifications.showScamWarning(this, domain)
    }

    /** At most one warning per site per hour, however many lookups a page makes. */
    private fun shouldWarnAbout(domain: String): Boolean = synchronized(warnedAbout) {
        val now = System.currentTimeMillis()
        warnedAbout.values.removeAll { it <= now }
        if (domain in warnedAbout) return false
        if (warnedAbout.size >= MAX_WARNED) warnedAbout.remove(warnedAbout.keys.first())
        warnedAbout[domain] = now + WARNING_REPEAT_MS
        true
    }

    /** @param builtFrom what the running rules were built from; a change made while they were built still counts. */
    private suspend fun reloadRulesOnChange(builtFrom: Pair<List<Any>, Int>) {
        combine(app.settings.settings.map { ruleInputs(it) }.distinctUntilChanged(), app.filters.revision) { inputs, revision ->
            inputs to revision
        }
            .distinctUntilChanged()
            .dropWhile { it == builtFrom }
            .collectLatest {
                val rules = app.filters.buildRules(app.settings.settings.value)
                proxy?.let {
                    it.matcher = rules.matcher
                    VpnController.rules = rules
                }
            }
    }

    /** The user's own rules, allowances and pause are small, so they are swapped in without rebuilding the lists. */
    private suspend fun followUserRules() {
        app.settings.settings.collect { settings ->
            val rules = settings.userRules()
            VpnController.userRules = rules
            proxy?.userRules = rules
            updateStatusNotification() // it says whether blocking is paused
        }
    }

    /**
     * A pause or an allowance ends on time by itself, because every lookup checks the clock. This
     * then tidies it out of the settings, so the notification and the screens switch back too.
     */
    private suspend fun forgetPausesAndAllowancesWhenOver() {
        app.settings.settings.map { it.nextExpiry() }.distinctUntilChanged().collectLatest { end ->
            if (end == null) return@collectLatest
            while (true) {
                val left = end - System.currentTimeMillis()
                if (left <= 0) break
                delay(left)
            }
            app.settings.update { it.withoutExpired() }
        }
    }

    /** @param establishedWith the apps the running tunnel lets bypass it. */
    private suspend fun reconnectWhenBypassAppsChange(establishedWith: Set<String>) {
        app.settings.settings.map { it.bypassApps }.distinctUntilChanged().dropWhile { it == establishedWith }.collect {
            establish(app.settings.settings.value, VpnController.rules ?: Rules.EMPTY)
        }
    }

    /**
     * Community lists are refreshed weekly. Look for stale ones every hour while protection is
     * on, and whenever the phone gets online, which also retries a download that failed because
     * the network wasn't up yet.
     */
    private suspend fun refreshListsWhenDue() {
        app.network.snapshot.map { it.network }.distinctUntilChanged().collectLatest { network ->
            if (network == null) return@collectLatest
            while (true) {
                app.filters.refreshStale(app.settings.settings.value)
                delay(LIST_CHECK_INTERVAL_MS)
            }
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
            seconds++
            if (seconds % 10 == 0) {
                updateStatusNotification()
                refreshRunningState()
            }
            // shutDown() saves them too.
            if (seconds % 60 == 0) app.stats.persist()
        }
    }

    private fun protectionStatus() = ProtectionStatus(
        blockedToday = app.stats.totals.value.blockedToday,
        pausedUntil = app.settings.settings.value.pausedUntil?.takeIf { it > System.currentTimeMillis() },
    )

    /** Posts the status notification again, but only when what it shows has changed. */
    private fun updateStatusNotification() {
        val status = protectionStatus()
        if (status == shownStatus) return
        shownStatus = status
        Notifications.updateProtection(this, status)
    }

    /** Running, and whether Android keeps the VPN on and blocks everything without it. */
    private fun running(since: Long = System.currentTimeMillis()): VpnState.Running {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return VpnState.Running(since)
        return VpnState.Running(since, alwaysOn = isAlwaysOn, lockdown = isLockdownEnabled)
    }

    /**
     * Android switches Always-on VPN and "Block connections without VPN" without restarting a VPN
     * that is already connected, so look again now and then; Home shows both.
     */
    private fun refreshRunningState() {
        val current = VpnController.state.value as? VpnState.Running ?: return
        val latest = running(current.since)
        if (latest != current) VpnController.setState(latest)
    }

    /** What the lists are built from. The user's own rules don't need a rebuild; see [followUserRules]. */
    private fun ruleInputs(s: Settings): List<Any> = listOf(s.builtInLists, s.remoteLists.filter { it.enabled }.map { it.id })

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
            val status = protectionStatus()
            ServiceCompat.startForeground(this, Notifications.PROTECTION_ID, Notifications.protection(this, status), type)
            shownStatus = status
        } catch (e: RuntimeException) {
            // Android can refuse this for background starts; a connected VPN is kept alive anyway.
            Log.w(TAG, "Could not show the foreground notification", e)
        }
    }

    private fun releaseTunnel() {
        // establish() sets it again when it replaces the tunnel.
        VpnController.rules = null
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
        // A pause only means something while protection is on, so turning it off ends one.
        app.settings.update { it.copy(pausedUntil = null) }
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
        private val WARNING_REPEAT_MS = TimeUnit.HOURS.toMillis(1)
        private const val MAX_WARNED = 256
        // A failed download waits an hour too (see isDue), so the next check tries it again. A check
        // only compares dates, and waiting doesn't wake a sleeping phone, so this costs nothing.
        private val LIST_CHECK_INTERVAL_MS = TimeUnit.HOURS.toMillis(1)

        fun intent(context: Context, action: String): Intent =
            Intent(context, AdBlockVpnService::class.java).setAction(action)
    }
}
