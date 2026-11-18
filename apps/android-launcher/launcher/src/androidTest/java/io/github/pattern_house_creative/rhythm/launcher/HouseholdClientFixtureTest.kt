package io.github.pattern_house_creative.rhythm.launcher

import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.*
import org.junit.Assert.*
import java.net.URL

class HouseholdClientFixtureTest {
    private var active=false
    private val origin="https://10.0.2.2:9444"
    @Before fun boundary() {
        Assume.assumeTrue(InstrumentationRegistry.getInstrumentation().targetContext.packageName.endsWith(".fixture"))
        assertTrue(android.os.Build.FINGERPRINT.contains("generic")||android.os.Build.MODEL.contains("sdk"));active=true
    }
    private fun mode(value:String) { URL("$origin/__fixture/control?mode=$value").openConnection().apply { connectTimeout=3000;readTimeout=3000 }.getInputStream().use{it.readBytes()} }
    @After fun reset(){ if(active) mode("normal") }
    @Test fun dashboardsArriveBeforeSlowGraphAndSurviveGraphFailure()=runBlocking {
        mode("graph-slow")
        var delivered=0L;val start=android.os.SystemClock.elapsedRealtime()
        val loaded=HouseholdClient(origin).load { assertEquals(2,it.dashboards.size);assertTrue(it.previewsReady);delivered=android.os.SystemClock.elapsedRealtime() }
        assertEquals(10,loaded.stars.size);assertTrue(delivered>=start)
        assertTrue("Graph must still be in flight after previews are ready",android.os.SystemClock.elapsedRealtime()-delivered>=1300)
        mode("graph-error")
        val partial=HouseholdClient(origin).load()
        assertEquals(2,partial.dashboards.size);assertTrue(partial.stars.isEmpty());assertTrue(partial.warning.contains("Sky unavailable"))
        mode("boards-error")
        val sky=HouseholdClient(origin).load()
        assertTrue(sky.dashboards.isEmpty());assertEquals(10,sky.stars.size);assertTrue(sky.warning.contains("Dashboard previews unavailable"))
    }
    @Test fun discoveryIsReusedUntilExpiryAndDiscardedAfterAccessLoss() = runBlocking {
        mode("normal")
        var clock = 100L
        val client = HouseholdClient(origin) { clock }
        fun reads(): Int {
            val data = URL("$origin/__fixture/state").readText()
            return org.json.JSONObject(data).getJSONObject("reads").optInt("/api/graph")
        }
        val baseline = reads()
        assertEquals(10, client.load().stars.size)
        clock += 299_999
        assertEquals(10, client.load().stars.size)
        assertEquals(baseline + 1, reads())
        clock++
        client.load()
        assertEquals(baseline + 2, reads())
        mode("denied")
        assertTrue(runCatching { client.load() }.exceptionOrNull() is HouseholdAccessDenied)
        mode("normal")
        client.load()
        assertEquals(baseline + 3, reads())
    }

    @Test fun graphAccessDenialFailsClosedAndOldCompanionCannotSeedPreviews()=runBlocking {
        mode("graph-denied")
        assertTrue(runCatching { HouseholdClient(origin).load() }.isFailure)
        mode("old")
        val old=HouseholdClient(origin).load()
        assertFalse(old.previewsReady);assertTrue(old.warning.contains("update or access"))
    }
}
