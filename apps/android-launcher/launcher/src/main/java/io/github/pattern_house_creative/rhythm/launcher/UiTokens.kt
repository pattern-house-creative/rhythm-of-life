package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * The app's design tokens, under the web app's names (`--ui-edge-strong` is [edgeStrong]). The values are the
 * ones in app/src/index.css; change both together. Everything the launcher draws itself takes its colour, type
 * size, spacing and corners from here. Installed apps keep their own artwork and the icon styles keep their own
 * inks (AppIcons.kt). The living sky's paint is [SkyArt].
 */
internal object Ui {
    // Surfaces, darkest first.
    /** The page and the living sky. */
    val sky = Color(0xff0b1015)
    /** A full view drawn over the sky: a sheet, a dialog, the reader's backdrop. */
    val night = Color(0xff101a22)
    /** Cards, bars and sheets. */
    val surface = Color(0xff17252e)
    /** A control or row lifted off a surface. */
    val raised = Color(0xff20303b)
    /** A card's fill, the web's 126° gradient run corner to corner. */
    val card = Brush.linearGradient(0f to Color(0xff1c3037), .54f to Color(0xff17252e), 1f to Color(0xff14212b),
        start = Offset.Zero, end = Offset.Infinite)

    // Lines.
    /** A card's own edge, at rest. */
    val edge = Color(0xff435a64)
    /** That edge while it is pressed (the web's pointer hover). */
    val edgeStrong = Color(0xff8eafa9)
    /** A rule between regions; the border around a labelled control. */
    val line = Color(0xff415560)
    /** A boundary that alone marks a control: 3 : 1. */
    val lineStrong = Color(0xff678490)
    /** A quiet rule inside a surface. */
    val divider = Color(0x4f59767e)

    // Text. Nothing a person must read is fainter than faint.
    /** Names and values. */
    val ink = Color(0xffeef4f3)
    /** Labels and reading text. */
    val soft = Color(0xffb8c8cc)
    /** Times, counts and other metadata. */
    val faint = Color(0xff9cafb5)

    // Accents.
    /** Opens something, or is on. */
    val mint = Color(0xffa4daca)
    /** Behind a chosen control. */
    val mintFill = Color(0xff244039)
    /** The edge of a chosen control. */
    val mintEdge = Color(0xff6a9589)
    /** The keyboard focus ring. */
    val focus = Color(0xffb5e7da)
    /** Discovery: an insight, something found. */
    val lilac = Color(0xffcbb6ed)
    val lilacFill = Color(0xff292737)
    /** Stale, always beside words that say so. */
    val amber = Color(0xffe6c487)
    /** Real attention only. */
    val alert = Color(0xffe08a97)

    // Type: DM Sans (ObservatoryBody) for reading text, Manrope (ObservatoryTitle) for names and key values.
    /** The smallest running text. Only a short uppercase eyebrow goes to [textEyebrow]. */
    val textMeta = 12.sp
    val textSmall = 13.sp
    val textBody = 14.sp
    /** A card's or row's name. */
    val textTitle = 15.sp
    val textLead = 16.sp
    val textSection = 18.sp
    val textPage = 21.sp
    val textEyebrow = 11.sp
    /** Uppercase eyebrows keep modest tracking, about .08em. */
    val eyebrowTracking = .9.sp

    // Space, on 4 dp steps: related things 4 to 8 apart, groups 12 to 16, regions 24.
    val space1 = 4.dp
    val space2 = 8.dp
    val space3 = 12.dp
    val space4 = 16.dp
    val space6 = 24.dp
    val space8 = 32.dp

    // Corners.
    val radiusChip = 6.dp
    val radiusControl = 8.dp
    val radiusPanel = 10.dp
    val radiusCard = 14.dp

    // Focus, touch and motion.
    /** The keyboard focus ring around a control. */
    val focusRing = 2.dp
    /** The ring of a card that opens, drawn inset by [cardRingInset]. */
    val cardRing = 3.dp
    val cardRingInset = 4.dp
    /** The least a touch target measures, each way: Android's own, a little above the web's 44 px. */
    val target = 48.dp
    /** The dashboard frame's header and footer, at phone widths. */
    val frameHeader = 48.dp
    /** Colour and edge changes, in milliseconds, with [ObservatoryEase]. */
    const val fast = 120
    const val calm = 180
}

/**
 * The living sky's own paint, unchanged by the refresh: the night gradient, the light, the ridges, the dust and
 * the stars. Like the web's data colours, these mean the sky and are not general tokens.
 */
internal object SkyArt {
    val top = Color(0xff07181d)
    val middle = Color(0xff0b292b)
    val bottom = Color(0xff071e24)
    val upperLight = Color(0x80376c5d)
    val lowerLight = Color(0x6524594f)
    val ridges = listOf(Color(0xff102d2c), Color(0xff061b1f), Color(0xff031216))
    val dust = Color(0xffb3cfc9)
    val link = Color(0xff88c5b5)
    val halo = Color(0xffa9ebd5)
    val star = Color(0xffa3d7cb)
    // The explorer's stars and links, by neighbourhood.
    val groups = listOf(Color(0xffb2ffdf), Color(0xffb7e5dc), Color(0xffc1eadb), Color(0xffc9e7ca))
    val ungrouped = Color(0xff98b4bb)
    val explorerLink = Color(0xff7bcab7)
    val brightStar = Color(0xffe1fff3)
    val selectedRing = Color(0xffd8fff0)
}
