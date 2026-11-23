package io.github.pattern_house_creative.rhythm.launcher

import android.content.ComponentName
import android.content.Context
import android.content.pm.LauncherActivityInfo
import android.content.pm.LauncherApps
import android.graphics.Bitmap
import android.os.Process
import android.os.UserHandle
import android.os.UserManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

data class LaunchApp(val tile: Tile, val title: String, val icon: Bitmap, val component: ComponentName, val profile: UserHandle)

// Drawn icons for the current style, kept while the process lives: Home redraws its app list on every return,
// and a styled icon is worth drawing once. Any package change or a new style or size starts again.
private object DrawnIcons {
    private val icons = java.util.concurrent.ConcurrentHashMap<String, Bitmap>()
    @Volatile private var drawnWith = ""
    fun forget() = icons.clear()
    // The style in the key keeps an overlapping redraw in another style honest.
    private fun key(target: String, style: IconStyle, size: Int) = "$style@$size|$target"
    fun peek(target: String, style: IconStyle, size: Int): Bitmap? = icons[key(target, style, size)]
    fun get(target: String, style: IconStyle, size: Int, draw: () -> Bitmap): Bitmap {
        val with = "$style@$size"
        if (drawnWith != with) { icons.clear(); drawnWith = with }
        return icons.getOrPut(key(target, style, size), draw)
    }
}

/** Installed apps listed before their icons are drawn: each app has its icon already drawn in [style], or a
 * plain tile until [drawIcons] draws the ones [pending] names. Labels and pins work from the first list. */
internal class AppListing(val apps: List<LaunchApp>, val style: IconStyle, val size: Int,
    internal val pending: List<Pair<Int, LauncherActivityInfo>>)

internal fun listApps(context: Context, style: IconStyle): AppListing {
    val launcher = context.getSystemService(LauncherApps::class.java)
    val size = iconPixels(context)
    val users = context.getSystemService(UserManager::class.java)
    val current = Process.myUserHandle()
    val placeholder by lazy { placeholderIcon(style, size) }
    val found = launcher.profiles.flatMap { profile ->
        try {
            val serial = users.getSerialNumberForUser(profile)
            if (profile != current && serial < 0) return@flatMap emptyList()
            launcher.getActivityList(null, profile).filter { it.componentName.packageName != context.packageName }.mapNotNull { info ->
                try {
                    val component = info.componentName
                    val flat = component.flattenToString()
                    // Keep existing personal pins valid; the same app may also be installed at work.
                    val target = if (profile == current) flat else "profile-$serial:$flat"
                    val label = context.packageManager.getUserBadgedLabel(info.label, profile).toString()
                    val drawn = DrawnIcons.peek(target, style, size)
                    LaunchApp(Tile(TileKind.APP, target), label, drawn ?: placeholder, component, profile) to info.takeIf { drawn == null }
                } catch (_: RuntimeException) { null } // The activity can disappear during enumeration.
            }
        } catch (_: SecurityException) { emptyList() }
        catch (_: IllegalStateException) { emptyList() } // A profile may be locked or switching.
    }.distinctBy { it.first.tile.key }.sortedBy { it.first.title.lowercase() }
    return AppListing(found.map { it.first }, style, size, found.mapIndexedNotNull { i, (_, info) -> info?.let { i to it } })
}

private fun drawIcon(context: Context, listing: AppListing, index: Int, info: LauncherActivityInfo): Bitmap {
    val app = listing.apps[index]
    return DrawnIcons.get(app.tile.target, listing.style, listing.size) {
        // An activity removed since listing keeps its tile until the package change refreshes the list.
        try { appIcon(context, info, app.profile, listing.style, listing.size) }
        catch (_: RuntimeException) { placeholderIcon(listing.style, listing.size) }
    }
}

/** Every installed app with its icon drawn. */
internal fun installed(context: Context, style: IconStyle = IconStyle.DEFAULT): List<LaunchApp> {
    val listing = listApps(context, style)
    return listing.apps.toMutableList().also { apps ->
        listing.pending.forEach { (i, info) -> apps[i] = apps[i].copy(icon = drawIcon(context, listing, i, info)) }
    }
}

/** Draws the listing's missing icons in the background, the apps keyed in [first] (those on Home and in the dock)
 * before the rest, handing [show] the apps with every icon drawn so far about every [ICON_BATCH_NS], so the list
 * changes a few times rather than once per icon. [show] runs in the caller's context. */
internal suspend fun drawIcons(context: Context, listing: AppListing, first: Set<String> = emptySet(), show: (List<LaunchApp>) -> Unit) {
    val apps = listing.apps.toMutableList()
    val pending = listing.pending.sortedBy { (i, _) -> listing.apps[i].tile.key !in first }
    var next = 0
    while (next < pending.size) {
        val batch = withContext(Dispatchers.Default) {
            val start = System.nanoTime()
            buildList {
                while (next < pending.size && (isEmpty() || System.nanoTime() - start < ICON_BATCH_NS)) {
                    ensureActive()
                    val (i, info) = pending[next++]
                    add(i to drawIcon(context, listing, i, info))
                }
            }
        }
        batch.forEach { (i, icon) -> apps[i] = apps[i].copy(icon = icon) }
        show(apps.toList())
    }
}

private const val ICON_BATCH_NS = 120_000_000L

// A pinned tile can be tapped before icon discovery completes after a cold start.
internal fun launchInstalled(context: Context, target: String, loaded: LaunchApp?) {
    val launcher = context.getSystemService(LauncherApps::class.java)
    if (loaded != null) {
        launcher.startMainActivity(loaded.component, loaded.profile, null, null)
        return
    }
    val separator = target.indexOf(':')
    val profile = if (separator < 0) Process.myUserHandle() else {
        val serial = target.substring(0, separator).removePrefix("profile-").toLong()
        requireNotNull(context.getSystemService(UserManager::class.java).getUserForSerialNumber(serial))
    }
    val component = requireNotNull(ComponentName.unflattenFromString(target.substring(separator + 1)))
    launcher.startMainActivity(component, profile, null, null)
}

internal fun observeApps(context: Context, onChange: () -> Unit): () -> Unit {
    val launcher = context.getSystemService(LauncherApps::class.java)
    val changed = { DrawnIcons.forget(); onChange() }
    val callback = object : LauncherApps.Callback() {
        override fun onPackageAdded(name: String, user: UserHandle) = changed()
        override fun onPackageRemoved(name: String, user: UserHandle) = changed()
        override fun onPackageChanged(name: String, user: UserHandle) = changed()
        override fun onPackagesAvailable(names: Array<out String>, user: UserHandle, replacing: Boolean) = changed()
        override fun onPackagesUnavailable(names: Array<out String>, user: UserHandle, replacing: Boolean) = changed()
    }
    launcher.registerCallback(callback)
    return { launcher.unregisterCallback(callback) }
}
