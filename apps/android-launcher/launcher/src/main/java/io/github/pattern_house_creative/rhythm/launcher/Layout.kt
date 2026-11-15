package io.github.pattern_house_creative.rhythm.launcher

import java.net.URI

const val BUILTIN_GLUCOSE = "builtin:glucose"

enum class TileKind { APP, DASHBOARD, PREVIEW, STAR }
data class Tile(val kind: TileKind, val target: String) {
    val key: String get() = "${if (kind == TileKind.PREVIEW) TileKind.DASHBOARD else kind}:$target"
    val span: Int get() = if (kind == TileKind.PREVIEW) 2 else 1
}
data class HomeLayout(val tiles: List<Tile> = emptyList(), val dock: List<Tile> = emptyList()) {
    fun normalized() = copy(tiles = tiles.distinctBy { it.key }.take(120),
        dock = dock.filter { it.kind == TileKind.APP }.distinctBy { it.key }.take(6))
    // First connection adds real dashboards after the first row without moving or
    // removing existing pins. Empty/failed discovery never manufactures a preview.
    fun initialPreviews(ids: List<String>): HomeLayout {
        if (tiles.any { it.kind == TileKind.PREVIEW }) return this
        val fresh = ids.distinct().filter { id -> tiles.none { it.key == "DASHBOARD:$id" } }
            .take(minOf(2, (120 - tiles.size).coerceAtLeast(0))).map { Tile(TileKind.PREVIEW, it) }
        return insertPreviewRow(fresh)
    }
    fun showPreview(id: String): HomeLayout {
        val present = tiles.indexOfFirst { it.key == "DASHBOARD:$id" }
        return if (present >= 0) copy(tiles = tiles.mapIndexed { i, t -> if (i == present) t.copy(kind = TileKind.PREVIEW) else t })
        else if (tiles.size < 120) insertPreviewRow(listOf(Tile(TileKind.PREVIEW, id))) else this
    }
    private fun insertPreviewRow(previews: List<Tile>): HomeLayout {
        var end = 0; var columns = 0
        while (end < tiles.size && columns < 4) { columns += tiles[end].span; end++ }
        return copy(tiles = tiles.take(end) + previews + tiles.drop(end))
    }
    fun move(key: String, target: String): HomeLayout {
        val from = tiles.indexOfFirst { it.key == key }
        val to = tiles.indexOfFirst { it.key == target }
        if (from < 0 || to < 0 || from == to) return this
        return copy(tiles = tiles.toMutableList().apply { add(to, removeAt(from)) })
    }
    fun add(tile: Tile) = copy(tiles = tiles.filterNot { it.key == tile.key } + tile).normalized()
    fun remove(key: String) = copy(tiles = tiles.filterNot { it.key == key }, dock = dock.filterNot { it.key == key })
    fun removeHome(key: String) = copy(tiles = tiles.filterNot { it.key == key })
    fun removeDock(key: String) = copy(dock = dock.filterNot { it.key == key })
    fun insertHome(tile: Tile, at: Int, fromDock: Boolean = false): HomeLayout {
        val prior=tiles.indexOfFirst { it.key==tile.key }
        if(prior<0 && tiles.size>=120)return this
        val rest=tiles.filterNot { it.key==tile.key }.toMutableList()
        val destination=(at-if(prior>=0 && prior<at)1 else 0).coerceIn(0,rest.size)
        rest.add(destination, if(prior>=0)tiles[prior] else tile)
        return copy(tiles=rest,dock=if(fromDock)dock.filterNot { it.key==tile.key } else dock)
    }
    // Inserting into a full dock keeps the displaced app on Home. Never silently
    // discard an existing pin. Moving an existing dock icon simply reorders it.
    fun insertDock(tile: Tile, at: Int, fromHome: Boolean = false): HomeLayout {
        if(tile.kind!=TileKind.APP)return this
        val rest=dock.filterNot { it.key==tile.key }.toMutableList()
        rest.add(at.coerceIn(0,minOf(5,rest.size)),tile)
        var home=if(fromHome)tiles.filterNot { it.key==tile.key } else tiles
        val spill=rest.drop(6).filter { old->home.none { it.key==old.key } }
        if(home.size+spill.size>120)return this
        home=home+spill
        return copy(tiles=home,dock=rest.take(6))
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
        "$origin/launcher-card.html#builtin=glucose" else "$origin/launcher.html#rhythm-launcher=builtin:glucose"
    val encoded = java.net.URLEncoder.encode(tile.target, "UTF-8").replace("+", "%20")
    return if (preview) "$origin/launcher-card.html#dashboard=$encoded"
    else "$origin/launcher.html#rhythm-launcher=${if (tile.kind == TileKind.STAR) "star" else "dashboard"}:$encoded"
}
