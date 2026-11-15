package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.*
import org.junit.Assert.*

class InteractiveDrawerTest {
    @get:Rule val rule=createAndroidComposeRule<MainActivity>()
    private val context get()=InstrumentationRegistry.getInstrumentation().targetContext
    private lateinit var store:LauncherStore
    private lateinit var saved:HomeLayout
    @Before fun prepare(){
        assertTrue(android.os.Build.MODEL.contains("sdk"));store=LauncherStore(context)
        rule.waitUntil(15000){store.load().tiles.isNotEmpty()};saved=store.load()
    }
    @After fun unchanged(){if(::saved.isInitialized)assertEquals(saved,store.load());rule.mainClock.autoAdvance=true}
    private fun open(){rule.onNodeWithTag("swipe-search").performClick();rule.onNodeWithTag("search-input").assertIsDisplayed()}
    private fun root()=rule.onNodeWithTag("launcher-root")
    @Test fun openingTracksFingerBeforeReleaseReversesAndCancellationRollsBack(){
        val start=rule.onNodeWithTag("swipe-search").fetchSemanticsNode().boundsInRoot.center
        rule.mainClock.autoAdvance=false
        root().performTouchInput{down(start);moveTo(start+Offset(0f,-140f),40)}
        rule.mainClock.advanceTimeBy(32)
        val a=rule.onNodeWithTag("app-drawer").fetchSemanticsNode().boundsInRoot.top
        root().performTouchInput{moveTo(start+Offset(0f,-260f),40)};rule.mainClock.advanceTimeBy(32)
        val b=rule.onNodeWithTag("app-drawer").fetchSemanticsNode().boundsInRoot.top
        assertEquals("120px finger travel must move the drawer 120px while still held",120f,a-b,3f)
        root().performTouchInput{moveTo(start+Offset(0f,-190f),40)};rule.mainClock.advanceTimeBy(32)
        val c=rule.onNodeWithTag("app-drawer").fetchSemanticsNode().boundsInRoot.top
        assertEquals("Reversal must track 70px before finger release",70f,c-b,3f)
        root().performTouchInput{cancel()};rule.mainClock.advanceTimeBy(400)
        rule.onNodeWithTag("search-input").assertDoesNotExist()
    }
    @Test fun downwardHandleTracksFingerAndMultiTouchCancellationRestoresOpenDrawer(){
        open()
        val start=rule.onNodeWithTag("drawer-handle").fetchSemanticsNode().boundsInRoot.center
        val initial=rule.onNodeWithTag("app-drawer").fetchSemanticsNode().boundsInRoot.top
        rule.mainClock.autoAdvance=false
        root().performTouchInput{down(start);moveTo(start+Offset(0f,100f),40)};rule.mainClock.advanceTimeBy(32)
        assertEquals(100f,rule.onNodeWithTag("app-drawer").fetchSemanticsNode().boundsInRoot.top-initial,3f)
        root().performTouchInput{down(1,start+Offset(30f,100f));up(1);up(0)}
        rule.mainClock.advanceTimeBy(400)
        assertEquals(initial,rule.onNodeWithTag("app-drawer").fetchSemanticsNode().boundsInRoot.top,3f)
        rule.onNodeWithTag("search-input").assertExists()
        rule.mainClock.autoAdvance=true
        rule.onNodeWithContentDescription("Close apps").performClick()
    }
    @Test fun gridScrollingKeepsOwnershipUntilNextGestureAtTopThenDownwardSwipeDismisses(){
        open()
        val grid=rule.onNodeWithTag("drawer-grid")
        fun imeVisible():Boolean {
            var visible=false
            rule.activityRule.scenario.onActivity { visible=it.window.decorView.rootWindowInsets.isVisible(android.view.WindowInsets.Type.ime()) }
            return visible
        }
        android.util.Log.i("RhythmScrollProbe","Before IME wait: visible=${imeVisible()} bounds=${grid.fetchSemanticsNode().boundsInRoot}")
        // Compose idleness does not wait for the external IME's animated insets.
        // Require the intended overflowing keyboard viewport before testing ownership.
        rule.waitUntil(5000){imeVisible() && grid.fetchSemanticsNode().config[SemanticsProperties.VerticalScrollAxisRange].maxValue()>0f}
        Thread.sleep(300)
        android.util.Log.i("RhythmScrollProbe","After IME wait: visible=${imeVisible()} bounds=${grid.fetchSemanticsNode().boundsInRoot}")
        grid.performTouchInput{swipeUp(durationMillis=220)}
        val value=grid.fetchSemanticsNode().config[SemanticsProperties.VerticalScrollAxisRange].value()
        assertTrue("Fixture app population must overflow the keyboard viewport",value>0f)
        val top=rule.onNodeWithTag("app-drawer").fetchSemanticsNode().boundsInRoot.top
        grid.performTouchInput{swipeDown(durationMillis=220)}
        rule.onNodeWithTag("search-input").assertIsDisplayed()
        assertEquals(top,rule.onNodeWithTag("app-drawer").fetchSemanticsNode().boundsInRoot.top,3f)
        grid.performScrollToIndex(0)
        val handle=rule.onNodeWithTag("drawer-handle").fetchSemanticsNode().boundsInRoot.center
        root().performTouchInput{swipe(handle,handle+Offset(0f,600f),220)}
        rule.onNodeWithTag("search-input").assertDoesNotExist()
    }
    @Test fun lifecycleAndHomeCancelPartialGestureWithoutSavingOrLeavingStaleInput(){
        val start=rule.onNodeWithTag("swipe-search").fetchSemanticsNode().boundsInRoot.center
        root().performTouchInput{down(start);moveTo(start+Offset(0f,-180f),60)}
        rule.activityRule.scenario.moveToState(Lifecycle.State.CREATED)
        rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        root().performTouchInput{cancel()}
        rule.onNodeWithTag("search-input").assertDoesNotExist()
        open()
        rule.activityRule.scenario.onActivity{it.startActivity(android.content.Intent(it,MainActivity::class.java)
            .setAction(android.content.Intent.ACTION_MAIN).addCategory(android.content.Intent.CATEGORY_HOME).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK))}
        rule.onNodeWithTag("search-input").assertDoesNotExist()
        rule.onNodeWithTag("swipe-search").assertIsDisplayed()
    }
}
