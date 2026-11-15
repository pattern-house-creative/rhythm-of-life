package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.material3.Typography
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontVariation
import androidx.compose.ui.text.ExperimentalTextApi

// Bundled OFL fonts used by the recovered, hash-verified version 7 prototype.
@OptIn(ExperimentalTextApi::class)
internal val ObservatoryBody = FontFamily(Font(R.font.dmsans, variationSettings = FontVariation.Settings(FontVariation.weight(400))))
@OptIn(ExperimentalTextApi::class)
internal val ObservatoryTitle = FontFamily(Font(R.font.manrope, variationSettings = FontVariation.Settings(FontVariation.weight(400))))
internal val ObservatoryEase = CubicBezierEasing(.2f,.8f,.2f,1f)
internal val ObservatoryTypography = Typography().let { t -> t.copy(
    bodyLarge=t.bodyLarge.copy(fontFamily=ObservatoryBody),
    bodyMedium=t.bodyMedium.copy(fontFamily=ObservatoryBody),
    bodySmall=t.bodySmall.copy(fontFamily=ObservatoryBody),
    titleLarge=t.titleLarge.copy(fontFamily=ObservatoryTitle),
    titleMedium=t.titleMedium.copy(fontFamily=ObservatoryTitle),
    titleSmall=t.titleSmall.copy(fontFamily=ObservatoryTitle),
    labelLarge=t.labelLarge.copy(fontFamily=ObservatoryBody),
    labelMedium=t.labelMedium.copy(fontFamily=ObservatoryBody),
    labelSmall=t.labelSmall.copy(fontFamily=ObservatoryBody)
) }
