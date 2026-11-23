package io.github.pattern_house_creative.rhythm.launcher

import org.junit.Assert.*
import org.junit.Test

/** What TalkBack is told a launcher control does, beside the handler that control runs. */
class LauncherLabelsTest {
    private val app = Tile(TileKind.APP, "example/.Camera")
    private val preview = Tile(TileKind.PREVIEW, "garden")
    private val star = Tile(TileKind.STAR, "star")

    @Test fun aTileWhileHomeIsEditedSaysItOpensItsOptions() {
        // MainActivity gives a Home tile onOpen = if (editHome) edit(tile) else open(tile), and editing = editHome.
        assertEquals("Edit Camera", tileActionLabel(app, "Camera", editing = true))
        assertEquals("Edit Garden", tileActionLabel(preview, "Garden", editing = true))
        assertEquals("Edit Notes", tileActionLabel(star, "Notes", editing = true))
    }

    @Test fun aTileOnOrdinaryHomeOrInTheDrawerSaysItOpens() {
        assertEquals("Open Camera", tileActionLabel(app, "Camera", editing = false))
        assertEquals("Open Notes", tileActionLabel(star, "Notes", editing = false))
        assertEquals("Open dashboard", tileActionLabel(preview, "Garden", editing = false))
    }

    @Test fun anEmptyDockSlotIsNamedForWhatItIs() {
        assertEquals("Empty dock slot 1", emptyDockSlotName(0))
        assertEquals("Empty dock slot 6", emptyDockSlotName(5))
    }

    @Test fun theSkyCountsTheStarsItDraws() {
        assertEquals("0 stars shown", starsShown(0))
        assertEquals("1 star shown", starsShown(1))
        assertEquals("362 stars shown", starsShown(362))
        assertEquals("About these stars", ABOUT_STARS)
    }
}
