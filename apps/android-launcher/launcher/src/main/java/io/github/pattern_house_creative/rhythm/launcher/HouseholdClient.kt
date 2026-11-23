package io.github.pattern_house_creative.rhythm.launcher

import android.webkit.CookieManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

data class RhythmItem(val id: String, val title: String, val dashboard: Boolean)
data class SkyLink(val from: String, val to: String, val verified: Boolean = true)
data class Household(val dashboards: List<RhythmItem>, val stars: List<RhythmItem>, val links: List<SkyLink>, val coverage: String = "Server discovery view; not all household records.", val previewsReady: Boolean = true, val warning: String = "", val builtins: List<RhythmItem> = emptyList())

internal class HouseholdAccessDenied : Exception()

class HouseholdClient(
    private val origin: String,
    private val now: () -> Long = android.os.SystemClock::elapsedRealtime,
) {
    init { require(householdOrigin(origin) == origin) }
    private suspend fun read(path: String, body: JSONObject? = null): String = withContext(Dispatchers.IO) {
        val connection = URL(origin + path).openConnection() as HttpURLConnection
        try {
            connection.instanceFollowRedirects = false
            connection.connectTimeout = 8000; connection.readTimeout = 22000
            connection.useCaches = false
            connection.setRequestProperty("Accept", "application/json")
            connection.setRequestProperty("Cache-Control", "no-store")
            connection.setRequestProperty("Origin", origin)
            withWebProvider { CookieManager.getInstance().getCookie(origin) }?.let { connection.setRequestProperty("Cookie", it) }
            if (body != null) {
                connection.requestMethod = "POST"; connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/json")
                connection.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            }
            if (connection.responseCode in listOf(401, 403)) throw HouseholdAccessDenied()
            check(connection.responseCode == 200 && connection.contentType?.startsWith("application/json") == true)
            connection.inputStream.use {
                val output = java.io.ByteArrayOutputStream()
                val chunk = ByteArray(8192)
                while (true) {
                    val count = it.read(chunk)
                    if (count < 0) break
                    check(output.size() + count <= 2_000_000)
                    output.write(chunk, 0, count)
                }
                val bytes = output.toByteArray()
                check(bytes.size <= 2_000_000)
                bytes.toString(Charsets.UTF_8)
            }
        } finally { connection.disconnect() }
    }
    private fun items(array: JSONArray?, board: Boolean): List<RhythmItem> = (0 until minOf(array?.length() ?: 0, 2000)).mapNotNull { i ->
        val row = array!!.optJSONObject(i) ?: return@mapNotNull null
        if (!board) {
            val resource = row.optJSONObject("resource") ?: return@mapNotNull null
            if (resource.optJSONObject("access")?.optBoolean("canRead") != true ||
                resource.optString("availability") !in listOf("available", "stale")) return@mapNotNull null
        }
        val id = row.optString("id"); val title = row.optString("title")
        if (id.length !in 1..1000 || title.length !in 1..500) null else RhythmItem(id, title, board)
    }
    suspend fun companionReady(requiredBuiltin: String? = null): Boolean {
        val marker = JSONObject(read("/launcher-companion.json"))
        return marker.optInt("version") == 1 && (requiredBuiltin == null ||
            marker.optJSONArray("builtins")?.let { a -> (0 until a.length()).any { a.optString(it) == requiredBuiltin } } == true)
    }

    private suspend fun optional(path: String, body: JSONObject? = null): JSONObject? = try { JSONObject(read(path, body)) }
        catch (e: kotlinx.coroutines.CancellationException) { throw e }
        catch (e: HouseholdAccessDenied) { throw e }
        catch (_: Exception) { null }

    private var graphCache: Household? = null
    private var graphReadAt = 0L
    internal var accessGeneration = 0L
        private set

    internal fun clearDiscovery() {
        graphCache = null
        accessGeneration++
    }

    suspend fun load(onDashboards: (Household) -> Unit = {}): Household = try {
        loadCurrent(onDashboards)
    } catch (denied: HouseholdAccessDenied) {
        clearDiscovery()
        throw denied
    }

    private suspend fun loadCurrent(onDashboards: (Household) -> Unit): Household {
        val generation = accessGeneration
        val instance = JSONObject(read("/api/instance"))
        // Never show an unconfigured server's fictional sky as the household.
        check(instance.optString("mode") == "live")
        val boards = optional("/api/dashboards")
        val marker = optional("/launcher-companion.json")
        val ready = marker?.optInt("version") == 1
        val builtins = if (ready && marker?.optJSONArray("builtins")?.let { a -> (0 until a.length()).any { a.optString(it) == "glucose" } } == true)
            listOf(RhythmItem(BUILTIN_GLUCOSE, "Glucose", true)) else emptyList()
        val dashboardItems = items(boards?.optJSONArray("dashboards"), true)
        val warning = if (boards == null) "Dashboard previews unavailable" else if (!ready) "Household companion update or access required." else ""
        // A slow/unavailable graph must not withhold dashboards already read.
        if (generation != accessGeneration) throw kotlinx.coroutines.CancellationException("Household changed")
        if (boards != null) onDashboards(Household(dashboardItems, emptyList(), emptyList(), "Loading the household sky.", ready, warning, builtins))
        val age = now() - graphReadAt
        val cached = graphCache
        if (cached != null && age in 0 until 300_000L && cached.dashboards == dashboardItems) {
            return cached.copy(previewsReady = ready, warning = warning, builtins = builtins)
        }
        val discovered = optional("/api/graph", JSONObject().put("operation", "discover"))
        if (generation != accessGeneration) throw kotlinx.coroutines.CancellationException("Household changed")
        val graph = discovered ?: JSONObject()
        val stars = items(graph.optJSONArray("nodes"), false)
        val ids = (stars + dashboardItems).map { it.id }.toSet()
        val edges = graph.optJSONArray("edges") ?: JSONArray()
        val links = (0 until minOf(edges.length(), 4000)).mapNotNull { i ->
            val edge = edges.optJSONObject(i) ?: return@mapNotNull null
            val from = edge.optString("source"); val to = edge.optString("target")
            if (from in ids && to in ids) SkyLink(from, to, edge.optString("verification") == "verified") else null
        }
        val received = graph.optJSONArray("nodes")?.length() ?: 0
        val processed = minOf(received, 2000)
        val more = graph.optString("nextCursor").let { it.isNotEmpty() && it != "null" } || graph.optString("completeness") != "complete"
        val coverage = "${dashboardItems.size} dashboards and ${stars.size} readable records from $received returned records; ${processed-stars.size} processed records excluded by access, availability or invalid metadata. " +
            "${maxOf(0,received-processed)} records beyond the local 2,000-record limit. ${links.size} relationships loaded from ${edges.length()} returned; at most 4,000 relationships processed, and both endpoints must be readable. " +
            "${if(more) "The server reports more or incomplete results. " else ""}Discovery is a server overview, not an inventory of all household stars. Search can find records outside this view."
        val result = Household(dashboardItems, stars, links, coverage, ready, if (discovered == null) "Sky unavailable; dashboard previews remain available." else warning, builtins)
        graphCache = result.takeIf { discovered != null }
        graphReadAt = now()
        return result
    }
    suspend fun search(query: String): List<RhythmItem> {
        require(query.trim().length in 1..200)
        return items(JSONObject(read("/api/graph", JSONObject().put("operation", "search")
            .put("value", query.trim()))).optJSONArray("nodes"), false)
    }

    internal suspend fun insights(): List<DailyInsight> {
        check(JSONObject(read("/api/instance")).optString("mode") == "live")
        val rows=JSONObject(read("/api/insights")).getJSONArray("insights")
        check(rows.length() <= 50)
        fun instant(value:String)=runCatching { java.time.Instant.parse(value).toEpochMilli() }.getOrNull()
        return (0 until rows.length()).mapNotNull { index ->
            runCatching {
                val row=rows.getJSONObject(index)
                fun text(name:String,max:Int):String = row.getString(name).also { check(it.length <= max) }
                val id=text("id",100).also { check(it.matches(Regex("[A-Za-z0-9_-]{1,100}"))) }
                val kind=text("kind",20).also { check(it in listOf("focus","discovery","suggestion")) }
                val title=text("title",120).also { check(it.isNotBlank()) }
                val status=text("status",20).also { check(it in listOf("new","saved","explored","snoozed","dismissed","superseded","handled")) }
                val feedback = row.optJSONObject("feedback")
                val points=row.getJSONArray("points");check(points.length() <= 30)
                val sources=row.getJSONArray("sources");check(sources.length() <= 30)
                DailyInsight(id,kind,title,text("summary",600),text("whyItMatters",600),text("nextStep",600),
                    text("limitations",1000),text("evidenceLevel",20),
                    instant(row.getString("generatedAt")) ?: error("Invalid date"),status,
                    row.optString("supersedes").takeIf { it.isNotBlank() && it != "null" },
                    instant(feedback?.optString("until").orEmpty()),
                    (0 until points.length()).map {
                        val point=points.getJSONObject(it)
                        val type=point.getString("type").also { t -> check(t in listOf("observed","sourced","inferred","hypothesis")) }
                        type.replaceFirstChar { it.uppercase() }+": "+point.getString("text").also { s -> check(s.length<=1200) }
                    },
                    (0 until sources.length()).map { sources.getJSONObject(it).getString("title").also { s -> check(s.length<=500) } },
                    InsightChoice.entries.firstOrNull { it.value == feedback?.optString("value") },
                    (feedback?.opt("note") as? String).orEmpty().also { check(it.length <= 500) })
            }.getOrNull()
        }
    }

    internal suspend fun saveInsightFeedback(id: String, choice: InsightChoice, note: String, until: java.time.Instant?) {
        require(id.matches(Regex("[A-Za-z0-9_-]{1,100}")) && note.length <= 500)
        require(choice != InsightChoice.Handled || note.isNotBlank())
        require((choice == InsightChoice.Later) == (until != null))
        val body = JSONObject().put("id", id).put("value", choice.value)
        if (note.isNotBlank()) body.put("note", note.trim())
        if (until != null) body.put("until", until.toString())
        val saved = JSONObject(read("/api/insights", body))
        val feedback = saved.getJSONObject("feedback")
        check(saved.getString("id") == id && saved.getString("status") == choice.status &&
            feedback.getString("value") == choice.value && (feedback.opt("note") as? String).orEmpty() == note.trim())
        if (until != null) check(java.time.Instant.parse(feedback.getString("until")) == until)
    }
}
