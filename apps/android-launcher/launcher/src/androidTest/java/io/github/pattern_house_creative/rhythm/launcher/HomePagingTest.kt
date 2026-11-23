package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.semantics.getOrNull
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.*
import org.junit.Assert.*

/** Home is a set of fixed-height pages that swipe sideways; nothing on Home scrolls vertically. */
class HomePagingTest {
    @get:Rule val rule=createAndroidComposeRule<MainActivity>()
    private val context get()=InstrumentationRegistry.getInstrumentation().targetContext
    private lateinit var store:LauncherStore
    private lateinit var before:HomeLayout
    private var still=false
    private var origin=""

    @Before fun prepare() {
        assertTrue(android.os.Build.MODEL.contains("sdk"))
        store=LauncherStore(context)
        rule.waitUntil(15000){store.load().tiles.count{it.kind==TileKind.APP}>=2}
        before=store.load();still=store.still;origin=store.origin
    }
    @After fun restore(){if(::before.isInitialized){store.save(before);store.still=still;store.origin=origin}}

    private fun installedApps()=context.packageManager.queryIntentActivities(android.content.Intent(android.content.Intent.ACTION_MAIN).addCategory(android.content.Intent.CATEGORY_LAUNCHER),0)
        .filter{!it.activityInfo.packageName.startsWith("io.github.pattern_house_creative.rhythm.launcher")}
        .map{Tile(TileKind.APP,android.content.ComponentName(it.activityInfo.packageName,it.activityInfo.name).flattenToString())}.distinctBy{it.key}
    /** Synthetic previews (offline, so no web content loads) and apps: more than one page holds. */
    private fun crowded():HomeLayout {
        val apps=installedApps()
        val previews=(0 until 10).map{Tile(TileKind.PREVIEW,"synthetic-page-$it")}
        return HomeLayout(previews.take(2)+apps.take(4)+previews.drop(2)+apps.drop(4).take(8),apps.take(6))
    }
    private fun seed(layout:HomeLayout){store.origin="";store.save(layout);store.still=true;rule.activityRule.scenario.recreate();rule.waitForIdle()}
    private fun waitPaged()=rule.waitUntil(5000){store.load().pageCount>1}
    private fun bounds(tag:String)=rule.onNodeWithTag(tag).fetchSemanticsNode().boundsInRoot
    private fun shown(tag:String)=rule.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty()
    private fun currentPage()=rule.onNodeWithTag("home-grid").fetchSemanticsNode().config.getOrNull(SemanticsProperties.StateDescription)

    @Test fun overflowGoesToAnotherPageWithNoVerticalScrollOrClipping() {
        val seeded=crowded();seed(seeded);waitPaged()
        val paged=store.load()
        // Every pin is kept, in order, and the dock is untouched.
        assertEquals(seeded.tiles,paged.tiles);assertEquals(seeded.dock,paged.dock)
        val grid=rule.onNodeWithTag("home-grid").fetchSemanticsNode()
        assertNull("Home must not scroll vertically",grid.config.getOrNull(SemanticsActions.ScrollBy))
        assertEquals("Home page 1 of ${paged.pageCount}",currentPage())
        rule.onNodeWithTag("home-page-indicator").assertExists()
        val dock=bounds("home-dock");val handle=bounds("swipe-search")
        for(page in 0 until paged.pageCount) {
            paged.page(page).forEach { tile->
                val cell=bounds("tile:${tile.key}")
                assertTrue("${tile.key} lies inside Home",cell.top>=grid.boundsInRoot.top-.5f && cell.bottom<=grid.boundsInRoot.bottom+.5f)
                assertTrue("${tile.key} clears the search handle and the dock",cell.bottom<=handle.top && cell.bottom<=dock.top)
            }
            // Tiles of other pages are not exposed while this page shows.
            paged.pageLists().filterIndexed{i,_->i!=page}.flatten().forEach{assertFalse(shown("tile:${it.key}"))}
            if(page<paged.pageCount-1)assertTrue(rule.homePageAction("Next Home page"))
        }
    }

    @Test fun swipesTurnPagesWithoutOpeningPreviewsAndUpStillOpensTheDrawer() {
        seed(crowded());waitPaged()
        val paged=store.load()
        val first=paged.page(0).first{it.kind==TileKind.PREVIEW}
        val second=paged.page(1).first()
        // A swipe that starts on a preview turns the page and does not open it.
        val start=bounds("tile:${first.key}").center
        rule.onNodeWithTag("launcher-root").performTouchInput{swipe(start,start-Offset(width*.6f,0f),220)}
        rule.waitForIdle()
        rule.onNodeWithTag("full-household").assertDoesNotExist()
        assertEquals("Home page 2 of ${paged.pageCount}",currentPage())
        assertTrue(shown("tile:${second.key}"));assertFalse(shown("tile:${first.key}"))
        // Right returns to the earlier page.
        val grid=bounds("home-grid")
        rule.onNodeWithTag("launcher-root").performTouchInput{swipe(Offset(grid.left+40f,grid.center.y),Offset(grid.right-40f,grid.center.y),220)}
        rule.waitForIdle()
        assertEquals("Home page 1 of ${paged.pageCount}",currentPage())
        // A short sideways wobble stays on the page.
        rule.onNodeWithTag("launcher-root").performTouchInput{swipe(grid.center,grid.center-Offset(30f,0f),300)}
        rule.waitForIdle();assertEquals("Home page 1 of ${paged.pageCount}",currentPage())
        // No page before the first.
        rule.onNodeWithTag("launcher-root").performTouchInput{swipe(Offset(grid.left+40f,grid.center.y),Offset(grid.right-40f,grid.center.y),220)}
        rule.waitForIdle();assertEquals("Home page 1 of ${paged.pageCount}",currentPage())
        // Swipe up from the grid still opens the drawer.
        rule.onNodeWithTag("launcher-root").performTouchInput{swipe(grid.center,grid.center-Offset(0f,500f),220)}
        rule.waitUntil(3000){shown("search-input")}
        assertEquals(paged,store.load())
    }

    @Test fun pagesSurviveRelaunchAndAnOldSingleListUpgrades() {
        val seeded=crowded()
        // Saved as by an earlier build: one list, no pages.
        context.getSharedPreferences("phone-home-v1",android.content.Context.MODE_PRIVATE).edit().putString("layout",
            org.json.JSONObject().put("tiles",org.json.JSONArray().apply{seeded.tiles.forEach{put(org.json.JSONObject().put("kind",it.kind.name).put("target",it.target))}})
                .put("dock",org.json.JSONArray().apply{seeded.dock.forEach{put(org.json.JSONObject().put("kind",it.kind.name).put("target",it.target))}}).toString()).commit()
        store.origin="";store.still=true;rule.activityRule.scenario.recreate();rule.waitForIdle();waitPaged()
        val upgraded=store.load()
        assertEquals(seeded.tiles,upgraded.tiles);assertEquals(seeded.dock,upgraded.dock)
        // Move a pin to the next page through its accessible action; the arrangement survives a relaunch.
        val moved=upgraded.page(0).first{it.kind==TileKind.APP}
        val action=rule.onNodeWithTag("tile:${moved.key}").fetchSemanticsNode().config[SemanticsActions.CustomActions].first{it.label=="Move to next page"}
        rule.runOnIdle{assertTrue(action.action())}
        rule.waitForIdle()
        val arranged=store.load()
        assertEquals(1,arranged.pageOf(moved.key))
        assertEquals("Home follows the moved pin","Home page 2 of ${arranged.pageCount}",currentPage())
        rule.activityRule.scenario.recreate();rule.waitForIdle()
        assertEquals(arranged,store.load())
        assertEquals(arranged.pageLists(),LauncherStore(context).load().pageLists())
    }

    @Test fun aHeldTileAtTheEdgeMovesToTheNextPage() {
        seed(crowded());waitPaged()
        val paged=store.load()
        val tile=paged.page(0).first{it.kind==TileKind.APP}
        val source=bounds("tile:${tile.key}").center
        val grid=bounds("home-grid")
        val root=rule.onNodeWithTag("launcher-root")
        root.performTouchInput{down(source);advanceEventTime(700);moveTo(source+Offset(0f,40f))}
        rule.waitForIdle();rule.onNodeWithTag("drag-lifted").assertExists()
        root.performTouchInput{moveTo(Offset(grid.right-6f,grid.center.y))}
        rule.waitUntil(4000){currentPage()=="Home page 2 of ${paged.pageCount+1}"}
        root.performTouchInput{moveTo(grid.center)}
        rule.waitForIdle()
        root.performTouchInput{up()}
        rule.waitForIdle()
        val after=store.load()
        assertEquals(1,after.pageOf(tile.key))
        assertEquals(paged.tiles.toSet(),after.tiles.toSet());assertEquals(paged.dock,after.dock)
    }
}
