package io.github.pattern_house_creative.rhythm.launcher

// What Settings says about the household address. The consequence is the one MainActivity's changeOrigin
// has when an address that differs from the saved one is saved: it clears the WebView's cookies and web
// storage, and unpins every star, dashboard and preview (the built-in glucose card included) from Home.
// Apps stay on Home and in the dock, which only ever holds apps.

internal const val ADDRESS_LABEL = "Household address"
internal const val ADDRESS_EXAMPLE = "https://household.example"
internal const val ADDRESS_HELP = "Use your household’s HTTPS address. Sign-in must stay at that address."
internal const val ADDRESS_ERROR = "Enter an HTTPS address with just the host and optional port, such as $ADDRESS_EXAMPLE."

/** Saving the address already saved only refreshes Home: "Save". Anything else is an address to save, or to correct. */
internal fun addressSaveLabel(entered: String, saved: String): String =
    if (householdOrigin(entered) == saved) "Save" else "Save address"

/**
 * What saving [entered] will do, shown before it is saved; null when it is the saved address, or not one that can
 * be saved (Save then shows [ADDRESS_ERROR] instead). The removal is named only when Home holds something to remove.
 */
internal fun addressChangeNotice(entered: String, saved: String, layout: HomeLayout): String? {
    val next = householdOrigin(entered) ?: return null
    if (next == saved) return null
    return if (layout.tiles.any { it.kind != TileKind.APP })
        "Saving a new address clears the launcher’s web session and removes pinned stars, dashboards and previews " +
            "from Home. Your apps stay on Home and in the dock."
    else "Saving a new address clears the launcher’s web session."
}
