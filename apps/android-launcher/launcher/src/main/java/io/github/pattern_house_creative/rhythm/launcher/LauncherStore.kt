package io.github.pattern_house_creative.rhythm.launcher

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject

class LauncherStore(context: Context) {
    private val prefs = context.getSharedPreferences("phone-home-v1", Context.MODE_PRIVATE)
    var origin: String
        get() = prefs.getString("origin", "").orEmpty().let { householdOrigin(it).orEmpty() }
        set(value) { prefs.edit().putString("origin", value).apply() }
    var still: Boolean
        get() = prefs.getBoolean("still", false)
        set(value) { prefs.edit().putBoolean("still", value).apply() }
    var alphabeticalDrawer: Boolean
        get() = prefs.getBoolean("alphabetical-drawer", false)
        set(value) { prefs.edit().putBoolean("alphabetical-drawer", value).apply() }
    internal var iconStyle: IconStyle
        get() = IconStyle.of(prefs.getString("icon-style", null))
        set(value) { prefs.edit().putString("icon-style", value.name).apply() }
    val initialized get() = prefs.contains("layout")
    fun initialPreviews(layout: HomeLayout, dashboardIds: List<String>): HomeLayout {
        if (origin.isBlank() || dashboardIds.isEmpty() || prefs.getString("preview-setup-origin", null) == origin) return layout
        val next = layout.initialPreviews(dashboardIds.filterNot { it in removedPreviews() })
        save(next)
        // Once a person removes a preview, reconnecting must not add it back.
        prefs.edit().putString("preview-setup-origin", origin).apply()
        return next
    }
    private fun previewRemovals()=runCatching {JSONObject(prefs.getString("removed-previews-v1","{}").orEmpty())}.getOrDefault(JSONObject())
    private fun removedPreviews():Set<String> {
        val rows=previewRemovals().optJSONArray(origin) ?: return emptySet()
        return (0 until rows.length()).map{rows.optString(it)}.toSet()
    }
    fun recordPreviewRemoval(id:String) {
        if(origin.isBlank())return
        val records=previewRemovals();records.put(origin,JSONArray((removedPreviews()+id).toList()))
        prefs.edit().putString("removed-previews-v1",records.toString()).putString("preview-setup-origin",origin).apply()
    }
    fun allowPreview(id:String) {
        if(origin.isBlank() || id !in removedPreviews())return
        val records=previewRemovals();records.put(origin,JSONArray((removedPreviews()-id).toList()))
        prefs.edit().putString("removed-previews-v1",records.toString()).apply()
    }
    fun load(): HomeLayout = runCatching {
        val json = JSONObject(prefs.getString("layout", "{}").orEmpty())
        fun read(name: String): List<Tile> {
            val array = json.optJSONArray(name) ?: return emptyList()
            return (0 until array.length()).mapNotNull { i -> runCatching {
                val row = array.getJSONObject(i)
                val target = row.getString("target")
                require(target.length in 1..1000)
                Tile(TileKind.valueOf(row.getString("kind")), target)
            }.getOrNull() }
        }
        HomeLayout(read("tiles"), read("dock"), readBreaks(json)).normalized()
    }.getOrDefault(HomeLayout())
    // "tiles" keeps every Home pin in reading order, as before pages existed, so an older build still
    // finds them all. "pages" says how many of them each page holds and what kind of page it is. A layout
    // saved without pages loads as one page, which the Home screen then splits to fit.
    private fun readBreaks(json: JSONObject): List<Int> {
        val pages = json.optJSONArray("pages") ?: return emptyList()
        var at = 0
        return (0 until pages.length()).mapNotNull { i ->
            val row = pages.optJSONObject(i) ?: return@mapNotNull null
            if (HomePageKind.entries.none { it.name == row.optString("kind") }) return@mapNotNull null
            at += row.optInt("size", 0).coerceAtLeast(0); at
        }.dropLast(1)
    }
    fun save(layout: HomeLayout) {
        fun rows(tiles: List<Tile>) = JSONArray().apply { tiles.forEach {
            put(JSONObject().put("kind", it.kind.name).put("target", it.target))
        } }
        val clean = layout.normalized()
        val pages = JSONArray().apply { clean.pageLists().forEach {
            put(JSONObject().put("kind", HomePageKind.GRID.name).put("size", it.size))
        } }
        prefs.edit().putString("layout", JSONObject().put("tiles", rows(clean.tiles)).put("pages", pages)
            .put("dock", rows(clean.dock)).toString()).apply()
    }
}
