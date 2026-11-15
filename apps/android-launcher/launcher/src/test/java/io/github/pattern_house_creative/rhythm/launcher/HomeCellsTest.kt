package io.github.pattern_house_creative.rhythm.launcher

import org.junit.Assert.*
import org.junit.Test

class HomeCellsTest {
    private fun app(i:Int)=Tile(TileKind.APP,"app$i")
    @Test fun fourIconsOccupyBothRowsBesidePreviewWithoutChangingOrder() {
        val tiles=listOf(Tile(TileKind.PREVIEW,"board"))+(0..3).map(::app)
        val cells=packHomeCells(tiles)
        assertEquals(listOf(0 to 0,2 to 0,3 to 0,2 to 1,3 to 1),cells.map{it.column to it.row})
        assertEquals(tiles,cells.map{it.tile})
        assertEquals(2,cells.first().rows)
    }
    @Test fun irregularMixedSequencesNeverOverlapOrExceedFourColumns() {
        repeat(200) { seed->
            val random=kotlin.random.Random(seed)
            val tiles=(0 until 120).map{Tile(if(random.nextBoolean())TileKind.PREVIEW else TileKind.APP,"$it")}
            val cells=packHomeCells(tiles)
            val occupied=hashSetOf<Pair<Int,Int>>()
            var last=-1
            cells.forEach { c->
                assertTrue(c.column>=0 && c.column+c.columns<=4)
                assertTrue(c.row*4+c.column>last);last=c.row*4+c.column
                repeat(c.rows){y->repeat(c.columns){x->assertTrue(occupied.add(c.column+x to c.row+y))}}
            }
            assertEquals(cells,packHomeCells(tiles))
        }
    }
    @Test fun fullDockInsertPreservesEveryPinAndReorderDoesNotSpill() {
        val before=HomeLayout(listOf(app(6)),(0..5).map(::app))
        val after=before.insertDock(app(6),2,true)
        assertEquals(listOf(0,1,6,2,3,4).map(::app),after.dock)
        assertEquals(listOf(app(5)),after.tiles)
        assertEquals((0..6).map(::app).toSet(),(after.tiles+after.dock).toSet())
        assertEquals(after.tiles,after.insertDock(app(6),5).tiles)
        assertEquals(app(6),after.insertDock(app(6),5).dock.last())
        assertEquals(after.tiles,after.removeDock(app(6).key).tiles)
    }
    @Test fun transfersPreservePreviewModeCapacityAndOldLayout() {
        val preview=Tile(TileKind.PREVIEW,"board")
        val old=HomeLayout(listOf(preview,app(0)),(1..4).map(::app))
        assertEquals(old,old.normalized()) // Four-slot upgrade is not an autofill.
        assertEquals(preview,old.insertHome(preview.copy(kind=TileKind.DASHBOARD),2).tiles.last())
        val moved=old.insertHome(app(2),0,true)
        assertFalse(moved.dock.contains(app(2)));assertEquals(app(2),moved.tiles.first())
        val full=HomeLayout((0 until 120).map(::app),(200..205).map(::app))
        assertEquals(full,full.insertHome(app(300),0))
        assertEquals(full,full.insertDock(app(300),0))
        assertEquals(full,full.insertDock(preview,0))
    }
}
