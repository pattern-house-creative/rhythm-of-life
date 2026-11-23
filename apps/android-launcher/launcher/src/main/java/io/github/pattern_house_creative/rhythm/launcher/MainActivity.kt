package io.github.pattern_house_creative.rhythm.launcher

import android.content.*
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.*
import androidx.activity.viewModels
import androidx.activity.enableEdgeToEdge
import androidx.activity.SystemBarStyle
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.foundation.*
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.animation.*
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.core.Animatable
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.zIndex
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.*
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import kotlinx.coroutines.*
import kotlin.math.sin

class LauncherNavigation : androidx.lifecycle.ViewModel() {
    val reader = mutableStateOf<String?>(null)
}
/** Where a removed Home pin was, for Undo: its page, its place on that page, and whether it was alone there. */
internal data class HomeRemoval(val tile:Tile,val page:Int,val index:Int,val alone:Boolean)
class MainActivity : ComponentActivity() {
    private val navigation by viewModels<LauncherNavigation>()
    private var homeSignal by mutableIntStateOf(0)
    private var homeAgain by mutableIntStateOf(0)
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState); enableEdgeToEdge(statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT), navigationBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT))
        // Owner-authorized: screenshots and system captures of the launcher are allowed.
        setContent { MaterialTheme(typography = ObservatoryTypography, colorScheme = ObservatoryColors) {
                CompositionLocalProvider(LocalContentColor provides Ui.ink) { Launcher(homeSignal, navigation, homeAgain) }
            } }
    }
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        // Home pressed while Home is already showing returns to the first page; coming back from an app keeps the page.
        if (lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) homeAgain++
        homeSignal++
    }
}
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun Launcher(homeSignal: Int, navigation: LauncherNavigation, homeAgain: Int = 0) {
    val context = LocalContext.current
    val store = remember { LauncherStore(context) }
    var layout by remember { mutableStateOf(store.load()) }
    var origin by remember { mutableStateOf(store.origin) }
    val client = remember(origin) { origin.takeIf { it.isNotEmpty() }?.let { HouseholdClient(it) } }
    var appRevision by remember { mutableIntStateOf(0) }
    var switching by remember { mutableStateOf(false) }
    var sessionError by remember { mutableStateOf("") }
    var appsReady by remember { mutableStateOf(false) }
    var previews by remember { mutableStateOf(false) }
    var apps by remember { mutableStateOf(emptyList<LaunchApp>()) }
    var household by remember(origin) { mutableStateOf<Household?>(null) }
    var found by remember { mutableStateOf(emptyList<RhythmItem>()) }
    var query by remember { mutableStateOf("") }
    var insight by remember { mutableStateOf<DailyInsight?>(null) }
    var insightState by remember { mutableStateOf("Connect your household") }
    var insightOpen by remember { mutableStateOf(false) }
    var insightRefresh by remember { mutableIntStateOf(0) }
    val searchFocus = remember { FocusRequester() }
    val keyboard = LocalSoftwareKeyboardController.current
    val focus = LocalFocusManager.current
    val drawerScope=rememberCoroutineScope()
    val drawerMotion=remember { DrawerController(drawerScope) }
    val drawer by remember { derivedStateOf { drawerMotion.exposed } }
    var editHome by remember { mutableStateOf(false) }
    var undoHome by remember { mutableStateOf<HomeRemoval?>(null) }
    val dragState=remember { LauncherDrag() }
    val pager=rememberHomePager()
    // Rows a Home page holds, from the real height of Home with the sky folded away (0 until measured).
    var pageRows by remember { mutableIntStateOf(0) }
    SideEffect { dragState.currentPage={pager.page};dragState.pageRows={pageRows} }
    val drawerScroll=rememberLazyGridState()
    var alphabetical by remember { mutableStateOf(store.alphabeticalDrawer) }
    var sky by remember { mutableStateOf(false) }
    var settings by remember { mutableStateOf(false) }
    var selected by remember { mutableStateOf<Tile?>(null) }
    var web by navigation.reader
    var status by remember { mutableStateOf("") }
    var visible by remember { mutableStateOf(false) }
    var online by remember { mutableStateOf(true) }
    var refresh by remember { mutableIntStateOf(0) }
    var still by remember { mutableStateOf(store.still) }
    var iconStyle by remember { mutableStateOf(store.iconStyle) }
    val appsByKey = remember(apps) { apps.associateBy { it.tile.key } }
    val owner = LocalLifecycleOwner.current
    fun accessLost() {
        client?.clearDiscovery()
        insight = null; insightOpen = false; household = null; found = emptyList()
        if (web != origin) web = null
        status = "Household access denied"
    }
    DisposableEffect(context) {
        val unregister = observeApps(context) { appRevision++ }
        onDispose { unregister() }
    }
    DisposableEffect(context, client) {
        val manager = context.getSystemService(android.net.ConnectivityManager::class.java)
        val handler = android.os.Handler(android.os.Looper.getMainLooper())
        val callback = object : android.net.ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: android.net.Network) { handler.post { online = true } }
            override fun onLost(network: android.net.Network) { handler.post {
                online = manager.activeNetwork != null
                if (!online) { client?.clearDiscovery(); insight = null; insightOpen = false; household = null; found = emptyList(); status = "Offline · you can still open apps" }
            } }
        }
        online = manager.activeNetwork != null
        manager.registerDefaultNetworkCallback(callback)
        onDispose { manager.unregisterNetworkCallback(callback) }
    }
    // Every change is fitted to the page height, so a page never needs to scroll.
    fun save(next: HomeLayout) { val fitted=next.fit(pageRows); layout = fitted; store.save(fitted) }
    LaunchedEffect(pageRows) { if (pageRows > 0 && layout.fit(pageRows) != layout) save(layout) }
    fun showPreview(id:String) {
        store.allowPreview(id);save(layout.showPreview(id,pageRows))
        layout.pageOf("DASHBOARD:$id").takeIf { it>=0 }?.let(pager::go)
    }
    fun addHome(tile:Tile) { save(layout.add(tile,pageRows));layout.pageOf(tile.key).takeIf { it>=0 }?.let(pager::go) }
    fun removeHome(tile:Tile) {
        val page=layout.pageOf(tile.key)
        if(page<0)return
        val tiles=layout.page(page)
        if(tile.kind==TileKind.PREVIEW || tile.kind==TileKind.DASHBOARD)store.recordPreviewRemoval(tile.target)
        save(layout.removeHome(tile.key));undoHome=HomeRemoval(tile,page,tiles.indexOfFirst { it.key==tile.key },tiles.size==1);selected=null
    }
    fun undoRemoval() {
        val removed=undoHome ?: return
        if(layout.tiles.none { it.key==removed.tile.key }) {
            if(layout.tiles.size>=HOME_LIMIT) {status="Home is full; make room before restoring this item";return}
            if(removed.tile.kind==TileKind.PREVIEW)store.allowPreview(removed.tile.target)
            save(layout.placeHome(removed.tile,removed.page,removed.index,newPage=removed.alone))
            layout.pageOf(removed.tile.key).takeIf { it>=0 }?.let(pager::go)
        }
        undoHome=null
    }
    fun moveTile(next:HomeLayout,key:String) { save(next);layout.pageOf(key).takeIf { it>=0 }?.let(pager::go) }
    LaunchedEffect(undoHome) { if(undoHome!=null){delay(8000);undoHome=null} }
    fun edit(tile:Tile) { keyboard?.hide();focus.clearFocus();selected=tile }
    fun close() { drawerMotion.snapClosed();dragState.cancel(); editHome=false;insightOpen = false; keyboard?.hide(); focus.clearFocus(); web = null; query = ""; selected = null; settings = false; previews = false }
    LaunchedEffect(homeSignal) { if (homeSignal > 0) close() }
    LaunchedEffect(homeAgain) { if (homeAgain > 0) pager.go(0) }
    // Pages left behind by a removal or an ended drag fold back to the last real page.
    SideEffect { pager.count=(layout.pageCount+if(dragState.tile!=null)1 else 0).coerceAtLeast(1) }
    LaunchedEffect(pager.count) { if (pager.page >= pager.count) pager.go(pager.count - 1) }
    DisposableEffect(owner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) { visible = true; refresh++ }
            if (event == Lifecycle.Event.ON_PAUSE) { drawerMotion.snapClosed();dragState.cancel();editHome=false;visible = false; insight = null; insightOpen = false; found = emptyList() }
        }
        owner.lifecycle.addObserver(observer)
        visible = owner.lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)
        onDispose { owner.lifecycle.removeObserver(observer) }
    }
    LaunchedEffect(refresh, appRevision, visible, iconStyle) {
        if (!visible) return@LaunchedEffect
        val listing = withContext(Dispatchers.IO) { listApps(context, iconStyle) }
        apps = listing.apps
        if (!store.initialized && apps.isNotEmpty()) save(HomeLayout(apps.take(8).map { it.tile }, apps.take(6).map { it.tile }))
        appsReady = true
        // Apps and their labels show at once; icons not yet drawn in this process fill in, Home and dock first.
        drawIcons(context, listing, (layout.dock + layout.tiles).mapTo(HashSet()) { it.key }) { apps = it }
    }
    LaunchedEffect(household, appsReady, origin) {
        val loaded = household
        if (appsReady && loaded?.previewsReady == true) layout = store.initialPreviews(layout, loaded.dashboards.map { it.id })
    }
    LaunchedEffect(origin, visible, refresh, web, online) {
        if (client == null || !online) { household = null; return@LaunchedEffect }
        if (!visible || web != null) return@LaunchedEffect
        while (isActive) {
            if (household == null) status = "Connecting to household…"
            try {
                val loaded = client.load { dashboards ->
                    if (household == null) household = dashboards
                    status = dashboards.warning
                }
                household = loaded; status = loaded.warning
            }
            catch (e: CancellationException) { throw e }
            catch (_: HouseholdAccessDenied) { accessLost() }
            catch (_: Exception) { client.clearDiscovery(); household = null; status = "Household unavailable · you can still open apps" }
            delay(60_000)
        }
    }
    DrawerKeyboardEffects(drawerMotion,searchFocus,drawerScroll,{keyboard?.show()},{keyboard?.hide()},
        {query="";focus.clearFocus()})
    LaunchedEffect(drawer) {if(drawer)editHome=false}
    LaunchedEffect(origin,visible,online,refresh,web,insightRefresh) {
        insight=null; insightOpen=false
        insightState=when {
            origin.isBlank() -> "Connect your household"
            !online -> "Offline · reconnect for insights"
            else -> "Loading your insight…"
        }
        if(origin.isBlank() || !visible || !online || web!=null)return@LaunchedEffect
        while(isActive) {
            try {
                val current = client ?: return@LaunchedEffect
                val generation = current.accessGeneration
                val result = currentDailyInsight(current.insights(), System.currentTimeMillis())
                if (generation == current.accessGeneration) insight = result
                insightState="No current insight available"
            } catch(e:CancellationException) { throw e }
            catch(e:HouseholdAccessDenied) {
                accessLost()
                insightState="Household access denied";break
            } catch(_:Exception) {
                insight=null;insightOpen=false;insightState="Daily insight unavailable"
            }
            delay(60_000)
        }
    }
    LaunchedEffect(query, origin, visible, online) {
        found = emptyList()
        if (query.isBlank() || origin.isBlank() || !visible || !online) return@LaunchedEffect
        delay(350)
        val current = client ?: return@LaunchedEffect
        val generation = current.accessGeneration
        try {
            val result = current.search(query)
            if (generation == current.accessGeneration) found = result
        }
        catch (e: CancellationException) { throw e }
        catch (_: HouseholdAccessDenied) { accessLost() }
        catch (_: Exception) { status = "Rhythm search unavailable" }
    }
    fun title(tile: Tile): String = if (tile.kind == TileKind.APP) appsByKey[tile.key]?.title ?: "App unavailable"
        else if (tile.target == BUILTIN_GLUCOSE) "Glucose"
        else (household?.dashboards.orEmpty() + household?.stars.orEmpty() + found).find { it.id == tile.target }?.title
            ?: if (tile.kind == TileKind.STAR) "Rhythm star" else "Dashboard"
    fun open(tile: Tile) {
        drawerMotion.snapClosed();query="";keyboard?.hide();focus.clearFocus()
        if (tile.kind == TileKind.APP) {
            try {
                launchInstalled(context, tile.target, appsByKey[tile.key])
            }
            catch (_: Exception) { status = "This app couldn’t open"; refresh++ }
        } else if (origin.isEmpty()) settings = true else web = webDestination(origin, tile)
    }
    BackHandler {
        when {
            dragState.tile != null -> dragState.cancel()
            web != null -> web = null
            insightOpen -> insightOpen = false
            previews -> previews = false
            settings -> settings = false
            selected != null -> selected = null
            drawer -> drawerMotion.request(false)
            sky -> sky = false
        }
    }
    val systemStill = android.provider.Settings.Global.getFloat(context.contentResolver,
        android.provider.Settings.Global.ANIMATOR_DURATION_SCALE, 1f) == 0f
    SideEffect {drawerMotion.reduced=still || systemStill;pager.reduced=still || systemStill}
    val skyGraph = rememberHouseholdSky(household)
    val homeInsets=WindowInsets.systemBars.union(WindowInsets.displayCutout)
    val results=remember(apps,query,household,found) {
        (apps.filter { it.title.contains(query,true) }.map { it.tile } +
            (household?.builtins.orEmpty()+household?.dashboards.orEmpty()).filter { it.title.contains(query,true) }.map { Tile(TileKind.DASHBOARD,it.id) } +
            (if(query.isBlank())household?.stars.orEmpty() else found).map { Tile(TileKind.STAR,it.id) }).distinctBy { it.key }
    }
    val drawerTitles = remember(alphabetical, results, apps, household, found) {
        if (alphabetical) results.associate { it.key to title(it) } else emptyMap()
    }
    // A page's height is Home's column without the insight bar and the search handle, with the sky folded
    // away and with room for one status line whether or not one shows.
    var homeHeight by remember { mutableIntStateOf(0) }
    var insightHeight by remember { mutableIntStateOf(0) }
    var statusHeight by remember { mutableIntStateOf(0) }
    val density = LocalDensity.current
    val statusStyle = LocalTextStyle.current.merge(androidx.compose.ui.text.TextStyle(fontSize = 12.sp))
    val statusLine = rememberTextMeasurer().measure("Ag", statusStyle).size.height + with(density) { 16.dp.roundToPx() }
    LaunchedEffect(homeHeight, insightHeight, statusHeight, statusLine) {
        if (homeHeight <= 0 || insightHeight <= 0) return@LaunchedEffect
        val pagePx = homeHeight + statusHeight - statusLine - insightHeight - with(density) { 24.dp.roundToPx() }
        pageRows = homePageRows(with(density) { pagePx.toDp().value })
    }
    val rootGestures=launcherGestures(dragState,visible && web==null && !previews && !settings && selected==null && !insightOpen,
        drawerMotion,{!drawerScroll.canScrollBackward},results,pager,!sky && !drawer && web==null,layout,{save(it)},::edit,{previews=true},
        {drawerMotion.snapClosed(false);keyboard?.hide();focus.clearFocus()},{drawerMotion.request(true)})
    BoxWithConstraints(Modifier.fillMaxSize().testTag("launcher-root").background(SkyArt.top).onGloballyPositioned { dragState.root=it.boundsInRoot() }.then(rootGestures)) {
        val revealHeight=skyRevealDp(maxHeight.value).dp
        AmbientSky(skyGraph, household?.links.orEmpty(), still || systemStill, visible && web == null && (!drawer || drawerMotion.settledOpen), sky)
        Column(Modifier.fillMaxSize().windowInsetsPadding(homeInsets)
            .then(if(web!=null)Modifier.clearAndSetSemantics{} else Modifier)) {
            Row(Modifier.fillMaxWidth().padding(start=Ui.space4,end=Ui.space1).height(52.dp), verticalAlignment = Alignment.CenterVertically) {
                RhythmGlyph(Modifier.size(16.dp))
                Spacer(Modifier.width(Ui.space2))
                Text("Rhythm OS", fontSize = Ui.textSection, fontFamily = ObservatoryTitle, fontWeight = FontWeight.SemiBold,
                    letterSpacing = (-.3).sp, color = Ui.ink, maxLines = 1, modifier = Modifier.weight(1f))
                // While Home is being edited the same button reads Done and looks chosen, so the mode is never colour alone.
                TextButton(onClick = {if(editHome)editHome=false else sky=!sky},contentPadding=PaddingValues(horizontal=Ui.space3),
                    shape=RoundedCornerShape(Ui.radiusControl),
                    border=if(editHome)BorderStroke(1.dp,Ui.mintEdge) else null,
                    colors=ButtonDefaults.textButtonColors(containerColor=if(editHome)Ui.mintFill else Color.Transparent,contentColor=Ui.mint)) {
                    Text(if(editHome)"Done" else if(sky)"Less sky" else "More sky",fontSize=Ui.textSmall,fontWeight=FontWeight.Medium)
                }
                IconButton(onClick = { settings = true }, modifier = Modifier.size(Ui.target).semantics { contentDescription = "Settings" }) {
                    Canvas(Modifier.size(20.dp)) {
                        drawCircle(Ui.soft, size.width * .32f, style = Stroke(1.4.dp.toPx()))
                        drawCircle(Ui.soft, size.width * .09f)
                        repeat(8) { i ->
                            val angle = i * Math.PI / 4
                            val direction = Offset(kotlin.math.cos(angle).toFloat(), kotlin.math.sin(angle).toFloat())
                            drawLine(Ui.soft, center + direction * size.width * .34f, center + direction * size.width * .47f, 1.4.dp.toPx(), cap = androidx.compose.ui.graphics.StrokeCap.Round)
                        }
                    }
                }
            }
            // One line at most, so a status message coming and going never changes how many rows a page holds.
            if (status.isNotEmpty()) Text(status, color = Ui.soft, fontSize = Ui.textMeta, maxLines = 1, overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(horizontal=Ui.space6,vertical=Ui.space2).onSizeChanged { statusHeight = it.height })
            else SideEffect { statusHeight = 0 }
            Box(Modifier.weight(1f).fillMaxWidth().clipToBounds().onGloballyPositioned{drawerMotion.viewport=it.boundsInRoot()}) {
                // Home geometry remains stable throughout drawer entry, dragging and exit.
                Column(Modifier.fillMaxSize().onSizeChanged { homeHeight = it.height }.padding(horizontal=16.dp).graphicsLayer{alpha=1f-drawerMotion.fraction}
                    .pointerInput(drawer){if(drawer)awaitPointerEventScope{while(true){val event=awaitPointerEvent(PointerEventPass.Initial);event.changes.forEach{it.consume()}}}}
                    .then(if(drawer)Modifier.clearAndSetSemantics{} else Modifier)) {
                    AnimatedSkyRegion(sky && !drawer && !(dragState.tile!=null && dragState.source==DragSource.DRAWER),
                        revealHeight,still || systemStill) {
                        SkyExplorer(household,skyGraph,still || systemStill,visible && web==null && sky && !drawer){open(it)}
                    }
                    Box(Modifier.weight(1f).fillMaxWidth()) {
                        val previewOrigin = origin.takeIf { it.isNotBlank() && online && household != null }
                        HomePages(layout,pager,dragState,still || systemStill,previewOrigin,Modifier.fillMaxSize()) { tile,cellModifier,inView ->
                            val page=layout.pageOf(tile.key)
                            val index=layout.tiles.indexOfFirst{it.key==tile.key}
                            LauncherTile(tile,title(tile),appsByKey[tile.key],origin,
                                previewOrigin != null,online,cellModifier,
                                {if(editHome)edit(tile)else open(tile)},{edit(tile)},
                                if(index>0)({moveTile(layout.shift(tile.key,true,pageRows),tile.key)}) else null,
                                if(index>=0 && index<layout.tiles.lastIndex)({moveTile(layout.shift(tile.key,false,pageRows),tile.key)}) else null,
                                ::accessLost,
                                onRemove={removeHome(tile)},editing=editHome,webActive={inView && visible && web==null && !drawerMotion.covered},
                                pageMoves=buildList {
                                    if(page>0)add("Move to previous page" to {moveTile(layout.moveToPage(tile.key,page-1,pageRows),tile.key)})
                                    if(page<layout.pageCount-1 || layout.page(page).size>1)
                                        add("Move to next page" to {moveTile(layout.moveToPage(tile.key,page+1,pageRows),tile.key)})
                                })
                        }
                        if(layout.tiles.isEmpty()) Text("Swipe up for apps · hold empty space for previews",fontSize=Ui.textMeta,
                            color=Ui.soft,textAlign=TextAlign.Center,modifier=Modifier.align(Alignment.Center).padding(Ui.space6))
                    }
                    Box(Modifier.fillMaxWidth().onSizeChanged { insightHeight = it.height }) {
                        Box(Modifier.graphicsLayer{alpha=if(undoHome==null)1f else 0f}
                            .then(if(undoHome!=null)Modifier.clearAndSetSemantics{} else Modifier)) {
                            DailyInsightBar(insight,insightState){if(undoHome==null)insightOpen=true}
                        }
                        undoHome?.let {removed->
                            val shape=RoundedCornerShape(Ui.radiusCard)
                            Row(Modifier.matchParentSize().background(Ui.raised,shape).border(1.dp,Ui.line,shape)
                                .padding(start=Ui.space4,end=Ui.space1).semantics{liveRegion=LiveRegionMode.Polite},verticalAlignment=Alignment.CenterVertically) {
                                Text("Removed ${title(removed.tile)} from Home",fontSize=Ui.textMeta,lineHeight=16.sp,maxLines=2,
                                    overflow=TextOverflow.Ellipsis,color=Ui.ink,modifier=Modifier.weight(1f))
                                TextButton(onClick=::undoRemoval){Text("Undo",fontSize=Ui.textBody,fontWeight=FontWeight.SemiBold)}
                                IconButton(onClick={undoHome=null},modifier=Modifier.size(Ui.target).semantics{contentDescription="Dismiss removal notice"}){
                                    Canvas(Modifier.size(12.dp)) {
                                        val stroke=1.6.dp.toPx()
                                        drawLine(Ui.soft,Offset.Zero,Offset(size.width,size.height),stroke,androidx.compose.ui.graphics.StrokeCap.Round)
                                        drawLine(Ui.soft,Offset(size.width,0f),Offset(0f,size.height),stroke,androidx.compose.ui.graphics.StrokeCap.Round)
                                    }
                                }
                            }
                        }
                    }
                    Box(Modifier.fillMaxWidth().height(24.dp).testTag("swipe-search")
                        .onGloballyPositioned{drawerMotion.handle=it.boundsInRoot()}
                        .clickable(role=Role.Button,onClickLabel="Open apps and search"){drawerMotion.request(true)},contentAlignment=Alignment.Center) {
                        Text("⌃  Swipe up to search",fontSize=Ui.textMeta,lineHeight=16.sp,color=Ui.faint,maxLines=1)
                    }
                }
                DrawerSurface(drawerMotion) {
                    Column(Modifier.fillMaxSize().padding(horizontal=Ui.space2)
                        .drawerImePadding(homeInsets,drawerMotion).testTag("app-drawer").then(if(!drawer)Modifier.clearAndSetSemantics{} else Modifier)) {
                        Box(Modifier.fillMaxWidth().height(20.dp).testTag("drawer-handle")
                            .onGloballyPositioned{drawerMotion.header=it}
                            .semantics{contentDescription="Drag down to close apps"}
                            .clickable(onClickLabel="Close apps"){drawerMotion.request(false)},contentAlignment=Alignment.Center) {
                            Box(Modifier.width(36.dp).height(4.dp).background(Ui.faint,RoundedCornerShape(2.dp)))
                        }
                        OutlinedTextField(query,{query=it.take(200)},singleLine=true,shape=RoundedCornerShape(Ui.radiusCard),
                            textStyle=androidx.compose.ui.text.TextStyle(fontSize=Ui.textLead,fontFamily=ObservatoryBody),
                            placeholder={Text("Search apps & Rhythm",fontSize=Ui.textLead,color=Ui.soft)},
                            leadingIcon={ SearchGlyph() },
                            colors=OutlinedTextFieldDefaults.colors(focusedContainerColor=Ui.surface.copy(alpha=.92f),
                                unfocusedContainerColor=Ui.surface.copy(alpha=.86f),focusedBorderColor=Ui.focus,unfocusedBorderColor=Ui.lineStrong,
                                cursorColor=Ui.mint,focusedTextColor=Ui.ink,unfocusedTextColor=Ui.ink),
                            modifier=Modifier.fillMaxWidth().testTag("search-input").focusRequester(searchFocus).onGloballyPositioned{drawerMotion.field=it})
                        DrawerToolbar(alphabetical, onLayoutChange = {
                            alphabetical = it
                            store.alphabeticalDrawer = it
                            drawerScroll.requestScrollToItem(0)
                        }, onAddPreview = { previews = true })
                        DrawerResults(results, alphabetical, drawerTitles, drawerScroll, drawerMotion) { tile, modifier ->
                            DisposableEffect(tile.key){onDispose{dragState.drawerCoordinates.remove(tile.key)}}
                            LauncherTile(tile,title(tile),appsByKey[tile.key],origin,false,online,
                                modifier.onGloballyPositioned{dragState.drawerCoordinates[tile.key]=it},
                                {open(tile)},{edit(tile)},null,null,{}, listRow = alphabetical)
                        }
                    }
                }
            }
            Row(Modifier.fillMaxWidth().padding(horizontal=Ui.space4,vertical=Ui.space2).height(56.dp).testTag("home-dock")
                .onGloballyPositioned { dragState.dock=it.boundsInRoot() }
                .background(Ui.surface.copy(alpha=.9f),RoundedCornerShape(DOCK_RADIUS)).border(1.dp,Ui.edge,RoundedCornerShape(DOCK_RADIUS)),
                verticalAlignment=Alignment.CenterVertically) {
                repeat(6) { index->
                    val tile=layout.dock.getOrNull(index)
                    val app=apps.find{it.tile.key==tile?.key}
                    Box(Modifier.weight(1f).fillMaxHeight().testTag("dock-slot:$index")
                        .onGloballyPositioned { dragState.slots[index]=it.boundsInRoot() }
                        .graphicsLayer { alpha=if(tile!=null && dragState.tile?.key==tile.key && dragState.source==DragSource.DOCK).25f else 1f }
                        .semantics {
                            contentDescription=if(tile==null)emptyDockSlotName(index) else title(tile)
                            if(tile!=null)customActions=listOf(CustomAccessibilityAction("Edit "+title(tile)){edit(tile);true},
                                CustomAccessibilityAction("Move left"){save(layout.insertDock(tile,(index-1).coerceAtLeast(0)));true},
                                CustomAccessibilityAction("Move right"){save(layout.insertDock(tile,(index+1).coerceAtMost(5)));true},
                                CustomAccessibilityAction("Remove from dock"){save(layout.removeDock(tile.key));true})
                        }.clickable(role=Role.Button,onClickLabel=if(tile==null)"Open apps" else null){if(tile==null)drawerMotion.request(true) else open(tile)},contentAlignment=Alignment.Center) {
                        if(app!=null)Image(app.icon.asImageBitmap(),null,Modifier.size(40.dp))
                        else Text(if(tile==null)"+" else "?",fontSize=20.sp,color=Ui.faint)
                    }
                }
            }
        }
        DragOverlay(dragState,::title,apps)
        if (web != null) {
            val entering = remember(web) { Animatable(if (still || systemStill) 1f else 0f) }
            LaunchedEffect(web,still,systemStill) { entering.animateTo(1f,tween(if (still || systemStill) 0 else 500,easing = ObservatoryEase)) }
            Column(Modifier.testTag("full-household").semantics {
                isTraversalGroup=true
                customActions=listOf(CustomAccessibilityAction("Return to Home"){web=null;refresh++;true})
            }.fillMaxSize().graphicsLayer { alpha = entering.value; translationY = (1f-entering.value)*35.dp.toPx() }.background(Ui.night).safeDrawingPadding()) {
            if (online && sameOrigin(origin, web!!)) key(web) {
                RestrictedWeb(origin, web!!, Modifier.weight(1f).fillMaxWidth(), active = visible, accessLost = ::accessLost)
            }
            else Column(Modifier.padding(Ui.space6)) {
                Text("Offline · reconnect to open this view",fontSize=Ui.textLead,lineHeight=22.sp,color=Ui.ink)
                TextButton(onClick={web=null;refresh++},modifier=Modifier.padding(top=Ui.space2)){Text("Return to Home",fontSize=Ui.textBody)}
            }
            }
        }
    }
    fun changeOrigin(next: String) {
        if (switching) return
        val old = origin
        web = null; accessLost(); status = ""; origin = ""; store.origin = ""
        switching = true; sessionError = ""
        drawerScope.launch {
            try {
                clearWebSession()
                if (next.isNotEmpty() && old != next) save(layout.tiles.filter { it.kind != TileKind.APP }.fold(layout) { kept, tile -> kept.removeHome(tile.key) }
                    .copy(dock = layout.dock.filter { it.kind == TileKind.APP }))
                store.origin = next; origin = next; refresh++; settings = false
            } catch (e: CancellationException) { throw e }
            catch (_: RuntimeException) {
                sessionError = "Disconnected. Update Android System WebView and retry clearing the web session."
            } finally { switching = false }
        }
    }
    val availableDashboards = household?.builtins.orEmpty() + household?.dashboards.orEmpty()
    if (previews) AlertDialog(onDismissRequest = { previews = false }, title = { Text("Dashboard previews") },
        shape = RoundedCornerShape(Ui.radiusCard), containerColor = Ui.surface,
        modifier = Modifier.border(1.dp, Ui.edge, RoundedCornerShape(Ui.radiusCard)),
        text = { Column {
            Text("Live dashboards on your Home screen", fontSize = Ui.textSmall, color = Ui.soft)
            Spacer(Modifier.height(Ui.space2))
            when {
                origin.isBlank() -> TextButton(onClick = { previews = false; settings = true }) { Text("Connect your household") }
                household == null -> TextButton(onClick = { refresh++ }) { Text("Reconnect to load dashboards") }
                availableDashboards.isEmpty() -> Text("No dashboards are available from this household.", fontSize = Ui.textBody, color = Ui.soft)
                else -> LazyColumn(Modifier.heightIn(max = 360.dp)) {
                    items(availableDashboards, key = { it.id }) { board ->
                        val shown = layout.tiles.any { it.target == board.id && it.kind == TileKind.PREVIEW }
                        TextButton(enabled = !shown, onClick = { showPreview(board.id); previews = false; drawerMotion.request(false) },
                            shape = RoundedCornerShape(Ui.radiusControl), contentPadding = PaddingValues(horizontal = Ui.space2, vertical = Ui.space2),
                            modifier = Modifier.fillMaxWidth().heightIn(min = Ui.target).testTag("preview-choice:${board.id}")) {
                            Column(Modifier.weight(1f)) {
                                Text(if (board.id == BUILTIN_GLUCOSE) "Glucose \u00b7 built-in" else board.title, maxLines = 2, overflow = TextOverflow.Ellipsis,
                                    fontFamily = ObservatoryTitle, fontWeight = TitleWeight, fontSize = Ui.textTitle, color = if (shown) Ui.soft else Ui.ink)
                                Text(if (shown) "Already on Home" else "Add live preview", fontSize = Ui.textMeta, color = if (shown) Ui.faint else Ui.mint)
                            }
                            if (!shown) OpenArrow(Modifier.padding(start = Ui.space2).size(16.dp))
                        }
                    }
                }
            }
        } }, confirmButton = { TextButton(onClick = { previews = false }) { Text("Done") } })
    if (insightOpen && visible && online && client != null) insight?.let { opened ->
        key(client, opened.id) {
            DailyInsightSheet(opened, onSave={ choice, note, until ->
                try {
                    client.saveInsightFeedback(opened.id, choice, note, until)
                    insightOpen=false
                    insightRefresh++
                    android.widget.Toast.makeText(context, "Feedback saved", android.widget.Toast.LENGTH_SHORT).show()
                } catch (_: HouseholdAccessDenied) {
                    accessLost()
                    insightState="Household access denied"
                }
            }, onClose={ insightOpen=false })
        }
    }
    selected?.let { tile ->
        val pinned=layout.tiles.any { it.key==tile.key }
        ModalBottomSheet(onDismissRequest={selected=null},containerColor=Ui.surface,
            shape=RoundedCornerShape(topStart=SHEET_RADIUS,topEnd=SHEET_RADIUS)) {
            Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal=Ui.space6).padding(bottom=Ui.space6).testTag("tile-actions")) {
                Row(verticalAlignment=Alignment.CenterVertically) {
                    val icon=apps.find{it.tile.key==tile.key}?.icon
                    if(icon!=null)Image(icon.asImageBitmap(),null,Modifier.size(44.dp))
                    else DashboardGlyph(title(tile),tile.target)
                    Column(Modifier.padding(start=Ui.space3).weight(1f)) {
                        Text(title(tile),fontSize=Ui.textSection,fontFamily=ObservatoryTitle,fontWeight=TitleWeight,lineHeight=24.sp,
                            color=Ui.ink,maxLines=2,overflow=TextOverflow.Ellipsis)
                        Text(if(pinned) "Hold, then drag to move" else "Add this to your Home",
                            fontSize=Ui.textMeta,color=Ui.soft)
                    }
                }
                Spacer(Modifier.height(Ui.space3))
                HorizontalDivider(color=Ui.divider)
                Spacer(Modifier.height(Ui.space2))
                if(!pinned) SheetAction("Add to Home","＋") {addHome(tile);selected=null}
                if(tile.kind==TileKind.DASHBOARD || tile.kind==TileKind.PREVIEW)
                    SheetAction(if(tile.kind==TileKind.PREVIEW) "Show as icon" else "Show live preview",if(tile.kind==TileKind.PREVIEW) "▦" else "▤") {
                        if(tile.kind==TileKind.DASHBOARD)showPreview(tile.target)else{store.recordPreviewRemoval(tile.target);save(layout.togglePreview(tile.key))};selected=null
                    }
                if(tile.kind==TileKind.APP) SheetAction("Pin to dock","↓") {save(layout.dock(tile));selected=null}
                if(pinned) SheetAction("Remove from Home","−",Ui.alert) {removeHome(tile)}
                if(layout.dock.any{it.key==tile.key}) SheetAction("Remove from dock",null) {save(layout.removeDock(tile.key));selected=null}
                TextButton(onClick={selected=null},modifier=Modifier.align(Alignment.End).padding(top=Ui.space2)) { Text("Done") }
            }
        }
    }
    if (settings) {
        var address by remember { mutableStateOf(origin) }
        var invalid by remember { mutableStateOf(false) }
        // Grouped as Home, household, then how the launcher looks and moves; every control is the one it was.
        AlertDialog(onDismissRequest = { settings = false }, title = { Text("Settings") },
            shape = RoundedCornerShape(Ui.radiusCard), containerColor = Ui.surface,
            modifier = Modifier.border(1.dp, Ui.edge, RoundedCornerShape(Ui.radiusCard)),
            text = { Column(Modifier.verticalScroll(rememberScrollState())) {
            TextButton(onClick={settings=false;editHome=true},modifier=Modifier.testTag("edit-home")){Text("Edit Home",fontSize=Ui.textBody)}
            Text("Remove buttons only unpin Home items. Apps and dashboards are kept.",fontSize=Ui.textSmall,lineHeight=18.sp,color=Ui.soft,
                modifier=Modifier.padding(horizontal=Ui.space3))
            HorizontalDivider(color=Ui.divider,modifier=Modifier.padding(vertical=Ui.space4))
            SettingsHeading("Your household")
            Text(ADDRESS_HELP,
                fontSize=Ui.textSmall,lineHeight=18.sp,color=Ui.soft,modifier=Modifier.padding(horizontal=Ui.space3).padding(bottom=Ui.space2))
            // The error is seen under the field and heard as the field's own error, once: the visible line is kept out of TalkBack.
            OutlinedTextField(address, { address = it; invalid = false }, label = { Text(ADDRESS_LABEL) },
                placeholder = { Text(ADDRESS_EXAMPLE, color = Ui.faint) }, isError = invalid,
                supportingText = if (invalid) {{ Text(ADDRESS_ERROR, modifier = Modifier.clearAndSetSemantics {}) }} else null,
                shape = RoundedCornerShape(Ui.radiusControl), modifier = Modifier.fillMaxWidth().semantics { if (invalid) error(ADDRESS_ERROR) })
            // Said before Save, while the entry is an address that differs from the saved one.
            addressChangeNotice(address, origin, layout)?.let { notice ->
                Text(notice, fontSize=Ui.textSmall, lineHeight=18.sp, color=Ui.ink,
                    modifier=Modifier.padding(horizontal=Ui.space3).padding(top=Ui.space2).testTag("address-change-notice")
                        .semantics { liveRegion = LiveRegionMode.Polite })
            }
            if (origin.isNotEmpty()) TextButton(onClick = { settings = false; web = origin }) { Text("Open household sign-in",fontSize=Ui.textBody) }
            if (sessionError.isNotEmpty()) Text(sessionError,fontSize=Ui.textSmall,color=Ui.alert,modifier=Modifier.padding(horizontal=Ui.space3))
            TextButton(enabled = !switching, onClick = { changeOrigin("") }) { Text("Disconnect and clear web session",fontSize=Ui.textBody) }
            HorizontalDivider(color=Ui.divider,modifier=Modifier.padding(vertical=Ui.space3))
            TextButton(onClick = { still = !still; store.still = still }) { Text(if (still) "Motion: reduced" else "Motion: gentle",fontSize=Ui.textBody) }
            IconStyleChoice(iconStyle) { iconStyle = it; store.iconStyle = it }
        } }, confirmButton = { TextButton(enabled = !switching, onClick = {
            val clean = householdOrigin(address)
            if (clean == null) invalid = true
            else if (clean != origin) changeOrigin(clean)
            else { refresh++; settings = false }
        }) { Text(addressSaveLabel(address, origin)) } })
    }
}

/** The dock's corner: the card radius scaled to the dock's height, as in the design review. */
private val DOCK_RADIUS = 20.dp
private val SHEET_RADIUS = Ui.radiusCard + Ui.space1

/** One choice in a bottom sheet: a full-width row at least 48 dp tall, its mark drawn beside the words and kept out of TalkBack. */
@Composable private fun SheetAction(label:String,mark:String?,color:Color=Ui.ink,onClick:()->Unit) {
    TextButton(onClick=onClick,shape=RoundedCornerShape(Ui.radiusControl),contentPadding=PaddingValues(horizontal=Ui.space2),
        modifier=Modifier.fillMaxWidth().heightIn(min=Ui.target)) {
        Text(mark.orEmpty(),fontSize=Ui.textLead,color=if(color==Ui.ink)Ui.mint else color,textAlign=TextAlign.Center,
            modifier=Modifier.width(28.dp).clearAndSetSemantics{})
        Text(label,fontSize=Ui.textLead,color=color,modifier=Modifier.padding(start=Ui.space2).weight(1f))
    }
}

/** A section's name inside a dialog. */
@Composable internal fun SettingsHeading(text:String) {
    Text(text,fontFamily=ObservatoryTitle,fontWeight=TitleWeight,fontSize=Ui.textTitle,color=Ui.ink,
        modifier=Modifier.padding(horizontal=Ui.space3).padding(bottom=Ui.space1).semantics{heading()})
}

/** The dashboard frame's mint opening arrow: drawn, so it never depends on a font's symbols. */
@Composable internal fun OpenArrow(modifier:Modifier=Modifier,color:Color=Ui.mint) {
    Canvas(modifier.clearAndSetSemantics{}) {
        val s=size.width/16f;val stroke=1.6f*s
        val cap=androidx.compose.ui.graphics.StrokeCap.Round
        drawLine(color,Offset(4*s,12*s),Offset(12*s,4*s),stroke,cap)
        drawLine(color,Offset(6*s,4*s),Offset(12*s,4*s),stroke,cap)
        drawLine(color,Offset(12*s,4*s),Offset(12*s,10*s),stroke,cap)
    }
}

/** The search field's magnifier. */
@Composable private fun SearchGlyph() {
    Canvas(Modifier.size(18.dp).clearAndSetSemantics{}) {
        val s=size.width/18f;val stroke=1.6f*s
        drawCircle(Ui.soft,5.5f*s,Offset(7.5f*s,7.5f*s),style=Stroke(stroke))
        drawLine(Ui.soft,Offset(11.6f*s,11.6f*s),Offset(16f*s,16f*s),stroke,androidx.compose.ui.graphics.StrokeCap.Round)
    }
}

@Composable
private fun RhythmGlyph(modifier: Modifier) {
    Canvas(modifier) {
        val path = Path()
        val w = size.width; val h = size.height
        path.moveTo(0f, h / 2)
        path.lineTo(w * .16f, h / 2)
        path.cubicTo(w * .25f, h * .03f, w * .34f, h * .03f, w * .42f, h / 2)
        path.cubicTo(w * .5f, h * .97f, w * .59f, h * .97f, w * .67f, h / 2)
        path.cubicTo(w * .75f, h * .03f, w * .84f, h * .03f, w * .92f, h / 2)
        path.lineTo(w, h / 2)
        drawPath(path, Ui.mint, style = Stroke(1.5.dp.toPx(), cap = androidx.compose.ui.graphics.StrokeCap.Round))
    }
}

@Composable private fun AnimatedSkyRegion(expanded:Boolean,height:androidx.compose.ui.unit.Dp,reduced:Boolean,content:@Composable ()->Unit) {
    val reveal=animateDpAsState(if(expanded)height else 0.dp,tween(if(reduced)0 else 700,easing=ObservatoryEase),label="Sky reveal")
    Box(Modifier.fillMaxWidth().height(reveal.value).clipToBounds()){if(expanded || reveal.value>0.dp)content()}
}

@Composable private fun DrawerKeyboardEffects(state:DrawerController,search:FocusRequester,scroll:LazyGridState,
    show:()->Unit,hide:()->Unit,closed:()->Unit) {
    val showNow by rememberUpdatedState(show);val hideNow by rememberUpdatedState(hide);val closeNow by rememberUpdatedState(closed)
    LaunchedEffect(state.settledOpen) {
        if(state.settledOpen){withFrameNanos{};if(state.settledOpen){search.requestFocus();showNow()}}
        else hideNow()
    }
    LaunchedEffect(state.fullyClosed){if(state.fullyClosed){closeNow();scroll.scrollToItem(0)}}
}
