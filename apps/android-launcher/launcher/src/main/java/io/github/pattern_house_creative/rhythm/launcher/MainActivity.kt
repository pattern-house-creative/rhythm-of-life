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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.*
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
internal data class HomeRemoval(val tile:Tile,val index:Int)
class MainActivity : ComponentActivity() {
    private val navigation by viewModels<LauncherNavigation>()
    private var homeSignal by mutableIntStateOf(0)
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState); enableEdgeToEdge(statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT), navigationBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT))
        // Owner-authorized: screenshots and system captures of the launcher are allowed.
        setContent { MaterialTheme(typography = ObservatoryTypography, colorScheme = darkColorScheme(primary = Color(0xffa8e1c6),
            background = Color(0xff10171c), surface = Color(0xff18272c), onSurface = Color(0xffdfebe9))) {
                CompositionLocalProvider(LocalContentColor provides Color(0xffdfebe9)) { Launcher(homeSignal, navigation) }
            } }
    }
    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); homeSignal++ }
}
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun Launcher(homeSignal: Int, navigation: LauncherNavigation) {
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
    val searchFocus = remember { FocusRequester() }
    val keyboard = LocalSoftwareKeyboardController.current
    val focus = LocalFocusManager.current
    val drawerScope=rememberCoroutineScope()
    val drawerMotion=remember { DrawerController(drawerScope) }
    val drawer by remember { derivedStateOf { drawerMotion.exposed } }
    var editHome by remember { mutableStateOf(false) }
    var undoHome by remember { mutableStateOf<HomeRemoval?>(null) }
    val dragState=remember { LauncherDrag() }
    val homeScroll=rememberScrollState()
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
    val appsByKey = remember(apps) { apps.associateBy { it.tile.key } }
    val owner = LocalLifecycleOwner.current
    fun accessLost() {
        client?.clearDiscovery()
        insight = null; insightOpen = false; household = null; found = emptyList()
        if (web != origin) web = null
        status = "Household access unavailable"
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
                if (!online) { client?.clearDiscovery(); insight = null; insightOpen = false; household = null; found = emptyList(); status = "Offline · apps remain available" }
            } }
        }
        online = manager.activeNetwork != null
        manager.registerDefaultNetworkCallback(callback)
        onDispose { manager.unregisterNetworkCallback(callback) }
    }
    fun save(next: HomeLayout) { layout = next; store.save(next) }
    fun showPreview(id:String) { store.allowPreview(id);save(layout.showPreview(id)) }
    fun removeHome(tile:Tile) {
        val index=layout.tiles.indexOfFirst { it.key==tile.key }
        if(index<0)return
        if(tile.kind==TileKind.PREVIEW || tile.kind==TileKind.DASHBOARD)store.recordPreviewRemoval(tile.target)
        save(layout.removeHome(tile.key));undoHome=HomeRemoval(tile,index);selected=null
    }
    fun undoRemoval() {
        val removed=undoHome ?: return
        if(layout.tiles.none { it.key==removed.tile.key }) {
            if(layout.tiles.size>=120) {status="Home is full; make room before restoring this item";return}
            if(removed.tile.kind==TileKind.PREVIEW)store.allowPreview(removed.tile.target)
            save(layout.insertHome(removed.tile,removed.index,false))
        }
        undoHome=null
    }
    LaunchedEffect(undoHome) { if(undoHome!=null){delay(8000);undoHome=null} }
    fun edit(tile:Tile) { keyboard?.hide();focus.clearFocus();selected=tile }
    fun close() { drawerMotion.snapClosed();dragState.cancel(); editHome=false;insightOpen = false; keyboard?.hide(); focus.clearFocus(); web = null; query = ""; selected = null; settings = false; previews = false }
    LaunchedEffect(homeSignal) { if (homeSignal > 0) close() }
    DisposableEffect(owner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) { visible = true; refresh++ }
            if (event == Lifecycle.Event.ON_PAUSE) { drawerMotion.snapClosed();dragState.cancel();editHome=false;visible = false; insight = null; insightOpen = false; found = emptyList() }
        }
        owner.lifecycle.addObserver(observer)
        visible = owner.lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)
        onDispose { owner.lifecycle.removeObserver(observer) }
    }
    LaunchedEffect(refresh, appRevision, visible) {
        if (!visible) return@LaunchedEffect
        apps = withContext(Dispatchers.IO) { installed(context) }
        if (!store.initialized && apps.isNotEmpty()) save(HomeLayout(apps.take(8).map { it.tile }, apps.take(6).map { it.tile }))
        appsReady = true
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
            catch (_: Exception) { client.clearDiscovery(); household = null; status = "Household unavailable · apps work offline" }
            delay(60_000)
        }
    }
    DrawerKeyboardEffects(drawerMotion,searchFocus,drawerScroll,{keyboard?.show()},{keyboard?.hide()},
        {query="";focus.clearFocus()})
    LaunchedEffect(drawer) {if(drawer)editHome=false}
    LaunchedEffect(origin,visible,online,refresh,web) {
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
                insightState="Household access unavailable";break
            } catch(_:Exception) {
                insight=null;insightOpen=false;insightState="Insight unavailable · try reconnecting"
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
            catch (_: Exception) { status = "This app is no longer available"; refresh++ }
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
    SideEffect {drawerMotion.reduced=still || systemStill}
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
    val rootGestures=launcherGestures(dragState,visible && web==null && !previews && !settings && selected==null && !insightOpen,
        drawerMotion,{!drawerScroll.canScrollBackward},results,homeScroll,layout,{save(it)},::edit,{previews=true},
        {drawerMotion.snapClosed(false);keyboard?.hide();focus.clearFocus()},{drawerMotion.request(true)})
    BoxWithConstraints(Modifier.fillMaxSize().testTag("launcher-root").background(Color(0xff07181d)).onGloballyPositioned { dragState.root=it.boundsInRoot() }.then(rootGestures)) {
        val revealHeight=skyRevealDp(maxHeight.value).dp
        AmbientSky(skyGraph, household?.links.orEmpty(), still || systemStill, visible && web == null && (!drawer || drawerMotion.settledOpen), sky)
        Column(Modifier.fillMaxSize().windowInsetsPadding(homeInsets)
            .then(if(web!=null)Modifier.clearAndSetSemantics{} else Modifier)) {
            Row(Modifier.fillMaxWidth().padding(horizontal=16.dp).height(52.dp), verticalAlignment = Alignment.CenterVertically) {
                RhythmGlyph(Modifier.size(14.dp))
                Spacer(Modifier.width(4.dp))
                Text("Rhythm OS", fontSize = 19.sp, fontFamily = ObservatoryTitle, fontWeight = FontWeight.Normal,
                    letterSpacing = (-.4).sp, color = Color(0xffd8eee4), modifier = Modifier.weight(1f))
                TextButton(onClick = {if(editHome)editHome=false else sky=!sky},contentPadding=PaddingValues(horizontal=12.dp)) {
                    Text(if(editHome)"Done" else if(sky)"Less sky" else "More sky",fontSize=11.sp,fontWeight=FontWeight.Normal)
                }
                IconButton(onClick = { settings = true }, modifier = Modifier.size(48.dp).semantics { contentDescription = "Settings" }) {
                    Canvas(Modifier.size(18.dp)) {
                        drawCircle(Color(0xff9db4b9), size.width * .32f, style = Stroke(1.3.dp.toPx()))
                        drawCircle(Color(0xff9db4b9), size.width * .09f)
                        repeat(8) { i ->
                            val angle = i * Math.PI / 4
                            val direction = Offset(kotlin.math.cos(angle).toFloat(), kotlin.math.sin(angle).toFloat())
                            drawLine(Color(0xff9db4b9), center + direction * size.width * .34f, center + direction * size.width * .47f, 1.3.dp.toPx())
                        }
                    }
                }
            }
            if (status.isNotEmpty()) Text(status, color = Color(0xffa1b6bc), fontSize = 12.sp, modifier = Modifier.padding(horizontal=24.dp,vertical=8.dp))
            Box(Modifier.weight(1f).fillMaxWidth().clipToBounds().onGloballyPositioned{drawerMotion.viewport=it.boundsInRoot()}) {
                // Home geometry remains stable throughout drawer entry, dragging and exit.
                Column(Modifier.fillMaxSize().padding(horizontal=16.dp).graphicsLayer{alpha=1f-drawerMotion.fraction}
                    .pointerInput(drawer){if(drawer)awaitPointerEventScope{while(true){val event=awaitPointerEvent(PointerEventPass.Initial);event.changes.forEach{it.consume()}}}}
                    .then(if(drawer)Modifier.clearAndSetSemantics{} else Modifier)) {
                    AnimatedSkyRegion(sky && !drawer && !(dragState.tile!=null && dragState.source==DragSource.DRAWER),
                        revealHeight,still || systemStill) {
                        SkyExplorer(household,skyGraph,still || systemStill,visible && web==null && sky && !drawer){open(it)}
                    }
                    Box(Modifier.weight(1f).fillMaxWidth()) {
                        HomeCellGrid(layout.tiles,homeScroll,dragState,still || systemStill,Modifier.fillMaxSize()) { tile,cellModifier ->
                            LauncherTile(tile,title(tile),appsByKey[tile.key],origin,
                                origin.isNotBlank() && online && household!=null,online,cellModifier,
                                {if(editHome)edit(tile)else open(tile)},{edit(tile)},{
                                    val i=layout.tiles.indexOfFirst{it.key==tile.key};if(i>0)save(layout.move(tile.key,layout.tiles[i-1].key))
                                },{
                                    val i=layout.tiles.indexOfFirst{it.key==tile.key};if(i>=0 && i<layout.tiles.lastIndex)save(layout.move(tile.key,layout.tiles[i+1].key))
                                },::accessLost,
                                onRemove={removeHome(tile)},editing=editHome,webActive={visible && web==null && !drawerMotion.covered})
                        }
                        if(layout.tiles.isEmpty()) Text("Swipe up for apps · hold empty space for previews",fontSize=12.sp,
                            color=Color(0xff9fb6b0),modifier=Modifier.align(Alignment.Center).padding(24.dp))
                    }
                    Box(Modifier.fillMaxWidth()) {
                        Box(Modifier.graphicsLayer{alpha=if(undoHome==null)1f else 0f}
                            .then(if(undoHome!=null)Modifier.clearAndSetSemantics{} else Modifier)) {
                            DailyInsightBar(insight,insightState){if(undoHome==null)insightOpen=true}
                        }
                        undoHome?.let {removed->
                            Row(Modifier.matchParentSize().background(Color(0xff193b35),RoundedCornerShape(18.dp))
                                .padding(horizontal=10.dp).semantics{liveRegion=LiveRegionMode.Polite},verticalAlignment=Alignment.CenterVertically) {
                                Text("Removed ${title(removed.tile)} from Home",fontSize=12.sp,lineHeight=16.sp,maxLines=2,
                                    overflow=TextOverflow.Ellipsis,modifier=Modifier.weight(1f))
                                TextButton(onClick=::undoRemoval){Text("Undo")}
                                IconButton(onClick={undoHome=null},modifier=Modifier.semantics{contentDescription="Dismiss removal notice"}){Text("×")}
                            }
                        }
                    }
                    Box(Modifier.fillMaxWidth().height(24.dp).testTag("swipe-search")
                        .onGloballyPositioned{drawerMotion.handle=it.boundsInRoot()}
                        .clickable(role=Role.Button,onClickLabel="Open apps and search"){drawerMotion.request(true)},contentAlignment=Alignment.Center) {
                        Text("⌃  Swipe up to search",fontSize=9.sp,color=Color(0xff8ba99f))
                    }
                }
                DrawerSurface(drawerMotion) {
                    Column(Modifier.fillMaxSize().padding(horizontal=8.dp)
                        .drawerImePadding(homeInsets,drawerMotion).testTag("app-drawer").then(if(!drawer)Modifier.clearAndSetSemantics{} else Modifier)) {
                        Box(Modifier.fillMaxWidth().height(20.dp).testTag("drawer-handle")
                            .onGloballyPositioned{drawerMotion.header=it}
                            .semantics{contentDescription="Drag down to close apps"}
                            .clickable(onClickLabel="Close apps"){drawerMotion.request(false)},contentAlignment=Alignment.Center) {
                            Box(Modifier.width(38.dp).height(4.dp).background(Color(0xff91b9ae),RoundedCornerShape(2.dp)))
                        }
                        OutlinedTextField(query,{query=it.take(200)},singleLine=true,shape=RoundedCornerShape(22.dp),
                            textStyle=androidx.compose.ui.text.TextStyle(fontSize=14.sp),placeholder={Text("Search apps & Rhythm",fontSize=13.sp)},
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
            Row(Modifier.fillMaxWidth().padding(horizontal=16.dp,vertical=8.dp).height(56.dp).testTag("home-dock")
                .onGloballyPositioned { dragState.dock=it.boundsInRoot() }
                .background(Color(0xee061b1f),RoundedCornerShape(28.dp)).border(.5.dp,Color(0x1cd0f5e5),RoundedCornerShape(28.dp)),
                verticalAlignment=Alignment.CenterVertically) {
                repeat(6) { index->
                    val tile=layout.dock.getOrNull(index)
                    val app=apps.find{it.tile.key==tile?.key}
                    Box(Modifier.weight(1f).fillMaxHeight().testTag("dock-slot:$index")
                        .onGloballyPositioned { dragState.slots[index]=it.boundsInRoot() }
                        .graphicsLayer { alpha=if(tile!=null && dragState.tile?.key==tile.key && dragState.source==DragSource.DOCK).25f else 1f }
                        .semantics {
                            contentDescription=if(tile==null)"Add app to dock slot ${index+1}" else title(tile)
                            if(tile!=null)customActions=listOf(CustomAccessibilityAction("Edit "+title(tile)){edit(tile);true},
                                CustomAccessibilityAction("Move left"){save(layout.insertDock(tile,(index-1).coerceAtLeast(0)));true},
                                CustomAccessibilityAction("Move right"){save(layout.insertDock(tile,(index+1).coerceAtMost(5)));true},
                                CustomAccessibilityAction("Remove from dock"){save(layout.removeDock(tile.key));true})
                        }.clickable(role=Role.Button){if(tile==null)drawerMotion.request(true) else open(tile)},contentAlignment=Alignment.Center) {
                        if(app!=null)Image(app.icon.asImageBitmap(),null,Modifier.size(40.dp))
                        else Text(if(tile==null)"+" else "?",fontSize=20.sp,color=Color(0xff6d9388))
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
            }.fillMaxSize().graphicsLayer { alpha = entering.value; translationY = (1f-entering.value)*35.dp.toPx() }.background(Color(0xff091d23)).safeDrawingPadding()) {
            if (online && sameOrigin(origin, web!!)) key(web) {
                RestrictedWeb(origin, web!!, Modifier.weight(1f).fillMaxWidth(), active = visible, accessLost = ::accessLost)
            }
            else Column(Modifier.padding(20.dp)) {
                Text("Offline · reconnect to open this view")
                TextButton(onClick={web=null;refresh++}){Text("Return to Home")}
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
                if (next.isNotEmpty() && old != next) save(layout.copy(tiles = layout.tiles.filter { it.kind == TileKind.APP },
                    dock = layout.dock.filter { it.kind == TileKind.APP }))
                store.origin = next; origin = next; refresh++; settings = false
            } catch (e: CancellationException) { throw e }
            catch (_: RuntimeException) {
                sessionError = "Disconnected. Update Android System WebView and retry clearing the web session."
            } finally { switching = false }
        }
    }
    val availableDashboards = household?.builtins.orEmpty() + household?.dashboards.orEmpty()
    if (previews) AlertDialog(onDismissRequest = { previews = false }, title = { Text("Dashboard previews") },
        text = { Column {
            Text("Live dashboards on your Home screen", fontSize = 13.sp, color = Color(0xffa8c5c0))
            when {
                origin.isBlank() -> TextButton(onClick = { previews = false; settings = true }) { Text("Connect your household") }
                household == null -> TextButton(onClick = { refresh++ }) { Text("Reconnect to load dashboards") }
                availableDashboards.isEmpty() -> Text("No dashboards are available from this household.")
                else -> LazyColumn(Modifier.heightIn(max = 360.dp)) {
                    items(availableDashboards, key = { it.id }) { board ->
                        val shown = layout.tiles.any { it.target == board.id && it.kind == TileKind.PREVIEW }
                        TextButton(enabled = !shown, onClick = { showPreview(board.id); previews = false; drawerMotion.request(false) }, modifier = Modifier.fillMaxWidth().testTag("preview-choice:${board.id}")) {
                            Column(Modifier.fillMaxWidth()) {
                                Text(if (board.id == BUILTIN_GLUCOSE) "Glucose \u00b7 built-in" else board.title, maxLines = 2, overflow = TextOverflow.Ellipsis)
                                Text(if (shown) "Already on Home" else "Add live preview", fontSize = 11.sp, color = Color(0xff9eb4bb))
                            }
                        }
                    }
                }
            }
        } }, confirmButton = { TextButton(onClick = { previews = false }) { Text("Done") } })
    if(insightOpen) insight?.let { DailyInsightSheet(it) { insightOpen=false } }
    selected?.let { tile ->
        val pinned=layout.tiles.any { it.key==tile.key }
        ModalBottomSheet(onDismissRequest={selected=null},containerColor=Color(0xff132b2c)) {
            Column(Modifier.fillMaxWidth().padding(horizontal=24.dp).padding(bottom=24.dp).testTag("tile-actions")) {
                Row(verticalAlignment=Alignment.CenterVertically) {
                    val icon=apps.find{it.tile.key==tile.key}?.icon
                    if(icon!=null)Image(icon.asImageBitmap(),null,Modifier.size(42.dp))
                    else DashboardGlyph(title(tile),tile.target)
                    Column(Modifier.padding(start=14.dp).weight(1f)) {
                        Text(title(tile),fontSize=19.sp,maxLines=2,overflow=TextOverflow.Ellipsis)
                        Text(if(pinned) "Hold, then drag to move" else "Add this to your Home",
                            fontSize=12.sp,color=Color(0xff9ab8ae))
                    }
                }
                Spacer(Modifier.height(16.dp))
                if(!pinned) TextButton(onClick={save(layout.add(tile));selected=null},modifier=Modifier.fillMaxWidth()) { Text("＋  Add to Home") }
                if(tile.kind==TileKind.DASHBOARD || tile.kind==TileKind.PREVIEW)
                    TextButton(onClick={
                        if(tile.kind==TileKind.DASHBOARD)showPreview(tile.target)else{store.recordPreviewRemoval(tile.target);save(layout.togglePreview(tile.key))};selected=null
                    },modifier=Modifier.fillMaxWidth()) { Text(if(tile.kind==TileKind.PREVIEW) "▦  Show as icon" else "▤  Show live preview") }
                if(tile.kind==TileKind.APP) TextButton(onClick={save(layout.dock(tile));selected=null},modifier=Modifier.fillMaxWidth()) { Text("↓  Pin to dock") }
                if(pinned)
                    TextButton(onClick={removeHome(tile)},modifier=Modifier.fillMaxWidth()) { Text("−  Remove from Home",color=Color(0xffdfbbb1)) }
                if(layout.dock.any{it.key==tile.key}) TextButton(onClick={save(layout.removeDock(tile.key));selected=null},modifier=Modifier.fillMaxWidth()) { Text("Remove from dock") }
                TextButton(onClick={selected=null},modifier=Modifier.align(Alignment.End)) { Text("Done") }
            }
        }
    }
    if (settings) {
        var address by remember { mutableStateOf(origin) }
        var error by remember { mutableStateOf(false) }
        AlertDialog(onDismissRequest = { settings = false }, title = { Text("Settings") }, text = { Column {
            TextButton(onClick={settings=false;editHome=true},modifier=Modifier.testTag("edit-home")){Text("Edit Home")}
            Text("Remove buttons only unpin Home items. Apps and dashboards are kept.",fontSize=12.sp)
            Spacer(Modifier.height(12.dp));Text("Your household")
            Text("Use your existing HTTPS household address. External sign-in redirects are not supported yet.")
            OutlinedTextField(address, { address = it; error = false }, label = { Text("https://household.example") }, isError = error)
            TextButton(onClick = { still = !still; store.still = still }) { Text(if (still) "Motion: reduced" else "Motion: gentle") }
            if (origin.isNotEmpty()) TextButton(onClick = { settings = false; web = origin }) { Text("Open household sign-in") }
            if (sessionError.isNotEmpty()) Text(sessionError)
            TextButton(enabled = !switching, onClick = { changeOrigin("") }) { Text("Disconnect and clear web session") }
        } }, confirmButton = { TextButton(enabled = !switching, onClick = {
            val clean = householdOrigin(address)
            if (clean == null) error = true
            else if (clean != origin) changeOrigin(clean)
            else { refresh++; settings = false }
        }) { Text("Save") } })
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
        drawPath(path, Color(0xff8dd7c0), style = Stroke(1.4.dp.toPx()))
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
