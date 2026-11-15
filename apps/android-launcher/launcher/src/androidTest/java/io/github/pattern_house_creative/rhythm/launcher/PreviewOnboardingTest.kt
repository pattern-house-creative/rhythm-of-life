package io.github.pattern_house_creative.rhythm.launcher

import android.graphics.Bitmap
import android.view.WindowManager
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.*
import org.junit.Assert.*

class PreviewOnboardingTest {
    @get:Rule val rule=createAndroidComposeRule<MainActivity>()
    private fun onMain(action:(MainActivity)->Unit) {
        InstrumentationRegistry.getInstrumentation().runOnMainSync {
            action(androidx.test.runner.lifecycle.ActivityLifecycleMonitorRegistry.getInstance().getActivitiesInStage(androidx.test.runner.lifecycle.Stage.RESUMED).filterIsInstance<MainActivity>().single())
        }
    }
    private fun views(v:android.view.View):List<android.webkit.WebView> = when(v) {
        is android.webkit.WebView -> listOf(v)
        is android.view.ViewGroup -> (0 until v.childCount).flatMap{views(v.getChildAt(it))}
        else -> emptyList()
    }
    private fun drawnPreviews():Int {
        val count=java.util.concurrent.atomic.AtomicInteger(0)
        onMain { a -> views(a.window.decorView).forEach { v -> v.evaluateJavascript("Boolean(document.querySelector('[data-preview-ready=true],.launcher-authored.drawn'))") { if(it=="true")count.incrementAndGet() } } }
        Thread.sleep(100);return count.get()
    }
    private fun capture(name:String) {
        onMain { assertTrue(it.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE==0);assertTrue(it.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0) }
        try {
            Thread.sleep(350)
            rule.mainClock.advanceTimeBy(32)
            onMain { it.window.decorView.invalidate() }
            Thread.sleep(150)
            val c=InstrumentationRegistry.getInstrumentation().targetContext
            val values=android.content.ContentValues().apply { put(android.provider.MediaStore.Images.Media.DISPLAY_NAME,name);put(android.provider.MediaStore.Images.Media.MIME_TYPE,"image/png");put(android.provider.MediaStore.Images.Media.RELATIVE_PATH,"Pictures/RhythmOnboarding") }
            val uri=c.contentResolver.insert(android.provider.MediaStore.Images.Media.EXTERNAL_CONTENT_URI,values)!!
            c.contentResolver.openOutputStream(uri)!!.use { InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot().compress(Bitmap.CompressFormat.PNG,100,it) }
        } finally { onMain { assertTrue(it.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0) } }
    }
    @Test fun existingAppsHomeGetsVisiblePreviewsOnConnection() {
        val c=InstrumentationRegistry.getInstrumentation().targetContext
        Assume.assumeTrue(c.packageName.endsWith(".fixture"))
        assertTrue(android.os.Build.FINGERPRINT.contains("generic")||android.os.Build.MODEL.contains("sdk"))
        val store=LauncherStore(c);assertTrue(store.origin.isEmpty())
        rule.waitUntil(10000){store.load().tiles.count{it.kind==TileKind.APP}>=4}
        val old=store.load();val still=store.still
        val prefs=c.getSharedPreferences("phone-home-v1",android.content.Context.MODE_PRIVATE)
        val marker=prefs.getString("preview-setup-origin",null)
        try {
            prefs.edit().remove("preview-setup-origin").apply()
            val apps=old.tiles.filter{it.kind==TileKind.APP}
            store.save(old.copy(tiles=apps));store.still=true
            rule.activityRule.scenario.recreate()
            rule.onNodeWithContentDescription("Settings").performClick()
            rule.onNode(hasSetTextAction()).performTextReplacement("https://10.0.2.2:9443")
            rule.onNodeWithText("Save").performClick()
            rule.waitUntil(20000){store.load().tiles.count{it.kind==TileKind.PREVIEW}==2}
            rule.mainClock.autoAdvance=false
            rule.waitUntil(25000){rule.mainClock.advanceTimeBy(100);drawnPreviews()==2}
            capture(InstrumentationRegistry.getArguments().getString("captureName")?:"v018-connected-home.png")
            assertEquals("Connection should add two actual preview tiles to an apps-only Home",2,store.load().tiles.count{it.kind==TileKind.PREVIEW})
            assertEquals(apps,store.load().tiles.filter{it.kind==TileKind.APP})
            assertEquals(old.dock,store.load().dock)
            val preview=store.load().tiles.first{it.kind==TileKind.PREVIEW}
            rule.mainClock.autoAdvance=true
            rule.onNodeWithTag("tile:${preview.key}").assertIsDisplayed()
            rule.onNodeWithTag("tile:${store.load().tiles.filter{it.kind==TileKind.PREVIEW}[1].key}").assertIsDisplayed()
            rule.onNodeWithTag("tile:${preview.key}").performTouchInput { longClick(center) }
            capture("v018-longhold-debug.png")
            rule.onNodeWithText("▦  Show as icon").assertExists()
            rule.onNodeWithText("Done").performClick()
            val density=InstrumentationRegistry.getInstrumentation().targetContext.resources.displayMetrics.density
            val searchBounds=rule.onNodeWithTag("home-grid").fetchSemanticsNode().boundsInRoot
            val previewBounds=rule.onNodeWithTag("tile:${preview.key}").fetchSemanticsNode().boundsInRoot
            val dockBounds=rule.onNodeWithTag("home-dock").fetchSemanticsNode().boundsInRoot
            rule.onNodeWithTag("add-preview").assertDoesNotExist()
            assertEquals(168f,previewBounds.height/density,1f)
            assertEquals(56f,dockBounds.height/density,1f)
            android.util.Log.i("RhythmGeometry",org.json.JSONObject().put("searchTopDp",searchBounds.top/density)
                .put("searchHeightDp",searchBounds.height/density).put("previewTopDp",previewBounds.top/density)
                .put("previewWidthDp",previewBounds.width/density).put("previewHeightDp",previewBounds.height/density)
                .put("dockTopDp",dockBounds.top/density).put("dockHeightDp",dockBounds.height/density).toString())
            val beforeDrag=store.load()
            val source=rule.onNodeWithTag("tile:${preview.key}")
            val other=beforeDrag.tiles.first { it.kind==TileKind.PREVIEW && it.key!=preview.key }
            val target=rule.onNodeWithTag("tile:${other.key}")
            val delta=target.fetchSemanticsNode().boundsInRoot.center-source.fetchSemanticsNode().boundsInRoot.center
            source.performTouchInput { down(center);advanceEventTime(700);moveBy(delta);cancel() }
            rule.waitForIdle();assertEquals(beforeDrag,store.load())
            rule.onNodeWithTag("tile:${preview.key}").performTouchInput { longClick(center) }
            rule.onNodeWithText("▦  Show as icon").performClick()
            assertEquals(TileKind.DASHBOARD,store.load().tiles.first{it.key==preview.key}.kind)
            rule.onNodeWithTag("swipe-search").performClick()
            rule.onNodeWithTag("add-preview").performClick()
            rule.onNodeWithTag("preview-choice:${preview.target}").performClick()
            assertEquals(beforeDrag,store.load())
            // A short swipe over preview content scrolls; it must not open or reorder.
            rule.onNodeWithText("More sky").performClick();rule.waitForIdle()
            val expandedSearch=rule.onNodeWithTag("home-grid").fetchSemanticsNode().boundsInRoot
            android.util.Log.i("RhythmGeometry",org.json.JSONObject().put("expandedSearchTopDp",expandedSearch.top/density)
                .put("skyRevealDp",(expandedSearch.top-searchBounds.top)/density).toString())
            val expandedDock=rule.onNodeWithTag("home-dock").fetchSemanticsNode().boundsInRoot
            assertEquals(dockBounds.top,expandedDock.top,.5f)
            rule.onNodeWithTag("home-grid").performTouchInput { swipeUp() };rule.waitForIdle()
            rule.onNodeWithTag("full-household").assertDoesNotExist()
            assertEquals(beforeDrag,store.load())
            rule.onNodeWithText("Less sky").performClick();rule.waitForIdle()
            // Removing previews is intentional and must survive later connections.
            store.save(store.load().copy(tiles=apps));onMain{it.recreate()}
            Thread.sleep(800);rule.waitForIdle()
            assertTrue(store.load().tiles.none{it.kind==TileKind.PREVIEW})
            rule.onNodeWithTag("swipe-search").performClick()
            rule.onNodeWithTag("add-preview").performClick()
            rule.waitUntil(15000){rule.onAllNodesWithTag("preview-choice:${preview.target}").fetchSemanticsNodes().isNotEmpty()}
            rule.onNodeWithTag("preview-choice:${preview.target}").performClick()
            rule.waitUntil(10000){store.load().tiles.any{it.kind==TileKind.PREVIEW}}
            assertEquals(1,store.load().tiles.count{it.kind==TileKind.PREVIEW})
            onMain{it.recreate()};rule.waitForIdle()
            assertEquals(1,store.load().tiles.count{it.kind==TileKind.PREVIEW})
        } finally {
            store.origin="";store.still=still;store.save(old)
            if(marker==null)prefs.edit().remove("preview-setup-origin").apply() else prefs.edit().putString("preview-setup-origin",marker).apply()
        }
    }
}
