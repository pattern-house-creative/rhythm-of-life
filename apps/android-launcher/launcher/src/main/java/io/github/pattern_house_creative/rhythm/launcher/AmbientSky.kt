package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.foundation.layout.Box
import androidx.compose.ui.draw.drawWithCache
import androidx.compose.ui.graphics.Canvas
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.drawscope.CanvasDrawScope
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlin.math.sin

// Read only during drawing. Compose's infinite-animation policy lets tests pause
// ambient motion without holding navigation idle; real devices use display vsync.
@Composable
internal fun ambientTime(active: Boolean, reduced: Boolean): State<Float> {
    val time=remember { mutableFloatStateOf(0f) }
    LaunchedEffect(active,reduced) {
        if (!active || reduced) return@LaunchedEffect
        val offset=time.floatValue
        val start=androidx.compose.animation.core.withInfiniteAnimationFrameNanos { it }
        while(true) androidx.compose.animation.core.withInfiniteAnimationFrameNanos { time.floatValue=offset+(it-start)/1_000_000_000f }
    }
    return time
}

@Composable
internal fun rememberHouseholdSky(data: Household?): SkyGraph {
    var graph by remember { mutableStateOf(SkyGraph(emptyList(), emptyList())) }
    LaunchedEffect(data) {
        if (data == null) { graph = SkyGraph(emptyList(), emptyList()); return@LaunchedEffect }
        val previous = graph.points.associateBy { it.item.id }
        graph = withContext(Dispatchers.Default) { layoutSky(data.dashboards + data.stars, data.links, previous) }
    }
    return graph
}

// The same real graph anchors appear behind Home and in the interactive sky.
// Background dust is deliberately smaller; it never creates graph relationships.
@Composable
internal fun AmbientSky(graph: SkyGraph, links: List<SkyLink>, reduced: Boolean, active: Boolean, expanded: Boolean) {
    val seconds = ambientTime(active,reduced)
    val overview = remember(graph) { skyOverviewEdges(graph) }
    val dust = remember { List(150) { i -> val r=kotlin.random.Random(431+i); Offset(r.nextFloat(),r.nextFloat()) } }
    val dashed = remember(graph, links) {
        val unverified=links.filter { !it.verified }.map { setOf(it.from,it.to) }.toSet()
        graph.edges.map { (a,b) -> setOf(graph.points[a].item.id,graph.points[b].item.id) in unverified }
    }
    Box(Modifier.fillMaxSize().drawWithCache {
        // Batch immutable geometry instead of allocating/drawing hundreds of
        // individual halos and lines on every display frame. Breathing changes
        // only light and a sub-pixel drift; real graph topology stays unchanged.
        val dustPaths=List(4){Path()}
        dust.forEachIndexed { i,p ->
            val r=if(i%13==0) .7.dp.toPx() else .35.dp.toPx()
            val x=p.x*size.width;val y=p.y*size.height
            dustPaths[i%4].addOval(Rect(x-r,y-r,x+r,y+r))
        }
        // Layout already allocates portrait space. Do not zoom a collapsed
        // landscape graph or clip outer records to disguise its geometry.
        val points=graph.points.map { p -> val (x,y)=ambientSkyPosition(p,size.width,size.height);Offset(x,y) }
        val edges=List(2){Path()}
        overview.forEach { i -> val(a,b)=graph.edges[i];edges[if(dashed[i])1 else 0].apply { moveTo(points[a].x,points[a].y);lineTo(points[b].x,points[b].y) } }
        val cores=List(4){Path()};val halos=List(4){Path()}
        graph.points.forEachIndexed { i,p ->
            val bucket=(p.item.id.hashCode() and Int.MAX_VALUE)%4
            val r=(if(p.item.dashboard) 2.1f else if(p.degree>2) 1.7f else 1.15f).dp.toPx()
            val at=points[i];cores[bucket].addOval(Rect(at.x-r,at.y-r,at.x+r,at.y+r))
            if(p.item.dashboard || (p.degree>2 && bucket==0)) {
                val halo=r*4.5f;halos[bucket].addOval(Rect(at.x-halo,at.y-halo,at.x+halo,at.y+halo))
            }
        }
        val dash=PathEffect.dashPathEffect(floatArrayOf(3.dp.toPx(),5.dp.toPx()))
        val line=androidx.compose.ui.graphics.drawscope.Stroke(.55.dp.toPx())
        val suggested=androidx.compose.ui.graphics.drawscope.Stroke(.55.dp.toPx(),pathEffect=dash)
        val upperLight=Brush.radialGradient(listOf(Color(0x80376c5d),Color.Transparent),Offset(size.width*.76f,size.height*.24f),size.height*.45f)
        val lowerLight=Brush.radialGradient(listOf(Color(0x6524594f),Color.Transparent),Offset(size.width*.32f,size.height*.75f),size.height*.48f)
        // Low, quiet silhouettes from the approved motion study, never graph nodes.
        val ridges=listOf(
            listOf(0f to 95f,45f to 110f,98f to 65f,148f to 103f,195f to 75f,240f to 101f,293f to 58f,351f to 96f,400f to 70f,440f to 91f),
            listOf(0f to 151f,70f to 120f,120f to 166f,188f to 118f,220f to 149f,294f to 108f,340f to 152f,399f to 124f,440f to 150f),
            listOf(0f to 190f,100f to 164f,180f to 204f,263f to 170f,350f to 200f,440f to 156f)
        ).map { row -> Path().apply {
            row.forEachIndexed { i,(x,y) -> val px=x/440f*size.width;val py=size.height*.78f+y/280f*size.height*.22f
                if(i==0)moveTo(px,py) else lineTo(px,py) }
            lineTo(size.width,size.height);lineTo(0f,size.height);close()
        } }
        val ridgeColors=listOf(Color(0xff102d2c),Color(0xff061b1f),Color(0xff031216))
        // Static, non-sensitive lighting only. Rasterize once per cache build;
        // the real graph and dust remain live drawings above it. Never disk cached.
        val backdrop=ImageBitmap(size.width.toInt().coerceAtLeast(1),size.height.toInt().coerceAtLeast(1))
        CanvasDrawScope().draw(this,layoutDirection,Canvas(backdrop),size) {
            drawRect(Brush.verticalGradient(listOf(Color(0xff07181d),Color(0xff0b292b),Color(0xff071e24))))
            drawRect(upperLight);drawRect(lowerLight)
            ridges.forEachIndexed { i,path -> drawPath(path,ridgeColors[i].copy(alpha=.38f)) }
        }
        val driftX=.6.dp.toPx();val driftY=.5.dp.toPx()
        onDrawBehind {
            val time=seconds.value;val strength=if(expanded) .12f else 1f
            drawImage(backdrop)
            dustPaths.forEachIndexed { i,path -> drawPath(path,Color(0xffb3cfc9).copy(alpha=.14f+(if(reduced) .15f else (1+sin(time/5+i))*.14f))) }
            withTransform({ if(!reduced) translate(sin(time/5.2f)*driftX,sin(time/6.8f)*driftY) }) {
                drawPath(edges[0],Color(0xff88c5b5).copy(alpha=.10f*strength),style=line)
                drawPath(edges[1],Color(0xff88c5b5).copy(alpha=.10f*strength),style=suggested)
                cores.forEachIndexed { i,path ->
                    val breath=if(reduced) .5f else (sin(time/4.8f+i*1.7f)+1)/2
                    drawPath(halos[i],Color(0xffa9ebd5).copy(alpha=(.025f+.035f*breath)*strength))
                    drawPath(path,Color(0xffa3d7cb).copy(alpha=(.3f+.25f*breath)*strength))
                }
            }
        }
    })
}
