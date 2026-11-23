package io.github.pattern_house_creative.rhythm.launcher

import org.junit.Assert.*
import org.junit.Test

class AppIconsTest {
    private val size = 32
    private val clear = 0
    private val white = 0xffffffff.toInt()
    private val green = 0xff34a853.toInt()
    private val blue = 0xff1a73e8.toInt()
    private fun art(fill: Int, block: (x: Int, y: Int) -> Int? = { _, _ -> null }) =
        IntArray(size * size) { block(it % size, it / size) ?: fill }
    private fun centre(x: Int, y: Int, half: Int) = x in size / 2 - half until size / 2 + half && y in size / 2 - half until size / 2 + half
    private fun at(values: FloatArray, x: Int, y: Int) = values[y * size + x]

    @Test fun storedStyleFallsBackToTheRecommendedDefault() {
        assertEquals(IconStyle.SKY, IconStyle.DEFAULT)
        assertEquals(IconStyle.SKY, IconStyle.of(null))
        assertEquals(IconStyle.SKY, IconStyle.of("stars"))
        IconStyle.entries.forEach { assertEquals(it, IconStyle.of(it.name)) }
    }

    @Test fun inkFollowsTheAppsColourFamily() {
        val (mint, skyBlue, sand, lilac) = SKY_INKS.toList()
        assertEquals(mint, skyInk(art(green)))
        assertEquals(skyBlue, skyInk(art(blue)))
        assertEquals(sand, skyInk(art(0xffff9800.toInt())))
        assertEquals(sand, skyInk(art(0xffe53935.toInt())))
        assertEquals(lilac, skyInk(art(0xff8e24aa.toInt())))
        assertEquals(lilac, skyInk(art(0xffab47bc.toInt())))
    }

    @Test fun neutralOrManyColouredArtKeepsTheDefaultInk() {
        assertEquals(SKY_INKS[0], skyInk(art(white)))
        assertEquals(SKY_INKS[0], skyInk(art(0xff5f6368.toInt())))
        assertEquals(SKY_INKS[0], skyInk(art(clear)))
        // A small accent on a white face is not the app's colour family.
        assertEquals(SKY_INKS[0], skyInk(art(white) { x, y -> if (centre(x, y, 3)) blue else null }))
        assertEquals(SKY_INKS[1], skyInk(art(white) { x, y -> if (centre(x, y, 8)) blue else null }))
        val quarters = intArrayOf(0xffff0000.toInt(), 0xff80ff00.toInt(), 0xff00ffff.toInt(), 0xff8000ff.toInt())
        assertEquals(SKY_INKS[0], skyInk(art(clear) { x, y -> quarters[(if (x < size / 2) 0 else 1) + (if (y < size / 2) 0 else 2)] }))
    }

    @Test fun glyphOnAClearLayerKeepsItsOutlineAndDimsBackdropColouredParts() {
        val layer = art(clear) { x, y -> when { centre(x, y, 5) -> white; x < 6 && y < 6 -> green; else -> null } }
        val glyph = glyphAlpha(layer, green)!!
        assertEquals(1f, at(glyph, size / 2, size / 2), .01f)
        assertEquals(.3f, at(glyph, 2, 2), .01f)
        assertEquals(0f, at(glyph, size - 1, size - 1), 0f)
    }

    @Test fun softShadowsAreDroppedAndALegacyShapeIsItsOwnOutline() {
        val shadowed = art(clear) { x, y -> when { centre(x, y, 5) -> 0xff4285f4.toInt(); x > size / 2 && y > size / 2 -> 0x33000000; else -> null } }
        val glyph = glyphAlpha(shadowed, null)!!
        assertEquals(1f, at(glyph, size / 2 - 2, size / 2 - 2), .01f)
        assertEquals("shadow", 0f, at(glyph, size - 2, size - 2), 0f)
        assertNull("a clear ring has no backdrop colour", borderColour(shadowed, size))
        val kept = withoutShadows(intArrayOf(0x33000000, 0xffea4335.toInt(), 0x80ea4335.toInt()))
        assertEquals(0, kept[0])
        assertEquals(0xffea4335.toInt(), kept[1])
        assertTrue("a soft edge stays soft", (kept[2] ushr 24) in 1..254 && kept[2] and 0xffffff == 0xea4335)
    }

    @Test fun fullBleedArtKeepsOnlyWhatDiffersFromItsBorder() {
        val face = art(blue) { x, y -> if (centre(x, y, 5)) white else null }
        assertEquals(blue, borderColour(face, size))
        val glyph = glyphAlpha(face, borderColour(face, size))!!
        assertEquals(1f, at(glyph, size / 2, size / 2), .01f)
        assertEquals(0f, at(glyph, 1, 1), .01f)
    }

    @Test fun aDerivedGlyphIsCentredAtTheThemedGlyphSize() {
        val large = glyphFrame(FloatArray(size * size) { if (it % size in 2 until 30 && it / size in 4 until 26) 1f else 0f }, size)!!
        assertEquals(GlyphFrame(2, 4, 30, 26, GLYPH_SHARE * size / 28), large)
        val small = glyphFrame(FloatArray(size * size) { if (it % size in 0 until 4 && it / size in 0 until 2) 1f else 0f }, size)!!
        assertEquals("never blown up past 1.2x", 1.2f, small.scale, 0f)
        assertNull(glyphFrame(FloatArray(size * size), size))
    }

    @Test fun artWithoutAUsableGlyphFallsBack() {
        assertNull("flat full-bleed art", glyphAlpha(art(blue), blue))
        assertNull("nothing drawn", glyphAlpha(art(clear), blue))
        assertNull("a speck", glyphAlpha(art(clear) { x, y -> if (x == 3 && y == 3) white else null }, blue))
        // Art mostly unlike its backdrop is a picture, which one ink cannot carry.
        val picture = art(white) { x, y -> if (x + y < 36) 0xff202124.toInt() else null }
        assertNull("a picture", glyphAlpha(picture, white))
    }

    @Test fun onlyALightGlyphOnAClearLayerKeepsItsOwnColours() {
        assertTrue(keepsOwnColours(art(clear) { x, y -> if (centre(x, y, 8)) 0xffea4335.toInt() else null }))
        assertFalse("full art keeps its backdrop", keepsOwnColours(art(white) { x, y -> if (centre(x, y, 8)) blue else null }))
        assertFalse("a dark glyph would vanish on the tile", keepsOwnColours(art(clear) { x, y -> if (centre(x, y, 8)) 0xff202124.toInt() else null }))
        assertFalse(keepsOwnColours(art(clear)))
    }

    @Test fun aStyleThatFailsToDrawFallsBackToTheOriginalIcon() {
        assertEquals("original", styledOrOriginal<String>(IconStyle.SKY, { throw IllegalStateException("broken icon") }, { "original" }))
        assertEquals("original", styledOrOriginal<String>(IconStyle.GLASS, { throw IllegalArgumentException() }, { "original" }))
        assertEquals("sky", styledOrOriginal(IconStyle.SKY, { "sky" }, { "original" }))
        assertEquals("original", styledOrOriginal(IconStyle.ORIGINAL, { fail("Original is never styled"); "" }, { "original" }))
    }

    @Test fun colourBasics() {
        assertEquals(0f, colourDistance(blue, blue), 0f)
        assertEquals(colourDistance(blue, white), colourDistance(white, blue), .001f)
        assertEquals(765f, colourDistance(white, 0xff000000.toInt()), 1f)
        assertEquals(120f, hsv(0xff00ff00.toInt()).first, .5f)
        assertEquals(0f, hsv(white).second, 0f)
        assertEquals(1.0, coverage(art(white)), 1e-9)
        assertEquals(0.0, coverage(art(clear)), 1e-9)
    }
}
