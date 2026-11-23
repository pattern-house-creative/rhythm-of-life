package io.github.pattern_house_creative.rhythm.launcher

import org.junit.Assert.*
import org.junit.Test

class HouseholdAddressTest {
    private val saved = "https://household.example"
    private val apps = (0..5).map { Tile(TileKind.APP, "app$it") }
    private val pins = listOf(Tile(TileKind.PREVIEW, "board"), Tile(TileKind.DASHBOARD, "plan"), Tile(TileKind.STAR, "star"),
        Tile(TileKind.PREVIEW, BUILTIN_GLUCOSE))
    private val mixed = HomeLayout(apps.take(3) + pins + apps.drop(3), apps.take(4))

    @Test fun saveNamesTheAddressUnlessItOnlyRefreshesTheSavedOne() {
        assertEquals("Save", addressSaveLabel(saved, saved))
        // Spelled differently, the same origin: Settings refreshes Home and saves nothing.
        assertEquals("Save", addressSaveLabel(" https://Household.Example/ ", saved))
        assertEquals("Save address", addressSaveLabel("https://other.example", saved))
        assertEquals("Save address", addressSaveLabel(saved, ""))
        assertEquals("Save address", addressSaveLabel("", ""))
        assertEquals("Save address", addressSaveLabel("http://household.example", saved))
    }

    @Test fun theNoticeShowsOnlyForANewAddressThatCanBeSaved() {
        assertNull(addressChangeNotice(saved, saved, mixed))
        assertNull(addressChangeNotice("https://household.example:443/", "https://household.example:443", mixed))
        for (invalid in listOf("", "household.example", "http://household.example", "https://household.example/app",
            "https://sam@household.example", "https://household.example/?a=1", "https://household.example/#top"))
            assertNull(invalid, addressChangeNotice(invalid, saved, mixed))
        assertNotNull(addressChangeNotice("https://other.example", saved, mixed))
        // No address saved yet (a first connection, or after Disconnect) is still a change.
        assertNotNull(addressChangeNotice(saved, "", mixed))
    }

    @Test fun theNoticeSaysWhatChangingTheAddressRemovesAndKeeps() {
        val notice = addressChangeNotice("https://other.example", saved, mixed)!!
        assertEquals("Saving a new address clears the launcher’s web session and removes pinned stars, dashboards and " +
            "previews from Home. Your apps stay on Home and in the dock.", notice)
        // What MainActivity's changeOrigin saves for a new address: every pin that is not an app leaves Home.
        val after = mixed.tiles.filter { it.kind != TileKind.APP }.fold(mixed) { kept, tile -> kept.removeHome(tile.key) }
            .copy(dock = mixed.dock.filter { it.kind == TileKind.APP })
        assertEquals(apps, after.tiles)
        assertEquals(mixed.dock, after.dock)
        assertTrue(after.tiles.none { it in pins })
        // The dock never holds anything but apps, so the notice names Home alone for what is removed.
        assertEquals(apps.take(2), HomeLayout(dock = apps.take(2) + pins).normalized().dock)
        assertEquals(mixed, mixed.insertDock(pins[0], 0))
    }

    @Test fun anAppsOnlyHomeIsToldOnlyAboutTheWebSession() {
        assertEquals("Saving a new address clears the launcher’s web session.",
            addressChangeNotice("https://other.example", saved, HomeLayout(apps, apps.take(4))))
    }

    @Test fun theErrorDescribesExactlyWhatTheAddressCheckAccepts() {
        assertEquals("https://household.example", householdOrigin(ADDRESS_EXAMPLE))
        assertEquals("https://household.example:8443", householdOrigin("https://household.example:8443"))
        assertTrue(ADDRESS_ERROR.contains(ADDRESS_EXAMPLE))
        assertEquals("Enter an HTTPS address with just the host and optional port, such as https://household.example.", ADDRESS_ERROR)
    }
}
