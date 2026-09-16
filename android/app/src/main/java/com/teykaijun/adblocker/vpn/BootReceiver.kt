package com.teykaijun.adblocker.vpn

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.net.VpnService
import com.teykaijun.adblocker.app

/** Turns protection back on after a reboot or an app update, if it was on before. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val settings = context.app.settings.settings.value
        val wanted = when (intent.action) {
            Intent.ACTION_BOOT_COMPLETED -> settings.protectionOn && settings.startOnBoot
            Intent.ACTION_MY_PACKAGE_REPLACED -> settings.protectionOn
            else -> false
        }
        // prepare() returns null only while the user's VPN consent is still valid.
        if (wanted && VpnService.prepare(context) == null) VpnController.start(context)
    }
}
