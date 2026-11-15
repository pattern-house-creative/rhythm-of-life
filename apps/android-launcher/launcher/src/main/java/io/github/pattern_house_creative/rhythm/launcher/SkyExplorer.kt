package io.github.pattern_house_creative.rhythm.launcher

import androidx.activity.compose.BackHandler
import androidx.compose.animation.core.animate
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.*
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.*
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import kotlin.math.*


@Composable
internal fun SkyExplorer(data: Household?, graph: SkyGraph, reduced: Boolean, active: Boolean, open: (Tile) -> Unit) {
    val scope=rememberCoroutineScope()
    var camera by remember { mutableStateOf(SkyCamera()) }
    var selection by remember { mutableStateOf<String?>(null) }
    var cameraJob by remember { mutableStateOf<Job?>(null) }
    var catalogue by remember { mutableStateOf(false) }
    var coverage by remember { mutableStateOf(false) }
    var followingFit by remember { mutableStateOf(true) }
    val seconds=ambientTime(active,reduced)
    LaunchedEffect(data==null) { if(data==null) { selection=null;followingFit=true;cameraJob?.cancel() } }
    // One immutable graph snapshot per composition/draw: async layout publication
    // must not pair new edge indexes with the previous frame's style/point arrays.
    val rendered=graph
    val overview=remember(rendered) { skyOverviewEdges(rendered) }
    val visibleEdges=remember(rendered,overview,selection) { skyVisibleEdges(rendered,overview,selection) }
    val extent=remember(rendered) {
        if(rendered.points.isEmpty()) Rect(0f,0f,820f,560f) else Rect(
            rendered.points.minOf { it.x }-45f,rendered.points.minOf { it.y }-45f,
            rendered.points.maxOf { it.x }+45f,rendered.points.maxOf { it.y }+45f)
    }
    val fitCamera=SkyCamera(extent.center.x,extent.center.y)
    LaunchedEffect(rendered) { if(followingFit)camera=fitCamera }
    val density=LocalDensity.current
    val measurer=rememberTextMeasurer(cacheSize=128)
    val onOpen by rememberUpdatedState(open)
    val unverified=remember(data) { data?.links.orEmpty().filter { !it.verified }.map { setOf(it.from,it.to) }.toSet() }
    val dashed=remember(rendered,unverified) { rendered.edges.map { (a,b) -> setOf(rendered.points[a].item.id,rendered.points[b].item.id) in unverified } }
    val dashPattern=remember(density) { PathEffect.dashPathEffect(floatArrayOf(3*density.density,3*density.density)) }
    fun openPoint(p: SkyPoint) = onOpen(Tile(if(p.item.dashboard) TileKind.DASHBOARD else TileKind.STAR,p.item.id))
    fun moveCamera(target: SkyCamera) {
        followingFit=false
        cameraJob?.cancel()
        if(reduced) { camera=target; return }
        val from=camera
        cameraJob=scope.launch { animate(0f,1f,animationSpec=tween(500,easing=ObservatoryEase)) { value,_ ->
            camera=SkyCamera(from.x+(target.x-from.x)*value,from.y+(target.y-from.y)*value,from.zoom+(target.zoom-from.zoom)*value)
        } }
    }
    fun select(p: SkyPoint) {
        if(selection==p.item.id) { openPoint(p);return }
        selection=p.item.id
        moveCamera(SkyCamera(p.x,p.y,maxOf(camera.zoom,1.65f)))
    }
    BackHandler(selection!=null && active) { selection=null }
    val chosen=rendered.points.firstOrNull { it.item.id==selection }
    val palette=listOf(Color(0xffb2ffdf),Color(0xffb7e5dc),Color(0xffc1eadb),Color(0xffc9e7ca))
    val glowRadius=with(density) { 18.dp.toPx() }
    val glows=remember(density) { palette.map { color -> Brush.radialGradient(listOf(color.copy(alpha=.27f),color.copy(alpha=.08f),Color.Transparent),center=Offset.Zero,radius=glowRadius) } }
    Column(Modifier.fillMaxSize()) {
        BoxWithConstraints(Modifier.fillMaxWidth().weight(1f).clipToBounds().testTag("sky-map").semantics { stateDescription="${rendered.points.size} loaded; zoom ${camera.zoom}" }) {
            val w=with(density){maxWidth.toPx()};val h=with(density){maxHeight.toPx()}
            val viewport=remember(w,h,extent) { SkyViewport(w,h,extent.width,extent.height) }
            fun screen(p: SkyPoint,time:Float=0f): Offset {
                val (x,y)=skyDrift(p,time,reduced)
                return Offset(viewport.x(x,camera),viewport.y(y,camera))
            }
            fun transform(center:Offset,delta:Offset,factor:Float) {
                camera=viewport.transform(camera,center.x,center.y,delta.x,delta.y,factor)
            }
            val visible=rendered.points.filter { val p=screen(it);p.x in -30f..w+30 && p.y in -30f..h+30 }
            val labelBoxes=mutableListOf<Rect>()
            val labels=visible.sortedWith(compareByDescending<SkyPoint>{it.item.id==selection}.thenByDescending { it.degree }.thenBy { it.item.id })
                .mapNotNull { p ->
                    if(camera.zoom<1.4f && p.degree==0 && !p.item.dashboard && selection!=p.item.id) return@mapNotNull null
                    if(camera.zoom<2.5f && labelBoxes.size>=(if(camera.zoom<1.4f) 7 else 14) && selection!=p.item.id) return@mapNotNull null
                    val text=measurer.measure(p.item.title,TextStyle(color=Color(0xffd8eee4),fontSize=11.sp,fontFamily=ObservatoryBody,letterSpacing=.5.sp),maxLines=1,overflow=TextOverflow.Ellipsis,
                        constraints=Constraints(maxWidth=with(density){110.dp.roundToPx()}))
                    val at=screen(p)+Offset(7*density.density,5*density.density)
                    val box=Rect(at.x,at.y,at.x+text.size.width,at.y+text.size.height)
                    if(box.right>w || box.bottom>h || box.left<0 || box.top<0 || labelBoxes.any { it.overlaps(box.inflate(3*density.density)) }) null
                    else { labelBoxes.add(box);Triple(p,text,at) }
                }
            val currentLabels by rememberUpdatedState(labels)
            Canvas(Modifier.fillMaxSize().pointerInput(rendered,w,h,reduced) {
                awaitEachGesture {
                    val down=awaitFirstDown(false,PointerEventPass.Initial);down.consume();cameraJob?.cancel();followingFit=false
                    var moved=false;var cancelled=false;var event:PointerEvent
                    var lastDelta=Offset.Zero;var lastTime=down.uptimeMillis;var velocity=Offset.Zero
                    do {
                        event=awaitPointerEvent(PointerEventPass.Initial)
                        if(event.changes.any { it.isConsumed })cancelled=true
                        val fingers=event.changes.count { it.pressed }
                        val change=event.changes.firstOrNull { it.id==down.id }
                        if(fingers>1 || (change?.position?.minus(down.position)?.getDistance()?:0f)>viewConfiguration.touchSlop) moved=true
                        if(moved && fingers>0) {
                            lastDelta=event.calculatePan()
                            val now=event.changes.first().uptimeMillis
                            velocity=if(fingers==1) lastDelta/(now-lastTime).coerceAtLeast(1).toFloat()*1000f else Offset.Zero
                            lastTime=now
                            transform(event.calculateCentroid(false),lastDelta,event.calculateZoom())
                        }
                        event.changes.forEach { it.consume() }
                    } while(event.changes.any { it.pressed })
                    if(!moved && !cancelled) {
                        val up=event.changes.firstOrNull { it.id==down.id }
                        val labelled=up?.let { u -> currentLabels.firstOrNull { (p,text,_) ->
                            val at=screen(p,seconds.value)+Offset(7*density.density,5*density.density)
                            Rect(at.x,at.y,at.x+text.size.width,at.y+text.size.height).inflate(4*density.density).contains(u.position)
                        }?.first }
                        val closest=up?.let { u -> rendered.points.minByOrNull { (screen(it,seconds.value)-u.position).getDistance() } }
                        val exact=closest?.takeIf { (screen(it,seconds.value)-up!!.position).getDistance()<=8*density.density }
                        val nearest=exact ?: labelled ?: closest
                        if(nearest!=null && (labelled!=null || (screen(nearest,seconds.value)-up!!.position).getDistance()<=24*density.density)) select(nearest)
                        else selection=null
                    } else if(!cancelled && !reduced && velocity.getDistance()>150) {
                        val initial=velocity;cameraJob=scope.launch {
                            var speed=initial;var before=withFrameNanos { it }
                            repeat(24) {
                                val now=withFrameNanos { it };val dt=((now-before)/1_000_000_000f).coerceAtMost(.04f);before=now
                                transform(Offset(w/2,h/2),speed*dt,1f);speed*=exp(-dt*9f)
                            }
                        }
                    }
                }
            }.semantics { contentDescription="Household constellation. Pinch to zoom, drag to pan. Tap a star to focus, then Open. Use Stars for an accessible list." }) {
                val t=seconds.value
                val points=rendered.points.map { screen(it,t) }
                visibleEdges.forEach { edgeIndex ->
                    val(a,b)=rendered.edges[edgeIndex]
                    val focused=rendered.points[a].item.id==selection || rendered.points[b].item.id==selection
                    val pa=points[a];val pb=points[b]
                    if(maxOf(pa.x,pb.x)>=0 && minOf(pa.x,pb.x)<=w && maxOf(pa.y,pb.y)>=0 && minOf(pa.y,pb.y)<=h) {
                        val automatic=dashed[edgeIndex]
                        drawLine(Color(0xff7bcab7).copy(alpha=if(focused) .8f else if(selection!=null) .05f else .20f),pa,pb,
                            if(focused) 1.dp.toPx() else .55.dp.toPx(),
                            pathEffect=if(automatic) dashPattern else null)
                    }
                }
                visible.forEach { p ->
                    val at=screen(p,t);val selected=p.item.id==selection
                    val phase=(p.item.id.hashCode() and 255)*.03f
                    val breath=if(reduced) .5f else (sin(t*2f*PI.toFloat()/3f+phase)+1)/2
                    val color=if(p.group<0) Color(0xff98b4bb) else palette[p.group%palette.size]
                    val prominent=rendered.points.size<=60 || p.item.dashboard || p.degree>2 || selected
                    val core=(if(p.item.dashboard) 2.8f else if(prominent) 2.3f else 1.25f).dp.toPx()
                    if(prominent) withTransform({ translate(at.x,at.y);scale(.8f+breath*.2f,.8f+breath*.2f,Offset.Zero) }) {
                        drawCircle(glows[if(p.group<0)0 else p.group%glows.size],glowRadius,Offset.Zero,alpha=if(selected)1f else .65f+breath*.35f)
                    }
                    drawCircle(color.copy(alpha=.12f+breath*.08f),core*2f,at)
                    drawCircle(if(prominent) Color(0xffe1fff3) else color.copy(alpha=.72f),core,at)
                    if(selected) drawCircle(Color(0xffd8fff0),core+5.dp.toPx(),at,style=Stroke(1.dp.toPx()))
                }
                labels.forEach { (p,text,_) -> drawText(text,topLeft=screen(p,t)+Offset(7.dp.toPx(),5.dp.toPx())) }
            }
            // Semantics follow visible anchors; pointer input uses nearest-star hit testing.
            // Dense maps expose labelled targets here and every record in the Stars
            // list. Hundreds of overlapping semantics boxes are neither usable to a
            // screen reader nor cheap during a pinch. Physical hit testing still sees all.
            visible.filter { rendered.points.size<=60 || it.item.id==selection || labels.any { l -> l.first.item.id==it.item.id } }.forEach { p -> val at=screen(p)
                Box(Modifier.offset { IntOffset((at.x-24*density.density).toInt(),(at.y-24*density.density).toInt()) }.size(48.dp)
                    .testTag("sky:${p.item.id}").semantics { contentDescription=p.item.title;role=Role.Button
                        onClick("Focus ${p.item.title}") { select(p);true }
                        customActions=listOf(CustomAccessibilityAction("Open ${p.item.title}") { openPoint(p);true }) })
            }
            if(rendered.points.isEmpty()) Text(if((data != null && (data.dashboards.isNotEmpty() || data.stars.isNotEmpty()) && graph.points.isEmpty())) "Finding the constellation…" else if(data==null) "Connect your household to explore its stars" else "No readable stars in this discovery view",
                color=Color(0xff9bb6b7),fontSize=12.sp,modifier=Modifier.align(Alignment.Center).padding(24.dp))
        }
        Row(Modifier.fillMaxWidth().height(48.dp).testTag("sky-controls"),verticalAlignment=Alignment.CenterVertically) {
            if(chosen!=null) {
                Text(chosen.item.title,maxLines=1,overflow=TextOverflow.Ellipsis,fontSize=12.sp,color=Color(0xffc5dedb),modifier=Modifier.weight(1f))
                TextButton(onClick={openPoint(chosen)},modifier=Modifier.testTag("open-sky-selection")) { Text("Open") }
            } else {
                TextButton(onClick={coverage=true},contentPadding=PaddingValues(2.dp),modifier=Modifier.weight(1f)) { Text("${rendered.points.size} loaded · discovery",fontSize=10.sp,maxLines=1) }
                TextButton(onClick={catalogue=true},contentPadding=PaddingValues(4.dp)) { Text("Stars",fontSize=11.sp,color=Color(0xffa4ead7)) }
            }
            IconButton(onClick={moveCamera(camera.copy(zoom=(camera.zoom/1.5f).coerceAtLeast(.75f)))},modifier=Modifier.size(48.dp).semantics { contentDescription="Zoom out sky" }) { Canvas(Modifier.size(16.dp)) {
                drawLine(Color(0xffa4ead7),center-Offset(5.dp.toPx(),0f),center+Offset(5.dp.toPx(),0f),1.4.dp.toPx())
            } }
            IconButton(onClick={moveCamera(camera.copy(zoom=(camera.zoom*1.5f).coerceAtMost(8f)))},modifier=Modifier.size(48.dp).semantics { contentDescription="Zoom in sky" }) { Canvas(Modifier.size(16.dp)) {
                drawLine(Color(0xffa4ead7),center-Offset(5.dp.toPx(),0f),center+Offset(5.dp.toPx(),0f),1.4.dp.toPx())
                drawLine(Color(0xffa4ead7),center-Offset(0f,5.dp.toPx()),center+Offset(0f,5.dp.toPx()),1.4.dp.toPx())
            } }
            TextButton(onClick={selection=null;moveCamera(fitCamera);followingFit=true},contentPadding=PaddingValues(2.dp),modifier=Modifier.semantics { contentDescription="Fit sky" }) { Text("Fit",fontSize=11.sp) }
        }
    }
    if(catalogue) AlertDialog(onDismissRequest={catalogue=false},title={Text("Loaded stars")},text={
        LazyColumn(Modifier.heightIn(max=400.dp).testTag("sky-catalogue")) { items(rendered.points,key={it.item.id}) { p ->
            TextButton(onClick={catalogue=false;select(p)},modifier=Modifier.fillMaxWidth()) { Text(p.item.title) }
        } }
    },confirmButton={TextButton(onClick={catalogue=false}) { Text("Close") }})
    if(coverage) AlertDialog(onDismissRequest={coverage=false},title={Text("Discovery view")},text={Text((data?.coverage ?: "Connect your household to load its readable discovery view. This is not a count of all household records.")+"\n\nOverview lines are simplified. Select a star to see all its loaded connections. Visual neighborhoods are not source categories.")},confirmButton={TextButton(onClick={coverage=false}){Text("Close")}})
}
