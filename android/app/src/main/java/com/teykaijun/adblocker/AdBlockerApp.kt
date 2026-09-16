package com.teykaijun.adblocker

import android.app.Application
import android.content.Context
import com.teykaijun.adblocker.data.FilterRepository
import com.teykaijun.adblocker.data.NetworkMonitor
import com.teykaijun.adblocker.data.SettingsStore
import com.teykaijun.adblocker.data.StatsStore
import com.teykaijun.adblocker.vpn.Notifications
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob

/** Holds the app-wide singletons shared by the UI and the VPN service. */
class AdBlockerApp : Application() {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    lateinit var settings: SettingsStore
        private set
    lateinit var stats: StatsStore
        private set
    lateinit var filters: FilterRepository
        private set
    lateinit var network: NetworkMonitor
        private set

    override fun onCreate() {
        super.onCreate()
        settings = SettingsStore(this)
        stats = StatsStore(this)
        filters = FilterRepository(this, scope)
        network = NetworkMonitor(this)
        Notifications.createChannels(this)
    }
}

val Context.app: AdBlockerApp get() = applicationContext as AdBlockerApp
