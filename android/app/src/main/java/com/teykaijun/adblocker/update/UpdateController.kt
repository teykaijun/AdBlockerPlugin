package com.teykaijun.adblocker.update

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import androidx.core.content.edit
import com.teykaijun.adblocker.BuildConfig
import com.teykaijun.adblocker.app
import java.io.File
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

sealed interface UpdateState {
    data object Idle : UpdateState
    data object Checking : UpdateState
    data class UpToDate(val version: String) : UpdateState
    data class Available(val release: Release) : UpdateState
    data class Downloading(val percent: Int) : UpdateState

    /** The download is handed to Android, which hasn't asked the user yet. */
    data object Installing : UpdateState

    /** Android asked the user to confirm the install; its screen can be opened again. */
    data object Confirming : UpdateState

    /** @property canRetry false when trying again can't help, such as a copy signed with another key. */
    data class Failed(val message: String, val canRetry: Boolean = true) : UpdateState
}

/** Drives the update check and install for the UI. */
object UpdateController {
    private val stateFlow = MutableStateFlow<UpdateState>(UpdateState.Idle)
    val state: StateFlow<UpdateState> = stateFlow.asStateFlow()

    private var found: Release? = null

    /** Android's install screen, kept in case it couldn't open while AdBlocker was in the background. */
    @Volatile
    private var confirmIntent: Intent? = null

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
                showFound(release)
            } catch (e: Exception) {
                if (!quiet) showUnlessBusy(UpdateState.Failed(Updates.describeFailure(e, "Could not reach GitHub.")))
            }
        }
    }

    /**
     * Shows what a check found. A quiet check can still be running when the user starts an
     * install from an earlier one, and the install keeps the screen then.
     */
    internal fun showFound(release: Release?) {
        found = release
        showUnlessBusy(release?.let { UpdateState.Available(it) } ?: UpdateState.UpToDate(BuildConfig.VERSION_NAME))
    }

    private fun showUnlessBusy(state: UpdateState) = stateFlow.update { current -> if (current.isBusy()) current else state }

    /** Downloads the release and asks Android to install it. */
    fun downloadAndInstall(context: Context) {
        val release = found ?: return
        if (busy()) return
        val app = context.app
        // Never open the install screen of an earlier try for this one.
        confirmIntent = null
        stateFlow.value = UpdateState.Downloading(0)
        app.scope.launch(Dispatchers.IO) {
            try {
                val apk = Updates.download(release, File(app.cacheDir, "updates")) { percent ->
                    stateFlow.value = UpdateState.Downloading(percent)
                }
                stateFlow.value = UpdateState.Installing
                ApkInstaller.install(app, apk)
            } catch (e: Exception) {
                stateFlow.value = UpdateState.Failed(Updates.describeFailure(e, "The download failed."))
            }
        }
    }

    /**
     * Opens Android's install screen again. Android doesn't open it while AdBlocker is in the
     * background, and nothing reports back if the user leaves it without answering.
     */
    fun confirmInstall(context: Context) {
        val intent = confirmIntent ?: return installCancelled()
        try {
            context.startActivity(Intent(intent).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        } catch (e: ActivityNotFoundException) {
            installFailed("Android did not offer the install screen.")
        }
    }

    internal fun awaitConfirmation(intent: Intent) {
        confirmIntent = intent
        stateFlow.value = UpdateState.Confirming
    }

    internal fun installFinished() {
        confirmIntent = null
        stateFlow.value = UpdateState.UpToDate(found?.version ?: BuildConfig.VERSION_NAME)
    }

    internal fun installCancelled() {
        confirmIntent = null
        stateFlow.value = found?.let { UpdateState.Available(it) } ?: UpdateState.Idle
    }

    internal fun installFailed(message: String?, canRetry: Boolean = true) {
        confirmIntent = null
        stateFlow.value = UpdateState.Failed(message ?: "Android refused the update.", canRetry)
    }

    private fun busy() = stateFlow.value.isBusy()

    private fun UpdateState.isBusy() =
        this is UpdateState.Downloading || this is UpdateState.Installing || this is UpdateState.Confirming

    private const val PREFS = "updates"
    private const val KEY_LAST_CHECK = "last_check"
    private val CHECK_INTERVAL_MS = TimeUnit.DAYS.toMillis(1)
}
