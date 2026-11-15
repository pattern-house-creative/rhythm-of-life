package io.github.pattern_house_creative.rhythm.launcher

import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.webkit.WebView
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.*
import org.junit.Assert.*
import java.util.concurrent.atomic.AtomicBoolean

// Explicit opt-in, emulator-only, read-only live verification. Never captures pixels,
// logs household IDs/text, disables secure windows, or changes household content.
class LiveCompanionReadTest {
    @get:Rule val rule = createAndroidComposeRule<MainActivity>()
    private fun onMain(action: (MainActivity) -> Unit) {
        InstrumentationRegistry.getInstrumentation().runOnMainSync {
            val a = androidx.test.runner.lifecycle.ActivityLifecycleMonitorRegistry.getInstance()
                .getActivitiesInStage(androidx.test.runner.lifecycle.Stage.RESUMED).filterIsInstance<MainActivity>().single()
            assertTrue(a.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0)
            action(a)
        }
    }
    private fun views(v: View): List<WebView> = when(v) {
        is WebView -> listOf(v)
        is ViewGroup -> (0 until v.childCount).flatMap { views(v.getChildAt(it)) }
        else -> emptyList()
    }
    private fun waitPage(expression: String): Boolean {
        rule.mainClock.autoAdvance = false
        val deadline = android.os.SystemClock.uptimeMillis() + 45000
        while(android.os.SystemClock.uptimeMillis() < deadline) {
            rule.mainClock.advanceTimeBy(100)
            val found=AtomicBoolean(false)
            onMain { a -> views(a.window.decorView).forEach { w ->
                w.evaluateJavascript("Boolean($expression)") { if(it=="true") found.set(true) }
            } }
            Thread.sleep(150)
            if(found.get()) return true
        }
        return false
    }
    private fun back() { onMain { it.onBackPressedDispatcher.onBackPressed() }; rule.mainClock.autoAdvance=true;rule.waitForIdle() }
    @Test fun livePreviewDashboardStarAndBack() {
        val args=InstrumentationRegistry.getArguments()
        val supplied=args.getString("liveOrigin")
        Assume.assumeTrue("Explicit liveOrigin opt-in required", supplied != null)
        val origin=requireNotNull(supplied)
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        assertEquals("io.github.pattern_house_creative.rhythm.launcher",context.packageName)
        assertTrue(android.os.Build.FINGERPRINT.contains("generic") || android.os.Build.MODEL.contains("sdk"))
        assertTrue(origin.startsWith("https://") && householdOrigin(origin)==origin)
        val store=LauncherStore(context);assertTrue(store.origin.isEmpty())
        val previous=store.load();val previousStill=store.still
        val client=HouseholdClient(origin)
        assertTrue("Live version-1 companion",runBlocking { client.companionReady() })
        val household=runBlocking { client.load() }
        val board=household.dashboards.firstOrNull();val star=household.stars.firstOrNull()
        assertNotNull("At least one live dashboard is available",board)
        assertNotNull("At least one readable live star is available",star)
        try {
            store.origin=origin;store.still=true
            store.save(previous.copy(tiles=previous.tiles.take(4)+listOf(Tile(TileKind.PREVIEW,board!!.id),Tile(TileKind.STAR,star!!.id))))
            onMain { it.recreate() }
            assertTrue("Live preview rendered",waitPage("document.querySelector('.dashboard-card-frame.drawn')"))
            android.util.Log.i("RhythmLiveCheck","preview=rendered; secure=true")
            rule.mainClock.autoAdvance=true
            repeat(2) {
                rule.onNodeWithTag("tile:DASHBOARD:${board.id}").performScrollTo().performTouchInput { click(center) }
                assertTrue("Live dashboard rendered",waitPage("document.querySelector('.dashboard-frame.drawn')"))
                back();rule.onNodeWithText("Rhythm OS").assertExists()
            }
            android.util.Log.i("RhythmLiveCheck","dashboard=rendered; repeatedBack=passed")
            rule.onNodeWithTag("tile:STAR:${star.id}").performScrollTo().performTouchInput { click(center) }
            assertTrue("Live source content rendered",waitPage("document.querySelector('.explorer-reader .source-content:not([aria-busy=true])') && !/unavailable|retains citation metadata/i.test(document.querySelector('.explorer-reader .source-content').textContent) && document.querySelector('.explorer-reader .source-content').textContent.trim().length>0"))
            back();rule.onNodeWithText("Rhythm OS").assertExists()
            android.util.Log.i("RhythmLiveCheck","star=content-rendered; back=passed; secure=true")
        } finally {
            store.origin="";store.still=previousStill;store.save(previous)
            onMain { it.recreate() }
        }
    }
}
