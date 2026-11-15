package io.github.pattern_house_creative.rhythm.launcher

import android.view.WindowManager
import android.graphics.Bitmap
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Rule
import org.junit.Test
import org.junit.Assert.*

// Instrumentation APK only. Never included in the deliverable APK. Capture requires
// an unconnected, disposable emulator and checks screenshots are allowed without changing window flags.
class VisualCaptureTest {
    @get:Rule val rule = createAndroidComposeRule<MainActivity>()
    @Test fun captureMixedOfflineLayout() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        assertTrue(android.os.Build.FINGERPRINT.contains("generic") || android.os.Build.MODEL.contains("sdk"))
        val store = LauncherStore(context)
        assertEquals("", store.origin)
        rule.waitUntil(10000) { store.load().tiles.size >= 4 }
        val previous = store.load()
        try {
            store.save(previous.copy(tiles = previous.tiles.take(4) + listOf(
                Tile(TileKind.PREVIEW, "synthetic-layout-a"), Tile(TileKind.PREVIEW, "synthetic-layout-b")) + previous.tiles.drop(4)))
            rule.activityRule.scenario.recreate()
            rule.waitForIdle()
            val a = rule.onNodeWithTag("tile:DASHBOARD:synthetic-layout-a").fetchSemanticsNode().boundsInRoot
            val b = rule.onNodeWithTag("tile:DASHBOARD:synthetic-layout-b").fetchSemanticsNode().boundsInRoot
            assertEquals(a.top, b.top)
            assertTrue(a.right <= b.left)
            rule.activityRule.scenario.onActivity { assertTrue(it.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0) }
            Thread.sleep(300)
            val values = android.content.ContentValues().apply {
                put(android.provider.MediaStore.Images.Media.DISPLAY_NAME, "visual-mixed-offline.png")
                put(android.provider.MediaStore.Images.Media.MIME_TYPE, "image/png")
                put(android.provider.MediaStore.Images.Media.RELATIVE_PATH, "Pictures/RhythmVisual")
            }
            val uri = context.contentResolver.insert(android.provider.MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values)!!
            context.contentResolver.openOutputStream(uri)!!.use {
                instrumentation.uiAutomation.takeScreenshot().compress(Bitmap.CompressFormat.PNG, 100, it)
            }
        } finally {
            store.save(previous)
            rule.activityRule.scenario.onActivity { assertTrue(it.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0) }
        }
    }
    @Test fun captureUnconnectedEmulator() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        assertTrue(android.os.Build.FINGERPRINT.contains("generic") || android.os.Build.MODEL.contains("sdk"))
        assertEquals("", LauncherStore(context).origin)
        val store = LauncherStore(context)
        rule.waitUntil(10000) { LauncherStore(context).load().tiles.isNotEmpty() }
        rule.waitForIdle()
        rule.activityRule.scenario.onActivity {
            assertTrue(it.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0)
            assertTrue(it.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0)
        }
        fun capture(name: String) {
            rule.waitForIdle()
            Thread.sleep(300)
            val bitmap = instrumentation.uiAutomation.takeScreenshot()
            assertNotNull(bitmap)
            val values = android.content.ContentValues().apply {
                put(android.provider.MediaStore.Images.Media.DISPLAY_NAME, "visual-$name.png")
                put(android.provider.MediaStore.Images.Media.MIME_TYPE, "image/png")
                put(android.provider.MediaStore.Images.Media.RELATIVE_PATH, "Pictures/RhythmVisual")
            }
            val uri = context.contentResolver.insert(android.provider.MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values)!!
            context.contentResolver.openOutputStream(uri)!!.use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
        }
        try {
            capture("home")
            val firstKey = store.load().tiles.first().key
            val before = rule.onNodeWithTag("tile:$firstKey").fetchSemanticsNode().boundsInRoot.top
            val dockBefore = rule.onNodeWithTag("home-dock").fetchSemanticsNode().boundsInRoot.top
            rule.onNodeWithText("More sky").performClick(); capture("sky")
            assertTrue(rule.onNodeWithTag("tile:$firstKey").fetchSemanticsNode().boundsInRoot.top > before)
            assertEquals(dockBefore, rule.onNodeWithTag("home-dock").fetchSemanticsNode().boundsInRoot.top)
            rule.onNodeWithText("Less sky").performClick()
            rule.onNodeWithTag("swipe-search").performClick(); capture("drawer")
        } finally {
            rule.activityRule.scenario.onActivity {
                assertTrue(it.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0)
                assertTrue(it.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0)
            }
        }
    }
}
