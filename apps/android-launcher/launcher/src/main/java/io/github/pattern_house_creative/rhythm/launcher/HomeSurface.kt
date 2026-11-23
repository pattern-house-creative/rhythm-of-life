package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.animation.core.*
import androidx.compose.foundation.*
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.animation.animateColorAsState
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.*
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.graphics.drawscope.clipRect
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
    /** The Home page in view and how many rows a page holds (0 before the first measure), read live. */
    var currentPage:()->Int={0}
    var pageRows:()->Int={0}
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
            if(original.tiles.none { it.key==item.key } && original.tiles.size>=HOME_LIMIT) {message="Home is full";return}
            // Only the page in view takes the drop. The page after the last one starts a new page.
            val page=currentPage();val rows=pageRows()
            val fresh=page>=original.pageCount
            val tiles=original.page(page)
            val visible=homeBounds.filter { (key,r)->r.overlaps(grid) && tiles.any { it.key==key } }
            val hit=visible.entries.firstOrNull { it.value.contains(point) }
            val prior=tiles.indexOfFirst { it.key==item.key }
            val index=if(hit!=null) {
                val i=tiles.indexOfFirst { it.key==hit.key }
                i+if(prior>=0 && prior<i)1 else 0
            } else {
                visible.entries.sortedWith(compareBy({it.value.top},{it.value.left})).firstOrNull {
                    it.value.center.y>point.y+it.value.height*.4f ||
                        (kotlin.math.abs(it.value.center.y-point.y)<it.value.height*.4f && it.value.center.x>point.x)
                }?.let { e->tiles.indexOfFirst { it.key==e.key } } ?: tiles.size
            }
            val placed=original.placeHome(item,page,index,source==DragSource.DOCK,newPage=fresh)
            val next=if(rows>0)placed.fit(rows) else placed
            candidate=next
            val landed=next.pageOf(item.key)
            val cell=packHomeCells(next.page(landed)).first { it.tile.key==item.key }
            // A tile alone on the last page that is dropped on the new page simply stays on a page of its own.
            if(landed==page || fresh) target=Rect(Offset(grid.left+cell.column*(cellWidth+cellGap),grid.top+cellGap*.8f+cell.row*rowStep),
                Size(cell.columns*cellWidth+(cell.columns-1)*cellGap,cell.rows*rowStep-cellGap*.8f)).intersect(grid)
            message=when {
                fresh -> "Place on a new page"
                landed!=page -> "This page is full · place at the start of page ${landed+1}"
                next.pageCount>1 -> "Place on page ${page+1}"
                else -> "Place on Home"
            }
        } else message="Move over Home or the dock"
    }
}

@Composable internal fun LauncherTile(tile:Tile,title:String,app:LaunchApp?,origin:String,live:Boolean,online:Boolean,
    modifier:Modifier,onOpen:()->Unit,onEdit:()->Unit,onEarlier:(()->Unit)?,onLater:(()->Unit)?,onAccessLost:()->Unit,
    onRemove:(()->Unit)?=null,editing:Boolean=false,listRow:Boolean=false,webActive:()->Boolean={true},
    pageMoves:List<Pair<String,()->Unit>> =emptyList()) {
    // Keyboard and D-pad focus land on the tile itself. A preview shows that focus as its frame's ring
    // (PreviewFrame), not as a square overlay past its rounded corners.
    val tileInteraction=remember { MutableInteractionSource() }
    val preview=tile.kind==TileKind.PREVIEW && !listRow
    Box(modifier.testTag("tile:${tile.key}").semantics {
        customActions=buildList {
            add(CustomAccessibilityAction("Edit $title"){onEdit();true})
            if(onRemove!=null)add(CustomAccessibilityAction("Remove from Home"){onRemove();true})
            if(onEarlier!=null)add(CustomAccessibilityAction("Move earlier"){onEarlier();true})
            if(onLater!=null)add(CustomAccessibilityAction("Move later"){onLater();true})
            pageMoves.forEach { (label,move)->add(CustomAccessibilityAction(label){move();true}) }
        }
        onLongClick(label="Home options"){onEdit();true}
    }.clickable(interactionSource=tileInteraction,indication=if(preview)null else LocalIndication.current,
        role=Role.Button,onClickLabel=tileActionLabel(tile,title,editing),onClick=onOpen)) {
      if (listRow) {
          Row(Modifier.fillMaxSize().padding(horizontal=Ui.space2), verticalAlignment=Alignment.CenterVertically,
              horizontalArrangement=Arrangement.spacedBy(Ui.space4)) {
              if(app!=null) Image(app.icon.asImageBitmap(),null,Modifier.size(36.dp))
              else DashboardGlyph(title,tile.target,Modifier.size(36.dp))
              Text(title,fontSize=Ui.textLead,lineHeight=20.sp,color=Ui.ink,maxLines=2,overflow=TextOverflow.Ellipsis)
          }
      } else Column(Modifier.fillMaxSize(),horizontalAlignment=Alignment.CenterHorizontally) {
        if(tile.kind==TileKind.PREVIEW) {
            PreviewFrame(title,tileActionLabel(tile,title,editing),onOpen,tileInteraction) {
                if(live)RestrictedWeb(origin,webDestination(origin,tile,true),Modifier.fillMaxSize(),active=webActive(),backdrop=Ui.card,accessLost=onAccessLost)
                // Online with an address but no household yet: still connecting, unavailable or denied. The status line says which.
                else PreviewUnavailable(title,if(origin.isBlank())"Connect your household" else if(!online)"Reconnect to load live data" else "Household not loaded")
            }
        } else {
            Spacer(Modifier.height(2.dp))
            if(app!=null)Image(app.icon.asImageBitmap(),null,Modifier.size(44.dp)) else DashboardGlyph(title,tile.target)
            Spacer(Modifier.height(Ui.space1))
            // At least 12 sp and legible over any part of the sky. Two lines when they fit; when a large font
            // leaves room for one, the label ends in an ellipsis instead of being cut through.
            Text(title,style=AppLabel,maxLines=2,overflow=TextOverflow.Ellipsis,modifier=Modifier.weight(1f,fill=false))
        }
      }
      if(editing && onRemove!=null) IconButton(onClick=onRemove,modifier=Modifier.align(Alignment.TopEnd).offset(x=Ui.space2,y=(-6).dp).size(Ui.target)
          .testTag("remove-home:${tile.key}").semantics{contentDescription="Remove $title from Home"}) {
          Box(Modifier.size(28.dp).background(Ui.raised,CircleShape).border(1.5.dp,Ui.alert,CircleShape),contentAlignment=Alignment.Center) {
              Canvas(Modifier.size(12.dp)) { drawLine(Ui.alert,Offset(0f,size.height/2),Offset(size.width,size.height/2),2.dp.toPx(),StrokeCap.Round) }
          }
      }
    }
}

/** What TalkBack says activating a tile does. While Home is edited a tap opens the tile's Home options; otherwise it opens the tile. */
internal fun tileActionLabel(tile:Tile,title:String,editing:Boolean):String =
    if(editing)"Edit $title" else if(tile.kind==TileKind.PREVIEW)"Open dashboard" else "Open $title"

/** An empty place in the dock: activating it opens the apps drawer. */
internal fun emptyDockSlotName(index:Int)="Empty dock slot ${index+1}"

/** An app's name on Home and in the drawer grid: 12 sp ink with a soft shadow of the night, so it reads over stars and light. */
internal val AppLabel = TextStyle(fontFamily=ObservatoryBody,fontSize=Ui.textMeta,lineHeight=15.sp,color=Ui.ink,textAlign=TextAlign.Center,
    shadow=Shadow(Ui.sky.copy(alpha=.9f),Offset(0f,1f),6f))

/**
 * The frame Rhythm draws around a dashboard preview on Home (design system, the dashboard frame): the card fill,
 * a 1 dp edge and the card corner, the edge strong while pressed or focused, and the 3 dp focus ring inset 4 dp.
 * The whole card is the opening target, and [actionLabel] is what TalkBack says activating it does. What is inside,
 * the live page or the unavailable state, is drawn by [content];
 * the live page's own title and freshness line come from the household's launcher card page.
 */
@Composable internal fun PreviewFrame(title:String,actionLabel:String,onOpen:()->Unit,tile:MutableInteractionSource?=null,
    content:@Composable BoxScope.()->Unit) {
    val shape=RoundedCornerShape(Ui.radiusCard)
    val interaction=remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val ownFocus by interaction.collectIsFocusedAsState()
    // The tile that holds this frame takes keyboard and D-pad focus first; its focus lights the same ring.
    val tileFocus by (tile ?: interaction).collectIsFocusedAsState()
    val focused=ownFocus || tileFocus
    val edge by animateColorAsState(if(pressed || focused) Ui.edgeStrong else Ui.edge,tween(Ui.fast,easing=ObservatoryEase),label="Preview edge")
    Box(Modifier.fillMaxSize().clip(shape).background(Ui.card)) {
        content()
        Box(Modifier.matchParentSize().border(1.dp,edge,shape))
        if(focused) Box(Modifier.matchParentSize().padding(Ui.cardRingInset)
            .border(Ui.cardRing,Ui.focus,RoundedCornerShape(Ui.radiusCard-Ui.cardRingInset)))
        // Native full-card hit target. Renderer HTML receives no native bridge.
        Box(Modifier.fillMaxSize().clearAndSetSemantics { contentDescription=title }
            .clickable(interactionSource=interaction,indication=LocalIndication.current,role=Role.Button,onClickLabel=actionLabel,onClick=onOpen))
    }
}

/** A launcher preview's insets and its compact header: what the household's generated preview draws (app/src/launcherCard.css,
 * .preview-head), so a preview the launcher draws itself lines up with one beside it. */
private val PREVIEW_INSET = Ui.space2
private val PREVIEW_HEAD_MIN = 24.dp
private val PREVIEW_HEAD_GAP = 6.dp
/** The web drops the header's mark below 10.5 of the title's em, which grows with the font scale. */
private const val PREVIEW_MARK_EMS = 10.5f

/** A preview the launcher cannot load, in the same shape as a generated preview beside it, so a card keeps its shape
 * when the household drops: the compact header (one line 8 dp in from the tile's sides, the name in Manrope 650 at
 * 15 sp, the 14 dp mint opening arrow at the end), growing to hold a whole line at the active font scale, its mark
 * going first on a narrow tile or with enlarged text; then, just under it, the existing message, its first line in ink. */
@Composable private fun PreviewUnavailable(title:String,message:String) {
    Column(Modifier.fillMaxSize()) {
        BoxWithConstraints(Modifier.fillMaxWidth().padding(start=PREVIEW_INSET,top=PREVIEW_INSET,end=PREVIEW_INSET)) {
            val mark=maxWidth>=with(LocalDensity.current){(Ui.textTitle*PREVIEW_MARK_EMS).toDp()}
            Row(Modifier.fillMaxWidth().heightIn(min=PREVIEW_HEAD_MIN),verticalAlignment=Alignment.CenterVertically,
                horizontalArrangement=Arrangement.spacedBy(PREVIEW_HEAD_GAP)) {
                if(mark) FrameMark(Modifier.size(14.dp))
                Text(title,fontFamily=ObservatoryTitle,fontWeight=TitleWeight,fontSize=Ui.textTitle,lineHeight=19.2.sp,color=Ui.ink,
                    maxLines=1,overflow=TextOverflow.Ellipsis,modifier=Modifier.weight(1f))
                // Sized to the web's 14 px arrow icon as the WebView draws it beside this card.
                Box(Modifier.size(14.dp),contentAlignment=Alignment.Center) { OpenArrow(Modifier.size(13.dp)) }
            }
        }
        // As the web's state line: 2 dp under the header and 4 dp more before the words, on the same insets.
        Column(Modifier.fillMaxSize().padding(start=PREVIEW_INSET,top=6.dp,end=PREVIEW_INSET,bottom=PREVIEW_INSET)) {
            Text("Preview unavailable",fontSize=Ui.textBody,lineHeight=18.sp,color=Ui.ink)
            Text(message,fontSize=Ui.textMeta,lineHeight=16.sp,color=Ui.soft,modifier=Modifier.padding(top=Ui.space1))
        }
    }
}

/** The mark a generated preview's header starts with, the web's mint "◈": a diamond around a small solid one, drawn so
 * it never depends on a font's symbols. */
@Composable internal fun FrameMark(modifier:Modifier=Modifier) {
    Canvas(modifier.clearAndSetSemantics{}) {
        val s=size.width/14f;val c=center
        fun diamond(r:Float)=Path().apply { moveTo(c.x,c.y-r);lineTo(c.x+r,c.y);lineTo(c.x,c.y+r);lineTo(c.x-r,c.y);close() }
        drawPath(diamond(3.7f*s),Ui.mint,style=Stroke(1.1f*s,join=StrokeJoin.Round))
        drawPath(diamond(1.6f*s),Ui.mint)
    }
}

private const val RECENT_PREVIEW_PAGES = 3

/** Keep recently visited preview pages mounted so returning resumes the same documents.
 * App-only pages need no WebViews. Offscreen pages stay paused and out of accessibility navigation. */
@Composable internal fun HomePages(
    layout:HomeLayout, pager:HomePager, drag:LauncherDrag, still:Boolean, previewOrigin:String?,
    modifier:Modifier=Modifier, content:@Composable (Tile,Modifier,Boolean)->Unit
) {
    val density=LocalDensity.current
    val pages=remember(layout){layout.pageLists()}
    var recentPreviewPages by remember(pages, previewOrigin) { mutableStateOf(emptyList<Int>()) }
    val currentPage = pager.page
    if (previewOrigin != null && recentPreviewPages.firstOrNull() != currentPage &&
        pages.getOrNull(currentPage)?.any { it.kind == TileKind.PREVIEW } == true) {
        SideEffect {
            recentPreviewPages = (listOf(currentPage) + recentPreviewPages.filter { it != currentPage }).take(RECENT_PREVIEW_PAGES)
        }
    }
    BoxWithConstraints(modifier.testTag("home-grid").clipToBounds()
        .onGloballyPositioned { drag.grid=it.boundsInRoot() }
        .semantics {
            isTraversalGroup=true
            if(pager.count>1)stateDescription="Home page ${pager.page+1} of ${pager.count}"
            customActions=buildList {
                if(pager.page<pager.count-1)add(CustomAccessibilityAction("Next Home page"){pager.go(pager.page+1);true})
                if(pager.page>0)add(CustomAccessibilityAction("Previous Home page"){pager.go(pager.page-1);true})
            }
        }) {
        val width=(maxWidth-30.dp)/4
        val widthPx=constraints.maxWidth.toFloat()
        SideEffect { pager.width=widthPx;drag.cellWidth=with(density){width.toPx()};drag.cellGap=with(density){10.dp.toPx()};drag.rowStep=with(density){HOME_ROW_DP.dp.toPx()} }
        // Drawing only: a cell below the line still lays out, pages and touch as before.
        val cellClip=with(density){homeCellClipDp(maxHeight.value).dp.toPx()}
        val position=pager.position
        val shown=(setOf(pager.page,kotlin.math.floor(position).toInt(),kotlin.math.ceil(position).toInt()) + recentPreviewPages)
            .filter { it in 0 until pager.count }.sorted()
        shown.forEach { index -> key(index) {
            val inView=index==pager.page
            HomePage(pages.getOrElse(index){emptyList()},width,drag,still,
                Modifier.fillMaxSize().testTag("home-page:$index")
                    .graphicsLayer { translationX=(index-pager.position)*widthPx }
                    .drawWithContent { clipRect(bottom=cellClip) { this@drawWithContent.drawContent() } }
                    .then(if(inView)Modifier else Modifier.clearAndSetSemantics{})) { tile,cell -> content(tile,cell,inView) }
        } }
        if(pager.count>1) Row(Modifier.align(Alignment.BottomCenter).height(HOME_INDICATOR_DP.dp).testTag("home-page-indicator")
            .clearAndSetSemantics{},horizontalArrangement=Arrangement.spacedBy(7.dp),verticalAlignment=Alignment.CenterVertically) {
            repeat(pager.count) { index ->
                val current=index==pager.page
                // The page in view is larger and brighter: size, not colour alone, says which it is.
                Box(Modifier.size(if(current)7.dp else 5.dp).background(if(current)Ui.ink else Ui.lineStrong,CircleShape))
            }
        }
    }
}

@Composable private fun HomePage(tiles:List<Tile>,width:Dp,drag:LauncherDrag,still:Boolean,modifier:Modifier,
    content:@Composable (Tile,Modifier)->Unit) {
    val cells=remember(tiles){packHomeCells(tiles)}
    Box(modifier.semantics { collectionInfo=CollectionInfo(cells.maxOfOrNull{it.row+it.rows} ?: 0,4) }) {
        cells.forEach { cell -> key(cell.tile.key) {
            val y=(cell.row*HOME_ROW_DP+8).dp
            val height=if(cell.rows==2)(2*HOME_ROW_DP-8).dp else (HOME_ROW_DP-8).dp
            val x by animateDpAsState((width+10.dp)*cell.column,tween(if(still)0 else 180),label="Cell x")
            val animatedY by animateDpAsState(y,tween(if(still)0 else 180),label="Cell y")
            DisposableEffect(cell.tile.key){onDispose{drag.homeBounds.remove(cell.tile.key)}}
            content(cell.tile,Modifier.offset(x,animatedY).width(width*cell.columns+10.dp*(cell.columns-1)).height(height)
                .onGloballyPositioned { drag.homeBounds[cell.tile.key]=it.boundsInRoot() }
                .graphicsLayer { alpha=if(drag.tile?.key==cell.tile.key && drag.source==DragSource.HOME).25f else 1f }
                .semantics { collectionItemInfo=CollectionItemInfo(cell.row,cell.rows,cell.column,cell.columns) })
        } }
    }
}

@Composable internal fun DragOverlay(drag:LauncherDrag,title:(Tile)->String,apps:List<LaunchApp>) {
    val item=drag.tile ?: return
    val density=LocalDensity.current
    drag.target?.let { rect->
        Box(Modifier.offset { IntOffset((rect.left-drag.root.left).roundToInt(),(rect.top-drag.root.top).roundToInt()) }
            .size(with(density){rect.width.toDp()},with(density){rect.height.toDp()})
            .border(2.dp,Ui.mint,RoundedCornerShape(Ui.radiusCard)).background(Ui.mintFill.copy(alpha=.45f),RoundedCornerShape(Ui.radiusCard)))
    }
    Column(Modifier.offset { IntOffset((drag.point.x-drag.root.left-44.dp.toPx()).roundToInt(),(drag.point.y-drag.root.top-64.dp.toPx()).roundToInt()) }
        .width(88.dp).background(Ui.raised.copy(alpha=.96f),RoundedCornerShape(Ui.radiusCard)).border(1.dp,Ui.edgeStrong,RoundedCornerShape(Ui.radiusCard))
        .padding(Ui.space2).testTag("drag-lifted"),horizontalAlignment=Alignment.CenterHorizontally) {
        val app=apps.find{it.tile.key==item.key}
        if(app!=null)Image(app.icon.asImageBitmap(),null,Modifier.size(44.dp)) else DashboardGlyph(title(item),item.target)
        Text(title(item),style=AppLabel,maxLines=2,overflow=TextOverflow.Ellipsis)
    }
    Box(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.systemBars.union(WindowInsets.displayCutout)).padding(bottom=88.dp),contentAlignment=Alignment.BottomCenter) {
        Text(if(drag.moving)drag.message else "Release for Home options",fontSize=Ui.textSmall,color=Ui.ink,
            modifier=Modifier.background(Ui.raised.copy(alpha=.96f),RoundedCornerShape(Ui.radiusPanel)).border(1.dp,Ui.line,RoundedCornerShape(Ui.radiusPanel))
                .padding(horizontal=Ui.space4,vertical=Ui.space3)
                .semantics { liveRegion=LiveRegionMode.Polite })
    }
}
