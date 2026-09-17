package com.teykaijun.adblocker.ui

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.provider.Settings
import android.text.format.DateUtils
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.net.toUri
import com.teykaijun.adblocker.dns.RuleParser
import java.net.URI
import java.text.DateFormat
import java.text.NumberFormat
import java.util.Date

object AppLinks {
    const val SOURCE = "https://github.com/teykaijun/AdBlockerPlugin"
    const val SUPPORT = "https://buymeacoffee.com/casunoxd"
}

/** The ⋮ menu shown in every screen's top bar. */
@Composable
fun AppMenu() {
    val context = LocalContext.current
    var expanded by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { expanded = true }) {
            Icon(Icons.Filled.MoreVert, contentDescription = "More options")
        }
        DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
            DropdownMenuItem(
                text = { Text("Support development ☕") },
                leadingIcon = { Icon(Icons.Filled.Favorite, contentDescription = null) },
                onClick = {
                    expanded = false
                    context.openUrl(AppLinks.SUPPORT)
                },
            )
            DropdownMenuItem(
                text = { Text("Source code") },
                leadingIcon = { Icon(Icons.Filled.Info, contentDescription = null) },
                onClick = {
                    expanded = false
                    context.openUrl(AppLinks.SOURCE)
                },
            )
        }
    }
}

@Composable
fun SectionHeader(title: String, subtitle: String? = null) {
    Column(Modifier.padding(start = 16.dp, end = 16.dp, top = 24.dp, bottom = 4.dp)) {
        Text(title, style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary)
        if (subtitle != null) {
            Text(
                subtitle,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 2.dp),
            )
        }
    }
}

@Composable
fun SwitchRow(title: String, body: String?, checked: Boolean, meta: String? = null, onCheckedChange: (Boolean) -> Unit) {
    ListItem(
        headlineContent = { Text(title) },
        supportingContent = if (body != null || meta != null) {
            {
                Column {
                    if (body != null) Text(body)
                    if (meta != null) Text(meta, style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(top = 2.dp))
                }
            }
        } else {
            null
        },
        trailingContent = { Switch(checked = checked, onCheckedChange = null) },
        modifier = Modifier.toggleable(value = checked, role = Role.Switch, onValueChange = onCheckedChange),
        colors = ListItemDefaults.colors(containerColor = Color.Transparent),
    )
}

@Composable
fun RadioRow(title: String, body: String, selected: Boolean, onSelect: () -> Unit) {
    ListItem(
        headlineContent = { Text(title) },
        supportingContent = { Text(body) },
        leadingContent = { RadioButton(selected = selected, onClick = null) },
        modifier = Modifier.selectable(selected = selected, role = Role.RadioButton, onClick = onSelect),
        colors = ListItemDefaults.colors(containerColor = Color.Transparent),
    )
}

@Composable
fun ClickRow(title: String, body: String?, onClick: () -> Unit) {
    ListItem(
        headlineContent = { Text(title) },
        supportingContent = body?.let { { Text(it) } },
        modifier = Modifier.clickable(onClick = onClick),
        colors = ListItemDefaults.colors(containerColor = Color.Transparent),
    )
}

enum class NoticeTone { Warning, Info }

@Composable
fun NoticeCard(
    title: String,
    body: String,
    tone: NoticeTone,
    icon: ImageVector,
    actionLabel: String? = null,
    onAction: (() -> Unit)? = null,
) {
    val colors = when (tone) {
        NoticeTone.Warning -> CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.errorContainer,
            contentColor = MaterialTheme.colorScheme.onErrorContainer,
        )
        NoticeTone.Info -> CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
            contentColor = MaterialTheme.colorScheme.onSurface,
        )
    }
    Card(colors = colors, modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.padding(16.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.Top) {
            Icon(icon, contentDescription = null)
            Column(Modifier.weight(1f)) {
                Text(title, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
                Text(body, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(top = 4.dp))
                if (actionLabel != null && onAction != null) {
                    TextButton(onClick = onAction, modifier = Modifier.padding(top = 4.dp)) { Text(actionLabel) }
                }
            }
        }
    }
}

fun formatCount(value: Number): String = NumberFormat.getIntegerInstance().format(value)

fun formatDate(millis: Long): String = DateFormat.getDateInstance(DateFormat.MEDIUM).format(Date(millis))

fun formatTime(millis: Long): String = DateFormat.getTimeInstance(DateFormat.MEDIUM).format(Date(millis))

fun relativeTime(millis: Long): String =
    DateUtils.getRelativeTimeSpanString(millis, System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS).toString()

/** Accepts `ads.example.com`, `https://ads.example.com/path` or `||ads.example.com^`. */
fun parseDomainInput(text: String): String? {
    val trimmed = text.trim()
    val host = if ("://" in trimmed) runCatching { URI(trimmed).host }.getOrNull() else trimmed.removePrefix("||").removeSuffix("^")
    return host?.let(RuleParser::normalizeDomain)
}

/** Opens the first of `actions` this device can handle. */
fun Context.openSystemSettings(vararg actions: String) {
    for (action in listOf(*actions, Settings.ACTION_SETTINGS)) {
        try {
            startActivity(Intent(action).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            return
        } catch (e: ActivityNotFoundException) {
            // Try the next one.
        }
    }
}

/** Opens AdBlocker's App info page in Android's settings. */
fun Context.openAppInfo() {
    try {
        startActivity(
            Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, "package:$packageName".toUri())
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
        )
    } catch (e: ActivityNotFoundException) {
        openSystemSettings()
    }
}

fun Context.openUrl(url: String) {
    try {
        startActivity(Intent(Intent.ACTION_VIEW, url.toUri()).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    } catch (e: ActivityNotFoundException) {
        // No browser installed; nothing sensible to do.
    }
}
