package io.github.pattern_house_creative.rhythm.launcher

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.ColorFilter
import android.graphics.PixelFormat
import android.graphics.drawable.AdaptiveIconDrawable
import android.graphics.drawable.ColorDrawable
import android.graphics.drawable.Drawable
import android.graphics.drawable.InsetDrawable
import android.graphics.drawable.ShapeDrawable
import android.graphics.drawable.shapes.OvalShape
import android.os.Build
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test

// Synthetic icons only: a white face with a red disc, the shape that looks pasted-on against the sky.
class AppIconStyleTest {
    private val size = 144
    private val red = 0xffe53935.toInt()
    private fun disc(colour: Int) = InsetDrawable(ShapeDrawable(OvalShape()).apply { paint.color = colour }, .3f)
    private fun icon(foreground: Drawable = disc(red), monochrome: Drawable? = null) =
        if (monochrome != null && Build.VERSION.SDK_INT >= 33) AdaptiveIconDrawable(ColorDrawable(Color.WHITE), foreground, monochrome)
        else AdaptiveIconDrawable(ColorDrawable(Color.WHITE), foreground)
    private fun Bitmap.anyWhite() = (0 until width).any { x -> (0 until height).any { y ->
        getPixel(x, y).let { Color.alpha(it) > 200 && Color.red(it) > 235 && Color.green(it) > 235 && Color.blue(it) > 235 } } }
    private fun near(expected: Int, actual: Int) =
        maxOf(Math.abs(Color.red(expected) - Color.red(actual)), Math.abs(Color.green(expected) - Color.green(actual)),
            Math.abs(Color.blue(expected) - Color.blue(actual))) <= 4 && Color.alpha(actual) == 255

    @Test fun skyDrawsTheAppAsAnInkedGlyphWithoutItsWhiteFace() {
        val tile = styledIcon(icon(), IconStyle.SKY, size)
        assertFalse(tile.anyWhite())
        assertTrue("a red app takes the warm ink", near(SKY_INKS[2], tile.getPixel(size / 2, size / 2)))
        assertEquals("the tile's rounded corner is clear", 0, Color.alpha(tile.getPixel(0, 0)))
    }

    @Test fun skyUsesTheAppsOwnThemedGlyphWhenItHasOne() {
        assumeTrue(Build.VERSION.SDK_INT >= 33)
        val tile = styledIcon(icon(ColorDrawable(red), InsetDrawable(ColorDrawable(Color.BLACK), .4f)), IconStyle.SKY, size)
        assertTrue(near(SKY_INKS[2], tile.getPixel(size / 2, size / 2)))
        assertFalse("outside the themed glyph is the tile, not the app's art",
            near(SKY_INKS[2], tile.getPixel(size / 2 + 40, size / 2)) || Color.red(tile.getPixel(size / 2 + 40, size / 2)) > 150)
    }

    @Test fun glassKeepsTheAppsColoursWithoutItsWhiteFace() {
        val tile = styledIcon(icon(), IconStyle.GLASS, size)
        assertFalse(tile.anyWhite())
        assertTrue(near(red, tile.getPixel(size / 2, size / 2)))
    }

    @Test fun fullArtIsSetInTheTileAndDimmed() {
        val tile = styledIcon(icon(ColorDrawable(Color.WHITE)), IconStyle.GLASS, size)
        assertFalse(tile.anyWhite())
        assertTrue(Color.red(tile.getPixel(size / 2, size / 2)) > 200)
        // Sky has no glyph to draw from a blank white face and uses the same treatment.
        assertTrue(styledIcon(icon(ColorDrawable(Color.WHITE)), IconStyle.SKY, size).sameAs(tile))
    }

    @Test fun anIconThatFailsToStyleShowsAsAndroidDrawsIt() {
        val broken = object : Drawable() {
            override fun draw(canvas: Canvas) = throw IllegalStateException("broken icon")
            override fun setAlpha(alpha: Int) {}
            override fun setColorFilter(colorFilter: ColorFilter?) {}
            @Deprecated("Required by Drawable") override fun getOpacity() = PixelFormat.TRANSLUCENT
        }
        val original = styledIcon(icon(), IconStyle.ORIGINAL, size)
        IconStyle.entries.forEach { style -> assertSame(style.name, original, styledOrOriginal(style, { styledIcon(broken, style, size) }, { original })) }
    }

    @Test fun everyStyleListsTheSameApps() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val keys = IconStyle.entries.map { style -> installed(context, style).map { it.tile.key } }
        assertTrue(keys.first().isNotEmpty())
        keys.forEach { assertEquals(keys.first(), it) }
        // The listing comes first, before any icon is drawn, with the same apps in the same order.
        assertEquals(keys.first(), listApps(context, IconStyle.SKY).apps.map { it.tile.key })
    }

    @Test fun originalIsAsAndroidDrawsIt() {
        val tile = styledIcon(icon(), IconStyle.ORIGINAL, size)
        assertTrue(near(red, tile.getPixel(size / 2, size / 2)))
        assertTrue(tile.anyWhite())
    }
}
