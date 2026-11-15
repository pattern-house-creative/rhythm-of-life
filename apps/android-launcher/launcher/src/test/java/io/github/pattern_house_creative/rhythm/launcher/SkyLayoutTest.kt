package io.github.pattern_house_creative.rhythm.launcher
import org.junit.Assert.*
import org.junit.Test

class SkyLayoutTest {
    private fun nodes(n:Int)=(0 until n).map { RhythmItem("record-$it","Record $it",false) }
    @Test fun relationshipsAndDisconnectedRecordsSurviveWithoutPages() {
        val items=nodes(360)
        val links=(1 until 300).filter { it%30!=0 }.map { SkyLink("record-${it-1}","record-$it") }
        val graph=layoutSky(items,links+SkyLink("missing","record-1"))
        assertEquals(360,graph.points.size);assertEquals(links.size,graph.edges.size)
        assertTrue(graph.points.all { it.x.isFinite() && it.y.isFinite() && it.x in 0f..820f && it.y in 0f..560f })
        assertEquals(60,graph.points.count { it.degree==0 })
        assertEquals(graph,layoutSky(items.reversed(),links.reversed()))
    }
    @Test fun refreshKeepsAnchorsAndOnlyRealEdges() {
        val original=layoutSky(nodes(20),listOf(SkyLink("record-0","record-1")))
        val next=layoutSky(nodes(25),listOf(SkyLink("record-0","record-1")),original.points.associateBy { it.item.id })
        original.points.forEach { p -> val q=next.points.first { it.item.id==p.item.id };assertEquals(p.x,q.x);assertEquals(p.y,q.y) }
        assertEquals(1,next.edges.size)
        assertTrue(layoutSky(nodes(64),emptyList()).edges.isEmpty())
    }
    @Test fun breathingIsBoundedAndReducedMotionIsStill() {
        val p=layoutSky(nodes(1),emptyList()).points.single()
        assertNotEquals(skyDrift(p,0f,false),skyDrift(p,3f,false))
        repeat(30) { val(x,y)=skyDrift(p,it.toFloat(),false);assertTrue(kotlin.math.abs(x-p.x)<=2.01f);assertTrue(kotlin.math.abs(y-p.y)<=2.41f) }
        assertEquals(p.x to p.y,skyDrift(p,0f,true));assertEquals(p.x to p.y,skyDrift(p,30f,true))
    }
    @Test fun maximumDiscoveryLayoutStaysFiniteAndComplete() {
        val items=nodes(2000)
        val edges=(1 until 2000).filter { it%100!=0 }.map { SkyLink("record-${it-1}","record-$it") }
        val result=layoutSky(items,edges)
        assertEquals(2000,result.points.size);assertEquals(edges.size,result.edges.size)
        assertTrue(result.points.all { it.x.isFinite() && it.y.isFinite() })
    }

}
