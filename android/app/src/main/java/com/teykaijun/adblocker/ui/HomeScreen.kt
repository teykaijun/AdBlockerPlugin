package com.teykaijun.adblocker.ui

import android.provider.Settings
import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.teykaijun.adblocker.BuildConfig
import com.teykaijun.adblocker.R
import com.teykaijun.adblocker.data.PAUSE_MINUTES
import com.teykaijun.adblocker.data.Totals
import com.teykaijun.adblocker.ui.theme.ShieldBottom
import com.teykaijun.adblocker.ui.theme.ShieldTop
import com.teykaijun.adblocker.vpn.VpnState

/**
 * @param pausedUntil when the current pause ends, or null if blocking isn't paused.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(
    state: VpnState,
    pausedUntil: Long?,
    totals: Totals,
    privateDnsServer: String?,
    newVersion: String?,
    onToggle: () -> Unit,
    onPause: () -> Unit,
    onResume: () -> Unit,
    onOpenActivity: () -> Unit,
    onOpenSettings: () -> Unit,
) {
    val context = LocalContext.current
    val running = state as? VpnState.Running
    // A pause only means something while protection is on.
    val paused = if (running != null) pausedUntil else null
    Scaffold(
        topBar = { TopAppBar(title = { Text("AdBlocker") }, actions = { AppMenu() }) },
        contentWindowInsets = WindowInsets(0),
    ) { padding ->
        Column(
            modifier = Modifier
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp, vertical = 8.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            PowerButton(state, paused = paused != null, onToggle)
            StatusText(state, paused)
            if (paused != null) {
                Button(onClick = onResume) { Text("Resume now") }
            } else if (running != null) {
                TextButton(onClick = onPause) { Text("Pause for $PAUSE_MINUTES minutes") }
            }

            if (running?.lockdown == true) {
                NoticeCard(
                    title = "“Block connections without VPN” is on",
                    body = "AdBlocker only carries DNS lookups, so apps can't connect while this is on. " +
                        "Turn it off in VPN settings.",
                    tone = NoticeTone.Warning,
                    icon = Icons.Filled.Warning,
                    actionLabel = "Open VPN settings",
                    onAction = { context.openSystemSettings(Settings.ACTION_VPN_SETTINGS) },
                )
            }
            if (privateDnsServer != null) {
                NoticeCard(
                    title = "Private DNS is bypassing AdBlocker",
                    body = "Android sends every lookup straight to $privateDnsServer, so nothing gets blocked. " +
                        "Set Private DNS to Automatic or Off in Network settings.",
                    tone = NoticeTone.Warning,
                    icon = Icons.Filled.Warning,
                    actionLabel = "Open network settings",
                    onAction = { context.openSystemSettings(Settings.ACTION_WIRELESS_SETTINGS) },
                )
            }
            if (state is VpnState.Failed) {
                NoticeCard(title = "Protection stopped", body = state.message, tone = NoticeTone.Warning, icon = Icons.Filled.Warning)
            }
            if (newVersion != null) {
                NoticeCard(
                    title = "AdBlocker $newVersion is available",
                    body = "You have ${BuildConfig.VERSION_NAME}. Settings has a button to install it.",
                    tone = NoticeTone.Info,
                    icon = Icons.Filled.Info,
                    actionLabel = "Go to Settings",
                    onAction = onOpenSettings,
                )
            }

            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                StatCard("blocked today", totals.blockedToday, Modifier.weight(1f))
                StatCard("lookups today", totals.queriesToday, Modifier.weight(1f))
            }
            TotalsCard(totals)

            // Not needed once Always-on VPN is set up (Android 10 and later can tell).
            if (running?.alwaysOn != true) {
                NoticeCard(
                    title = "Keep protection on",
                    body = "Turn on Always-on VPN for AdBlocker so Android restarts it by itself. " +
                        "Leave “Block connections without VPN” off, or apps will lose their connection.",
                    tone = NoticeTone.Info,
                    icon = Icons.Filled.Info,
                    actionLabel = "Open VPN settings",
                    onAction = { context.openSystemSettings(Settings.ACTION_VPN_SETTINGS) },
                )
            }
            TextButton(onClick = onOpenActivity) { Text("See what was blocked") }
        }
    }
}

@Composable
private fun PowerButton(state: VpnState, paused: Boolean, onClick: () -> Unit) {
    val on = state is VpnState.Running
    val starting = state is VpnState.Starting
    val ring by animateColorAsState(
        if (on) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainerHigh,
        label = "ring",
    )
    val fill = if (on || starting) {
        Brush.verticalGradient(listOf(ShieldTop, ShieldBottom))
    } else {
        SolidColor(MaterialTheme.colorScheme.surfaceContainerHighest)
    }
    val iconColor = if (on || starting) Color.White else MaterialTheme.colorScheme.onSurfaceVariant

    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier
            .padding(top = 16.dp)
            .size(196.dp)
            .clip(CircleShape)
            .background(ring)
            .padding(16.dp)
            .clip(CircleShape)
            .background(fill)
            .toggleable(value = on || starting, role = Role.Switch, onValueChange = { onClick() })
            .semantics {
                contentDescription = "Protection"
                stateDescription = when {
                    on && paused -> "Paused"
                    on -> "On"
                    starting -> "Starting"
                    else -> "Off"
                }
            },
    ) {
        if (starting) {
            CircularProgressIndicator(color = iconColor, strokeWidth = 4.dp, modifier = Modifier.size(64.dp))
        } else {
            Icon(painterResource(R.drawable.ic_power), contentDescription = null, tint = iconColor, modifier = Modifier.size(72.dp))
        }
    }
}

@Composable
private fun StatusText(state: VpnState, pausedUntil: Long?) {
    val (title, detail) = when (state) {
        is VpnState.Running -> if (pausedUntil != null) {
            "Paused until ${formatClock(pausedUntil)}" to "Nothing is blocked until then. After that, blocking starts again by itself."
        } else {
            "Protection is on" to "Ads and trackers are blocked in apps, games and browsers."
        }
        VpnState.Starting -> "Starting…" to "Loading your filter lists."
        is VpnState.Failed -> "Protection is off" to "Tap the button to try again."
        VpnState.Stopped -> "Protection is off" to "Tap the button to start blocking."
    }
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Text(title, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
        Text(
            detail,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 4.dp),
        )
    }
}

@Composable
private fun StatCard(label: String, value: Long, modifier: Modifier = Modifier) {
    Card(
        modifier = modifier,
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer),
    ) {
        Column(Modifier.padding(16.dp)) {
            Text(formatCount(value), style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
            Text(label, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun TotalsCard(totals: Totals) {
    val share = if (totals.queriesTotal > 0) totals.blockedTotal * 100.0 / totals.queriesTotal else 0.0
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer),
    ) {
        Column(Modifier.padding(16.dp)) {
            Text(
                "${formatCount(totals.blockedTotal)} blocked since ${formatDate(totals.since)}",
                style = MaterialTheme.typography.titleMedium,
            )
            Text(
                "That is ${"%.1f".format(share)}% of ${formatCount(totals.queriesTotal)} lookups.",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}
