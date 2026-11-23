package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontVariation
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.ExperimentalTextApi
import androidx.compose.ui.unit.sp

// The web sets reading text without extra tracking, so Material's default letter spacing is dropped.
// The two bundled OFL fonts are variable. Each weight the launcher uses is its own entry, so a FontWeight
// picks the real cut instead of a synthetic bold: DM Sans for reading text, Manrope for names and key values.
@OptIn(ExperimentalTextApi::class)
private fun variable(resource: Int, vararg weights: Int) = FontFamily(weights.map { weight ->
    Font(resource, FontWeight(weight), variationSettings = FontVariation.Settings(FontVariation.weight(weight)))
})
internal val ObservatoryBody = variable(R.font.dmsans, 400, 500, 600)
internal val ObservatoryTitle = variable(R.font.manrope, 400, 500, 600, 650, 700)
/** A card's or row's name: Manrope 650, as on the web. */
internal val TitleWeight = FontWeight(650)
/** `--ui-ease`. */
internal val ObservatoryEase = CubicBezierEasing(.2f,.8f,.2f,1f)
internal val ObservatoryTypography = Typography().let { t -> t.copy(
    headlineSmall=t.headlineSmall.copy(fontFamily=ObservatoryTitle, fontWeight=FontWeight.SemiBold, fontSize=Ui.textPage, lineHeight=Ui.textPage*1.3f),
    bodyLarge=t.bodyLarge.copy(fontFamily=ObservatoryBody, letterSpacing=0.sp),
    bodyMedium=t.bodyMedium.copy(fontFamily=ObservatoryBody, letterSpacing=0.sp),
    bodySmall=t.bodySmall.copy(fontFamily=ObservatoryBody, fontSize=Ui.textMeta, lineHeight=16.sp, letterSpacing=0.sp),
    titleLarge=t.titleLarge.copy(fontFamily=ObservatoryTitle, fontWeight=FontWeight.SemiBold),
    titleMedium=t.titleMedium.copy(fontFamily=ObservatoryTitle, fontWeight=TitleWeight),
    titleSmall=t.titleSmall.copy(fontFamily=ObservatoryTitle, fontWeight=TitleWeight),
    labelLarge=t.labelLarge.copy(fontFamily=ObservatoryBody, letterSpacing=0.sp, fontWeight=FontWeight.Medium),
    labelMedium=t.labelMedium.copy(fontFamily=ObservatoryBody, fontSize=Ui.textMeta, lineHeight=16.sp, letterSpacing=0.sp),
    labelSmall=t.labelSmall.copy(fontFamily=ObservatoryBody, fontSize=Ui.textMeta, lineHeight=16.sp, letterSpacing=0.sp)
) }
/** Material's own controls (buttons, fields, chips, dialogs, sheets) drawn in the token palette. */
internal val ObservatoryColors = darkColorScheme(
    primary = Ui.mint, onPrimary = Ui.sky, primaryContainer = Ui.mintFill, onPrimaryContainer = Ui.ink,
    secondary = Ui.mint, onSecondary = Ui.sky, secondaryContainer = Ui.mintFill, onSecondaryContainer = Ui.ink,
    tertiary = Ui.lilac, onTertiary = Ui.sky, tertiaryContainer = Ui.lilacFill, onTertiaryContainer = Ui.ink,
    background = Ui.sky, onBackground = Ui.ink,
    surface = Ui.night, onSurface = Ui.ink, onSurfaceVariant = Ui.soft, surfaceVariant = Ui.raised,
    surfaceTint = Ui.night, inverseSurface = Ui.ink, inverseOnSurface = Ui.sky, inversePrimary = Ui.mintFill,
    surfaceBright = Ui.raised, surfaceDim = Ui.sky, surfaceContainerLowest = Ui.sky, surfaceContainerLow = Ui.night,
    surfaceContainer = Ui.surface, surfaceContainerHigh = Ui.surface, surfaceContainerHighest = Ui.raised,
    outline = Ui.lineStrong, outlineVariant = Ui.line, scrim = Ui.sky,
    error = Ui.alert, onError = Ui.sky, errorContainer = Ui.raised, onErrorContainer = Ui.alert
)
