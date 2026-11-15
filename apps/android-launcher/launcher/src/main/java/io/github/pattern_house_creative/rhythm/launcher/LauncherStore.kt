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
        HomeLayout(read("tiles"), read("dock")).normalized()
    }.getOrDefault(HomeLayout())
    fun save(layout: HomeLayout) {
        fun rows(tiles: List<Tile>) = JSONArray().apply { tiles.forEach {
            put(JSONObject().put("kind", it.kind.name).put("target", it.target))
        } }
        val clean = layout.normalized()
        prefs.edit().putString("layout", JSONObject().put("tiles", rows(clean.tiles))
            .put("dock", rows(clean.dock)).toString()).apply()
    }
}
