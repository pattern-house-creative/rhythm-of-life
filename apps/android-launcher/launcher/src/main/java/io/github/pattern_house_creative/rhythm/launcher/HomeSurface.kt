package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.animation.core.*
import androidx.compose.foundation.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.*
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.*
import androidx.compose.ui.graphics.*
import androidx.compose.ui.layout.*
import androidx.compose.ui.platform.*
import androidx.compose.ui.semantics.*
import androidx.compose.ui.unit.*
import androidx.compose.ui.text.style.TextOverflow
import kotlin.math.roundToInt

internal enum class DragSource { HOME, DRAWER, DOCK }
@Stable internal class LauncherDrag {
    var tile by mutableStateOf<Tile?>(null)
    var moving by mutableStateOf(false)
    var point by mutableStateOf(Offset.Zero)
    var target by mutableStateOf<Rect?>(null)
    var message by mutableStateOf("")
    var generation by mutableIntStateOf(0)
    var source=DragSource.HOME
    var original=HomeLayout()
    var candidate:HomeLayout?=null
    var root=Rect.Zero
    var grid by mutableStateOf(Rect.Zero)
    var dock=Rect.Zero
    var cellWidth=0f
    var cellGap=0f
    var rowStep=0f
    var scrollY=0f
    val homeBounds=mutableMapOf<String,Rect>()
    // Resolve live coordinates on touch: the drawer moves in a graphics layer.
    val drawerCoordinates=mutableMapOf<String,LayoutCoordinates>()
    val slots=mutableMapOf<Int,Rect>()
    fun clear() { tile=null;moving=false;target=null;candidate=null;message="" }
    fun cancel() { generation++;clear() }
    fun locate() {
        val item=tile ?: return
        target=null;candidate=null
        if(dock.contains(point)) {
            if(item.kind!=TileKind.APP) {message="Only apps belong in the dock";return}
            val index=slots.minByOrNull { (_,r)->kotlin.math.abs(r.center.x-point.x) }?.key ?: return
            val next=original.insertDock(item,index,source==DragSource.HOME)
            if(next==original && original.dock.none{it.key==item.key}) {message="Make room on Home before adding to the full dock";return}
            candidate=next;target=slots[next.dock.indexOfFirst{it.key==item.key}]
            val spill=next.tiles.any { t->original.tiles.none { it.key==t.key } }
            message=if(spill) "Place in dock · last app moves to Home" else "Place in dock"
        } else if(grid.contains(point)) {
            val visible=homeBounds.filter { (_,r)->r.overlaps(grid) }
            val hit=visible.entries.firstOrNull { it.value.contains(point) }
            val prior=original.tiles.indexOfFirst { it.key==item.key }
            val index=if(hit!=null) {
                val i=original.tiles.indexOfFirst { it.key==hit.key }
                i+if(prior>=0 && prior<i)1 else 0
            } else {
                visible.entries.sortedWith(compareBy({it.value.top},{it.value.left})).firstOrNull {
                    it.value.center.y>point.y+it.value.height*.4f ||
                        (kotlin.math.abs(it.value.center.y-point.y)<it.value.height*.4f && it.value.center.x>point.x)
                }?.let { e->original.tiles.indexOfFirst { it.key==e.key } } ?: original.tiles.size
            }
            val next=original.insertHome(item,index,source==DragSource.DOCK)
            if(prior<0 && original.tiles.size>=120) {message="Home is full";return}
            candidate=next
            val cell=packHomeCells(next.tiles).first { it.tile.key==item.key }
            target=Rect(Offset(grid.left+cell.column*(cellWidth+cellGap),grid.top+cellGap*.8f+cell.row*rowStep-scrollY),
                Size(cell.columns*cellWidth+(cell.columns-1)*cellGap,cell.rows*rowStep-cellGap*.8f)).intersect(grid)
            message="Place on Home"
        } else message="Move over Home or the dock"
    }
}

@Composable internal fun LauncherTile(tile:Tile,title:String,app:LaunchApp?,origin:String,live:Boolean,online:Boolean,
    modifier:Modifier,onOpen:()->Unit,onEdit:()->Unit,onEarlier:(()->Unit)?,onLater:(()->Unit)?,onAccessLost:()->Unit,
    onRemove:(()->Unit)?=null,editing:Boolean=false,listRow:Boolean=false,webActive:()->Boolean={true}) {
    Box(modifier.testTag("tile:${tile.key}").semantics {
        customActions=buildList {
            add(CustomAccessibilityAction("Edit $title"){onEdit();true})
            if(onRemove!=null)add(CustomAccessibilityAction("Remove from Home"){onRemove();true})
            if(onEarlier!=null)add(CustomAccessibilityAction("Move earlier"){onEarlier();true})
            if(onLater!=null)add(CustomAccessibilityAction("Move later"){onLater();true})
        }
        onLongClick(label="Home options"){onEdit();true}
    }.clickable(role=Role.Button,onClickLabel=if(tile.kind==TileKind.PREVIEW)"Open dashboard" else "Open $title",onClick=onOpen)) {
      if (listRow) {
          Row(Modifier.fillMaxSize().padding(horizontal=8.dp), verticalAlignment=Alignment.CenterVertically,
              horizontalArrangement=Arrangement.spacedBy(14.dp)) {
              if(app!=null) Image(app.icon.asImageBitmap(),null,Modifier.size(36.dp))
              else DashboardGlyph(title,tile.target,Modifier.size(36.dp))
              Text(title,fontSize=16.sp,maxLines=2,overflow=TextOverflow.Ellipsis)
          }
      } else Column(Modifier.fillMaxSize(),horizontalAlignment=Alignment.CenterHorizontally) {
        if(tile.kind==TileKind.PREVIEW) {
            Box(Modifier.fillMaxSize().clip(RoundedCornerShape(22.dp)).background(Color(0x99152d29))
                .border(1.dp,Color(0x2bbbe8d5),RoundedCornerShape(22.dp))) {
                if(live)RestrictedWeb(origin,webDestination(origin,tile,true),Modifier.fillMaxSize(),active=webActive(),accessLost=onAccessLost)
                else Column(Modifier.fillMaxSize().padding(12.dp),horizontalAlignment=Alignment.CenterHorizontally,verticalArrangement=Arrangement.Center) {
                    DashboardGlyph(title,tile.target,Modifier.size(32.dp))
                    Text(title,fontSize=12.sp,maxLines=2,modifier=Modifier.padding(top=8.dp))
                    Text("Preview unavailable",fontSize=11.sp,color=Color(0xffb4c9cc),modifier=Modifier.padding(top=8.dp))
                    Text(if(origin.isBlank())"Connect your household" else if(!online)"Reconnect to load live data" else "Check household access",fontSize=9.sp,color=Color(0xff80999f))
                }
                // Native full-card hit target. Renderer HTML receives no native bridge.
                Box(Modifier.fillMaxSize().clearAndSetSemantics { contentDescription=title }
                    .clickable(role=Role.Button,onClickLabel="Open dashboard",onClick=onOpen))
            }
        } else {
            Spacer(Modifier.height(3.dp))
            if(app!=null)Image(app.icon.asImageBitmap(),null,Modifier.size(44.dp)) else DashboardGlyph(title,tile.target)
            Spacer(Modifier.height(4.dp))
            Text(title,color=Color(0xffd6e2e3),fontSize=11.sp,lineHeight=13.sp,maxLines=2,overflow=TextOverflow.Ellipsis)
        }
      }
      if(editing && onRemove!=null) IconButton(onClick=onRemove,modifier=Modifier.align(Alignment.TopEnd).size(40.dp)
          .testTag("remove-home:${tile.key}").semantics{contentDescription="Remove $title from Home"}
          .background(Color(0xff563b3e),RoundedCornerShape(20.dp)).border(1.dp,Color(0xffe9b8b8),RoundedCornerShape(20.dp))) {
          Text("−",fontSize=24.sp,color=Color(0xffffdddd))
      }
    }
}

/** A four-column cell surface, not a row grid: previews reserve two rows. */
@Composable internal fun HomeCellGrid(
    tiles:List<Tile>, scroll:ScrollState, drag:LauncherDrag, still:Boolean,
    modifier:Modifier=Modifier, content:@Composable (Tile,Modifier)->Unit
) {
    val cells=remember(tiles){packHomeCells(tiles)}
    val density=LocalDensity.current
    BoxWithConstraints(modifier.testTag("home-grid").clipToBounds()
        .onGloballyPositioned { drag.grid=it.boundsInRoot() }
        .semantics { collectionInfo=CollectionInfo(cells.maxOfOrNull{it.row+it.rows} ?: 0,4) }
        .verticalScroll(scroll)) {
        val width=(maxWidth-30.dp)/4
        SideEffect { drag.cellWidth=with(density){width.toPx()};drag.cellGap=with(density){10.dp.toPx()};drag.rowStep=with(density){88.dp.toPx()};drag.scrollY=scroll.value.toFloat() }
        val rows=cells.maxOfOrNull{it.row+it.rows} ?: 0
        Box(Modifier.fillMaxWidth().height((rows*88+16).dp)) {
            cells.forEach { cell -> key(cell.tile.key) {
                val y=(cell.row*88+8).dp
                val height=if(cell.rows==2)168.dp else 80.dp
                val top=with(density){y.toPx()}
                // Keep a viewport of overscan; offscreen WebViews are disposed.
                if(top+with(density){height.toPx()} >= scroll.value-drag.grid.height && top<=scroll.value+drag.grid.height*2+600) {
                    val x by animateDpAsState((width+10.dp)*cell.column,tween(if(still)0 else 180),label="Cell x")
                    val animatedY by animateDpAsState(y,tween(if(still)0 else 180),label="Cell y")
                    DisposableEffect(cell.tile.key){onDispose{drag.homeBounds.remove(cell.tile.key)}}
                    content(cell.tile,Modifier.offset(x,animatedY).width(width*cell.columns+10.dp*(cell.columns-1)).height(height)
                        .onGloballyPositioned { drag.homeBounds[cell.tile.key]=it.boundsInRoot() }
                        .graphicsLayer { alpha=if(drag.tile?.key==cell.tile.key && drag.source==DragSource.HOME).25f else 1f }
                        .semantics { collectionItemInfo=CollectionItemInfo(cell.row,cell.rows,cell.column,cell.columns) })
                }
            } }
        }
    }
}

@Composable internal fun DragOverlay(drag:LauncherDrag,title:(Tile)->String,apps:List<LaunchApp>) {
    val item=drag.tile ?: return
    val density=LocalDensity.current
    drag.target?.let { rect->
        Box(Modifier.offset { IntOffset((rect.left-drag.root.left).roundToInt(),(rect.top-drag.root.top).roundToInt()) }
            .size(with(density){rect.width.toDp()},with(density){rect.height.toDp()})
            .border(2.dp,Color(0xffb3efd3),RoundedCornerShape(18.dp)).background(Color(0x227ae6b5),RoundedCornerShape(18.dp)))
    }
    Column(Modifier.offset { IntOffset((drag.point.x-drag.root.left-44.dp.toPx()).roundToInt(),(drag.point.y-drag.root.top-64.dp.toPx()).roundToInt()) }
        .width(88.dp).background(Color(0xf21c3734),RoundedCornerShape(20.dp)).border(1.dp,Color(0xff9acdb7),RoundedCornerShape(20.dp))
        .padding(8.dp).testTag("drag-lifted"),horizontalAlignment=Alignment.CenterHorizontally) {
        val app=apps.find{it.tile.key==item.key}
        if(app!=null)Image(app.icon.asImageBitmap(),null,Modifier.size(44.dp)) else DashboardGlyph(title(item),item.target)
        Text(title(item),fontSize=10.sp,maxLines=2)
    }
    Box(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.systemBars.union(WindowInsets.displayCutout)).padding(bottom=88.dp),contentAlignment=Alignment.BottomCenter) {
        Text(if(drag.moving)drag.message else "Release for Home options",fontSize=12.sp,
            modifier=Modifier.background(Color(0xf2203934),RoundedCornerShape(18.dp)).padding(12.dp)
                .semantics { liveRegion=LiveRegionMode.Polite })
    }
}
