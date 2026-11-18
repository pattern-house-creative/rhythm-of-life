package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.semantics.SemanticsActions
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.*
import org.junit.Assert.*

class CellInteractionTest {
    @get:Rule val rule=createAndroidComposeRule<MainActivity>()
    private val context get()=InstrumentationRegistry.getInstrumentation().targetContext
    private lateinit var store:LauncherStore
    private lateinit var before:HomeLayout
    private var still=false
    @Before fun prepare() {
        assertTrue(android.os.Build.MODEL.contains("sdk"))
        store=LauncherStore(context)
        rule.waitUntil(15000){store.load().tiles.count{it.kind==TileKind.APP}>=2}
        before=store.load();still=store.still
    }
    @After fun restore(){if(::before.isInitialized){store.save(before);store.still=still}}
    private fun installedApps()=context.packageManager.queryIntentActivities(android.content.Intent(android.content.Intent.ACTION_MAIN).addCategory(android.content.Intent.CATEGORY_LAUNCHER),0)
        .filter{!it.activityInfo.packageName.startsWith("io.github.pattern_house_creative.rhythm.launcher")}
        .sortedBy{it.loadLabel(context.packageManager).toString()}
        .map{Tile(TileKind.APP,android.content.ComponentName(it.activityInfo.packageName,it.activityInfo.name).flattenToString())}.distinctBy{it.key}
    private fun imeVisible()=androidx.core.view.ViewCompat.getRootWindowInsets(rule.activity.window.decorView)?.isVisible(androidx.core.view.WindowInsetsCompat.Type.ime())==true
    private fun seed(layout:HomeLayout){store.save(layout);store.still=false;rule.activityRule.scenario.recreate();rule.waitForIdle();rule.waitUntil(5000){!imeVisible()}}
    private fun openDrawer() {
        rule.waitUntil(5000){!imeVisible()}
        Thread.sleep(250) // IME's real-time exit can outlive its visibility flag.
        rule.onNodeWithTag("swipe-search").performClick()
        rule.waitUntil(3000){rule.onAllNodesWithTag("search-input").fetchSemanticsNodes().isNotEmpty()}
    }
    private fun screenshot(name:String) {
        Thread.sleep(300)
        rule.mainClock.advanceTimeBy(32)
        InstrumentationRegistry.getInstrumentation().runOnMainSync { rule.activity.window.decorView.invalidate() }
        Thread.sleep(100)
        val values=android.content.ContentValues().apply {
            val phase=InstrumentationRegistry.getArguments().getString("capturePrefix") ?: "v0110"
            put(android.provider.MediaStore.Images.Media.DISPLAY_NAME,"$phase-$name.png")
            put(android.provider.MediaStore.Images.Media.MIME_TYPE,"image/png")
            put(android.provider.MediaStore.Images.Media.RELATIVE_PATH,"Pictures/RhythmCells")
        }
        val uri=context.contentResolver.insert(android.provider.MediaStore.Images.Media.EXTERNAL_CONTENT_URI,values)!!
        context.contentResolver.openOutputStream(uri)!!.use{InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot().compress(android.graphics.Bitmap.CompressFormat.PNG,100,it)}
    }
    @Test fun previewAndFourIconsShareTwoRowsAndSixDockTargetsRemainAccessible() {
        val apps=installedApps()
        val pins=listOf(Tile(TileKind.PREVIEW,"synthetic-garden"),Tile(TileKind.PREVIEW,"synthetic-reading"),Tile(TileKind.PREVIEW,"synthetic-training"))+apps.take(4)
        val seeded=HomeLayout(pins,apps.take(6));seed(seeded)
        val preview=rule.onNodeWithTag("tile:"+pins[2].key).fetchSemanticsNode().boundsInRoot
        val rects=apps.take(4).map{rule.onNodeWithTag("tile:"+it.key).fetchSemanticsNode().boundsInRoot}
        assertEquals(rects[0].top,rects[1].top,.5f);assertEquals(rects[2].top,rects[3].top,.5f)
        assertEquals(preview.top,rects[0].top,.5f);assertEquals(preview.bottom,rects[3].bottom,.5f)
        assertTrue(rects.all{it.left>preview.right});assertTrue(rects[2].top>rects[0].bottom)
        repeat(6){rule.onNodeWithTag("dock-slot:$it").assertHasClickAction().assertIsDisplayed()}
        rule.onNodeWithText("Open dashboard").assertDoesNotExist()
        rule.onNodeWithText("Hold a tile to move",substring=true).assertDoesNotExist()
        rule.onNodeWithTag("add-preview").assertDoesNotExist()
        screenshot("mixed-cells")
        val actions=rule.onNodeWithTag("tile:"+pins[2].key).fetchSemanticsNode().config[SemanticsActions.CustomActions]
        assertTrue(actions.any{it.label.startsWith("Edit")})
        assertEquals(seeded,store.load())
        val dockBefore=rule.onNodeWithTag("home-dock").fetchSemanticsNode().boundsInRoot
        openDrawer();rule.onNodeWithTag("add-preview").assertIsDisplayed()
        rule.waitUntil(5000){imeVisible()}
        Thread.sleep(350);rule.waitForIdle()
        assertEquals("IME must not move the dock",dockBefore,rule.onNodeWithTag("home-dock").fetchSemanticsNode().boundsInRoot)
        screenshot("drawer")
        rule.onNodeWithTag("drawer-handle").performClick()
        assertEquals(seeded,store.load())
    }
    @Test fun drawerDragCanCancelThenPlaceOnHomeAndIntoFullDockWithoutLosingPins() {
        val apps=installedApps()
        val picked=apps.last()
        val seeded=HomeLayout(apps.filterNot{it.key==picked.key}.take(2),apps.filterNot{it.key==picked.key}.take(6))
        seed(seeded)
        fun lift() {
            openDrawer()
            val label=context.packageManager.getActivityInfo(android.content.ComponentName.unflattenFromString(picked.target)!!,0).loadLabel(context.packageManager).toString()
            rule.onNodeWithTag("search-input").performTextReplacement(label)
            rule.onNodeWithTag("tile:"+picked.key).assertIsDisplayed()
            val source=rule.onNodeWithTag("tile:"+picked.key).fetchSemanticsNode().boundsInRoot.center
            val root=rule.onNodeWithTag("launcher-root")
            root.performTouchInput{down(source);advanceEventTime(700);moveTo(source+Offset(0f,55f))}
            rule.waitForIdle();rule.onNodeWithTag("drag-lifted").assertExists()
        }
        lift()
        rule.onNodeWithTag("launcher-root").performTouchInput{cancel()}
        rule.waitForIdle();assertEquals(seeded,store.load())
        rule.onNodeWithTag("search-input").assertExists()
        rule.onNodeWithTag("drawer-handle").performClick()
        lift()
        val end=rule.onNodeWithTag("home-grid").fetchSemanticsNode().boundsInRoot.center
        rule.onNodeWithTag("launcher-root").performTouchInput{moveTo(end)}
        rule.waitForIdle();screenshot("drawer-home-target")
        rule.onNodeWithTag("launcher-root").performTouchInput{up()}
        rule.waitForIdle();assertTrue(store.load().tiles.any{it.key==picked.key})
        val afterHome=store.load()
        lift()
        val dock=rule.onNodeWithTag("dock-slot:2").fetchSemanticsNode().boundsInRoot.center
        rule.onNodeWithTag("launcher-root").performTouchInput{moveTo(dock)}
        rule.waitForIdle();screenshot("drawer-dock-target")
        rule.onNodeWithTag("launcher-root").performTouchInput{up()}
        rule.waitForIdle()
        assertEquals(picked,store.load().dock[2])
        assertTrue((store.load().tiles+store.load().dock).containsAll(afterHome.tiles+afterHome.dock))
        assertEquals(6,store.load().dock.size)
        // Existing dock item reorders directly; cancellation leaves byte-equivalent model.
        val ordered=store.load()
        val a=rule.onNodeWithTag("dock-slot:2").fetchSemanticsNode().boundsInRoot.center
        val b=rule.onNodeWithTag("dock-slot:0").fetchSemanticsNode().boundsInRoot.center
        rule.onNodeWithTag("launcher-root").performTouchInput{down(a);advanceEventTime(700);moveTo(b);up()}
        rule.waitForIdle();assertEquals(picked,store.load().dock.first());assertEquals(ordered.tiles,store.load().tiles)
        val saved=store.load()
        rule.activityRule.scenario.recreate();assertEquals(saved,store.load())
        val remove=rule.onNodeWithTag("dock-slot:0").fetchSemanticsNode().config[SemanticsActions.CustomActions].first{it.label=="Remove from dock"}
        rule.runOnIdle { assertTrue(remove.action()) }
        assertEquals(saved.tiles,store.load().tiles);assertFalse(store.load().dock.contains(picked))
    }
    @Test fun emptySpaceHoldOpensPreviewPickerAndReducedMotionDrawerSurvivesRepeatedBack() {
        seed(HomeLayout(emptyList(),before.dock))
        rule.onNodeWithTag("home-grid").performTouchInput{longClick(center)}
        rule.onNodeWithText("Dashboard previews").assertExists()
        rule.onNodeWithText("Done").performClick()
        store.still=true;rule.activityRule.scenario.recreate()
        repeat(4) {
            openDrawer()
            rule.onNodeWithTag("search-input").performTextInput("Clock")
            rule.activityRule.scenario.onActivity{it.onBackPressedDispatcher.onBackPressed()}
            rule.onNodeWithTag("search-input").assertDoesNotExist()
        }
        assertTrue(store.load().tiles.isEmpty())
    }
    @Test fun drawerAnimationCanReverseMidFlightWithoutChangingLayout() {
        seed(before)
        val saved=store.load()
        // Drive state via semantic actions while pausing Compose time. Android IME
        // moves real screen coordinates independently; raw touch is covered above.
        rule.mainClock.autoAdvance=false
        try {
            repeat(3) {
                rule.onNodeWithTag("swipe-search").performSemanticsAction(SemanticsActions.OnClick){it()}
                rule.mainClock.advanceTimeBy(64)
                val first=rule.onNodeWithTag("app-drawer").fetchSemanticsNode().boundsInRoot.top
                if(it==0)screenshot("drawer-entering")
                rule.mainClock.advanceTimeBy(96)
                val second=rule.onNodeWithTag("app-drawer").fetchSemanticsNode().boundsInRoot.top
                assertTrue("Drawer should glide toward its resting position",second<=first)
                rule.activityRule.scenario.onActivity{it.onBackPressedDispatcher.onBackPressed()}
                rule.mainClock.advanceTimeBy(64)
                val grip=rule.onNodeWithTag("drawer-handle").fetchSemanticsNode().boundsInRoot.center
                rule.onNodeWithTag("launcher-root").performTouchInput{swipe(grip,grip+Offset(0f,-700f),180)}
                rule.mainClock.advanceTimeBy(300)
                // Android's IME animation is independent of Compose's paused clock.
                try { rule.waitUntil(3000) { rule.mainClock.advanceTimeBy(16);rule.onNodeWithTag("search-input").isDisplayed() } }
                catch(e:Exception) {
                    screenshot("drawer-interruption-failure")
                    android.util.Log.e("RhythmDrawer",rule.onRoot().printToString())
                    throw e
                }
                rule.onNodeWithTag("search-input").assertIsDisplayed()
                rule.onNodeWithTag("drawer-handle").performSemanticsAction(SemanticsActions.OnClick){it()}
                rule.mainClock.advanceTimeBy(300)
                rule.onNodeWithTag("search-input").assertDoesNotExist()
            }
            assertEquals(saved,store.load())
        } finally { rule.mainClock.autoAdvance=true }
    }
}
