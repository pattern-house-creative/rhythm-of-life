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
            val grid = rule.onNodeWithTag("drawer-grid")
            val range = androidx.compose.ui.semantics.SemanticsProperties.VerticalScrollAxisRange
            // The system keyboard animates independently of Compose. A downward swipe
            // dismisses at the top, so first scroll to a known position below it.
            rule.waitUntil(5000) { grid.fetchSemanticsNode().config[range].maxValue() > 0 }
            grid.performScrollToIndex(installed(context).lastIndex)
            assertTrue(grid.fetchSemanticsNode().config[range].value() > 0)
            grid.performTouchInput { swipeDown() }
            rule.onNodeWithTag("search-input").performTextReplacement("zz-no-installed-app-or-star-7193")
            rule.onNodeWithTag("drawer-grid").onChildren().assertCountEquals(0)
            rule.onNodeWithTag("search-input").performTextReplacement("Clock")
            assertTrue(rule.onNodeWithTag("drawer-grid").onChildren().fetchSemanticsNodes().isNotEmpty())
            rule.onNodeWithTag("search-input").performTextClearance()
            assertTrue(rule.onNodeWithTag("drawer-grid").onChildren().fetchSemanticsNodes().isNotEmpty())
            rule.onNodeWithTag("drawer-handle").performClick()
            rule.onNodeWithTag("search-input").assertDoesNotExist()
        }
    }
    @Test fun eitherLayoutSearchesAndAlphabetScrubbingSurvivesReopening() {
        val original = store.alphabeticalDrawer
        try {
            open()
            val search = rule.onNodeWithTag("search-input")
            val grid = rule.onNodeWithTag("drawer-grid")
            val az = rule.onNodeWithText("A–Z")
            rule.onNodeWithText("Grid").performClick()
            search.performTextReplacement("Clock")
            grid.onChildren().filter(hasText("Clock")).assertCountEquals(1)
            az.performClick()
            az.assertIsSelected()
            grid.onChildren().filter(hasText("Clock")).assertCountEquals(1)
            search.performTextClearance()
            val rail = rule.onNodeWithTag("alphabet-index")
            val range = androidx.compose.ui.semantics.SemanticsProperties.VerticalScrollAxisRange
            rail.performTouchInput { swipe(topCenter, bottomCenter, durationMillis = 280) }
            assertTrue("Dragging to the last letter should scroll the list", grid.fetchSemanticsNode().config[range].value() > 0)
            rail.performTouchInput { swipe(bottomCenter, topCenter, durationMillis = 280) }
            assertEquals(0f, grid.fetchSemanticsNode().config[range].value(), .01f)
            search.performTextReplacement("zz-no-such-app-7193")
            grid.onChildren().assertCountEquals(0)
            rail.assertDoesNotExist()
            rule.activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
            search.assertDoesNotExist()
            rule.activityRule.scenario.recreate()
            open()
            az.assertIsSelected()
            rail.assertIsDisplayed()
            rule.onNodeWithContentDescription("Close apps").assertDoesNotExist()
        } finally {
            store.alphabeticalDrawer = original
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
