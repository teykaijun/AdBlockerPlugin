package com.teykaijun.adblocker.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.painter.Painter
import androidx.compose.ui.graphics.vector.rememberVectorPainter
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.teykaijun.adblocker.R
import com.teykaijun.adblocker.app
import com.teykaijun.adblocker.vpn.VpnController
import com.teykaijun.adblocker.vpn.VpnState

private enum class Tab(val label: String) {
    Home("Home"),
    Activity("Activity"),
    Filters("Filters"),
    Apps("Apps"),
    Settings("Settings"),
}

@Composable
private fun Tab.icon(): Painter = when (this) {
    Tab.Home -> painterResource(R.drawable.ic_notification)
    Tab.Activity -> painterResource(R.drawable.ic_tab_activity)
    Tab.Filters -> painterResource(R.drawable.ic_tab_filters)
    Tab.Apps -> painterResource(R.drawable.ic_tab_apps)
    Tab.Settings -> rememberVectorPainter(Icons.Filled.Settings)
}

@Composable
fun AdBlockerUi(onStartProtection: () -> Unit, onStopProtection: () -> Unit) {
    val app = LocalContext.current.app
    val vpnState by VpnController.state.collectAsStateWithLifecycle()
    val settings by app.settings.settings.collectAsStateWithLifecycle()
    val totals by app.stats.totals.collectAsStateWithLifecycle()
    val recent by app.stats.recent.collectAsStateWithLifecycle()
    val network by app.network.snapshot.collectAsStateWithLifecycle()
    val listStatus by app.filters.status.collectAsStateWithLifecycle()
    var tab by rememberSaveable { mutableStateOf(Tab.Home) }

    // Counters are otherwise only published while the VPN runs.
    LaunchedEffect(Unit) { app.stats.publish() }

    Scaffold(
        bottomBar = {
            NavigationBar {
                Tab.entries.forEach { item ->
                    NavigationBarItem(
                        selected = tab == item,
                        onClick = { tab = item },
                        icon = { Icon(item.icon(), contentDescription = null) },
                        label = { Text(item.label) },
                    )
                }
            }
        },
    ) { padding ->
        // Each screen's top bar handles the status bar; only keep room for the bottom bar here.
        Box(Modifier.fillMaxSize().padding(bottom = padding.calculateBottomPadding())) {
            when (tab) {
                Tab.Home -> HomeScreen(
                    state = vpnState,
                    totals = totals,
                    privateDnsServer = network.privateDnsServer,
                    onToggle = { if (VpnController.isActive) onStopProtection() else onStartProtection() },
                    onOpenActivity = { tab = Tab.Activity },
                )
                Tab.Activity -> ActivityScreen(
                    events = recent,
                    settings = settings,
                    running = vpnState is VpnState.Running,
                    onRule = { domain, allow ->
                        app.settings.update { if (allow == null) it.withoutDomainRule(domain) else it.withDomainRule(domain, allow) }
                    },
                    onClear = app.stats::clearRecent,
                )
                Tab.Filters -> FiltersScreen(
                    builtIn = remember { app.filters.builtIn },
                    settings = settings,
                    status = listStatus,
                    onUpdateSettings = app.settings::update,
                    onRefreshList = app.filters::refresh,
                    onDeleteList = app.filters::delete,
                )
                Tab.Apps -> AppsScreen(
                    bypassApps = settings.bypassApps,
                    onToggle = { pkg, bypass ->
                        app.settings.update { it.copy(bypassApps = if (bypass) it.bypassApps + pkg else it.bypassApps - pkg) }
                    },
                )
                Tab.Settings -> SettingsScreen(
                    settings = settings,
                    totals = totals,
                    onUpdateSettings = app.settings::update,
                    onResetStats = app.stats::reset,
                )
            }
        }
    }
}
