package io.github.pattern_house_creative.rhythm.launcher

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import androidx.compose.foundation.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

// Same selection policy as app/src/insights.ts. Sensitive text lives only in
// memory and is discarded on pause, offline, disconnect, denied or failed reads.
internal data class DailyInsight(
    val id: String, val kind: String, val title: String, val summary: String,
    val why: String, val next: String, val limitations: String, val evidence: String,
    val generatedAt: Long, val status: String, val supersedes: String?,
    val until: Long?, val points: List<String>, val sources: List<String>
)
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
private fun insightColor(kind: String?) = when(kind) {
    "discovery" -> Color(0xffd0b6e7)
    "suggestion" -> Color(0xffc1a66d)
    else -> Color(0xff8dd7bd)
}
@Composable internal fun DailyInsightBar(insight: DailyInsight?, state: String, onOpen: () -> Unit) {
    Row(Modifier.fillMaxWidth().testTag("daily-insight")
        .clipForInsight().clickable(enabled=insight != null, onClick=onOpen)
        .padding(horizontal=14.dp, vertical=11.dp), verticalAlignment=Alignment.CenterVertically) {
        // Same three marks as the site's InsightCard: focus, connected discovery,
        // and suggestion. Vector geometry avoids device-dependent font symbols.
        Canvas(Modifier.padding(end=12.dp).size(22.dp)) {
            val s=size.width/14f;val color=insightColor(insight?.kind)
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
                color=insightColor(insight?.kind), fontSize=9.sp, letterSpacing=1.sp)
            Text(insight?.title ?: state, fontSize=12.sp, lineHeight=16.sp, maxLines=2,
                overflow=TextOverflow.Ellipsis, color=Color(0xffd6e7e1))
        }
        if(insight != null) Text("›", fontSize=22.sp, color=Color(0xff9ab7ad), modifier=Modifier.padding(start=8.dp))
    }
}
private fun Modifier.clipForInsight() = this.background(Color(0x99102526), RoundedCornerShape(18.dp))
    .border(.5.dp, Color(0x26b2e0cc), RoundedCornerShape(18.dp))

@OptIn(ExperimentalMaterial3Api::class)
@Composable internal fun DailyInsightSheet(insight: DailyInsight, onClose: () -> Unit) {
    ModalBottomSheet(onDismissRequest=onClose, containerColor=Color(0xff122a2b)) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal=24.dp).padding(bottom=28.dp).testTag("insight-detail")) {
            Text("${insight.kind.replaceFirstChar { it.uppercase() }} · ${insightDate(insight.generatedAt)}",
                color=insightColor(insight.kind), fontSize=12.sp)
            Text(insight.title, fontFamily=ObservatoryTitle, fontSize=24.sp, modifier=Modifier.padding(vertical=12.dp))
            for((label, value) in listOf("" to insight.summary, "Why it matters" to insight.why, "A possible next step" to insight.next,
                "Evidence · ${insight.evidence}" to insight.points.joinToString("\n\n"), "Limitations" to insight.limitations,
                "Sources" to insight.sources.joinToString("\n"))) {
                if(value.isNotBlank()) {
                    if(label.isNotEmpty()) Text(label, color=Color(0xff9fc7b8),fontSize=11.sp,modifier=Modifier.padding(top=14.dp,bottom=6.dp))
                    Text(value,fontSize=14.sp,lineHeight=21.sp)
                }
            }
            TextButton(onClick=onClose,modifier=Modifier.align(Alignment.End).padding(top=12.dp)) { Text("Close insight") }
        }
    }
}
