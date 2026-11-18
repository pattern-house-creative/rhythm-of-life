package io.github.pattern_house_creative.rhythm.launcher

import android.content.ComponentName
import android.content.Context
import android.content.pm.LauncherApps
import android.graphics.Bitmap
import android.graphics.Canvas
import android.os.Process
import android.os.UserHandle
import android.os.UserManager

data class LaunchApp(val tile: Tile, val title: String, val icon: Bitmap, val component: ComponentName, val profile: UserHandle)

internal fun installed(context: Context): List<LaunchApp> {
    val launcher = context.getSystemService(LauncherApps::class.java)
    val users = context.getSystemService(UserManager::class.java)
    val current = Process.myUserHandle()
    return launcher.profiles.flatMap { profile ->
        try {
            val serial = users.getSerialNumberForUser(profile)
            if (profile != current && serial < 0) return@flatMap emptyList()
            launcher.getActivityList(null, profile).filter { it.componentName.packageName != context.packageName }.mapNotNull { info ->
                try {
                    val component = info.componentName
                    val flat = component.flattenToString()
                    // Keep existing personal pins valid; the same app may also be installed at work.
                    val target = if (profile == current) flat else "profile-$serial:$flat"
                    val icon = info.getBadgedIcon(context.resources.displayMetrics.densityDpi)
                    val bitmap = Bitmap.createBitmap(96, 96, Bitmap.Config.ARGB_8888)
                    icon.setBounds(0, 0, 96, 96)
                    icon.draw(Canvas(bitmap))
                    val label = context.packageManager.getUserBadgedLabel(info.label, profile).toString()
                    LaunchApp(Tile(TileKind.APP, target), label, bitmap, component, profile)
                } catch (_: RuntimeException) { null } // The activity can disappear during enumeration.
            }
        } catch (_: SecurityException) { emptyList() }
        catch (_: IllegalStateException) { emptyList() } // A profile may be locked or switching.
    }.distinctBy { it.tile.key }.sortedBy { it.title.lowercase() }
}

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

internal fun observeApps(context: Context, changed: () -> Unit): () -> Unit {
    val launcher = context.getSystemService(LauncherApps::class.java)
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
