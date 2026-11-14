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
import androidx.compose.foundation.lazy.LazyListState
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
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.teykaijun.adblocker.data.QueryEvent
import com.teykaijun.adblocker.data.Settings
import com.teykaijun.adblocker.dns.UserRules
import com.teykaijun.adblocker.vpn.VpnController

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
 * @param onBlockAgain ends the allowances that let a domain through for a while.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ActivityScreen(
    events: List<QueryEvent>,
    settings: Settings,
    running: Boolean,
    onRule: (domain: String, allow: Boolean?) -> Unit,
    onAllowForAWhile: (domain: String) -> Unit,
    onBlockAgain: (domain: String) -> Unit,
    onClear: () -> Unit,
) {
    var filter by rememberSaveable { mutableStateOf(ActivityFilter.All) }
    var query by rememberSaveable { mutableStateOf("") }
    var selected by remember { mutableStateOf<QueryEvent?>(null) }
    val userRules = remember(settings) { settings.userRules() }
    val visible = remember(events, filter, query) {
        val needle = query.trim().lowercase()
        events.filter { filter.matches(it) && (needle.isEmpty() || needle in it.domain) }
    }

    // A keyed list keeps showing the same rows when newer lookups arrive above them. While the
    // user is at the top, follow the newest instead; while they scroll, leave the list alone.
    // The position isn't saved when another tab is shown: by the time the user comes back the
    // list has moved on, and a saved row number would point somewhere else, so it opens at the
    // newest lookups again.
    val listState = remember { LazyListState() }
    var followNewest by remember { mutableStateOf(true) }
    LaunchedEffect(listState) {
        snapshotFlow { listState.isScrollInProgress }.collect { scrolling ->
            followNewest = !scrolling && listState.firstVisibleItemIndex == 0 && listState.firstVisibleItemScrollOffset == 0
        }
    }
    // After "Clear", or a search that matches nothing, the next lookups start at the top again.
    LaunchedEffect(visible.isEmpty()) {
        if (visible.isEmpty()) {
            followNewest = true
            listState.requestScrollToItem(0)
        }
    }
    LaunchedEffect(visible.firstOrNull()?.id) {
        if (followNewest) listState.requestScrollToItem(0)
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
                val now = System.currentTimeMillis()
                LazyColumn(Modifier.fillMaxSize(), state = listState) {
                    items(visible, key = { it.id }) { event ->
                        QueryRow(event, userRules.match(event.domain, now), userRules.allowedUntil(event.domain, now)) { selected = event }
                    }
                }
            }
        }
    }

    selected?.let { event ->
        val now = System.currentTimeMillis()
        // What the lists say now rather than at the time of the lookup, which is only the best
        // guess while protection is off.
        val lists = VpnController.rules
        val listed = lists?.matcher?.isBlocked(event.domain) ?: event.blocked
        DomainDialog(
            event = event,
            match = userRules.match(event.domain, now),
            allowedUntil = userRules.allowedUntil(event.domain, now),
            listed = listed,
            scam = lists?.isScam(event.domain) ?: event.scam,
            blockedAgain = settings.blockedOnceAllowancesEnd(event.domain, listed, now),
            pausedUntil = settings.pausedUntil?.takeIf { running && it > now },
            onDismiss = { selected = null },
            onRule = { allow ->
                onRule(event.domain, allow)
                selected = null
            },
            onAllowForAWhile = {
                onAllowForAWhile(event.domain)
                selected = null
            },
            onBlockAgain = {
                onBlockAgain(event.domain)
                selected = null
            },
        )
    }
}

/**
 * @param match the user's rule that decides this name now, if any.
 * @param allowedUntil until when allowances for a while let it through (see [UserRules.allowedUntil]).
 */
@Composable
private fun QueryRow(event: QueryEvent, match: UserRules.Match?, allowedUntil: Long?, onClick: () -> Unit) {
    val color = when {
        event.scam -> MaterialTheme.colorScheme.error
        event.blocked -> MaterialTheme.colorScheme.primary
        else -> MaterialTheme.colorScheme.outlineVariant
    }
    val status = buildString {
        append(formatTime(event.time))
        append(if (event.scam) " · Suspected scam" else if (event.blocked) " · Blocked" else " · Allowed")
        when {
            match == null -> Unit
            match.until != null -> append(" · allowed until ${formatClock(allowedUntil ?: match.until)}")
            match.domain != event.domain -> Unit
            match.allows -> append(" · you allowed this")
            else -> append(" · you blocked this")
        }
    }
    ListItem(
        headlineContent = { Text(event.domain, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        supportingContent = { Text(status) },
        leadingContent = { Box(Modifier.size(10.dp).clip(CircleShape).background(color)) },
        modifier = Modifier.clickable(onClick = onClick),
    )
}

/**
 * Explains, from the rules as they are now, why a domain is blocked or not, and offers what
 * changes that.
 *
 * @param match the user's rule that decides this name now, if any.
 * @param allowedUntil until when allowances for a while let it through (see [UserRules.allowedUntil]).
 * @param listed whether the filter lists block it.
 * @param blockedAgain whether it is blocked once the allowances that cover it end.
 * @param pausedUntil when the current pause ends, if blocking is paused.
 */
@Composable
private fun DomainDialog(
    event: QueryEvent,
    match: UserRules.Match?,
    allowedUntil: Long?,
    listed: Boolean,
    scam: Boolean,
    blockedAgain: Boolean,
    pausedUntil: Long?,
    onDismiss: () -> Unit,
    onRule: (Boolean?) -> Unit,
    onAllowForAWhile: () -> Unit,
    onBlockAgain: () -> Unit,
) {
    val context = LocalContext.current
    val choices = domainChoices(event.domain, match, listed, blockedAgain)
    val confirmLabel = when (choices.rule) {
        null -> "Remove my rule"
        true -> "Always allow"
        false -> "Always block"
    }
    val explanation = when {
        match == null -> when {
            scam -> "This domain is on a list of fake shops, subscription traps and similar scams. " +
                "Please be careful before allowing it."
            listed -> "A filter list blocks this domain."
            else -> "No filter list blocks this domain."
        }
        match.until != null -> buildString {
            val until = allowedUntil ?: match.until
            when {
                // A parent's allowance runs longer than this name's own.
                until != match.until -> append("Domains you allowed for a while let this one through until ${formatClock(until)}.")
                match.domain == event.domain -> append("You allowed this domain until ${formatClock(until)}.")
                else -> append("You allowed ${match.domain} and its subdomains until ${formatClock(until)}.")
            }
            if (blockedAgain) append(" After that it's blocked again.")
        }
        match.domain == event.domain ->
            if (match.allows) "You allowed this domain, so it is never blocked." else "You blocked this domain and all of its subdomains."
        match.allows -> "You allowed ${match.domain} and its subdomains, so this one is never blocked."
        else -> "You blocked ${match.domain} and all of its subdomains, so this one is blocked too."
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(event.domain) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(explanation)
                if (pausedUntil != null) Text("Blocking is paused until ${formatClock(pausedUntil)}, so nothing is blocked right now.")
                Text(
                    "Changes apply to new lookups. Apps that already looked the name up may need a restart.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                when (choices.extra) {
                    DomainChoices.Extra.BlockAgain -> TextButton(onClick = onBlockAgain) { Text("Block again now") }
                    // For a site that broke: let it through briefly without a permanent exception.
                    DomainChoices.Extra.AllowForAWhile ->
                        TextButton(onClick = onAllowForAWhile) { Text("Allow for $ALLOW_FOR_MINUTES minutes") }
                    null -> Unit
                }
                TextButton(
                    onClick = {
                        context.getSystemService(ClipboardManager::class.java)
                            .setPrimaryClip(ClipData.newPlainText("Domain", event.domain))
                    },
                ) { Text("Copy domain") }
            }
        },
        confirmButton = { TextButton(onClick = { onRule(choices.rule) }) { Text(confirmLabel) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

/**
 * The Activity dialog's buttons for a domain.
 *
 * @property rule what the main button does: `true` always allow, `false` always block, `null`
 *   remove the user's own rule for it.
 */
internal data class DomainChoices(val rule: Boolean?, val extra: Extra?) {
    enum class Extra { AllowForAWhile, BlockAgain }
}

/**
 * @param match the user's rule that decides `domain` now, if any.
 * @param listed whether the filter lists block it.
 * @param blockedAgain whether it is blocked once the allowances that cover it end.
 */
internal fun domainChoices(domain: String, match: UserRules.Match?, listed: Boolean, blockedAgain: Boolean): DomainChoices = when {
    match != null && match.domain == domain && match.until == null -> DomainChoices(rule = null, extra = null)
    match?.until != null -> if (blockedAgain) {
        DomainChoices(rule = true, extra = DomainChoices.Extra.BlockAgain)
    } else {
        DomainChoices(rule = false, extra = null)
    }
    match?.let { !it.allows } ?: listed -> DomainChoices(rule = true, extra = DomainChoices.Extra.AllowForAWhile)
    else -> DomainChoices(rule = false, extra = null)
}

/** How long "Allow for a while" lets a site through. */
const val ALLOW_FOR_MINUTES = 10L
