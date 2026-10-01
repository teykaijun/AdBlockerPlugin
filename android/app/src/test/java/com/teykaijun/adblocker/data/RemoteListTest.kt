package com.teykaijun.adblocker.data

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class RemoteListTest {
    private val presets = RemoteList.parsePresets(File("../../filters/community-lists.json").readText())

    @Test
    fun `reads the presets shared with the Windows app`() {
        assertEquals(
            listOf("hagezi-normal", "hagezi-popupads", "hagezi-fake", "hagezi-tif-mini", "hagezi-light", "adguard-dns", "stevenblack"),
            presets.map { it.id },
        )
        assertEquals(setOf("hagezi-normal", "hagezi-popupads", "hagezi-fake"), presets.filter { it.enabled }.map { it.id }.toSet())
        assertEquals(setOf("hagezi-fake", "hagezi-tif-mini"), presets.filter { it.scam }.map { it.id }.toSet())
    }

    @Test
    fun `every preset is complete and downloads over https`() {
        for (list in presets) {
            assertTrue(list.id, list.id.matches(Regex("[a-z0-9-]+")))
            assertTrue(list.id, list.title.isNotBlank() && list.description.isNotBlank())
            assertTrue(list.id, list.url.startsWith("https://"))
            assertTrue(list.id, !list.custom)
        }
        assertEquals("ids are unique", presets.size, presets.map { it.id }.toSet().size)
    }
}
