package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.animation.core.animate
import androidx.compose.animation.core.tween
import androidx.compose.runtime.*
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.floor

/** Height of one Home row and of the grid's top and bottom margin, in dp. A preview takes two rows. */
internal const val HOME_ROW_DP = 88
internal const val HOME_GRID_MARGIN_DP = 16
/** Room kept under the grid for the page dots. */
internal const val HOME_INDICATOR_DP = 14

/** Clear sky kept between a page's cells and the page dots. */
internal const val HOME_INDICATOR_GAP_DP = 8

/**
 * Where a page's cells stop being drawn, in dp from the top of a grid [gridHeightDp] tall: [HOME_INDICATOR_GAP_DP] above
 * the page dots, or higher, at the foot of the last whole row above that line. More sky shortens the grid; this keeps
 * a row from showing cut through behind the dots. A full page always fits, so ordinary Home draws as before.
 */
internal fun homeCellClipDp(gridHeightDp: Float): Float {
    val line = gridHeightDp - HOME_INDICATOR_DP - HOME_INDICATOR_GAP_DP
    // A row's cells run from 8 dp below its top to its foot; halfway down the gap after the last whole row.
    val rows = floor(line / HOME_ROW_DP).coerceAtLeast(0f)
    return minOf(line, rows * HOME_ROW_DP + 4f).coerceAtLeast(0f)
}

/** How many rows of icons fit on a fixed-height page [heightDp] tall. Never fewer than a preview needs. */
internal fun homePageRows(heightDp: Float): Int =
    floor((heightDp - HOME_GRID_MARGIN_DP - HOME_INDICATOR_DP) / HOME_ROW_DP).toInt().coerceAtLeast(2)

/**
 * Which Home page shows and how far a swipe has moved it. Horizontal swipes come from the one gesture
 * owner (launcherGestures); this only keeps the position and animates it to rest. A finger moving left
 * ([drag] with a negative distance) brings the next page in.
 */
@Stable internal class HomePager(private val scope: CoroutineScope) {
    var page by mutableIntStateOf(0); private set
    /** Pages that can be shown: Home's pages, plus an empty one after them while a tile is being dragged. */
    var count by mutableIntStateOf(1)
    var width = 1f
    var reduced = false
    private var offset by mutableFloatStateOf(0f)
    private var settling: Job? = null
    /** The page in view, fractional while a swipe or animation is under way. */
    val position: Float get() = page - offset / width.coerceAtLeast(1f)
    val moving: Boolean get() = offset != 0f

    fun begin() { settling?.cancel() }
    fun drag(dx: Float) {
        val next = offset + dx
        // Resist past the first and last page.
        val beyond = (page == 0 && next > 0f) || (page >= count - 1 && next < 0f)
        offset = (offset + if (beyond) dx * .3f else dx).coerceIn(-width, width)
    }
    /** Ends a swipe: a quarter of the width or a quick flick turns the page. [velocity] is in px per second. */
    fun release(velocity: Float, density: Float) {
        val fling = 400 * density
        val step = when {
            offset < -width * .25f || velocity < -fling -> 1
            offset > width * .25f || velocity > fling -> -1
            else -> 0
        }
        go(page + step)
    }
    /** Animates to [target] from wherever the view is now. */
    fun go(target: Int) {
        settling?.cancel()
        val from = position
        page = target.coerceIn(0, (count - 1).coerceAtLeast(0))
        offset = (page - from) * width
        if (offset == 0f) return
        settling = scope.launch {
            animate(offset, 0f, animationSpec = tween(if (reduced) 0 else (180 + 120 * abs(offset / width)).toInt(), easing = ObservatoryEase)) { value, _ ->
                offset = value
            }
        }
    }
    fun snap(target: Int) { settling?.cancel(); page = target.coerceIn(0, (count - 1).coerceAtLeast(0)); offset = 0f }
}

@Composable internal fun rememberHomePager(): HomePager {
    val scope = rememberCoroutineScope()
    return remember { HomePager(scope) }
}
