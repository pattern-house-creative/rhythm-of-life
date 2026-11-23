package io.github.pattern_house_creative.rhythm.launcher

import org.junit.Assert.*
import org.junit.Test

class HomePagesTest {
    private fun app(i: Int) = Tile(TileKind.APP, "app$i")
    private fun preview(id: String) = Tile(TileKind.PREVIEW, id)
    private fun rows(page: List<Tile>) = packHomeCells(page).maxOfOrNull { it.row + it.rows } ?: 0

    @Test fun anOldSingleListSplitsIntoPagesWithoutReorderingOrLosingPins() {
        val tiles = (0 until 30).map(::app)
        val old = HomeLayout(tiles, (40..45).map(::app))
        val paged = old.fit(5)
        assertEquals(tiles, paged.tiles)
        assertEquals(old.dock, paged.dock)
        assertEquals(listOf(20, 10), paged.pageLists().map { it.size })
        paged.pageLists().forEach { assertTrue(rows(it) <= 5) }
        assertEquals(paged, paged.fit(5))
    }

    @Test fun fittingPushesOnlyTheLastPinsOfAPageAndCascades() {
        val layout = HomeLayout((0 until 16).map(::app), breaks = listOf(12))
        // Three rows a page: the first page is full, and a preview added in its second row pushes its last row on.
        val grown = layout.placeHome(preview("board"), 0, 4).fit(3)
        assertEquals(preview("board"), grown.page(0)[4])
        assertEquals((8 until 16).map(::app), grown.page(1))
        grown.pageLists().forEach { assertTrue(rows(it) <= 3) }
        assertEquals(layout.tiles.size + 1, grown.tiles.size)
        assertEquals(grown.tiles.filter { it.kind == TileKind.APP }, layout.tiles)
    }

    @Test fun aNewPinGoesOnTheFirstPageWithRoomOrANewPage() {
        val full = HomeLayout((0 until 8).map(::app) + (8 until 12).map(::app), breaks = listOf(8)).fit(2)
        val added = full.add(app(50), 2)
        assertEquals(1, added.pageOf(app(50).key))
        val crowded = HomeLayout((0 until 16).map(::app), breaks = listOf(8)).fit(2)
        val third = crowded.add(preview("board"), 2)
        assertEquals(2, third.pageOf(preview("board").key))
        assertEquals(3, third.pageCount)
        // Shown as a preview on the first page with room for one.
        assertEquals(1, HomeLayout((0 until 12).map(::app), breaks = listOf(8)).showPreview("b", 3).pageOf("DASHBOARD:b"))
    }

    @Test fun emptyPagesDisappear() {
        val layout = HomeLayout(listOf(app(0), app(1), app(2)), breaks = listOf(1, 2))
        assertEquals(3, layout.pageCount)
        val removed = layout.removeHome(app(1).key)
        assertEquals(listOf(listOf(app(0)), listOf(app(2))), removed.pageLists())
        assertEquals(1, HomeLayout().pageCount) // An empty Home still has its first page.
    }

    @Test fun placingAcrossPagesAndOntoANewPage() {
        val layout = HomeLayout((0 until 6).map(::app), breaks = listOf(3))
        val moved = layout.placeHome(app(0), 1, 1)
        assertEquals(listOf(app(1), app(2)), moved.page(0))
        assertEquals(listOf(app(3), app(0), app(4), app(5)), moved.page(1))
        val fresh = layout.placeHome(app(4), layout.pageCount, 0, newPage = true)
        assertEquals(listOf(listOf(app(0), app(1), app(2)), listOf(app(3), app(5)), listOf(app(4))), fresh.pageLists())
        // A tile alone on the last page dropped on a new page stays where it is.
        val alone = HomeLayout((0 until 3).map(::app), breaks = listOf(2))
        assertEquals(alone, alone.placeHome(app(2), 2, 0, newPage = true))
        // Reordering on one page counts the tile's own place, like insertHome.
        assertEquals(listOf(app(1), app(0), app(2)), layout.placeHome(app(0), 0, 2).page(0))
        // From the dock onto a page.
        val docked = HomeLayout(listOf(app(0)), listOf(app(9)))
        assertEquals(listOf(app(9), app(0)), docked.placeHome(app(9), 0, 0, fromDock = true).tiles)
        assertTrue(docked.placeHome(app(9), 0, 0, fromDock = true).dock.isEmpty())
    }

    @Test fun accessibleMovesCrossPageEdgesAndMakeRoomOnAFullPage() {
        val layout = HomeLayout((0 until 12).map(::app), breaks = listOf(8)).fit(2)
        // First on page two, moved earlier: last place on the full first page, whose last icon moves on.
        val earlier = layout.shift(app(8).key, true, 2)
        assertEquals(0, earlier.pageOf(app(8).key))
        assertEquals(1, earlier.pageOf(app(7).key))
        earlier.pageLists().forEach { assertTrue(rows(it) <= 2) }
        // Last on page one, moved later: first on page two.
        val later = layout.shift(app(7).key, false, 2)
        assertEquals(app(7), later.page(1).first())
        // Next page from the last page starts a new page, unless the tile is alone there.
        val next = layout.moveToPage(app(11).key, 2, 2)
        assertEquals(listOf(app(11)), next.page(2))
        assertEquals(next, next.moveToPage(app(11).key, 3, 2))
        assertEquals(layout, layout.shift(app(11).key, false, 2))
        assertEquals(layout, layout.shift(app(0).key, true, 2))
    }

    @Test fun pageCapacityComesFromHeight() {
        assertEquals(5, homePageRows((5 * HOME_ROW_DP + HOME_GRID_MARGIN_DP + HOME_INDICATOR_DP).toFloat()))
        assertEquals(4, homePageRows((5 * HOME_ROW_DP + HOME_GRID_MARGIN_DP + HOME_INDICATOR_DP - 1).toFloat()))
        assertEquals(2, homePageRows(40f)) // A preview always fits.
    }

    @Test fun cellsStopAboveThePageDotsAndNeverCutARowThrough() {
        // More sky on a 1080 x 2400 phone leaves a grid about 118 dp tall: one whole row, and the next row's cards
        // (from 96 dp) stay hidden instead of peeking out behind the dots.
        assertEquals(92f, homeCellClipDp(118f), .001f)
        for (tenths in 0..12_000) {
            val grid = tenths / 10f
            val clip = homeCellClipDp(grid)
            assertTrue(clip >= 0f)
            assertTrue("$grid", clip <= maxOf(0f, grid - HOME_INDICATOR_DP - HOME_INDICATOR_GAP_DP))
            // Each row's cells, from 8 dp under its top to its foot, are drawn whole or not at all.
            for (row in 0 until 14) assertTrue("$grid row $row",
                row * HOME_ROW_DP + HOME_ROW_DP <= clip || row * HOME_ROW_DP + 8 >= clip)
        }
    }

    @Test fun aFullPageIsDrawnWhole() {
        // Ordinary Home: a page holds the rows its height allows, and the clip falls below the last of them.
        for (tenths in 2_000..12_000) {
            val page = tenths / 10f
            val rows = homePageRows(page)
            if (rows * HOME_ROW_DP + HOME_GRID_MARGIN_DP + HOME_INDICATOR_DP > page) continue // only the two-row floor
            assertTrue("$page", homeCellClipDp(page) >= rows * HOME_ROW_DP)
        }
    }

    @Test fun limitsHoldAcrossPages() {
        val full = HomeLayout((0 until HOME_LIMIT).map(::app), breaks = listOf(40, 80))
        assertEquals(full, full.add(app(500), 5))
        assertEquals(full, full.placeHome(app(500), 1, 0))
        assertEquals(full, full.showPreview("x", 5))
        assertEquals(3, full.normalized().pageCount)
    }
}
