package io.github.pattern_house_creative.rhythm.launcher

import android.view.WindowManager
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.*
import org.junit.Assert.*

class ProductionBoundaryTest {
    @get:Rule val rule = createAndroidComposeRule<MainActivity>()
    @Test fun deliveredVariantRejectsTheFixtureCertificateAndAllowsScreenshots() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        Assume.assumeTrue(context.packageName == "io.github.pattern_house_creative.rhythm.launcher")
        assertTrue(android.os.Build.FINGERPRINT.contains("generic") || android.os.Build.MODEL.contains("sdk"))
        assertEquals("", LauncherStore(context).origin)
        rule.activityRule.scenario.onActivity {
            assertTrue(it.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0)
        }
        // No origin is saved; only the disposable loopback server is contacted.
        val failure = runCatching { runBlocking { HouseholdClient("https://10.0.2.2:9443").companionReady() } }.exceptionOrNull()
        assertTrue("Debug must not trust the fixture CA: $failure", failure is javax.net.ssl.SSLHandshakeException)
    }
}
