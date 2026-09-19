package com.teykaijun.adblocker.vpn

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.widget.Toast
import com.teykaijun.adblocker.app
import com.teykaijun.adblocker.dns.RuleParser

/** The "Allow anyway" button on a scam warning: adds the site to the user's allowed domains. */
class AllowSiteReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val domain = RuleParser.normalizeDomain(intent.getStringExtra(EXTRA_DOMAIN).orEmpty()) ?: return
        context.app.settings.update { it.withDomainRule(domain, allow = true) }
        Notifications.cancelScamWarning(context)
        Toast.makeText(context, "$domain is allowed now. It may take a few seconds.", Toast.LENGTH_LONG).show()
    }

    companion object {
        private const val EXTRA_DOMAIN = "domain"

        fun intent(context: Context, domain: String): Intent =
            Intent(context, AllowSiteReceiver::class.java).putExtra(EXTRA_DOMAIN, domain)
    }
}
