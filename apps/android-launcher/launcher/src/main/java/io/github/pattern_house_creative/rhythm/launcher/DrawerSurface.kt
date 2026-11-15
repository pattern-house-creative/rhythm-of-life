package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.foundation.layout.*
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.layout
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.offset

/** Full-height layer displacement is the finger displacement, including reversals. */
@Composable internal fun DrawerSurface(state:DrawerController, content:@Composable ()->Unit) {
    Box(Modifier.layout { measurable,constraints ->
        android.os.Trace.beginSection("Rhythm.DrawerMeasure")
        val child=try {measurable.measure(constraints)} finally {android.os.Trace.endSection()}
        layout(child.width,child.height) {if(state.exposed)child.placeRelative(0,0)}
    }.graphicsLayer {translationY=size.height*(1-state.fraction)}) {content()}
}

@Composable internal fun Modifier.drawerImePadding(homeInsets:WindowInsets,state:DrawerController):Modifier {
    val ime=WindowInsets.ime
    val lastPadding=remember { intArrayOf(0) }
    return layout { measurable,constraints ->
        // Keep list constraints stable while the sheet follows the finger or settles.
        // IME exit must not repeatedly remeasure a moving LazyGrid.
        val bottom=when {
            state.settledOpen -> (ime.getBottom(this)-homeInsets.getBottom(this)-72.dp.roundToPx()).coerceIn(0,constraints.maxHeight).also{lastPadding[0]=it}
            state.fullyClosed -> 0.also{lastPadding[0]=0}
            else -> lastPadding[0].coerceIn(0,constraints.maxHeight)
        }
        val child=measurable.measure(constraints.offset(vertical=-bottom))
        layout(child.width,child.height+bottom) {child.placeRelative(0,0)}
    }
}
