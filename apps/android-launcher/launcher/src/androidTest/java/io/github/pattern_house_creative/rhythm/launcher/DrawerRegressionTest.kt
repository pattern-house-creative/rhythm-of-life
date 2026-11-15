package io.github.pattern_house_creative.rhythm.launcher

import android.content.Intent
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.*
import org.junit.Assert.*

/** Complement the raw drag/reversal tests with result and real Home-intent coverage. */
class DrawerRegressionTest {
    @get:Rule val rule=createAndroidComposeRule<MainActivity>()
    private val context get()=InstrumentationRegistry.getInstrumentation().targetContext
    private lateinit var store:LauncherStore
    private lateinit var saved:HomeLayout
    @Before fun prepare() {
        assertTrue(android.os.Build.MODEL.contains("sdk"))
        store=LauncherStore(context)
        rule.waitUntil(15000){store.load().tiles.isNotEmpty()}
        saved=store.load()
    }
    @After fun verifyPins(){if(::saved.isInitialized)assertEquals(saved,store.load())}
    private fun open() {
        rule.onNodeWithTag("swipe-search").performClick()
        rule.onNodeWithTag("search-input").assertIsDisplayed()
    }
    @Test fun populatedAndEmptySearchResultsSurviveScrollAndRepeatedClose() {
        repeat(3) {
            open()
            rule.onNodeWithTag("drawer-grid").performTouchInput{swipeUp()}
            rule.onNodeWithTag("drawer-grid").performTouchInput{swipeDown()}
            rule.onNodeWithTag("search-input").performTextReplacement("zz-no-installed-app-or-star-7193")
            rule.onNodeWithTag("drawer-grid").onChildren().assertCountEquals(0)
            rule.onNodeWithTag("search-input").performTextReplacement("Clock")
            assertTrue(rule.onNodeWithTag("drawer-grid").onChildren().fetchSemanticsNodes().isNotEmpty())
            rule.onNodeWithTag("search-input").performTextClearance()
            assertTrue(rule.onNodeWithTag("drawer-grid").onChildren().fetchSemanticsNodes().isNotEmpty())
            rule.onNodeWithContentDescription("Close apps").performClick()
            rule.onNodeWithTag("search-input").assertDoesNotExist()
        }
    }
    @Test fun actualHomeIntentClosesDrawerAndKeyboardWithoutChangingLayout() {
        repeat(3) {
            open()
            rule.onNodeWithTag("search-input").performTextReplacement("Clock")
            rule.activityRule.scenario.onActivity { activity ->
                activity.startActivity(Intent(activity,MainActivity::class.java)
                    .setAction(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
            rule.waitUntil(5000){rule.onAllNodesWithTag("search-input").fetchSemanticsNodes().isEmpty()}
            rule.onNodeWithTag("swipe-search").assertIsDisplayed()
        }
    }
}
