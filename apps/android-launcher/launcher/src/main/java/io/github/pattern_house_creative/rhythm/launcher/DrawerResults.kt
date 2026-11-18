package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.*
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.*
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import java.text.Collator
import java.text.Normalizer
import java.util.Locale

@Composable internal fun DrawerToolbar(alphabetical: Boolean, onLayoutChange: (Boolean) -> Unit, onAddPreview: () -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        TextButton(onClick = onAddPreview, modifier = Modifier.testTag("add-preview")) { Text("+  Add preview") }
        Spacer(Modifier.weight(1f))
        Row(Modifier.selectableGroup(), verticalAlignment = Alignment.CenterVertically) {
            for (list in listOf(false, true)) {
                val selected = alphabetical == list
                val color = MaterialTheme.colorScheme.primary
                Box(Modifier.clip(CircleShape)
                    .selectable(selected = selected, role = Role.RadioButton, onClick = { if (!selected) onLayoutChange(list) })
                    .sizeIn(minWidth = 52.dp, minHeight = 48.dp), contentAlignment = Alignment.Center) {
                    Text(if (list) "A–Z" else "Grid", color = if (selected) color else MaterialTheme.colorScheme.onSurface,
                        fontSize = 14.sp, modifier = Modifier.background(if (selected) color.copy(alpha = .16f) else Color.Transparent,
                            CircleShape).padding(horizontal = 12.dp, vertical = 3.dp))
                }
            }
        }
    }
}

private fun initial(title: String): String {
    val normalized = Normalizer.normalize(title.trimStart(), Normalizer.Form.NFD)
    val letter = normalized.codePoints().findFirst().orElse(0)
    return if (Character.isLetter(letter)) String(Character.toChars(letter)).uppercase(Locale.ROOT) else "#"
}

@Composable internal fun DrawerResults(
    results: List<Tile>, alphabetical: Boolean, titles: Map<String, String>, scroll: LazyGridState, drawer: DrawerController,
    tileContent: @Composable (Tile, Modifier) -> Unit
) {
    val locale = androidx.compose.ui.platform.LocalConfiguration.current.locales[0]
    val sections = remember(results, alphabetical, titles, locale) {
        if (!alphabetical) return@remember emptyList()
        val collator = Collator.getInstance(locale)
        results.groupBy { initial(titles[it.key].orEmpty()) }.entries.sortedWith(compareBy(collator) { it.key })
            .map { (letter, tiles) -> letter to tiles.sortedWith(compareBy<Tile, String>(collator) { titles[it.key].orEmpty() }.thenBy { it.key }) }
    }
    val tiles = remember(results, alphabetical, sections) { if (alphabetical) sections.flatMap { it.second } else results }
    val starts = remember(sections) {
        var offset = 0
        sections.map { (letter, group) -> (letter to offset).also { offset += group.size } }
    }
    Row(Modifier.fillMaxSize()) {
        LazyVerticalGrid(columns = GridCells.Fixed(if (alphabetical) 1 else 4), state = scroll,
            modifier = Modifier.weight(1f).fillMaxHeight().testTag("drawer-grid").onGloballyPositioned { drawer.grid = it },
            horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(if (alphabetical) 0.dp else 12.dp)) {
            items(tiles, key = { it.key }) { tile -> tileContent(tile, Modifier.height(if (alphabetical) 56.dp else 80.dp)) }
        }
        if (alphabetical && starts.isNotEmpty()) AlphabetIndex(starts, scroll)
    }
}

@Composable private fun AlphabetIndex(sections: List<Pair<String, Int>>, scroll: LazyGridState) {
    var touched by remember(sections) { mutableIntStateOf(-1) }
    val current by remember(sections, scroll) { derivedStateOf {
        if (touched >= 0) touched else sections.indexOfLast { it.second <= scroll.firstVisibleItemIndex }
    } }
    BoxWithConstraints(Modifier.width(48.dp).fillMaxHeight().testTag("alphabet-index").semantics {
        contentDescription = "Alphabet index"
        customActions = sections.map { (letter, position) ->
            CustomAccessibilityAction("Jump to " + letter) { scroll.requestScrollToItem(position); true }
        }
    }.pointerInput(sections) {
        awaitEachGesture {
            val down = awaitFirstDown()
            fun jump(y: Float) {
                val index = (y / size.height.coerceAtLeast(1) * sections.size).toInt().coerceIn(sections.indices)
                if (index != touched) { touched = index; scroll.requestScrollToItem(sections[index].second) }
            }
            try {
                down.consume()
                jump(down.position.y)
                do {
                    val change = awaitPointerEvent().changes.firstOrNull { it.id == down.id } ?: break
                    change.consume()
                    if (change.pressed) jump(change.position.y)
                } while (change.pressed)
            } finally { touched = -1 }
        }
    }) {
        // Keep the full touch map when the keyboard leaves too little room to print every letter.
        val labelHeight = with(androidx.compose.ui.platform.LocalDensity.current) { 16.sp.toDp() }
        val available = (maxHeight / labelHeight).toInt().coerceAtLeast(1)
        val stride = (sections.size + available - 1) / available
        sections.forEachIndexed { index, (letter, _) ->
            if (index % stride == 0 || index == touched) {
                val center = maxHeight * ((index + .5f) / sections.size)
                val y = (center - labelHeight / 2).coerceIn(0.dp, (maxHeight - labelHeight).coerceAtLeast(0.dp))
                Box(Modifier.offset(y = y).fillMaxWidth().height(labelHeight), contentAlignment = Alignment.Center) {
                    Text(letter, fontSize = 11.sp, lineHeight = 12.sp, maxLines = 1,
                        color = if (index == current) Color.White else MaterialTheme.colorScheme.primary)
                }
            }
        }
    }
}
