package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.animation.core.animate
import androidx.compose.animation.core.tween
import androidx.compose.runtime.*
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.layout.LayoutCoordinates
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.roundToInt

internal fun drawerReleaseTarget(fraction:Float,velocityY:Float,moved:Float,density:Float,anchorOpen:Boolean):Boolean {
    if(fraction<=0f)return false
    if(fraction>=1f)return true
    if(abs(velocityY)>400f*density && abs(moved)>12f*density)return velocityY<0f
    // Preserve the launcher's established short swipe distance in both directions.
    // A slow partial pull returns to its previous target unless it crosses 48dp.
    return if(abs(moved)>=48f*density)moved>0f else anchorOpen
}

/** One position drives drag and settling. Root composition observes only endpoint states. */
@Stable internal class DrawerController(private val scope:CoroutineScope) {
    var fraction by mutableFloatStateOf(0f); private set
    var targetOpen by mutableStateOf(false); private set
    var dragging by mutableStateOf(false); private set
    var settling by mutableStateOf(false); private set
    var interruption=0; private set
    val exposed by derivedStateOf { targetOpen || dragging || fraction>0f }
    val fullyClosed by derivedStateOf { !targetOpen && !dragging && !settling && fraction==0f }
    val settledOpen by derivedStateOf { targetOpen && !dragging && !settling && fraction==1f }
    val covered by derivedStateOf { fraction==1f }
    var reduced=false
    var viewport=Rect.Zero
    var handle=Rect.Zero
    var header:LayoutCoordinates?=null
    var field:LayoutCoordinates?=null
    var grid:LayoutCoordinates?=null
    private var job:Job?=null
    private var animation=0
    private var start=0f
    private var anchor=false
    val travel get()=viewport.height.coerceAtLeast(1f)
    fun contains(point:androidx.compose.ui.geometry.Offset)=exposed && Rect(viewport.left,
        viewport.top+(1-fraction)*travel,viewport.right,viewport.bottom).contains(point)
    fun request(open:Boolean) { interruption++;settle(open) }
    fun snapClosed(interrupt:Boolean=true) {
        if(interrupt)interruption++;animation++;job?.cancel();job=null
        dragging=false;settling=false;targetOpen=false;fraction=0f
    }
    fun beginDrag() {
        animation++;job?.cancel();job=null;settling=false
        anchor=targetOpen;start=fraction;dragging=true
    }
    fun drag(deltaY:Float) { fraction=(start-deltaY/travel).coerceIn(0f,1f) }
    fun release(velocityY:Float,density:Float) {
        val open=drawerReleaseTarget(fraction,velocityY,(fraction-start)*travel,density,anchor)
        settle(open)
    }
    fun cancelDrag() { if(dragging)settle(anchor) }
    private fun settle(open:Boolean) {
        val id=++animation;job?.cancel();dragging=false;targetOpen=open
        val end=if(open)1f else 0f
        if(reduced || fraction==end) {fraction=end;settling=false;return}
        val from=fraction;settling=true
        val duration=((if(open)260 else 200)*abs(end-from)).roundToInt().coerceIn(90,260)
        job=scope.launch {
            try { animate(from,end,animationSpec=tween(duration,easing=ObservatoryEase)) { value,_->fraction=value };fraction=end }
            finally {if(animation==id)settling=false}
        }
    }
}
