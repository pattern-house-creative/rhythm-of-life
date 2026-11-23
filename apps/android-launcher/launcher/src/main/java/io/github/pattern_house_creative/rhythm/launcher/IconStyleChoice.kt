package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.sp

/** The settings choice of app icon style, drawn like the drawer's Grid and A–Z choice. */
@Composable internal fun IconStyleChoice(style: IconStyle, onChange: (IconStyle) -> Unit) {
    Column(Modifier.padding(vertical = Ui.space1)) {
        SettingsHeading("App icons")
        Row(Modifier.selectableGroup().testTag("icon-style").padding(start = Ui.space1), verticalAlignment = Alignment.CenterVertically) {
            for (option in IconStyle.entries) Choice(option.label, option == style, Modifier.testTag("icon-style:${option.name}")) { onChange(option) }
        }
        Text(when (style) {
            IconStyle.SKY -> "Each app as a glyph in the sky's colours."
            IconStyle.GLASS -> "Each app's own colours on a dark tile."
            IconStyle.ORIGINAL -> "Icons as Android draws them."
        }, fontSize = Ui.textSmall, lineHeight = 18.sp, color = Ui.soft, modifier = Modifier.padding(horizontal = Ui.space3))
    }
}
