package com.teykaijun.adblocker.dns

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class UpstreamChooserTest {
    private val servers = listOf("A", "B")
    private val chooser = UpstreamChooser<String>()

    @Test
    fun `stays with the first server while it answers`() {
        repeat(3) { assertEquals("A", chooser.pick(servers)) }
    }

    @Test
    fun `a burst of timeouts from a dead server moves on once and stays there`() {
        assertEquals("A", chooser.pick(servers))
        // Every lookup sent to A in the last few seconds times out, one after another.
        repeat(7) { chooser.failed("A", servers) }
        assertEquals("B", chooser.pick(servers))
        repeat(3) { chooser.failed("A", servers) }
        assertEquals("B", chooser.pick(servers))
    }

    @Test
    fun `moves on again when the next server fails too`() {
        chooser.failed("A", servers)
        chooser.failed("B", servers)
        assertEquals("A", chooser.pick(servers))
    }

    @Test
    fun `copes with the list of servers changing`() {
        chooser.failed("A", servers)
        assertEquals("C", chooser.pick(listOf("C")))
        chooser.failed("C", listOf("C"))
        assertEquals("C", chooser.pick(listOf("C")))
        assertNull(chooser.pick(emptyList()))
        chooser.failed("A", emptyList())
    }
}
