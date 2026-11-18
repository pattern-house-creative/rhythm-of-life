package io.github.pattern_house_creative.rhythm.launcher

import android.os.ParcelFileDescriptor
import android.os.Process
import androidx.activity.compose.setContent
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Rule
import org.junit.Test

class LauncherAvailabilityTest {
    @get:Rule val rule = createAndroidComposeRule<MainActivity>()

    @Test fun unavailableProviderShowsANativeRetry() {
        val origin = "https://example.invalid"
        var attempts = 0
        rule.activity.runOnUiThread {
            rule.activity.setContent {
                RestrictedWeb(origin, origin, Modifier, createWebView = {
                    attempts++
                    throw NoClassDefFoundError("Synthetic provider update")
                }, accessLost = {})
            }
        }
        val message = "Android System WebView unavailable. Your apps are still available."
        rule.waitUntil(8000) { rule.onAllNodesWithText(message).fetchSemanticsNodes().isNotEmpty() }
        rule.onNodeWithText("Try again").performClick()
        rule.waitUntil(5000) { rule.onAllNodesWithText(message).fetchSemanticsNodes().isNotEmpty() && attempts == 2 }
    }

    @Test fun choosingAWorkPinStartsTheWorkCopyOfTheApp() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        assumeTrue(context.packageName.endsWith(".fixture") && android.os.Build.MODEL.contains("sdk"))
        val apps = installed(context)
        val current = Process.myUserHandle()
        val profile = context.getSystemService(android.content.pm.LauncherApps::class.java).profiles.firstOrNull { it != current }
        assumeTrue("A started emulator test work profile is required", profile != null)
        val clockPackages = setOf("com.google.android.deskclock", "com.android.deskclock")
        val personal = apps.first { it.profile == current && it.component.packageName in clockPackages }
        val work = apps.single { it.component == personal.component && it.profile == profile }
        assertEquals(personal.component.flattenToString(), personal.tile.target)
        assertNotEquals(personal.tile.key, work.tile.key)
        val store = LauncherStore(context)
        val previous = store.load()
        fun shell(command: String) = ParcelFileDescriptor.AutoCloseInputStream(
            instrumentation.uiAutomation.executeShellCommand(command)).bufferedReader().use { it.readText() }
        try {
            store.save(previous.copy(tiles = listOf(work.tile, personal.tile)))
            rule.activityRule.scenario.recreate()
            rule.onNodeWithTag("tile:" + work.tile.key).performClick()
            val serial = context.getSystemService(android.os.UserManager::class.java).getSerialNumberForUser(work.profile)
            val userId = Regex("""UserInfo\{(\d+):[^\n]*\bserialNo=$serial\b""")
                .find(shell("dumpsys user"))!!.groupValues[1]
            val resumed = Regex("""ResumedActivity.*\bu$userId ${Regex.escape(work.component.packageName)}/""")
            rule.waitUntil(8000) { resumed.containsMatchIn(shell("dumpsys activity activities")) }
        } finally {
            store.save(previous)
            shell("input keyevent KEYCODE_BACK")
        }
    }
}
