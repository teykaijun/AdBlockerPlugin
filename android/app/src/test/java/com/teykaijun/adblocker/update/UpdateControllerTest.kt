package com.teykaijun.adblocker.update

import android.content.ContextWrapper
import com.teykaijun.adblocker.BuildConfig
import android.content.Intent
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Test

class UpdateControllerTest {
    @After
    fun reset() {
        UpdateController.installCancelled()
    }

    @Test
    fun `offers the install screen only once Android has asked`() {
        UpdateController.awaitConfirmation(Intent())
        assertEquals(UpdateState.Confirming, UpdateController.state.value)

        // Still busy, so a check doesn't replace "Ready to install". It returns before it uses the context.
        UpdateController.check(ContextWrapper(null))
        assertEquals(UpdateState.Confirming, UpdateController.state.value)

        // When the user cancels on Android's screen, the update is offered again.
        UpdateController.installCancelled()
        assertEquals(UpdateState.Idle, UpdateController.state.value)
    }

    @Test
    fun `a check that ends during an install leaves the install on screen`() {
        // The daily check started before the user tapped Update, and finishes once Android has asked.
        UpdateController.awaitConfirmation(Intent())
        UpdateController.showFound(null)
        assertEquals(UpdateState.Confirming, UpdateController.state.value)

        UpdateController.installCancelled()
        UpdateController.showFound(null)
        assertEquals(UpdateState.UpToDate(BuildConfig.VERSION_NAME), UpdateController.state.value)
    }
}
