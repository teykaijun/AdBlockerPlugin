package com.teykaijun.adblocker.update

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import androidx.core.content.IntentCompat
import java.io.File

/**
 * Hands a downloaded APK to Android's package installer. Android shows its own confirmation
 * and only accepts a build signed with the same key as the installed app.
 */
object ApkInstaller {

    /** Whether the user has allowed AdBlocker to install apps ("Install unknown apps"). */
    fun isAllowed(context: Context): Boolean = context.packageManager.canRequestPackageInstalls()

    fun install(context: Context, apk: File) {
        val installer = context.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(context.packageName)
        }
        val sessionId = installer.createSession(params)
        try {
            installer.openSession(sessionId).use { session ->
                session.openWrite("adblocker", 0, apk.length()).use { output ->
                    apk.inputStream().use { it.copyTo(output) }
                    session.fsync(output)
                }
                val callback = PendingIntent.getBroadcast(
                    context,
                    sessionId,
                    Intent(context, InstallReceiver::class.java).setPackage(context.packageName),
                    PendingIntent.FLAG_MUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
                )
                session.commit(callback.intentSender)
            }
        } catch (e: Exception) {
            // Otherwise the half-written copy stays in the system until it expires.
            runCatching { installer.abandonSession(sessionId) }
            throw e
        }
        // The session holds its own copy now.
        apk.delete()
    }
}

/** Follows an install session: asks the user to confirm, then reports how it went. */
class InstallReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                val confirm = IntentCompat.getParcelableExtra(intent, Intent.EXTRA_INTENT, Intent::class.java)
                if (confirm == null) {
                    UpdateController.installFailed("Android did not offer the install screen.")
                } else {
                    // Android may not open it while AdBlocker is in the background; Settings can open it again.
                    UpdateController.awaitConfirmation(confirm)
                    runCatching { context.startActivity(Intent(confirm).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
                }
            }
            // The app is replaced right after this, so there is nothing left to show.
            PackageInstaller.STATUS_SUCCESS -> UpdateController.installFinished()
            PackageInstaller.STATUS_FAILURE_ABORTED -> UpdateController.installCancelled()
            PackageInstaller.STATUS_FAILURE_CONFLICT -> UpdateController.installFailed(
                "This copy of AdBlocker is signed with a different key, so Android can't update it. " +
                    "Uninstall it, then install AdBlocker.apk from the releases page.",
                canRetry = false,
            )
            else -> UpdateController.installFailed(
                intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: "Android refused the update ($status).",
            )
        }
    }
}
