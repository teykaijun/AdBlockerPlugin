package com.teykaijun.adblocker.vpn

import android.annotation.SuppressLint
import android.app.PendingIntent
import android.content.Intent
import android.net.VpnService
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import com.teykaijun.adblocker.MainActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

/** Quick Settings tile that turns protection on and off. */
class ToggleTileService : TileService() {
    private var listening: Job? = null

    override fun onStartListening() {
        listening = CoroutineScope(Dispatchers.Main.immediate).launch {
            VpnController.state.collect { render(it) }
        }
    }

    override fun onStopListening() {
        listening?.cancel()
        listening = null
    }

    override fun onClick() {
        when {
            VpnController.isActive -> VpnController.stop(this)
            // Consent (and the first-run setup) needs the app on screen.
            VpnService.prepare(this) != null -> openApp()
            !VpnController.start(this) -> openApp()
        }
    }

    private fun render(state: VpnState) {
        val tile = qsTile ?: return
        tile.state = when (state) {
            is VpnState.Running, VpnState.Starting -> Tile.STATE_ACTIVE
            else -> Tile.STATE_INACTIVE
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            tile.subtitle = when (state) {
                is VpnState.Running -> "On"
                VpnState.Starting -> "Starting…"
                is VpnState.Failed -> "Stopped"
                VpnState.Stopped -> "Off"
            }
        }
        tile.updateTile()
    }

    // Lint flags the pre-Android 14 branch even behind the version check.
    @SuppressLint("StartActivityAndCollapseDeprecated")
    private fun openApp() {
        val intent = Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE))
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(intent)
        }
    }
}
