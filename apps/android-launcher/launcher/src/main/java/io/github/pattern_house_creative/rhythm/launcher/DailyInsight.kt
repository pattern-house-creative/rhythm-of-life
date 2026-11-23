package io.github.pattern_house_creative.rhythm.launcher

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import androidx.compose.foundation.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.draw.clip
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

// Same selection policy as app/src/insights.ts. Sensitive text lives only in
// memory and is discarded on pause, offline, disconnect, denied or failed reads.
internal data class DailyInsight(
    val id: String, val kind: String, val title: String, val summary: String,
    val why: String, val next: String, val limitations: String, val evidence: String,
    val generatedAt: Long, val status: String, val supersedes: String?,
    val until: Long?, val points: List<String>, val sources: List<String>,
    val feedback: InsightChoice? = null, val feedbackNote: String = ""
)
internal enum class InsightChoice(val value: String, val label: String, val status: String) {
    Interesting("interesting", "Interesting", "saved"),
    Explore("explore", "Explore", "explored"),
    NotUseful("not_useful", "Not useful", "dismissed"),
    Later("later", "Remind me later", "snoozed"),
    Handled("handled", "Already handled", "handled")
}
internal fun currentDailyInsight(rows: List<DailyInsight>, now: Long): DailyInsight? {
    val replaced = rows.mapNotNull { it.supersedes }.toSet()
    return rows.mapNotNull { row ->
        val at = when (row.status) {
            "new", "saved" -> row.generatedAt
            "snoozed" -> row.until?.takeIf { it <= now }
            else -> null
        }
        if (row.id in replaced || at == null || now-at >= 3*86_400_000L || at-now > 3_600_000L) null
        else row to at
    }.maxByOrNull { it.second }?.first
}
internal fun insightDate(at: Long): String = DateTimeFormatter.ofPattern("MMM d")
    .withZone(ZoneId.systemDefault()).format(Instant.ofEpochMilli(at))
// The kinds of insight keep the web's own marks and colours (InsightCard.css): mint-teal for a focus,
// lilac for a discovery, the caution gold for a suggestion. The strip itself is the discovery surface.
private val FocusKind = Color(0xff75d8b9)
private val SuggestionKind = Color(0xffc1a66d)
private fun insightColor(kind: String?) = when(kind) {
    "discovery" -> Ui.lilac
    "suggestion" -> SuggestionKind
    else -> FocusKind
}
/** The strip's height before the refresh at one line of text, kept so a Home page holds the rows it did. */
private val INSIGHT_MIN_HEIGHT = 62.dp
@Composable internal fun DailyInsightBar(insight: DailyInsight?, state: String, onOpen: () -> Unit) {
    val found = insight != null
    val shape = RoundedCornerShape(Ui.radiusCard)
    Row(Modifier.fillMaxWidth().heightIn(min=INSIGHT_MIN_HEIGHT).testTag("daily-insight")
        .clip(shape).background(if(found) Ui.lilacFill.copy(alpha=.94f) else Ui.surface.copy(alpha=.86f), shape)
        .border(1.dp, if(found) Ui.lilac.copy(alpha=.45f) else Ui.edge, shape)
        .clickable(enabled=found, onClick=onOpen)
        .padding(horizontal=Ui.space4, vertical=Ui.space3), verticalAlignment=Alignment.CenterVertically) {
        // Same three marks as the site's InsightCard: focus, connected discovery,
        // and suggestion. Vector geometry avoids device-dependent font symbols.
        Canvas(Modifier.padding(end=Ui.space3).size(22.dp)) {
            val s=size.width/14f;val color=if(found) insightColor(insight?.kind) else Ui.faint
            when(insight?.kind) {
                "focus" -> { drawCircle(color,5.3f*s,style=Stroke(s));drawCircle(color,1.8f*s) }
                "discovery" -> {
                    drawLine(color.copy(alpha=.55f),Offset(3.2f*s,10.4f*s),Offset(10.8f*s,3.6f*s),s)
                    drawCircle(color,2*s,Offset(3.2f*s,10.4f*s));drawCircle(color,2*s,Offset(10.8f*s,3.6f*s))
                }
                else -> { val p=Path();p.moveTo(7*s,1*s);p.quadraticTo(7*s,7*s,13*s,7*s);p.quadraticTo(7*s,7*s,7*s,13*s);p.quadraticTo(7*s,7*s,s,7*s);p.quadraticTo(7*s,7*s,7*s,s);drawPath(p,color) }
            }
        }
        Column(Modifier.weight(1f)) {
            Text(if(insight == null) "DAILY INSIGHT" else "${insight.kind.uppercase()} · ${insightDate(insight.generatedAt)}",
                color=if(found) Ui.lilac else Ui.soft, fontSize=Ui.textEyebrow, lineHeight=16.sp, letterSpacing=Ui.eyebrowTracking,
                fontWeight=FontWeight.Medium, maxLines=1, overflow=TextOverflow.Ellipsis)
            Spacer(Modifier.height(2.dp))
            Text(insight?.title ?: state, fontSize=Ui.textBody, lineHeight=18.sp, maxLines=2,
                fontWeight=if(found) FontWeight.Medium else FontWeight.Normal,
                overflow=TextOverflow.Ellipsis, color=if(found) Ui.ink else Ui.soft)
        }
        if(found) Text("›", fontSize=22.sp, color=Ui.lilac, modifier=Modifier.padding(start=Ui.space2).clearAndSetSemantics{})
    }
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable internal fun DailyInsightSheet(
    insight: DailyInsight,
    onSave: suspend (InsightChoice, String, Instant?) -> Unit,
    onClose: () -> Unit
) {
    val scope = rememberCoroutineScope()
    var choice by remember { mutableStateOf(insight.feedback) }
    var note by remember { mutableStateOf(insight.feedbackNote) }
    var reminderDays by remember { mutableStateOf<Int?>(null) }
    var saving by remember { mutableStateOf(false) }
    var failed by remember { mutableStateOf(false) }
    val sheet = rememberModalBottomSheetState(skipPartiallyExpanded = true,
        confirmValueChange = { it != SheetValue.Hidden || !saving })
    ModalBottomSheet(onDismissRequest={ if (!saving) onClose() }, sheetState=sheet, containerColor=Ui.surface,
        shape=RoundedCornerShape(topStart=Ui.radiusCard+Ui.space1, topEnd=Ui.radiusCard+Ui.space1)) {
        Column(Modifier.fillMaxWidth().imePadding().verticalScroll(rememberScrollState()).padding(horizontal=24.dp).padding(bottom=28.dp).testTag("insight-detail")) {
            Text("${insight.kind.replaceFirstChar { it.uppercase() }} · ${insightDate(insight.generatedAt)}",
                color=Ui.lilac, fontSize=Ui.textMeta, fontWeight=FontWeight.Medium)
            Text(insight.title, fontFamily=ObservatoryTitle, fontWeight=FontWeight.SemiBold, fontSize=Ui.textPage, lineHeight=28.sp,
                color=Ui.ink, modifier=Modifier.padding(top=Ui.space2, bottom=Ui.space3))
            if (insight.summary.isNotBlank()) Text(insight.summary, fontSize=Ui.textBody, lineHeight=21.sp, color=Ui.ink)
            Text("Was this useful?", style=MaterialTheme.typography.titleMedium, color=Ui.ink, modifier=Modifier.padding(top=Ui.space6, bottom=Ui.space1))
            FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                InsightChoice.entries.forEach { option ->
                    FilterChip(selected=choice == option, enabled=!saving,
                        onClick={ choice=option; failed=false }, label={ Text(option.label) }, border=chipBorder(choice == option, !saving))
                }
            }
            if (choice == InsightChoice.Later) {
                FlowRow(horizontalArrangement=Arrangement.spacedBy(8.dp)) {
                    for ((days, label) in listOf(1 to "Tomorrow morning", 7 to "Next week")) {
                        FilterChip(selected=reminderDays == days, enabled=!saving,
                            onClick={ reminderDays=days; failed=false }, label={ Text(label) }, border=chipBorder(reminderDays == days, !saving))
                    }
                }
            }
            OutlinedTextField(value=note, onValueChange={ if (it.length <= 500) { note=it; failed=false } },
                enabled=!saving, label={ Text(if (choice == InsightChoice.Handled) "What happened?" else "Note (optional)") },
                minLines=1, maxLines=4, modifier=Modifier.fillMaxWidth().testTag("insight-note"))
            if (failed) Text("Couldn’t confirm the save. Your note is here to retry.", color=Ui.alert, fontSize=Ui.textSmall)
            Button(enabled=!saving && choice != null && (choice != InsightChoice.Handled || note.isNotBlank()) &&
                (choice != InsightChoice.Later || reminderDays != null), onClick={
                val selected = choice ?: return@Button
                if (saving) return@Button
                val until = if (selected == InsightChoice.Later)
                    java.time.LocalDate.now().plusDays(requireNotNull(reminderDays).toLong())
                        .atTime(7, 0).atZone(ZoneId.systemDefault()).toInstant() else null
                saving=true
                scope.launch {
                    try { onSave(selected, note.trim(), until) }
                    catch (e: CancellationException) { throw e }
                    catch (_: Exception) { failed=true }
                    finally { saving=false }
                }
            }, modifier=Modifier.padding(top=8.dp)) { Text(if (saving) "Saving…" else "Save feedback") }
            for((label, value) in listOf("Why it matters" to insight.why, "A possible next step" to insight.next,
                "Evidence · ${insight.evidence}" to insight.points.joinToString("\n\n"), "Limitations" to insight.limitations,
                "Sources" to insight.sources.joinToString("\n"))) {
                if(value.isNotBlank()) {
                    if(label.isNotEmpty()) Text(label, color=Ui.soft,fontSize=Ui.textMeta,fontWeight=FontWeight.Medium,
                        modifier=Modifier.padding(top=Ui.space4,bottom=Ui.space1).semantics{heading()})
                    Text(value,fontSize=Ui.textBody,lineHeight=21.sp,color=Ui.ink)
                }
            }
            TextButton(onClick=onClose,enabled=!saving,modifier=Modifier.align(Alignment.End).padding(top=12.dp)) { Text("Close insight") }
        }
    }
}

/** A chosen chip has a mint edge as well as its fill, so the choice reads in greyscale. */
@Composable private fun chipBorder(selected: Boolean, enabled: Boolean) = FilterChipDefaults.filterChipBorder(enabled = enabled,
    selected = selected, borderColor = Ui.line, selectedBorderColor = Ui.mintEdge, selectedBorderWidth = 1.dp)
