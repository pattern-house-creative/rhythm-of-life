package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.foundation.gestures.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.*
import androidx.compose.ui.input.pointer.util.VelocityTracker
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.platform.*
import kotlinx.coroutines.delay
import kotlin.math.abs

/** A single pointer owner arbitrates the drawer, horizontal Home paging, drawer scrolling and item long-drag. */
@Composable internal fun launcherGestures(
    drag:LauncherDrag,enabled:Boolean,drawer:DrawerController,drawerAtTop:()->Boolean,
    results:List<Tile>,pager:HomePager,paging:Boolean,layout:HomeLayout,onSave:(HomeLayout)->Unit,
    onHold:(Tile)->Unit,onEmptyHold:()->Unit,onLeaveDrawer:()->Unit,onCancelDrawer:()->Unit
):Modifier {
    val current by rememberUpdatedState(layout)
    val choices by rememberUpdatedState(results)
    val active by rememberUpdatedState(enabled)
    val canPage by rememberUpdatedState(paging)
    val atTop by rememberUpdatedState(drawerAtTop)
    val save by rememberUpdatedState(onSave)
    val hold by rememberUpdatedState(onHold)
    val emptyHold by rememberUpdatedState(onEmptyHold)
    val leave by rememberUpdatedState(onLeaveDrawer)
    val restore by rememberUpdatedState(onCancelDrawer)
    val haptic=LocalHapticFeedback.current
    val density=LocalDensity.current.density
    // Holding a lifted tile at the left or right edge of Home turns the page; past the last page is a new, empty one.
    LaunchedEffect(drag.tile,enabled) {
        var dwell=0L
        while(enabled && drag.tile!=null) {
            val grid=drag.grid
            if(drag.moving && canPage && drag.point.y in grid.top..grid.bottom) {
                val edge=28*density
                val direction=when {drag.point.x<grid.left+edge->-1;drag.point.x>grid.right-edge->1;else->0}
                val next=pager.page+direction
                if(direction!=0 && next in 0 until pager.count) {
                    dwell+=16
                    if(dwell>=550){pager.go(next);dwell=-250;haptic.performHapticFeedback(HapticFeedbackType.TextHandleMove)}
                } else dwell=0
                if(grid.contains(drag.point))drag.locate()
            } else dwell=0
            delay(16)
        }
    }
    return Modifier.pointerInput(enabled,drag.generation) {
        if(!enabled)return@pointerInput
        awaitEachGesture {
            val down=awaitFirstDown(false,PointerEventPass.Initial)
            val version=drawer.interruption
            val start=drag.root.topLeft+down.position
            val inDrawer=drawer.contains(start)
            val source=when {drag.dock.contains(start)->DragSource.DOCK;inDrawer->DragSource.DRAWER;else->DragSource.HOME}
            val item=when(source) {
                DragSource.DOCK->drag.slots.entries.firstOrNull{it.value.contains(start)}?.key?.let{current.dock.getOrNull(it)}
                DragSource.DRAWER->choices.firstOrNull{drag.drawerCoordinates[it.key]?.takeIf{it.isAttached}?.boundsInRoot()?.contains(start)==true}
                DragSource.HOME->current.tiles.firstOrNull{drag.homeBounds[it.key]?.contains(start)==true && drag.grid.contains(start)}
            }
            val field=drawer.field?.takeIf{it.isAttached}?.boundsInRoot()?.contains(start)==true
            val header=drawer.header?.takeIf{it.isAttached}?.boundsInRoot()?.contains(start)==true
            val grid=drawer.grid?.takeIf{it.isAttached}?.boundsInRoot()?.contains(start)==true
            val dismiss=inDrawer && !field && (header || (grid && atTop()) || drawer.dragging || drawer.settling)
            val empty=item==null && source==DragSource.HOME && drag.grid.contains(start)
            val canOpen=!inDrawer && (drag.dock.contains(start) || drawer.handle.contains(start) || drag.grid.contains(start))
            val pageable=!inDrawer && source==DragSource.HOME && canPage && drag.grid.contains(start)
            var held=false;var cancelled=false;var left=false;var done=false;var sliding=false;var paging=false
            var last=down
            val velocity=VelocityTracker().apply{addPosition(down.uptimeMillis,down.position)}
            fun beginItem() {held=true;drag.original=current;drag.source=source;drag.tile=item;drag.point=start;haptic.performHapticFeedback(HapticFeedbackType.LongPress)}
            try {
                while(true) {
                    val event=if(!held && !cancelled && !sliding && !paging && (item!=null || empty)) {
                        withTimeoutOrNull((viewConfiguration.longPressTimeoutMillis-(last.uptimeMillis-down.uptimeMillis)).coerceAtLeast(1)) {awaitPointerEvent(PointerEventPass.Initial)}
                    } else awaitPointerEvent(PointerEventPass.Initial)
                    if(drawer.interruption!=version){event?.changes?.forEach{it.consume()};break}
                    if(event==null){beginItem();continue}
                    val change=event.changes.firstOrNull{it.id==down.id} ?: break
                    last=change;velocity.addPosition(change.uptimeMillis,change.position)
                    val delta=change.position-down.position
                    // Compose delivers ACTION_CANCEL as a consumed release. It must roll back even after sliding starts.
                    if(event.changes.count{it.pressed}>1 || (change.isConsumed && (!(sliding || paging) || !change.pressed)))cancelled=true
                    if(!held && !cancelled && !sliding && !paging && (item!=null || empty) && change.uptimeMillis-down.uptimeMillis>=viewConfiguration.longPressTimeoutMillis)beginItem()
                    if(!held && !cancelled && !sliding && !paging && delta.getDistance()>viewConfiguration.touchSlop) {
                        val vertical=abs(delta.y)>abs(delta.x)*1.3f
                        if(vertical && ((canOpen && delta.y<0) || (dismiss && delta.y>0) || (inDrawer && drawer.settling && !field))) {
                            sliding=true;drawer.beginDrag()
                        } else if(pageable && abs(delta.x)>abs(delta.y)) {
                            // Consuming from here on cancels the tap on a tile or preview under the finger.
                            paging=true;pager.begin()
                        } else cancelled=true // The LazyGrid keeps this entire scrolling gesture.
                    }
                    if(sliding) {
                        change.consume()
                        if(!cancelled)drawer.drag(delta.y)
                    } else if(paging) {
                        change.consume()
                        if(!cancelled)pager.drag(change.position.x-change.previousPosition.x)
                    } else if(held) {
                        change.consume();drag.point=drag.root.topLeft+change.position
                        if(!cancelled && delta.getDistance()>viewConfiguration.touchSlop && item!=null) {
                            drag.moving=true
                            if(source==DragSource.DRAWER && !left){left=true;leave()}
                            drag.locate()
                        }
                    }
                    if(!change.pressed) {
                        if(sliding) {if(cancelled)drawer.cancelDrag() else drawer.release(velocity.calculateVelocity().y,density)}
                        else if(paging) {if(cancelled)pager.go(pager.page) else pager.release(velocity.calculateVelocity().x,density)}
                        else if(!cancelled)when {
                            held && drag.moving && drag.candidate!=null->{save(drag.candidate!!);haptic.performHapticFeedback(HapticFeedbackType.TextHandleMove)}
                            held && !drag.moving && item!=null->hold(item)
                            held && empty->emptyHold()
                            left->restore()
                        } else if(left)restore()
                        done=true;break
                    }
                }
            } finally {
                if(!done && sliding)drawer.cancelDrag()
                if(!done && paging)pager.go(pager.page)
                if(!done && left && active && drawer.interruption==version)restore()
                drag.clear()
            }
        }
    }
}
