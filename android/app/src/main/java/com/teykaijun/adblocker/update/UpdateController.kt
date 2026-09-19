package com.teykaijun.adblocker.update

import android.content.Context
import androidx.core.content.edit
import com.teykaijun.adblocker.BuildConfig
import com.teykaijun.adblocker.app
import java.io.File
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

sealed interface UpdateState {
    data object Idle : UpdateState
    data object Checking : UpdateState
    data class UpToDate(val version: String) : UpdateState
    data class Available(val release: Release) : UpdateState
    data class Downloading(val percent: Int) : UpdateState
    data object Installing : UpdateState
    data class Failed(val message: String) : UpdateState
}

/** Drives the update check and install for the UI. */
object UpdateController {
    private val stateFlow = MutableStateFlow<UpdateState>(UpdateState.Idle)
    val state: StateFlow<UpdateState> = stateFlow.asStateFlow()

    private var found: Release? = null

    /** Looks for a new version at most once a day, and only if automatic checks are on. */
    fun checkIfDue(context: Context) {
        if (!context.app.settings.settings.value.autoCheckUpdates) return
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        if (System.currentTimeMillis() - prefs.getLong(KEY_LAST_CHECK, 0) < CHECK_INTERVAL_MS) return
        check(context, quiet = true)
    }

    fun check(context: Context, quiet: Boolean = false) {
        if (stateFlow.value is UpdateState.Checking || busy()) return
        if (!quiet) stateFlow.value = UpdateState.Checking
        val app = context.app
        app.scope.launch(Dispatchers.IO) {
            try {
                val release = Updates.check(BuildConfig.VERSION_NAME)
                app.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                    .edit { putLong(KEY_LAST_CHECK, System.currentTimeMillis()) }
                found = release
                stateFlow.value = release?.let { UpdateState.Available(it) } ?: UpdateState.UpToDate(BuildConfig.VERSION_NAME)
            } catch (e: Exception) {
                if (!quiet) stateFlow.value = UpdateState.Failed(e.message ?: "Could not reach GitHub.")
            }
        }
    }

    /** Downloads the release and asks Android to install it. */
    fun downloadAndInstall(context: Context) {
        val release = found ?: return
        if (busy()) return
        val app = context.app
        stateFlow.value = UpdateState.Downloading(0)
        app.scope.launch(Dispatchers.IO) {
            try {
                val apk = Updates.download(release, File(app.cacheDir, "updates")) { percent ->
                    stateFlow.value = UpdateState.Downloading(percent)
                }
                stateFlow.value = UpdateState.Installing
                ApkInstaller.install(app, apk)
            } catch (e: Exception) {
                stateFlow.value = UpdateState.Failed(e.message ?: "The download failed.")
            }
        }
    }

    internal fun installFinished() {
        stateFlow.value = UpdateState.UpToDate(found?.version ?: BuildConfig.VERSION_NAME)
    }

    internal fun installCancelled() {
        stateFlow.value = found?.let { UpdateState.Available(it) } ?: UpdateState.Idle
    }

    internal fun installFailed(message: String?) {
        stateFlow.value = UpdateState.Failed(message ?: "Android refused the update.")
    }

    private fun busy() = stateFlow.value is UpdateState.Downloading || stateFlow.value is UpdateState.Installing

    private const val PREFS = "updates"
    private const val KEY_LAST_CHECK = "last_check"
    private val CHECK_INTERVAL_MS = TimeUnit.DAYS.toMillis(1)
}
