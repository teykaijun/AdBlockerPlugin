package com.teykaijun.adblocker.vpn

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import com.teykaijun.adblocker.MainActivity
import com.teykaijun.adblocker.R
import com.teykaijun.adblocker.data.Totals
import java.text.NumberFormat

object Notifications {
    const val PROTECTION_ID = 1
    private const val PROBLEM_ID = 2
    private const val CHANNEL_STATUS = "status"
    private const val CHANNEL_PROBLEMS = "problems"

    fun createChannels(context: Context) {
        val status = NotificationChannel(
            CHANNEL_STATUS,
            context.getString(R.string.channel_status),
            NotificationManager.IMPORTANCE_LOW,
        ).apply {
            description = context.getString(R.string.channel_status_description)
            setShowBadge(false)
        }
        val problems = NotificationChannel(
            CHANNEL_PROBLEMS,
            context.getString(R.string.channel_problems),
            NotificationManager.IMPORTANCE_DEFAULT,
        ).apply {
            description = context.getString(R.string.channel_problems_description)
        }
        manager(context).createNotificationChannels(listOf(status, problems))
    }

    /** The ongoing notification shown while the VPN runs. */
    fun protection(context: Context, totals: Totals): Notification {
        val stop = PendingIntent.getService(
            context,
            1,
            AdBlockVpnService.intent(context, AdBlockVpnService.ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        return NotificationCompat.Builder(context, CHANNEL_STATUS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Blocking ads and trackers")
            .setContentText(blockedText(totals.blockedToday))
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setSilent(true)
            .setShowWhen(false)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .setContentIntent(openAppIntent(context))
            .addAction(0, "Pause", stop)
            .build()
    }

    fun updateProtection(context: Context, totals: Totals) = notify(context, PROTECTION_ID, protection(context, totals))

    fun showProblem(context: Context, message: String) {
        val notification = NotificationCompat.Builder(context, CHANNEL_PROBLEMS)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("AdBlocker stopped")
            .setContentText(message)
            .setStyle(NotificationCompat.BigTextStyle().bigText(message))
            .setAutoCancel(true)
            .setContentIntent(openAppIntent(context))
            .build()
        notify(context, PROBLEM_ID, notification)
    }

    fun cancelProblem(context: Context) = manager(context).cancel(PROBLEM_ID)

    fun openAppIntent(context: Context): PendingIntent = PendingIntent.getActivity(
        context,
        0,
        Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    private fun notify(context: Context, id: Int, notification: Notification) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            return
        }
        manager(context).notify(id, notification)
    }

    private fun manager(context: Context) = context.getSystemService(NotificationManager::class.java)

    private fun blockedText(count: Long) = when (count) {
        0L -> "Nothing blocked yet today"
        1L -> "1 request blocked today"
        else -> "${NumberFormat.getIntegerInstance().format(count)} requests blocked today"
    }
}
