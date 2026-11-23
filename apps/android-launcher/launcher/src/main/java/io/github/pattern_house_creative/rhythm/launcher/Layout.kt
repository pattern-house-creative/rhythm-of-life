package io.github.pattern_house_creative.rhythm.launcher

import java.net.URI

const val BUILTIN_GLUCOSE = "builtin:glucose"

enum class TileKind { APP, DASHBOARD, PREVIEW, STAR }
data class Tile(val kind: TileKind, val target: String) {
    val key: String get() = "${if (kind == TileKind.PREVIEW) TileKind.DASHBOARD else kind}:$target"
    val span: Int get() = if (kind == TileKind.PREVIEW) 2 else 1
}
const val HOME_LIMIT = 120

/** What a Home page shows. Only grid pages exist today; the saved layout names each page's kind so a
 * later non-grid page (such as the sky) can be added without changing the saved format. */
enum class HomePageKind { GRID }

/**
 * Home is one ordered list of pins cut into pages. [tiles] keeps reading order across every page, and
 * [breaks] holds the index in [tiles] where each page after the first begins. A page never scrolls:
 * [fit] moves the last pins of a page that has grown too tall to the start of the next page, so nothing
 * is clipped and nothing is reordered. Empty pages are dropped.
 */
data class HomeLayout(val tiles: List<Tile> = emptyList(), val dock: List<Tile> = emptyList(), val breaks: List<Int> = emptyList()) {
    val pageCount: Int get() = pageLists().size
    fun page(index: Int): List<Tile> = pageLists().getOrElse(index) { emptyList() }
    fun pageOf(key: String): Int = pageLists().indexOfFirst { page -> page.any { it.key == key } }
    /** Pages as lists. Stale or out-of-range breaks (after a plain copy of [tiles]) are clamped. */
    fun pageLists(): List<List<Tile>> {
        val starts = (listOf(0) + breaks.filter { it in 1 until tiles.size }).distinct().sorted()
        return starts.mapIndexed { i, start -> tiles.subList(start, starts.getOrElse(i + 1) { tiles.size }) }
    }
    private fun withPages(pages: List<List<Tile>>, dock: List<Tile> = this.dock): HomeLayout {
        val kept = pages.filter { it.isNotEmpty() }
        val breaks = kept.runningFold(0) { at, page -> at + page.size }.drop(1).dropLast(1)
        return HomeLayout(kept.flatten(), dock, breaks)
    }
    private fun editPages(change: MutableList<MutableList<Tile>>.() -> Unit): HomeLayout =
        withPages(pageLists().map { it.toMutableList() }.toMutableList().apply(change))

    fun normalized(): HomeLayout {
        val seen = HashSet<String>()
        val pages = pageLists().map { page -> page.filter { seen.size < HOME_LIMIT && seen.add(it.key) } }
        return withPages(pages,
            dock.filter { it.kind == TileKind.APP }.distinctBy { it.key }.take(6))
    }

    /** Rows a page of these pins needs. */
    private fun rowsOf(page: List<Tile>) = packHomeCells(page).maxOfOrNull { it.row + it.rows } ?: 0

    /** Pushes the last pins of any page taller than [rows] onto the start of the next page, adding a
     * page when needed. Reading order never changes, and a single pin too tall to fit stays put. */
    fun fit(rows: Int): HomeLayout {
        if (rows <= 0) return this
        val pages = pageLists().map { it.toMutableList() }.toMutableList()
        var i = 0
        while (i < pages.size) {
            val page = pages[i]
            while (page.size > 1 && rowsOf(page) > rows) {
                if (i + 1 == pages.size) pages.add(mutableListOf())
                pages[i + 1].add(0, page.removeAt(page.lastIndex))
            }
            i++
        }
        val next = withPages(pages)
        return if (next == this) this else next
    }

    /** The first page that can take [tile] at its end without growing past [rows]; [pageCount] when none can. */
    private fun pageWithRoom(tile: Tile, rows: Int): Int =
        pageLists().indexOfFirst { rowsOf(it + tile) <= rows }.takeIf { it >= 0 } ?: pageCount

    // First connection adds real dashboards after the first row without moving or
    // removing existing pins. Empty/failed discovery never manufactures a preview.
    fun initialPreviews(ids: List<String>): HomeLayout {
        if (tiles.any { it.kind == TileKind.PREVIEW }) return this
        val fresh = ids.distinct().filter { id -> tiles.none { it.key == "DASHBOARD:$id" } }
            .take(minOf(2, (HOME_LIMIT - tiles.size).coerceAtLeast(0))).map { Tile(TileKind.PREVIEW, it) }
        return insertPreviewRow(0, fresh)
    }
    /** Shows a dashboard as a live preview: in place when it is pinned, otherwise after the first row of the
     * first page with room for it (with [rows] known), or of the first page. */
    fun showPreview(id: String, rows: Int = 0): HomeLayout {
        val present = tiles.indexOfFirst { it.key == "DASHBOARD:$id" }
        if (present >= 0) return copy(tiles = tiles.mapIndexed { i, t -> if (i == present) t.copy(kind = TileKind.PREVIEW) else t })
        if (tiles.size >= HOME_LIMIT) return this
        val preview = Tile(TileKind.PREVIEW, id)
        return insertPreviewRow(if (rows > 0) pageWithRoom(preview, rows) else 0, listOf(preview))
    }
    private fun insertPreviewRow(pageIndex: Int, previews: List<Tile>): HomeLayout {
        if (previews.isEmpty()) return this
        return editPages {
            if (pageIndex >= size) { add(previews.toMutableList()); return@editPages }
            val page = this[pageIndex]
            var end = 0; var columns = 0
            while (end < page.size && columns < 4) { columns += page[end].span; end++ }
            page.addAll(end, previews)
        }
    }
    fun move(key: String, target: String): HomeLayout {
        val from = tiles.indexOfFirst { it.key == key }
        val to = tiles.indexOfFirst { it.key == target }
        if (from < 0 || to < 0 || from == to) return this
        val tile = tiles[from]
        return editPages {
            first { page -> page.remove(tile) }
            val page = first { page -> page.any { it.key == target } }
            val at = page.indexOfFirst { it.key == target }
            page.add(if (from < to) at + 1 else at, tile)
        }
    }
    /** Pins [tile] at the end of the first page with room ([rows] known) or of the last page. A pin that is
     * already on Home keeps its place and takes the new kind. */
    fun add(tile: Tile, rows: Int = 0): HomeLayout {
        if (tiles.any { it.key == tile.key }) return copy(tiles = tiles.map { if (it.key == tile.key) tile else it }).normalized()
        if (tiles.size >= HOME_LIMIT) return this
        val target = if (rows > 0) pageWithRoom(tile, rows) else (pageCount - 1).coerceAtLeast(0)
        return editPages { if (target >= size) add(mutableListOf(tile)) else this[target].add(tile) }.normalized()
    }
    fun remove(key: String) = editPages { forEach { page -> page.removeAll { it.key == key } } }.copy(dock = dock.filterNot { it.key == key })
    fun removeHome(key: String) = editPages { forEach { page -> page.removeAll { it.key == key } } }
    fun removeDock(key: String) = copy(dock = dock.filterNot { it.key == key })
    /** Inserts by position in reading order, on the page that holds that position. */
    fun insertHome(tile: Tile, at: Int, fromDock: Boolean = false): HomeLayout {
        val prior = tiles.indexOfFirst { it.key == tile.key }
        if (prior < 0 && tiles.size >= HOME_LIMIT) return this
        val pages = pageLists().map { list -> list.filterNot { it.key == tile.key }.toMutableList() }.toMutableList()
        if (pages.isEmpty()) pages.add(mutableListOf())
        var rest = (at - if (prior >= 0 && prior < at) 1 else 0).coerceAtLeast(0)
        var target = 0
        while (target < pages.lastIndex && rest >= pages[target].size) { rest -= pages[target].size; target++ }
        pages[target].add(rest.coerceAtMost(pages[target].size), if (prior >= 0) tiles[prior] else tile)
        return withPages(pages, if (fromDock) dock.filterNot { it.key == tile.key } else dock)
    }
    /**
     * Puts [tile] on [page] at position [at] of that page as it is now (counting the tile itself when it is
     * already there), moving it when it is pinned elsewhere. [page] equal to [pageCount], or [newPage],
     * starts a page of its own there. A pin from the dock leaves the dock when [fromDock].
     */
    fun placeHome(tile: Tile, page: Int, at: Int, fromDock: Boolean = false, newPage: Boolean = false): HomeLayout {
        val prior = tiles.firstOrNull { it.key == tile.key }
        if (prior == null && tiles.size >= HOME_LIMIT) return this
        val placed = prior ?: tile
        val pages = pageLists().map { it.toMutableList() }.toMutableList()
        val priorPage = pages.indexOfFirst { list -> list.any { it.key == tile.key } }
        var index = at
        if (priorPage == page && !newPage) {
            val priorIndex = pages[page].indexOfFirst { it.key == tile.key }
            if (priorIndex < index) index--
        }
        // Keep the source page in place (even when it empties) until the tile is placed.
        if (priorPage >= 0) pages[priorPage].removeAll { it.key == tile.key }
        val target = page.coerceIn(0, pages.size)
        if (newPage || target == pages.size) pages.add(target, mutableListOf(placed))
        else pages[target].add(index.coerceIn(0, pages[target].size), placed)
        return withPages(pages, if (fromDock) dock.filterNot { it.key == tile.key } else dock)
    }
    /**
     * Moves a pinned tile to another page: to its end, or its start with [atStart]. When that page is full
     * (with [rows] known), the tile takes the last place it fits in and the pins after it move on to the
     * next page. [target] equal to [pageCount] starts a new page.
     */
    fun moveToPage(key: String, page: Int, rows: Int = 0, atStart: Boolean = false): HomeLayout {
        val tile = tiles.firstOrNull { it.key == key } ?: return this
        val from = pageOf(key)
        if (page < 0 || page > pageCount || page == from) return this
        if (page == pageCount && page(from).size == 1) return this
        if (page == pageCount) return placeHome(tile, page, 0, newPage = true)
        val landing = if (from < page) page - (if (page(from).size == 1) 1 else 0) else page
        val size = page(page).size
        val positions = if (atStart) listOf(0) else (size downTo 0).toList()
        for (at in positions) {
            val next = placeHome(tile, page, at).let { if (rows > 0) it.fit(rows) else it }
            if (next.pageOf(key) == landing || at == positions.last()) return next
        }
        return this
    }
    /** Moves a tile one place earlier or later in reading order, crossing onto the neighbouring page at a page edge. */
    fun shift(key: String, earlier: Boolean, rows: Int = 0): HomeLayout {
        val from = pageOf(key)
        if (from < 0) return this
        val page = page(from)
        val index = page.indexOfFirst { it.key == key }
        return when {
            earlier && index > 0 -> move(key, page[index - 1].key)
            !earlier && index < page.lastIndex -> move(key, page[index + 1].key)
            earlier && from > 0 -> moveToPage(key, page = from - 1, rows = rows)
            !earlier && from < pageCount - 1 -> moveToPage(key, page = from + 1, rows = rows, atStart = true)
            else -> this
        }.let { if (rows > 0) it.fit(rows) else it }
    }
    // Inserting into a full dock keeps the displaced app on Home. Never silently
    // discard an existing pin. Moving an existing dock icon simply reorders it.
    fun insertDock(tile: Tile, at: Int, fromHome: Boolean = false): HomeLayout {
        if(tile.kind!=TileKind.APP)return this
        val rest=dock.filterNot { it.key==tile.key }.toMutableList()
        rest.add(at.coerceIn(0,minOf(5,rest.size)),tile)
        val home=if(fromHome)removeHome(tile.key) else this
        val spill=rest.drop(6).filter { old->home.tiles.none { it.key==old.key } }
        if(home.tiles.size+spill.size>HOME_LIMIT)return this
        // A displaced dock app joins the end of the last page; fit moves it on when that page is full.
        val pages=home.pageLists().map { it.toMutableList() }.toMutableList()
        if(spill.isNotEmpty()) { if(pages.isEmpty())pages.add(mutableListOf()); pages.last().addAll(spill) }
        return withPages(pages,rest.take(6))
    }
    fun dock(tile: Tile) = insertDock(tile,dock.size)
    fun togglePreview(key: String) = copy(tiles = tiles.map {
        if (it.key != key || it.kind == TileKind.APP || it.kind == TileKind.STAR) it
        else it.copy(kind = if (it.kind == TileKind.PREVIEW) TileKind.DASHBOARD else TileKind.PREVIEW)
    })
}

// One HTTPS origin, no credentials, query strings, fragments, or hidden base paths.
// Authentication remains the deployment front door's existing cookie session.
fun householdOrigin(value: String): String? = runCatching {
    val uri = URI(value.trim())
    if (uri.scheme != "https" || uri.host.isNullOrBlank() || uri.rawUserInfo != null ||
        uri.rawQuery != null || uri.rawFragment != null || uri.path !in listOf("", "/") ||
        uri.port !in -1..65535 || uri.port == 0) null
    else URI("https", null, uri.host.lowercase(), uri.port, null, null, null).toASCIIString()
}.getOrNull()

fun sameOrigin(origin: String, destination: String): Boolean = runCatching {
    val a = URI(origin); val b = URI(destination)
    fun port(u: URI) = if (u.port == -1) 443 else u.port
    b.scheme == "https" && b.rawUserInfo == null && a.host.equals(b.host, true) && port(a) == port(b)
}.getOrDefault(false)

fun webDestination(origin: String, tile: Tile, preview: Boolean = false): String {
    require(householdOrigin(origin) == origin)
    require(tile.kind != TileKind.APP)
    if (tile.target == BUILTIN_GLUCOSE && tile.kind != TileKind.STAR) return if (preview)
        "$origin/launcher-card.html#builtin=glucose" else "$origin/$LAUNCHER_READER#rhythm-launcher=builtin:glucose"
    val encoded = java.net.URLEncoder.encode(tile.target, "UTF-8").replace("+", "%20")
    return if (preview) "$origin/launcher-card.html#dashboard=$encoded"
    else "$origin/$LAUNCHER_READER#rhythm-launcher=${if (tile.kind == TileKind.STAR) "star" else "dashboard"}:$encoded"
}

// The companion uses this capability flag to keep the copy-link UI on older launchers.
internal const val LAUNCHER_READER = "launcher.html?openOriginal=1"

private val editorFile = Regex("/(?:document|spreadsheets|presentation)/d/[A-Za-z0-9_-]+(?:/.*)?")
private val driveFile = Regex("/file/d/[A-Za-z0-9_-]+(?:/.*)?")

internal fun isGoogleFile(destination: String): Boolean {
    val uri = try { URI(destination) } catch (_: java.net.URISyntaxException) { return false }
    if (uri.scheme != "https" || uri.rawUserInfo != null || uri.port !in listOf(-1, 443)) return false
    return when (uri.host?.lowercase()) {
        "docs.google.com" -> editorFile.matches(uri.normalize().rawPath)
        "drive.google.com" -> driveFile.matches(uri.normalize().rawPath)
        else -> false
    }
}
