package io.github.pattern_house_creative.rhythm.launcher

import org.junit.Assert.*
import org.junit.Test

class LayoutTest {
    @Test fun builtinGlucoseRoutesAreDistinctAndDoNotReorderExistingPins() {
        val before=HomeLayout(listOf(Tile(TileKind.APP,"app"),Tile(TileKind.PREVIEW,"saved"),Tile(TileKind.STAR,"star")))
        val next=before.showPreview(BUILTIN_GLUCOSE)
        assertEquals(before.tiles,next.tiles.filterNot { it.target==BUILTIN_GLUCOSE })
        val tile=next.tiles.single { it.target==BUILTIN_GLUCOSE }
        assertEquals("https://example.org/launcher-card.html#builtin=glucose",webDestination("https://example.org",tile,true))
        assertEquals("https://example.org/launcher.html?openOriginal=1#rhythm-launcher=builtin:glucose",webDestination("https://example.org",tile))
        assertEquals(before,next.remove(tile.key))
        assertEquals(next,next.showPreview(BUILTIN_GLUCOSE))
        assertTrue(webDestination("https://example.org",Tile(TileKind.DASHBOARD,"glucose")).contains("dashboard:glucose"))
    }
    private val app = Tile(TileKind.APP, "example/.Main")
    private val board = Tile(TileKind.PREVIEW, "board")
    private val star = Tile(TileKind.STAR, "star")
    @Test fun previewOnboardingPreservesExistingOrderDockAndCapacity() {
        val apps=(0..7).map { app.copy(target="app$it") }
        val before=HomeLayout(apps,apps.take(4))
        val after=before.initialPreviews(listOf("a","b","c"))
        assertEquals(listOf("a","b"),after.tiles.filter{it.kind==TileKind.PREVIEW}.map{it.target})
        assertEquals(apps,after.tiles.filter{it.kind==TileKind.APP})
        assertEquals(before.dock,after.dock)
        assertEquals(TileKind.PREVIEW,after.tiles[4].kind)
        assertEquals(after,after.initialPreviews(listOf("a","b","c")))
        assertEquals(before,before.initialPreviews(emptyList()))
        val full=HomeLayout((0 until 120).map{app.copy(target="app$it")})
        assertEquals(full,full.initialPreviews(listOf("a")))
    }
    @Test fun explicitPreviewKeepsPinnedPositionAndExistingPreviewChoice() {
        val icon=Tile(TileKind.DASHBOARD,"a")
        val before=HomeLayout(listOf(app,icon,star))
        val auto=before.initialPreviews(listOf("a","b"))
        assertEquals(icon,auto.tiles[1])
        val explicit=before.showPreview("a")
        assertEquals(listOf(app.key,icon.key,star.key),explicit.tiles.map{it.key})
        assertEquals(TileKind.PREVIEW,explicit.tiles[1].kind)
        assertEquals(explicit,explicit.showPreview("a"))
    }
    @Test fun mixedReorderPreservesIdentityAndSize() {
        val layout = HomeLayout(listOf(app, board, star))
        assertEquals(listOf(board, star, app), layout.move(app.key, star.key).tiles)
        assertEquals(2, layout.move(app.key, star.key).tiles.first().span)
        assertEquals(layout, layout.move("missing", app.key))
        assertEquals(layout, layout.move(app.key, "missing"))
    }
    @Test fun previewAndIconCannotDuplicateSameDashboard() {
        val layout = HomeLayout(listOf(board)).add(board.copy(kind = TileKind.DASHBOARD))
        assertEquals(1, layout.tiles.size)
        assertEquals(board.key, layout.togglePreview(board.key).tiles.single().key)
        assertEquals(TileKind.PREVIEW, layout.togglePreview(board.key).tiles.single().kind)
    }
    @Test fun dockIsBoundedAndOnlyAcceptsApps() {
        var layout = HomeLayout().dock(board)
        assertTrue(layout.dock.isEmpty())
        repeat(7) { layout = layout.dock(app.copy(target = "app$it")) }
        assertEquals(6, layout.dock.size)
        assertEquals("app0", layout.dock.first().target)
        assertEquals("app5",layout.tiles.single().target)
    }
    @Test fun rejectsCredentialAndAmbiguousHouseholdAddresses() {
        listOf("http://example.org", "https://user:secret@example.org", "https://example.org/path",
            "https://example.org?token=x", "https://example.org#x", "javascript:alert(1)",
            "https://example.org:0", "https://example.org:65536").forEach { assertNull(it, householdOrigin(it)) }
        assertEquals("https://example.org", householdOrigin(" https://EXAMPLE.org/ "))
    }
    @Test fun webBoundaryRejectsNativeIntentsAndOriginConfusion() {
        val origin = "https://example.org"
        listOf("intent://example.org", "https://example.org.evil.test", "https://example.org@evil.test",
            "http://example.org", "https://example.org:8443", "file:///tmp/a").forEach { assertFalse(it, sameOrigin(origin, it)) }
        assertTrue(sameOrigin(origin, "https://example.org:443/api/dashboards"))
        val url = webDestination(origin, star.copy(target = "a/#b?x=1"))
        assertTrue(url.contains("star:a%2F%23b%3Fx%3D1"))
    }
    @Test fun onlyHttpsGoogleFileLinksQualifyForBrowserHandoff() {
        listOf(
            "https://docs.google.com/document/d/example/edit",
            "https://docs.google.com/spreadsheets/d/example/edit?resourcekey=example#gid=123",
            "https://docs.google.com/presentation/d/example/preview",
            "https://drive.google.com/file/d/example/view"
        ).forEach { assertTrue(it, isGoogleFile(it)) }
        listOf(
            "http://docs.google.com/document/d/example/edit", "https://docs.google.com.example.org/document/d/example/edit",
            "https://user@drive.google.com/file/d/example", "https://drive.google.com:8080/file/d/example",
            "https://drive.google.com/drive/folders/example", "https://docs.google.com/document/d/"
        ).forEach { assertFalse(it, isGoogleFile(it)) }
    }
}
