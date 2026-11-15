package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Rule
import org.junit.Test
import org.junit.Assert.*

class LauncherTest {
    @get:Rule val rule = createAndroidComposeRule<MainActivity>()
    private fun captureGesture(name:String) {
        val instrumentation=InstrumentationRegistry.getInstrumentation()
        val context=instrumentation.targetContext
        assertTrue(android.os.Build.MODEL.contains("sdk"))
        val values=android.content.ContentValues().apply {
            put(android.provider.MediaStore.Images.Media.DISPLAY_NAME,"v018-$name.png")
            put(android.provider.MediaStore.Images.Media.MIME_TYPE,"image/png")
            put(android.provider.MediaStore.Images.Media.RELATIVE_PATH,"Pictures/RhythmVisual")
        }
        val uri=context.contentResolver.insert(android.provider.MediaStore.Images.Media.EXTERNAL_CONTENT_URI,values)!!
        context.contentResolver.openOutputStream(uri)!!.use {
            instrumentation.uiAutomation.takeScreenshot().compress(android.graphics.Bitmap.CompressFormat.PNG,100,it)
        }
    }
    @Test fun realAndroidBackDismissesKeyboardAndSearchWithoutChangingPins() {
        val instrumentation=InstrumentationRegistry.getInstrumentation()
        val store=LauncherStore(instrumentation.targetContext)
        rule.waitUntil(10000){store.load().tiles.isNotEmpty()}
        val before=store.load()
        repeat(2) {
            rule.onNodeWithTag("swipe-search").performClick()
            rule.onNodeWithTag("search-input").performTextInput("Clock")
            rule.waitUntil(5000){androidx.core.view.ViewCompat.getRootWindowInsets(rule.activity.window.decorView)?.isVisible(androidx.core.view.WindowInsetsCompat.Type.ime())==true}
            instrumentation.sendKeyDownUpSync(android.view.KeyEvent.KEYCODE_BACK)
            rule.waitUntil(5000){androidx.core.view.ViewCompat.getRootWindowInsets(rule.activity.window.decorView)?.isVisible(androidx.core.view.WindowInsetsCompat.Type.ime())!=true}
            if(rule.onAllNodesWithTag("search-input").fetchSemanticsNodes().isNotEmpty())
                instrumentation.sendKeyDownUpSync(android.view.KeyEvent.KEYCODE_BACK)
            rule.waitForIdle();rule.onNodeWithTag("search-input").assertDoesNotExist()
            assertEquals(before,store.load())
        }
    }
    @Test fun stationaryHoldLiftsBeforeReleaseAndEdgeDragScrollsWithoutSavingACancel() {
        val store=LauncherStore(InstrumentationRegistry.getInstrumentation().targetContext)
        rule.waitUntil(10000){store.load().tiles.size>=4}
        val old=store.load()
        val many=old.copy(tiles=old.tiles.take(4)+(0 until 44).map{Tile(TileKind.STAR,"synthetic-drag-$it")})
        try {
            store.save(many);rule.activityRule.scenario.recreate();rule.waitForIdle()
            val first=rule.onNodeWithTag("tile:"+many.tiles.first().key)
            val grid=rule.onNodeWithTag("home-grid")
            val start=first.fetchSemanticsNode().boundsInRoot.center
            val bottom=grid.fetchSemanticsNode().boundsInRoot.bottom-8
            first.performTouchInput{down(center)}
            rule.mainClock.advanceTimeBy(700);Thread.sleep(600)
            rule.onNodeWithText("Release for Home options").assertExists()
            captureGesture("held-icon")
            assertEquals(many,store.load()) // hold itself never saves or reorders
            first.performTouchInput{moveBy(androidx.compose.ui.geometry.Offset(0f,bottom-start.y))}
            repeat(30){rule.mainClock.advanceTimeBy(32);Thread.sleep(16)}
            assertTrue(grid.fetchSemanticsNode().config[androidx.compose.ui.semantics.SemanticsProperties.VerticalScrollAxisRange].value()>0f)
            captureGesture("edge-drag")
            grid.performTouchInput{cancel()}
            rule.waitForIdle();assertEquals(many,store.load())
        } finally {
            store.save(old);rule.activityRule.scenario.recreate()
        }
    }
    @Test fun longPressDragPersistsAndInterruptedDragRollsBack() {
        val store = LauncherStore(InstrumentationRegistry.getInstrumentation().targetContext)
        rule.waitUntil(10000) { store.load().tiles.size >= 2 }
        val before = store.load()
        val a = before.tiles[0]; val b = before.tiles[1]
        val first = rule.onNodeWithTag("tile:${a.key}")
        val second = rule.onNodeWithTag("tile:${b.key}")
        val distance = second.fetchSemanticsNode().boundsInRoot.center - first.fetchSemanticsNode().boundsInRoot.center
        first.performTouchInput { down(center); advanceEventTime(700); moveBy(distance); up() }
        rule.waitForIdle()
        assertEquals(b.key, store.load().tiles.first().key)
        val moved = store.load()
        val start = rule.onNodeWithTag("tile:${b.key}")
        val end = rule.onNodeWithTag("tile:${a.key}")
        val delta = end.fetchSemanticsNode().boundsInRoot.center - start.fetchSemanticsNode().boundsInRoot.center
        start.performTouchInput { down(center); advanceEventTime(700); moveBy(delta); cancel() }
        rule.waitForIdle()
        assertEquals(moved, store.load())
        start.performTouchInput { down(0,center);advanceEventTime(700);down(1,center+androidx.compose.ui.geometry.Offset(20f,20f));up(1);up(0) }
        rule.waitForIdle();assertEquals(moved,store.load())
        rule.onNodeWithTag("tile-actions").assertDoesNotExist()
        rule.activityRule.scenario.recreate()
        assertEquals(moved, store.load())
    }
    @Test fun homeDrawerSettingsAndRepeatedBackSurviveRecreation() {
        rule.onNodeWithText("Rhythm OS").assertExists()
        repeat(3) {
            rule.onNodeWithTag("swipe-search").performClick()
            rule.onNodeWithContentDescription("Close apps").performClick()
            rule.onNodeWithText("More sky").performClick()
            rule.onNodeWithText("Less sky").performClick()
            rule.onNodeWithContentDescription("Settings").performClick()
            rule.onNodeWithText("Your household").assertExists()
            rule.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
        }
        rule.activityRule.scenario.recreate()
        rule.onNodeWithText("Rhythm OS").assertExists()
        rule.onNodeWithTag("home-search").assertDoesNotExist()
        rule.onNodeWithText("Swipe up to search",substring=true).assertExists()
    }
    @Test fun interruptedSkyRevealKeepsDockAndHomeOrder() {
        val store=LauncherStore(InstrumentationRegistry.getInstrumentation().targetContext)
        rule.waitUntil(10000) { store.load().tiles.isNotEmpty() }
        val before=store.load()
        val dock=rule.onNodeWithTag("home-dock").fetchSemanticsNode().boundsInRoot
        rule.mainClock.autoAdvance=false
        try {
            rule.onNodeWithText("More sky").performClick();rule.mainClock.advanceTimeBy(96)
            rule.onNodeWithText("Less sky").performClick();rule.mainClock.advanceTimeBy(96)
            rule.onNodeWithText("More sky").performClick();rule.mainClock.advanceTimeBy(800)
            assertEquals(dock,rule.onNodeWithTag("home-dock").fetchSemanticsNode().boundsInRoot)
            rule.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
            rule.mainClock.advanceTimeBy(800)
            rule.onNodeWithText("More sky").assertExists()
            assertEquals(before,store.load())
            assertEquals(dock,rule.onNodeWithTag("home-dock").fetchSemanticsNode().boundsInRoot)
        } finally { rule.mainClock.autoAdvance=true }
    }
    @Test fun mixedLayoutPersistsWithoutHouseholdPayloads() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val store = LauncherStore(context)
        val previous = store.load()
        try {
            val mixed = HomeLayout(listOf(Tile(TileKind.APP, "example/.Main"),
                Tile(TileKind.PREVIEW, "board"), Tile(TileKind.STAR, "star")))
            store.save(mixed.move("STAR:star", "APP:example/.Main"))
            assertEquals(listOf("star", "example/.Main", "board"), LauncherStore(context).load().tiles.map { it.target })
            assertEquals(2, LauncherStore(context).load().tiles.last().span)
        } finally { store.save(previous) }
    }
}
