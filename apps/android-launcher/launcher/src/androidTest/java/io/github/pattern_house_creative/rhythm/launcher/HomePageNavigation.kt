package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.semantics.getOrNull
import androidx.compose.ui.test.junit4.ComposeTestRule
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithTag

/** Runs one of Home's page actions ("Next Home page", "Previous Home page"); false when it is not offered. */
internal fun ComposeTestRule.homePageAction(label: String): Boolean {
    val action = onNodeWithTag("home-grid").fetchSemanticsNode().config.getOrNull(SemanticsActions.CustomActions)
        ?.firstOrNull { it.label == label } ?: return false
    runOnIdle { action.action() }
    waitForIdle()
    return true
}

/** Turns Home to the page that holds [tag]. Only the page in view exposes its tiles. */
internal fun ComposeTestRule.showOnHome(tag: String) {
    while (onAllNodesWithTag(tag).fetchSemanticsNodes().isEmpty() && homePageAction("Previous Home page")) Unit
    while (onAllNodesWithTag(tag).fetchSemanticsNodes().isEmpty() && homePageAction("Next Home page")) Unit
    onNodeWithTag(tag).fetchSemanticsNode()
}
