package io.github.pattern_house_creative.rhythm.launcher

import org.junit.Assert.*
import org.junit.Test
import kotlin.math.*

class SkyGeometryTest {
    private fun nodes(n:Int)=(0 until n).map { RhythmItem("synthetic-$it","Synthetic $it",false) }
    private fun link(a:Int,b:Int)=SkyLink("synthetic-$a","synthetic-$b")
    private fun dense(n:Int)=(0 until n).flatMap { a -> (1..5).map { step->link(a,(a+step)%n) } }
    private fun cases()=listOf(
        Triple("sparse",12,(1 until 12).map{link(it-1,it)}),
        Triple("dense",84,dense(84)),
        Triple("hub",84,(1 until 84).map{link(0,it)}),
        Triple("disconnected",64,emptyList()),
        Triple("bridged",96,(0 until 96).flatMap { a->(1..4).map { d->link(a,a/24*24+(a+d)%24) } }+listOf(link(0,24),link(24,48),link(48,72)))
    )

    @Test fun portraitCompositionUsesSidesWithoutClippingOrCollapsingToAColumn() {
        for((name,n,links) in cases()) {
            val graph=layoutSky(nodes(n),links)
            val xs=graph.points.map{it.x/820f};val ys=graph.points.map{it.y/560f}
            val spanX=xs.max()-xs.min();val spanY=ys.max()-ys.min()
            assertTrue("$name horizontal span $spanX",spanX>(if(n<20).60f else .74f))
            assertTrue("$name vertical span $spanY",spanY>.65f)
            assertTrue("$name left empty",xs.count{it<.34f}>=n/6)
            assertTrue("$name right empty",xs.count{it>.66f}>=n/6)
            assertTrue("$name central column",xs.count{it in .4f.. .6f}<n*.45f)
            for((w,h) in listOf(912f to 2048f,1080f to 2400f,720f to 1600f,1080f to 1920f,360f to 800f)) {
                val screen=graph.points.map{ambientSkyPosition(it,w,h)}
                assertTrue(screen.all{(x,y)->x>w*.02f && x<w*.98f && y>h*.02f && y<h*.96f})
                var nearest=Float.MAX_VALUE
                screen.forEachIndexed { i,a->screen.drop(i+1).forEach{b->nearest=minOf(nearest,hypot(a.first-b.first,a.second-b.second))} }
                assertTrue("$name overlapping stars at $w x $h: $nearest",nearest>6f)
            }
            println("GEOMETRY $name nodes=$n edges=${graph.edges.size} overview=${skyOverviewEdges(graph).size} spanX=$spanX spanY=$spanY left=${xs.count{it<.34f}} right=${xs.count{it>.66f}}")
        }
    }
    @Test fun deterministicAcrossInputOrderDuplicatesAndRefreshingMembership() {
        for((_,n,links) in cases()) {
            val first=layoutSky(nodes(n),links)
            assertEquals(first,layoutSky(nodes(n).reversed()+nodes(n).take(2),links.reversed()+links))
            val refreshed=layoutSky(nodes(n+4),links,first.points.associateBy{it.item.id})
            first.points.forEach { p->val q=refreshed.points.first{it.item.id==p.item.id};assertEquals(p.x,q.x);assertEquals(p.y,q.y) }
        }
        assertTrue(layoutSky(emptyList(),emptyList()).points.isEmpty())
        assertEquals(1,layoutSky(nodes(1),listOf(link(0,0))).points.size)
        assertEquals(2,layoutSky(nodes(2),listOf(link(0,1))).points.size)
    }
    @Test fun quietOverviewNeverInventsOrDeletesRelationshipsAndFocusRestoresEveryLink() {
        for((_,n,links) in cases()) {
            val graph=layoutSky(nodes(n),links+link(0,9999)+link(0,0))
            val expected=links.map{setOf(it.from,it.to)}.filter{it.size==2}.toSet()
            assertEquals(expected,graph.edges.map{(a,b)->setOf(graph.points[a].item.id,graph.points[b].item.id)}.toSet())
            val overview=skyOverviewEdges(graph)
            assertTrue(overview.all{it in graph.edges.indices})
            if(n>18)assertTrue(overview.size<=n)
            graph.points.forEachIndexed { index,p->
                val visible=skyVisibleEdges(graph,overview,p.item.id)
                val incident=graph.edges.indices.filter{graph.edges[it].first==index || graph.edges[it].second==index}
                assertTrue(visible.containsAll(incident));assertTrue(visible.all{it in graph.edges.indices})
            }
            assertEquals(overview,skyVisibleEdges(graph,overview,null))
        }
        val hub=layoutSky(nodes(84),(1 until 84).map{link(0,it)})
        assertEquals(83,skyVisibleEdges(hub,skyOverviewEdges(hub),"synthetic-0").size)
    }
    @Test fun largeDenseDiscoveryStaysBoundedFiniteAndSeparated() {
        val graph=layoutSky(nodes(2000),(1 until 2000).map{link(it-1,it)}+(1 until 2000).map{link(0,it)})
        assertEquals(2000,graph.points.size)
        assertTrue(graph.points.all{it.x in 24f..796f && it.y in 20f..540f})
        assertEquals(2000,graph.points.map{it.x to it.y}.distinct().size)
        assertTrue(skyOverviewEdges(graph).size<=2000)
    }
}
