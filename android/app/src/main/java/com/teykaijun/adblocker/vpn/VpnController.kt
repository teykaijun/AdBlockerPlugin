package com.teykaijun.adblocker.vpn

import android.content.Context
import androidx.core.content.ContextCompat
import com.teykaijun.adblocker.dns.DomainMatcher
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

sealed interface VpnState {
    data object Stopped : VpnState
    data object Starting : VpnState
    data class Running(val since: Long) : VpnState
    data class Failed(val message: String) : VpnState
}

/** Starts and stops [AdBlockVpnService] and exposes its state to the UI. */
object VpnController {
    private val mutableState = MutableStateFlow<VpnState>(VpnState.Stopped)
    val state: StateFlow<VpnState> = mutableState.asStateFlow()

    val isActive: Boolean
        get() = state.value.let { it is VpnState.Running || it is VpnState.Starting }

    /** The rules the running VPN answers with, or null while it isn't running. The tab closer reads them. */
    @Volatile
    var matcher: DomainMatcher? = null
        internal set

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
