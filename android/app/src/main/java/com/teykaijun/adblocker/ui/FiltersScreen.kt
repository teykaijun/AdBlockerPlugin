package com.teykaijun.adblocker.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.unit.dp
import com.teykaijun.adblocker.data.BuiltInList
import com.teykaijun.adblocker.data.ListStatus
import com.teykaijun.adblocker.data.RemoteList
import com.teykaijun.adblocker.data.Settings
import java.net.URI

private sealed interface FiltersDialog {
    data class AddDomain(val allow: Boolean) : FiltersDialog
    data object AddList : FiltersDialog
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FiltersScreen(
    builtIn: List<BuiltInList>,
    settings: Settings,
    status: Map<String, ListStatus>,
    onUpdateSettings: ((Settings) -> Settings) -> Unit,
    onRefreshList: (RemoteList) -> Unit,
    onDeleteList: (String) -> Unit,
) {
    var dialog by remember { mutableStateOf<FiltersDialog?>(null) }

    Scaffold(
        topBar = { TopAppBar(title = { Text("Filters") }, actions = { AppMenu() }) },
        contentWindowInsets = WindowInsets(0),
    ) { padding ->
        LazyColumn(Modifier.padding(padding).fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
            item { SectionHeader("Built-in lists", "Bundled with the app and shared with AdBlocker for Windows.") }
            items(builtIn, key = { "builtin-${it.id}" }) { list ->
                SwitchRow(
                    title = list.title,
                    body = list.description,
                    meta = "${formatCount(list.domains)} domains",
                    checked = settings.isBuiltInEnabled(list),
                    onCheckedChange = { on -> onUpdateSettings { it.copy(builtInLists = it.builtInLists + (list.id to on)) } },
                )
            }

            item {
                SectionHeader(
                    "Community lists",
                    "Much larger lists that catch far more ads in apps and games. They download from the address shown and update weekly.",
                )
            }
            items(settings.remoteLists, key = { "remote-${it.id}" }) { list ->
                val listStatus = status[list.id]
                RemoteListRow(
                    list = list,
                    status = listStatus,
                    onToggle = { on ->
                        onUpdateSettings { s ->
                            s.copy(remoteLists = s.remoteLists.map { if (it.id == list.id) it.copy(enabled = on) else it })
                        }
                        if (on && listStatus?.downloaded != true) onRefreshList(list)
                    },
                    onRefresh = { onRefreshList(list) },
                    onRemove = if (list.custom) {
                        {
                            onUpdateSettings { s -> s.copy(remoteLists = s.remoteLists.filterNot { it.id == list.id }) }
                            onDeleteList(list.id)
                        }
                    } else {
                        null
                    },
                )
            }
            item {
                TextButton(onClick = { dialog = FiltersDialog.AddList }, modifier = Modifier.padding(horizontal = 8.dp)) {
                    Icon(Icons.Filled.Add, contentDescription = null)
                    Spacer(Modifier.width(8.dp))
                    Text("Add a list by URL")
                }
            }

            item { SectionHeader("My blocked domains", "Always blocked, including their subdomains.") }
            items(settings.blockedDomains.sorted(), key = { "blocked-$it" }) { domain ->
                DomainRow(domain) { onUpdateSettings { it.withoutDomainRule(domain) } }
            }
            item {
                TextButton(onClick = { dialog = FiltersDialog.AddDomain(allow = false) }, modifier = Modifier.padding(horizontal = 8.dp)) {
                    Icon(Icons.Filled.Add, contentDescription = null)
                    Spacer(Modifier.width(8.dp))
                    Text("Block a domain")
                }
            }

            item { SectionHeader("My allowed domains", "Never blocked, even when a list includes them.") }
            items(settings.allowedDomains.sorted(), key = { "allowed-$it" }) { domain ->
                DomainRow(domain) { onUpdateSettings { it.withoutDomainRule(domain) } }
            }
            item {
                TextButton(onClick = { dialog = FiltersDialog.AddDomain(allow = true) }, modifier = Modifier.padding(horizontal = 8.dp)) {
                    Icon(Icons.Filled.Add, contentDescription = null)
                    Spacer(Modifier.width(8.dp))
                    Text("Allow a domain")
                }
            }
        }
    }

    when (val current = dialog) {
        is FiltersDialog.AddDomain -> AddDomainDialog(
            allow = current.allow,
            onDismiss = { dialog = null },
            onAdd = { domain ->
                onUpdateSettings { it.withDomainRule(domain, current.allow) }
                dialog = null
            },
        )
        FiltersDialog.AddList -> AddListDialog(
            onDismiss = { dialog = null },
            onAdd = { list ->
                onUpdateSettings { it.copy(remoteLists = it.remoteLists + list) }
                onRefreshList(list)
                dialog = null
            },
        )
        null -> Unit
    }
}

@Composable
private fun RemoteListRow(
    list: RemoteList,
    status: ListStatus?,
    onToggle: (Boolean) -> Unit,
    onRefresh: () -> Unit,
    onRemove: (() -> Unit)?,
) {
    val statusText = when {
        status?.updating == true -> "Downloading…"
        status?.error != null -> "Download failed: ${status.error}"
        status?.downloaded == true -> "${formatCount(status.domains)} domains · updated ${relativeTime(status.updatedAt)}"
        else -> "Not downloaded yet"
    }
    ListItem(
        headlineContent = { Text(list.title) },
        supportingContent = {
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(list.description)
                Text(list.url, style = MaterialTheme.typography.labelSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(
                    statusText,
                    style = MaterialTheme.typography.labelMedium,
                    color = if (status?.error != null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                )
                if (status?.updating == true) LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 4.dp))
                Row {
                    if (list.enabled || status?.downloaded == true) {
                        TextButton(onClick = onRefresh, enabled = status?.updating != true) { Text("Update now") }
                    }
                    if (onRemove != null) TextButton(onClick = onRemove) { Text("Remove") }
                }
            }
        },
        trailingContent = { Switch(checked = list.enabled, onCheckedChange = null) },
        modifier = Modifier.toggleable(value = list.enabled, role = Role.Switch, onValueChange = onToggle),
        colors = ListItemDefaults.colors(containerColor = Color.Transparent),
    )
}

@Composable
private fun DomainRow(domain: String, onRemove: () -> Unit) {
    ListItem(
        headlineContent = { Text(domain, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        trailingContent = {
            IconButton(onClick = onRemove) { Icon(Icons.Filled.Close, contentDescription = "Remove $domain") }
        },
        colors = ListItemDefaults.colors(containerColor = Color.Transparent),
    )
}

@Composable
private fun AddDomainDialog(allow: Boolean, onDismiss: () -> Unit, onAdd: (String) -> Unit) {
    var text by rememberSaveable { mutableStateOf("") }
    val domain = parseDomainInput(text)
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(if (allow) "Allow a domain" else "Block a domain") },
        text = {
            OutlinedTextField(
                value = text,
                onValueChange = { text = it },
                label = { Text("Domain") },
                placeholder = { Text("ads.example.com") },
                singleLine = true,
                isError = text.isNotBlank() && domain == null,
                supportingText = {
                    Text(if (text.isNotBlank() && domain == null) "Enter a domain such as ads.example.com" else "Subdomains are included.")
                },
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Done),
            )
        },
        confirmButton = {
            TextButton(onClick = { domain?.let(onAdd) }, enabled = domain != null) { Text(if (allow) "Allow" else "Block") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

@Composable
private fun AddListDialog(onDismiss: () -> Unit, onAdd: (RemoteList) -> Unit) {
    var name by rememberSaveable { mutableStateOf("") }
    var url by rememberSaveable { mutableStateOf("") }
    val host = runCatching { URI(url.trim()).takeIf { it.scheme == "https" }?.host }.getOrNull()
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Add a list") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(
                    "Hosts files and Adblock-style domain lists work. The file is downloaded from this address and refreshed weekly.",
                    style = MaterialTheme.typography.bodyMedium,
                )
                OutlinedTextField(
                    value = url,
                    onValueChange = { url = it },
                    label = { Text("URL") },
                    placeholder = { Text("https://example.com/hosts.txt") },
                    singleLine = true,
                    isError = url.isNotBlank() && host == null,
                    supportingText = if (url.isNotBlank() && host == null) {
                        { Text("Use an https:// address") }
                    } else {
                        null
                    },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Next),
                )
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it },
                    label = { Text("Name (optional)") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                )
            }
        },
        confirmButton = {
            TextButton(
                enabled = host != null,
                onClick = {
                    onAdd(
                        RemoteList(
                            id = "custom-${System.currentTimeMillis()}",
                            title = name.trim().ifEmpty { host.orEmpty() },
                            url = url.trim(),
                            description = "Added by you",
                            enabled = true,
                            custom = true,
                        ),
                    )
                },
            ) { Text("Add") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}
