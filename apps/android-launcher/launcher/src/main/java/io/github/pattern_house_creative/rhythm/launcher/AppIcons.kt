package io.github.pattern_house_creative.rhythm.launcher

import android.content.Context
import android.content.pm.LauncherActivityInfo
import android.graphics.*
import android.graphics.drawable.AdaptiveIconDrawable
import android.graphics.drawable.BitmapDrawable
import android.graphics.drawable.Drawable
import android.os.Build
import android.os.UserHandle
import kotlin.math.*

/** How app icons are drawn on Home, in the dock and in the drawer. */
internal enum class IconStyle(val label: String) {
    /** Each app as a glyph in the launcher's inks on a sky tile. */
    SKY("Sky"),
    /** The app's own colours on a sky tile, without its light backdrop. */
    GLASS("Glass"),
    /** As Android draws it. */
    ORIGINAL("Original");
    companion object {
        val DEFAULT = SKY
        fun of(stored: String?) = entries.firstOrNull { it.name == stored } ?: DEFAULT
    }
}

// The inks the launcher already draws dashboard glyphs with (DashboardGlyph): mint first, the default.
internal val SKY_INKS = intArrayOf(0xffa0d7bf.toInt(), 0xffb5c9e0.toInt(), 0xffd2c7a0.toInt(), 0xffc5b5d6.toInt())
// The live-preview card's panel and edge (HomeSurface), so icons sit on the same glass as previews.
private const val TILE_FILL = 0x99152d29.toInt()
private const val TILE_EDGE_ALPHA = 0x2b
private const val TILE_INK_WASH = 0.10f
/** At or above this share of the viewport, a layer is art rather than a glyph on a backdrop. */
internal const val FULL_BLEED = .75f
/** Below this colour distance (of about 765) art has nothing to tell a glyph from its backdrop. */
private const val MIN_CONTRAST = 90f
/** A glyph covering more of the tile than this is a picture, which a single ink cannot carry. */
private const val MAX_GLYPH = .55
/** How far across the tile a glyph drawn from an app's art reaches, as Android's themed glyphs do. */
internal const val GLYPH_SHARE = .56f
/** Colours are read from a small drawing: an ink or a backdrop needs no more pixels than this across. */
private const val SAMPLE = 32

private fun alpha(c: Int) = c ushr 24
private fun red(c: Int) = (c shr 16) and 255
private fun green(c: Int) = (c shr 8) and 255
private fun blue(c: Int) = c and 255
private fun luminance(c: Int) = (.2126f * red(c) + .7152f * green(c) + .0722f * blue(c)) / 255f

private fun value(c: Int) = maxOf(red(c), green(c), blue(c)) / 255f
private fun saturation(c: Int) = maxOf(red(c), green(c), blue(c)).let { max -> if (max == 0) 0f else (max - minOf(red(c), green(c), blue(c))) / max.toFloat() }
/** Hue of an ARGB colour in degrees. */
private fun hue(c: Int): Float {
    val r = red(c).toFloat(); val g = green(c).toFloat(); val b = blue(c).toFloat()
    val max = maxOf(r, g, b); val delta = max - minOf(r, g, b)
    return when {
        delta == 0f -> 0f
        max == r -> 60f * (((g - b) / delta).mod(6f))
        max == g -> 60f * ((b - r) / delta + 2f)
        else -> 60f * ((r - g) / delta + 4f)
    }
}
/** Hue in degrees, saturation and value (0 to 1) of an ARGB colour. */
internal fun hsv(c: Int) = Triple(hue(c), saturation(c), value(c))

/** Perceptual ("redmean") distance between two colours, 0 to about 765. */
internal fun colourDistance(a: Int, b: Int): Float {
    val mean = (red(a) + red(b)) / 2f
    val dr = (red(a) - red(b)).toFloat(); val dg = (green(a) - green(b)).toFloat(); val db = (blue(a) - blue(b)).toFloat()
    return sqrt((2f + mean / 256f) * dr * dr + 4f * dg * dg + (2f + (255f - mean) / 256f) * db * db)
}

private fun hueGap(a: Float, b: Float) = abs(a - b).let { min(it, 360f - it) }

/** The launcher ink nearest the app's own colour family, so a green app stays mint and a blue one blue.
 * Mostly neutral art, or art in many families at once, keeps the default ink. */
internal fun skyInk(art: IntArray, inks: IntArray = SKY_INKS): Int {
    var x = 0.0; var y = 0.0; var weight = 0.0; var opaque = 0; var coloured = 0
    for (p in art) {
        if (alpha(p) < 128) continue
        opaque++
        val s = saturation(p)
        if (s < .35f || value(p) < .25f) continue
        coloured++
        val angle = Math.toRadians(hue(p).toDouble())
        x += cos(angle) * s; y += sin(angle) * s; weight += s
    }
    if (opaque == 0 || coloured < opaque * .08 || hypot(x, y) / weight < .3) return inks[0]
    val mean = ((Math.toDegrees(atan2(y, x)) + 360) % 360).toFloat()
    return inks.minBy { hueGap(hue(it), mean) }
}

/** The value at [share] of the way through [count] values counted in [histogram] (index = value). */
private fun quantile(histogram: IntArray, count: Int, share: Double): Int {
    val rank = min(count - 1, (count * share).toInt()); var seen = 0
    histogram.forEachIndexed { value, n -> seen += n; if (seen > rank) return value }
    return histogram.size - 1
}

/** Per-channel median of the solid pixels in the outer ring of a [size]-wide square, or null when the ring is clear. */
internal fun borderColour(art: IntArray, size: Int): Int? {
    val r = IntArray(256); val g = IntArray(256); val b = IntArray(256); var n = 0
    val centre = (size - 1) / 2f
    art.forEachIndexed { i, p ->
        if (alpha(p) < 128 || max(abs(i % size - centre), abs(i / size - centre)) < size * .38f) return@forEachIndexed
        r[red(p)]++; g[green(p)]++; b[blue(p)]++; n++
    }
    return if (n < 16) null else (0xff shl 24) or (quantile(r, n, .5) shl 16) or (quantile(g, n, .5) shl 8) or quantile(b, n, .5)
}

internal fun coverage(art: IntArray) = art.sumOf { alpha(it).toDouble() } / (255.0 * art.size)

/** Opacity with soft shadows (the faint long shadows many icon layers carry) taken out; edges stay smooth. */
private fun solid(opacity: Float) = ((opacity - .35f) / .25f).coerceIn(0f, 1f).let { it * it * (3 - 2 * it) }

/** A clear layer's pixels with its soft shadows taken out. */
internal fun withoutShadows(layer: IntArray) = IntArray(layer.size) {
    ((solid(alpha(layer[it]) / 255f) * 255).roundToInt() shl 24) or (layer[it] and 0xffffff)
}

/** Alpha (0 to 1) of a single-ink glyph drawn from full-colour [art], or null when the art holds no usable glyph.
 * [reference] is the colour the glyph stands out from: the backdrop layer, or the art's own border when the
 * art fills the tile; with none, the glyph is the art's outline. A glyph on a clear layer keeps its outline,
 * drops soft shadows and dims parts that match the backdrop; full-bleed art keeps only what differs from its border. */
internal fun glyphAlpha(art: IntArray, reference: Int?): FloatArray? {
    val covered = coverage(art)
    if (covered < .01) return null
    val outline = covered < FULL_BLEED
    val floor = if (outline) .3f else 0f
    val distance = FloatArray(art.size) { if (alpha(art[it]) == 0 || reference == null) 0f else colourDistance(art[it], reference) }
    val spread = IntArray(800); var n = 0
    for (i in art.indices) if (alpha(art[i]) >= 128) { spread[min(799, distance[i].toInt())]++; n++ }
    val top = if (n == 0) 0f else quantile(spread, n, .95).toFloat()
    val flat = top < MIN_CONTRAST
    if (flat && !outline) return null
    val low = top * .15f; val span = max(top - low, 1e-3f)
    val glyph = FloatArray(art.size) { i ->
        val detail = if (flat) 1f else ((distance[i] - low) / span).coerceIn(0f, 1f)
        val body = alpha(art[i]) / 255f
        (if (outline) solid(body) else body) * (floor + (1 - floor) * detail)
    }
    return glyph.takeIf { it.average() in .015..MAX_GLYPH }
}

/** Where a glyph drawn from an app's art sits: its drawn bounds ([left], [top], [right], [bottom], exclusive),
 * centred in the tile at [scale], so it reaches [GLYPH_SHARE] of the way across as Android's themed glyphs do. */
internal data class GlyphFrame(val left: Int, val top: Int, val right: Int, val bottom: Int, val scale: Float)

internal fun glyphFrame(alpha: FloatArray, size: Int): GlyphFrame? {
    var left = size; var top = size; var right = -1; var bottom = -1
    for (i in alpha.indices) if (alpha[i] > .1f) {
        val x = i % size; val y = i / size
        left = min(left, x); right = max(right, x); top = min(top, y); bottom = max(bottom, y)
    }
    if (right < 0) return null
    val across = max(right - left + 1, bottom - top + 1)
    return GlyphFrame(left, top, right + 1, bottom + 1, min(GLYPH_SHARE * size / across, 1.2f))
}

/** Whether a foreground layer can stand on the dark tile in its own colours: it is a glyph, not full art,
 * and little of it is dark enough to vanish against the tile. */
internal fun keepsOwnColours(foreground: IntArray): Boolean {
    if (coverage(foreground) >= FULL_BLEED) return false
    var solid = 0; var dark = 0
    for (p in foreground) if (alpha(p) >= 128) { solid++; if (luminance(p) < .22f) dark++ }
    return solid > 0 && dark < solid * .35
}

/** Icon bitmap size: 48 dp, the largest the launcher shows an icon, within 96 to 192 pixels. */
internal fun iconPixels(context: Context) = (48 * context.resources.displayMetrics.density).roundToInt().coerceIn(96, 192)

/** The icon drawn in [style], or as Android draws it when styling fails, so the app is never lost from Home,
 * the dock or the drawer. */
internal fun <T> styledOrOriginal(style: IconStyle, styled: () -> T, original: () -> T): T =
    if (style == IconStyle.ORIGINAL) original() else try { styled() } catch (_: RuntimeException) { original() }

// Each drawable is mutated before drawing: a listing that is being replaced can still be drawing on another
// thread, and two drawables from one resource would otherwise share drawing state.
internal fun appIcon(context: Context, info: LauncherActivityInfo, profile: UserHandle, style: IconStyle, size: Int): Bitmap {
    val density = context.resources.displayMetrics.densityDpi
    return styledOrOriginal(style, {
        val tile = styledIcon(info.getIcon(density).mutate(), style, size)
        // A work app keeps Android's work badge on its tile.
        val badged = context.packageManager.getUserBadgedIcon(BitmapDrawable(context.resources, tile), profile)
        if (badged is BitmapDrawable && badged.bitmap === tile) tile
        else render(size) { badged.drawIn(it, 0, 0, size, size) }.also { tile.recycle() }
    }, { render(size) { info.getBadgedIcon(density).mutate().drawIn(it, 0, 0, size, size) } })
}

/** What an app shows until its icon is drawn: the bare tile, or nothing for Original. */
internal fun placeholderIcon(style: IconStyle, size: Int): Bitmap =
    render(size) { if (style != IconStyle.ORIGINAL) drawTile(it, size, SKY_INKS[0]) }

/** An unbadged icon in [style]. Sky falls back to Glass for an app whose art gives no clear glyph. */
internal fun styledIcon(source: Drawable, style: IconStyle, size: Int): Bitmap = when (style) {
    IconStyle.ORIGINAL -> render(size) { source.drawIn(it, 0, 0, size, size) }
    IconStyle.SKY -> skyTile(source, size) ?: glassTile(source, size)
    IconStyle.GLASS -> glassTile(source, size)
}

private fun render(size: Int, draw: (Canvas) -> Unit) = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888).also { draw(Canvas(it)) }
private fun Drawable.drawIn(canvas: Canvas, left: Int, top: Int, right: Int, bottom: Int) { setBounds(left, top, right, bottom); draw(canvas) }
private fun Bitmap.pixels() = IntArray(width * height).also { getPixels(it, 0, width, 0, 0, width, height) }

/** An adaptive icon's layer as the [size]-wide viewport shows it: the layer extends past the viewport on every side. */
private fun layer(drawable: Drawable, size: Int): Bitmap {
    val extra = (size * AdaptiveIconDrawable.getExtraInsetFraction()).roundToInt()
    return render(size) { drawable.drawIn(it, -extra, -extra, size + extra, size + extra) }
}

/** The whole icon, an adaptive one masked to the device's icon shape. */
private fun whole(source: Drawable, size: Int): Bitmap = render(size) { source.drawIn(it, 0, 0, size, size) }

/** Draws [glyph] centred in the tile at its [frame]'s scale. */
private fun Canvas.drawGlyph(glyph: Bitmap, frame: GlyphFrame, size: Int, paint: Paint) {
    val w = (frame.right - frame.left) * frame.scale; val h = (frame.bottom - frame.top) * frame.scale
    drawBitmap(glyph, Rect(frame.left, frame.top, frame.right, frame.bottom), RectF((size - w) / 2, (size - h) / 2, (size + w) / 2, (size + h) / 2), paint)
}

private fun tileShape(size: Int): Pair<RectF, Float> {
    val inset = size * .02f + size / 96f
    return RectF(inset, inset, size - inset, size - inset) to size * .32f
}

private fun drawTile(canvas: Canvas, size: Int, ink: Int) {
    val (rect, radius) = tileShape(size)
    val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    paint.color = TILE_FILL; canvas.drawRoundRect(rect, radius, radius, paint)
    paint.color = ink; paint.alpha = (255 * TILE_INK_WASH).roundToInt(); canvas.drawRoundRect(rect, radius, radius, paint)
    paint.style = Paint.Style.STROKE; paint.strokeWidth = size / 48f; paint.color = ink; paint.alpha = TILE_EDGE_ALPHA
    canvas.drawRoundRect(rect, radius, radius, paint)
}

private fun clipToTile(canvas: Canvas, size: Int) {
    val (rect, radius) = tileShape(size)
    canvas.clipPath(Path().apply { addRoundRect(rect, radius, radius, Path.Direction.CW) })
}

private fun skyTile(source: Drawable, size: Int): Bitmap? {
    val adaptive = source as? AdaptiveIconDrawable
    val ink = whole(source, SAMPLE).let { skyInk(it.pixels()).also { _ -> it.recycle() } }
    val monochrome = if (adaptive != null && Build.VERSION.SDK_INT >= 33) adaptive.monochrome else null
    val glyph: Bitmap; val frame: GlyphFrame
    if (monochrome != null) {
        // The app's own themed glyph, drawn where Android draws it.
        glyph = layer(monochrome, size); frame = GlyphFrame(0, 0, size, size, 1f)
    } else {
        val foreground = adaptive?.foreground
        val art = (if (adaptive != null && foreground != null) layer(foreground, size) else whole(source, size))
            .let { it.pixels().also { _ -> it.recycle() } }
        val backdrop = adaptive?.background
        // A glyph on a clear layer stands out from the backdrop layer and full art from its own border;
        // a legacy icon on a clear canvas is its own outline.
        val reference = when {
            coverage(art) >= FULL_BLEED -> borderColour(art, size)
            backdrop != null -> layer(backdrop, SAMPLE).let { borderColour(it.pixels(), SAMPLE).also { _ -> it.recycle() } }
            else -> null
        }
        val alpha = glyphAlpha(art, reference) ?: return null
        frame = glyphFrame(alpha, size) ?: return null
        glyph = Bitmap.createBitmap(IntArray(alpha.size) { ((alpha[it] * 255).roundToInt() shl 24) or 0xffffff }, size, size, Bitmap.Config.ARGB_8888)
    }
    return render(size) { canvas ->
        drawTile(canvas, size, ink)
        clipToTile(canvas, size)
        canvas.drawGlyph(glyph, frame, size,
            Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG).apply { colorFilter = PorterDuffColorFilter(ink, PorterDuff.Mode.SRC_IN) })
        glyph.recycle()
    }
}

private fun glassTile(source: Drawable, size: Int): Bitmap {
    val foreground = (source as? AdaptiveIconDrawable)?.foreground?.let { layer(it, size) }
    val own = foreground?.pixels()?.takeIf(::keepsOwnColours)?.let(::withoutShadows)
    val frame = own?.let { pixels -> glyphFrame(FloatArray(pixels.size) { alpha(pixels[it]) / 255f }, size) }
    foreground?.recycle()
    return render(size) { canvas ->
        drawTile(canvas, size, SKY_INKS[0])
        clipToTile(canvas, size)
        if (own != null && frame != null) {
            val glyph = Bitmap.createBitmap(own, size, size, Bitmap.Config.ARGB_8888)
            canvas.drawGlyph(glyph, frame, size, Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG))
            glyph.recycle()
        } else {
            // Full art keeps its backdrop, set in the tile and dimmed a little so a white face does not glare.
            val art = whole(source, size)
            val dusk = ColorMatrix().apply { setSaturation(.85f); postConcat(ColorMatrix().apply { setScale(.9f, .9f, .92f, 1f) }) }
            val inset = size * .13f
            canvas.drawBitmap(art, null, RectF(inset, inset, size - inset, size - inset),
                Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG).apply { colorFilter = ColorMatrixColorFilter(dusk) })
            art.recycle()
        }
    }
}
