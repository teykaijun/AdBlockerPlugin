package com.teykaijun.adblocker.vpn

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.teykaijun.adblocker.app
import com.teykaijun.adblocker.data.PAUSE_MINUTES

/** The "Pause 10 min" and "Resume" buttons on the status notification. The VPN follows the setting. */
class PauseReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            // A pause only means something while protection is on; a stale notification can't start one.
            ACTION_PAUSE -> if (VpnController.isActive) context.app.settings.update { it.pausedFor(PAUSE_MINUTES) }
            ACTION_RESUME -> context.app.settings.update { it.copy(pausedUntil = null) }
        }
    }

    companion object {
        const val ACTION_PAUSE = "com.teykaijun.adblocker.action.PAUSE"
        const val ACTION_RESUME = "com.teykaijun.adblocker.action.RESUME"

        fun pendingIntent(context: Context, action: String): PendingIntent = PendingIntent.getBroadcast(
            context,
            0,
            Intent(context, PauseReceiver::class.java).setAction(action),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
    }
}
