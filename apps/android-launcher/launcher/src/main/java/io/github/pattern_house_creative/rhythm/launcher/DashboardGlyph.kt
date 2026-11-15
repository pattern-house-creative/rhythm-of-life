package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.*
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

// Local visual hints derived from the real title, never invented dashboard data
// or an assertion of authored metadata. Unknown titles get a stable monogram.
internal fun dashboardSymbol(title: String, id: String): String {
    val words=title.lowercase().split(Regex("[^a-z0-9]+")).toSet()
    return when {
        id==BUILTIN_GLUCOSE || words.any { it in setOf("glucose","health","training","fitness") } -> "pulse"
        words.any { it in setOf("spend","spending","budget","money","finance","bills") } -> "wallet"
        words.any { it in setOf("read","reading","books","library") } -> "book"
        words.any { it in setOf("garden","plants","growing") } -> "leaf"
        words.any { it in setOf("today","week","calendar","plan","schedule") } -> "calendar"
        words.any { it in setOf("food","recipe","recipes","meal","meals","kitchen") } -> "bowl"
        else -> "initials"
    }
}
@Composable internal fun DashboardGlyph(title: String, id: String, modifier: Modifier=Modifier) {
    val symbol=dashboardSymbol(title,id)
    val colors=listOf(Color(0xffa0d7bf),Color(0xffb5c9e0),Color(0xffd2c7a0),Color(0xffc5b5d6))
    val color=colors[(id.hashCode() and Int.MAX_VALUE)%colors.size]
    Box(modifier.size(46.dp).background(color.copy(alpha=.09f),RoundedCornerShape(15.dp)),contentAlignment=Alignment.Center) {
        if(symbol=="initials") Text(title.split(Regex("\\s+")).filter{it.isNotBlank()}.take(2).joinToString(""){it.take(1).uppercase()},
            color=color,fontFamily=ObservatoryTitle,fontSize=17.sp)
        else Canvas(Modifier.size(27.dp)) {
            val s=size.width/24f
            val stroke=Stroke(1.35f*s,cap=StrokeCap.Round,join=StrokeJoin.Round)
            val path=Path()
            fun line(vararg points:Float) { path.moveTo(points[0]*s,points[1]*s); for(i in 2 until points.size step 2)path.lineTo(points[i]*s,points[i+1]*s) }
            when(symbol) {
                "pulse" -> line(1f,12f,6f,12f,9f,4f,14f,20f,17f,12f,23f,12f)
                "wallet" -> { path.addRoundRect(androidx.compose.ui.geometry.RoundRect(3*s,6*s,22*s,20*s,androidx.compose.ui.geometry.CornerRadius(2*s)));line(4f,6f,4f,3f,18f,3f,18f,6f);line(22f,11f,16f,11f,16f,16f,22f,16f) }
                "book" -> { line(12f,6f,9f,4f,2f,4f,2f,19f,9f,19f,12f,21f,15f,19f,22f,19f,22f,4f,15f,4f,12f,6f,12f,21f) }
                "leaf" -> { path.moveTo(5*s,20*s);path.cubicTo(0f,9*s,7*s,2*s,21*s,3*s);path.cubicTo(22*s,17*s,13*s,23*s,5*s,20*s);line(4f,22f,16f,9f) }
                "calendar" -> { path.addRoundRect(androidx.compose.ui.geometry.RoundRect(3*s,5*s,21*s,21*s,androidx.compose.ui.geometry.CornerRadius(2*s)));line(3f,10f,21f,10f);line(8f,2f,8f,7f);line(16f,2f,16f,7f);line(8f,15f,11f,15f) }
                "bowl" -> { path.moveTo(2*s,12*s);path.cubicTo(4*s,24*s,20*s,24*s,22*s,12*s);path.close();line(8f,4f,7f,7f,8f,9f);line(15f,2f,14f,5f,15f,8f) }
            }
            drawPath(path,color,style=stroke)
        }
    }
}
