package com.teykaijun.adblocker.ui

import android.provider.Settings as AndroidSettings
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.teykaijun.adblocker.BuildConfig
import com.teykaijun.adblocker.data.DnsProvider
import com.teykaijun.adblocker.data.Settings
import com.teykaijun.adblocker.data.Totals
import com.teykaijun.adblocker.data.parseIpLiteral

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(
    settings: Settings,
    totals: Totals,
    onUpdateSettings: ((Settings) -> Settings) -> Unit,
    onResetStats: () -> Unit,
) {
    val context = LocalContext.current
    var confirmReset by remember { mutableStateOf(false) }
    var customDns by rememberSaveable { mutableStateOf(settings.customDns) }

    Scaffold(
        topBar = { TopAppBar(title = { Text("Settings") }, actions = { AppMenu() }) },
        contentWindowInsets = WindowInsets(0),
    ) { padding ->
        Column(Modifier.padding(padding).verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
            SectionHeader("DNS server", "Answers every lookup that is not blocked.")
            DnsProvider.entries.forEach { provider ->
                RadioRow(provider.title, provider.detail, selected = settings.dnsProvider == provider) {
                    onUpdateSettings { it.copy(dnsProvider = provider) }
                }
            }
            if (settings.dnsProvider == DnsProvider.CUSTOM) {
                val valid = parseIpLiteral(customDns) != null
                OutlinedTextField(
                    value = customDns,
                    onValueChange = { text ->
                        customDns = text
                        if (parseIpLiteral(text) != null) onUpdateSettings { it.copy(customDns = text.trim()) }
                    },
                    label = { Text("Server IP address") },
                    singleLine = true,
                    isError = customDns.isNotBlank() && !valid,
                    supportingText = {
                        Text(
                            when {
                                customDns.isBlank() -> "Until you enter one, Cloudflare is used."
                                valid -> "Saved."
                                else -> "Enter an IPv4 or IPv6 address, for example 94.140.14.14."
                            },
                        )
                    },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                )
            }

            SectionHeader("Startup")
            SwitchRow(
                title = "Start after a restart",
                body = "Turn protection back on when the phone reboots, if it was on before.",
                checked = settings.startOnBoot,
                onCheckedChange = { on -> onUpdateSettings { it.copy(startOnBoot = on) } },
            )
            ClickRow(
                title = "Always-on VPN",
                body = "Let Android keep AdBlocker running. Leave “Block connections without VPN” off.",
                onClick = { context.openSystemSettings(AndroidSettings.ACTION_VPN_SETTINGS) },
            )

            SectionHeader("Statistics")
            ClickRow(
                title = "Reset statistics",
                body = "Counting since ${formatDate(totals.since)}",
                onClick = { confirmReset = true },
            )

            SectionHeader("How it works")
            Paragraph(
                "AdBlocker creates a VPN that only carries DNS lookups. Lookups for ad and tracker domains are " +
                    "answered with “not found”, so those servers are never contacted. Everything else goes to the DNS " +
                    "server chosen above, and all other traffic uses your normal connection.",
            )
            Paragraph(
                "Lookups stay on your phone apart from that one DNS server. The app only goes online to download " +
                    "community lists you turn on.",
            )

            SectionHeader("Good to know")
            Paragraph(
                "• Private DNS set to a hostname, or Chrome's “Use secure DNS” set to a specific provider, skips " +
                    "AdBlocker. Keep them on Automatic.\n" +
                    "• Ads served from the same domain as the content, like YouTube video ads, cannot be blocked by DNS.\n" +
                    "• Only one VPN app can run at a time.",
            )

            SectionHeader("About")
            ClickRow(title = "Version", body = BuildConfig.VERSION_NAME, onClick = {})
            ClickRow(
                title = "Support development ☕",
                body = "If AdBlocker is useful to you, you can buy me a coffee.",
                onClick = { context.openUrl(AppLinks.SUPPORT) },
            )
            ClickRow(title = "Source code", body = AppLinks.SOURCE.removePrefix("https://"), onClick = { context.openUrl(AppLinks.SOURCE) })
        }
    }

    if (confirmReset) {
        AlertDialog(
            onDismissRequest = { confirmReset = false },
            title = { Text("Reset statistics?") },
            text = { Text("All counters go back to zero. Filters and settings are not affected.") },
            confirmButton = {
                TextButton(onClick = {
                    onResetStats()
                    confirmReset = false
                }) { Text("Reset") }
            },
            dismissButton = { TextButton(onClick = { confirmReset = false }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun Paragraph(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp),
    )
}
