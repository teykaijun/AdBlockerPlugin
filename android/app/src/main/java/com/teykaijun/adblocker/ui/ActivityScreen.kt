package com.teykaijun.adblocker.ui

import android.content.ClipData
import android.content.ClipboardManager
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.teykaijun.adblocker.data.QueryEvent
import com.teykaijun.adblocker.data.Settings

private enum class ActivityFilter(val label: String) {
    All("All"),
    Blocked("Blocked"),
    Allowed("Allowed");

    fun matches(event: QueryEvent) = when (this) {
        All -> true
        Blocked -> event.blocked
        Allowed -> !event.blocked
    }
}

/**
 * @param onRule `true` to always allow a domain, `false` to always block it,
 *   `null` to remove the user's rule for it.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ActivityScreen(
    events: List<QueryEvent>,
    settings: Settings,
    running: Boolean,
    onRule: (domain: String, allow: Boolean?) -> Unit,
    onClear: () -> Unit,
) {
    var filter by rememberSaveable { mutableStateOf(ActivityFilter.All) }
    var query by rememberSaveable { mutableStateOf("") }
    var selected by remember { mutableStateOf<QueryEvent?>(null) }
    val visible = remember(events, filter, query) {
        val needle = query.trim().lowercase()
        events.filter { filter.matches(it) && (needle.isEmpty() || needle in it.domain) }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Activity") },
                actions = {
                    IconButton(onClick = onClear, enabled = events.isNotEmpty()) {
                        Icon(Icons.Filled.Delete, contentDescription = "Clear activity")
                    }
                    AppMenu()
                },
            )
        },
        contentWindowInsets = WindowInsets(0),
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize()) {
            OutlinedTextField(
                value = query,
                onValueChange = { query = it },
                placeholder = { Text("Search domains") },
                leadingIcon = { Icon(Icons.Filled.Search, contentDescription = null) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
            )
            Row(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                ActivityFilter.entries.forEach { option ->
                    FilterChip(selected = filter == option, onClick = { filter = option }, label = { Text(option.label) })
                }
            }
            if (visible.isEmpty()) {
                Box(Modifier.fillMaxSize().padding(32.dp), contentAlignment = Alignment.Center) {
                    Text(
                        when {
                            events.isNotEmpty() -> "Nothing matches."
                            running -> "Waiting for the first lookups. Open an app or a website."
                            else -> "Turn protection on to see which domains apps look up."
                        },
                        textAlign = TextAlign.Center,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            } else {
                LazyColumn(Modifier.fillMaxSize()) {
                    items(visible, key = { it.id }) { event ->
                        QueryRow(event, userRule(event.domain, settings)) { selected = event }
                    }
                }
            }
        }
    }

    selected?.let { event ->
        DomainDialog(
            event = event,
            rule = userRule(event.domain, settings),
            onDismiss = { selected = null },
            onRule = { allow ->
                onRule(event.domain, allow)
                selected = null
            },
        )
    }
}

private fun userRule(domain: String, settings: Settings): Boolean? = when (domain) {
    in settings.allowedDomains -> true
    in settings.blockedDomains -> false
    else -> null
}

@Composable
private fun QueryRow(event: QueryEvent, rule: Boolean?, onClick: () -> Unit) {
    val color = when {
        event.scam -> MaterialTheme.colorScheme.error
        event.blocked -> MaterialTheme.colorScheme.primary
        else -> MaterialTheme.colorScheme.outlineVariant
    }
    val status = buildString {
        append(formatTime(event.time))
        append(if (event.scam) " · Suspected scam" else if (event.blocked) " · Blocked" else " · Allowed")
        if (rule == true) append(" · you allowed this")
        if (rule == false) append(" · you blocked this")
    }
    ListItem(
        headlineContent = { Text(event.domain, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        supportingContent = { Text(status) },
        leadingContent = { Box(Modifier.size(10.dp).clip(CircleShape).background(color)) },
        modifier = Modifier.clickable(onClick = onClick),
    )
}

@Composable
private fun DomainDialog(event: QueryEvent, rule: Boolean?, onDismiss: () -> Unit, onRule: (Boolean?) -> Unit) {
    val context = LocalContext.current
    val (confirmLabel, confirmValue) = when {
        rule != null -> "Remove my rule" to null
        event.blocked -> "Always allow" to true
        else -> "Always block" to false
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(event.domain) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(
                    when {
                        rule == true -> "You allowed this domain, so it is never blocked."
                        rule == false -> "You blocked this domain and all of its subdomains."
                        event.scam -> "This domain is on a list of fake shops, subscription traps and similar scams. " +
                            "Please be careful before allowing it."
                        event.blocked -> "A filter list blocked this lookup."
                        else -> "No filter list matched this lookup."
                    },
                )
                Text(
                    "Changes apply to new lookups. Apps that already looked the name up may need a restart.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                TextButton(
                    onClick = {
                        context.getSystemService(ClipboardManager::class.java)
                            .setPrimaryClip(ClipData.newPlainText("Domain", event.domain))
                    },
                ) { Text("Copy domain") }
            }
        },
        confirmButton = { TextButton(onClick = { onRule(confirmValue) }) { Text(confirmLabel) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}
