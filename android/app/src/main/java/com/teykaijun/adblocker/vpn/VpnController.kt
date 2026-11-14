package com.teykaijun.adblocker.vpn

import android.content.Context
import androidx.core.content.ContextCompat
import com.teykaijun.adblocker.dns.Rules
import com.teykaijun.adblocker.dns.UserRules
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

sealed interface VpnState {
    data object Stopped : VpnState
    data object Starting : VpnState

    /**
     * @property alwaysOn Android keeps AdBlocker running (Always-on VPN).
     * @property lockdown "Block connections without VPN" is on, which cuts off everything but DNS.
     */
    data class Running(val since: Long, val alwaysOn: Boolean = false, val lockdown: Boolean = false) : VpnState
    data class Failed(val message: String) : VpnState
}

/** Starts and stops [AdBlockVpnService] and exposes its state to the UI. */
object VpnController {
    private val mutableState = MutableStateFlow<VpnState>(VpnState.Stopped)
    val state: StateFlow<VpnState> = mutableState.asStateFlow()

    val isActive: Boolean
        get() = state.value.let { it is VpnState.Running || it is VpnState.Starting }

    /** The filter lists the running VPN answers with, or null while it isn't running. The tab closer reads them. */
    @Volatile
    var rules: Rules? = null
        internal set

    /** The user's own rules, allowances and pause, which the running VPN applies before [rules]. */
    @Volatile
    var userRules: UserRules = UserRules.NONE
        internal set

    /** Whether the running VPN blocks `host` right now; false while it isn't running or is paused. */
    fun blocks(host: String): Boolean {
        val current = rules ?: return false
        return userRules.isBlocked(host, current.matcher, System.currentTimeMillis())
    }

    internal fun setState(state: VpnState) {
        mutableState.value = state
    }

    fun reportFailure(message: String) = setState(VpnState.Failed(message))

    /** Requires VPN consent to have been granted already. Returns false if Android refused. */
    fun start(context: Context): Boolean = try {
        ContextCompat.startForegroundService(context, AdBlockVpnService.intent(context, AdBlockVpnService.ACTION_START))
        true
    } catch (e: IllegalStateException) {
        // ForegroundServiceStartNotAllowedException: started from the background.
        false
    }

    fun stop(context: Context) {
        if (!isActive) return
        try {
            context.startService(AdBlockVpnService.intent(context, AdBlockVpnService.ACTION_STOP))
        } catch (e: IllegalStateException) {
            // Not allowed from the background; nothing is running to stop then.
        }
    }
}
