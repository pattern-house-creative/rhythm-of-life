package io.github.pattern_house_creative.rhythm.launcher
import org.junit.Assert.*
import org.junit.Test

class DailyInsightTest {
    private val now=1_800_000_000_000L
    private fun row(id:String="a",status:String="new",at:Long=now,until:Long?=null,supersedes:String?=null)=
        DailyInsight(id,"focus","A synthetic idea","","","","","weak",at,status,supersedes,until,emptyList(),emptyList())
    @Test fun sameCurrentPolicyAsSite() {
        assertEquals("b",currentDailyInsight(listOf(row(at=now-1000),row("b")),now)?.id)
        assertNull(currentDailyInsight(listOf(row(at=now-3*86_400_000)),now))
        assertNull(currentDailyInsight(listOf(row(at=now+3_600_001)),now))
        assertEquals("a",currentDailyInsight(listOf(row(at=now+3_600_000)),now)?.id)
        for(status in listOf("dismissed","handled","explored","superseded"))
            assertNull(currentDailyInsight(listOf(row(status=status)),now))
    }
    @Test fun snoozeAndSupersessionDoNotResurrectOldInsights() {
        assertNull(currentDailyInsight(listOf(row(status="snoozed",until=now+1)),now))
        assertNotNull(currentDailyInsight(listOf(row(status="snoozed",at=now-10*86_400_000,until=now-1)),now))
        assertNull(currentDailyInsight(listOf(row(status="snoozed",until=now-3*86_400_000)),now))
        assertNull(currentDailyInsight(listOf(row(),row("b","dismissed",supersedes="a")),now))
        assertNull(currentDailyInsight(emptyList(),now))
    }
    @Test fun dashboardIdentityHasSemanticHintsAndHonestFallback() {
        assertEquals("pulse",dashboardSymbol("Glucose",BUILTIN_GLUCOSE))
        assertEquals("wallet",dashboardSymbol("Spend This Week","spend"))
        assertEquals("book",dashboardSymbol("Reading overview","read"))
        assertEquals("leaf",dashboardSymbol("Garden overview","garden"))
        assertEquals("initials",dashboardSymbol("Orion observations","orion"))
    }
}
