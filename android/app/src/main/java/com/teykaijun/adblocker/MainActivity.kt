package com.teykaijun.adblocker

import android.Manifest
import android.content.ActivityNotFoundException
import android.content.pm.PackageManager
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import androidx.core.content.edit
import com.teykaijun.adblocker.ui.AdBlockerUi
import com.teykaijun.adblocker.ui.theme.AdBlockerTheme
import com.teykaijun.adblocker.vpn.VpnController

class MainActivity : ComponentActivity() {

    private val vpnConsent = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if (result.resultCode == RESULT_OK) {
            startProtection()
        } else {
            VpnController.reportFailure("AdBlocker needs Android's VPN permission to filter lookups. It only carries DNS, on this phone.")
        }
    }

    private val notificationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) {
        requestProtection()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        setContent {
            AdBlockerTheme {
                AdBlockerUi(
                    onStartProtection = ::requestProtection,
                    onStopProtection = { VpnController.stop(this) },
                )
            }
        }
    }

    /** Asks for the notification permission once, then for VPN consent if needed, then starts. */
    private fun requestProtection() {
        val prefs = getSharedPreferences("ui", MODE_PRIVATE)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            !prefs.getBoolean(KEY_ASKED_NOTIFICATIONS, false) &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            prefs.edit { putBoolean(KEY_ASKED_NOTIFICATIONS, true) }
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
            return
        }

        val consent = VpnService.prepare(this)
        if (consent == null) {
            startProtection()
            return
        }
        try {
            vpnConsent.launch(consent)
        } catch (e: ActivityNotFoundException) {
            VpnController.reportFailure("This device does not allow VPN apps.")
        }
    }

    private fun startProtection() {
        if (!VpnController.start(this)) VpnController.reportFailure("Android did not let AdBlocker start. Please try again.")
    }

    private companion object {
        const val KEY_ASKED_NOTIFICATIONS = "asked_notifications"
    }
}
