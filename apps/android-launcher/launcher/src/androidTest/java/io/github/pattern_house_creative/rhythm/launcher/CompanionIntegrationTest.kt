package io.github.pattern_house_creative.rhythm.launcher

import android.graphics.Bitmap
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.webkit.WebView
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.*
import org.junit.Assert.*
import java.net.URL
import java.util.concurrent.atomic.AtomicReference

// These tests require a separately installed .fixture variant, loopback HTTPS
// server, and its exact test CA. No SSL error handler is ever allowed to proceed.
class CompanionIntegrationTest {
    @get:Rule val rule = createAndroidComposeRule<MainActivity>()
    private val origin = "https://10.0.2.2:9443"
    private val star = "second-brain:fixture:note-0"
    private val board = "00000000-0000-4000-8000-000000000001"
    private lateinit var store: LauncherStore
    private lateinit var previous: HomeLayout
    private var active = false
    @Before fun start() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        Assume.assumeTrue(context.packageName.endsWith(".fixture"))
        assertTrue(android.os.Build.FINGERPRINT.contains("generic") || android.os.Build.MODEL.contains("sdk"))
        store = LauncherStore(context)
        assertTrue(store.origin.isEmpty())
        rule.waitUntil(10000) { store.load().tiles.size >= 4 }
        previous = store.load(); active = true
        control("normal")
        store.origin = origin
        store.save(previous.copy(tiles = previous.tiles.take(4) + listOf(Tile(TileKind.PREVIEW,board),
            Tile(TileKind.PREVIEW,"00000000-0000-4000-8000-000000000002"), Tile(TileKind.STAR,star))))
        onMain { it.recreate() }
        rule.waitUntil(15000) { rule.onAllNodesWithTag("tile:DASHBOARD:$board").fetchSemanticsNodes().isNotEmpty() }
    }
    @After fun finish() {
        if (!active) return
        try { control("normal") } finally {
            store.origin = ""; store.still=false; store.save(previous)
            onMain { assertTrue(it.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0) }
        }
    }
    private fun onMain(action: (MainActivity) -> Unit) {
        // ActivityScenario waits for Android's message queue to become entirely
        // idle, which an animating web reader need not do. Post directly instead.
        InstrumentationRegistry.getInstrumentation().runOnMainSync {
            val activity = androidx.test.runner.lifecycle.ActivityLifecycleMonitorRegistry.getInstance()
                .getActivitiesInStage(androidx.test.runner.lifecycle.Stage.RESUMED).filterIsInstance<MainActivity>().single()
            action(activity)
        }
    }
    private fun control(mode: String) {
        val c = URL("$origin/__fixture/control?mode=$mode").openConnection()
        c.connectTimeout=3000;c.readTimeout=3000;c.getInputStream().use { it.readBytes() }
    }
    private fun webViews(view: View): List<WebView> = when (view) {
        is WebView -> listOf(view)
        is ViewGroup -> (0 until view.childCount).flatMap { webViews(view.getChildAt(it)) }
        else -> emptyList()
    }
    private fun pageText(): String {
        val answer = AtomicReference("")
        onMain { a ->
            webViews(a.window.decorView).lastOrNull()?.evaluateJavascript("document.body.innerText") { answer.set(it.orEmpty()) }
        }
        Thread.sleep(80)
        return answer.get()
    }
    private fun waitText(text: String) {
        // Web readers animate independently of Compose's test clock. Do not ask
        // Compose to advance until this continuously drawing AndroidView is idle.
        rule.mainClock.autoAdvance = false
        val deadline = android.os.SystemClock.uptimeMillis() + 25000
        while (android.os.SystemClock.uptimeMillis() < deadline) {
            rule.mainClock.advanceTimeBy(100)
            if (pageText().contains(text)) { Thread.sleep(500); rule.mainClock.advanceTimeBy(100); return }
            Thread.sleep(100)
        }
        capture("reader-failure")
        fail("Reader did not show: $text; synthetic page: "+pageText().take(1600))
    }
    private fun back() { onMain { it.onBackPressedDispatcher.onBackPressed() }; rule.mainClock.autoAdvance = true; rule.waitForIdle() }
    private fun waitPreviewText(text: String) {
        rule.mainClock.autoAdvance=false
        val deadline=android.os.SystemClock.uptimeMillis()+25000
        while(android.os.SystemClock.uptimeMillis()<deadline) {
            val answers=java.util.concurrent.ConcurrentLinkedQueue<String>()
            onMain { a -> webViews(a.window.decorView).forEach { v -> v.evaluateJavascript("document.body.innerText") { answers.add(it.orEmpty()) } } }
            Thread.sleep(100);rule.mainClock.advanceTimeBy(100)
            if(answers.any{it.contains(text)})return
        }
        fail("Preview did not show: $text")
    }
    private fun postFixture(path:String, body:String="{}"):String {
        val c=URL("$origin$path").openConnection() as java.net.HttpURLConnection
        c.requestMethod="POST";c.doOutput=true;c.connectTimeout=3000;c.readTimeout=3000
        c.setRequestProperty("Content-Type","application/json")
        c.outputStream.use{it.write(body.toByteArray())}
        return c.inputStream.use{String(it.readBytes())}
    }
    @Test fun builtinGlucosePickerReadsStatesAndOpensTheBuiltinPanelWithoutChangingOtherPins() {
        val before=store.load();val key="DASHBOARD:$BUILTIN_GLUCOSE"
        assertTrue(before.tiles.none { it.target==BUILTIN_GLUCOSE })
        assertEquals(listOf(BUILTIN_GLUCOSE),runBlocking{HouseholdClient(origin).load()}.builtins.map{it.id})
        assertTrue(runBlocking{HouseholdClient(origin).load()}.dashboards.none{it.id==BUILTIN_GLUCOSE})
        control("glucose-fresh");onMain{it.recreate()};rule.mainClock.autoAdvance=true
        rule.onNodeWithTag("swipe-search").performClick()
            rule.onNodeWithTag("add-preview").performClick()
        rule.waitUntil(15000){rule.onAllNodesWithTag("preview-choice:$BUILTIN_GLUCOSE").fetchSemanticsNodes().isNotEmpty()}
        rule.onNodeWithText("Glucose \u00b7 built-in").assertExists()
        rule.onNodeWithTag("preview-choice:$BUILTIN_GLUCOSE").performClick()
        val pinned=store.load();assertEquals(before.tiles,pinned.tiles.filterNot{it.target==BUILTIN_GLUCOSE});assertEquals(before.dock,pinned.dock)
        waitPreviewText("123");waitPreviewText("mg/dL");waitPreviewText("2 min ago");capture("glucose-fresh")
        repeat(3) {
            rule.mainClock.autoAdvance=true;rule.onNodeWithTag("tile:$key").performClick()
            waitText("HEALTH / GLUCOSE");assertReaderViewport()
            val fullWidth=AtomicReference("false")
            onMain { a -> webViews(a.window.decorView).last().evaluateJavascript("Boolean(document.querySelector('.detail-glucose .detail-panel')?.getBoundingClientRect().width >= innerWidth - 2)") { fullWidth.set(it) } }
            Thread.sleep(100);assertEquals("true",fullWidth.get())
            if(it==0)capture("glucose-detail")
            back();waitPreviewText("123")
        }
        for((mode,expected) in listOf("glucose-stale" to "Stale reading","glucose-mmol" to "mmol/L","glucose-missing" to "No reading","glucose-error" to "Glucose unavailable")) {
            control(mode);onMain{it.recreate()};waitPreviewText(expected);capture(mode)
            assertEquals(pinned,store.load())
        }
        for(mode in listOf("offline","denied")) {
            control(mode);onMain{it.recreate()};rule.mainClock.autoAdvance=true
            rule.waitUntil(15000){rule.onAllNodesWithText("Household unavailable",substring=true).fetchSemanticsNodes().isNotEmpty()}
            assertEquals(pinned,store.load())
            control("glucose-fresh");onMain{it.recreate()};waitPreviewText("123")
        }
        control("old-builtins");onMain{it.recreate()};rule.mainClock.autoAdvance=true
        rule.waitUntil(15000){rule.onAllNodesWithText("Household companion update required").fetchSemanticsNodes().isNotEmpty()}
        assertEquals(pinned,store.load());assertFalse(runBlocking{HouseholdClient(origin).companionReady("glucose")})
        control("glucose-fresh");onMain{it.recreate()};waitPreviewText("123")
        rule.mainClock.autoAdvance=true
        rule.onNodeWithTag("tile:$key").performTouchInput { longClick(center) }
        rule.onNodeWithText("−  Remove from Home").performClick()
        assertEquals(before,store.load())
        onMain{it.recreate()};rule.waitForIdle()
        rule.waitUntil(15000){rule.onAllNodesWithTag("swipe-search").fetchSemanticsNodes().isNotEmpty()}
        assertTrue(store.load().tiles.none{it.target==BUILTIN_GLUCOSE})
        rule.onNodeWithTag("swipe-search").performClick()
            rule.onNodeWithTag("add-preview").performClick()
        rule.waitUntil(15000){rule.onAllNodesWithTag("preview-choice:$BUILTIN_GLUCOSE").fetchSemanticsNodes().isNotEmpty()}
        rule.onNodeWithTag("preview-choice:$BUILTIN_GLUCOSE").performClick()
        waitPreviewText("123");assertEquals(pinned,store.load())
    }
    private fun assertReaderViewport(dashboard:Boolean=false) {
        rule.onNodeWithText("Close · Home").assertDoesNotExist()
        val answer=AtomicReference("false")
        onMain { a ->
            val v=webViews(a.window.decorView).last()
            val insets=a.window.decorView.rootWindowInsets.getInsets(android.view.WindowInsets.Type.systemBars() or android.view.WindowInsets.Type.displayCutout())
            val location=IntArray(2);v.getLocationOnScreen(location)
            assertEquals(insets.left.toFloat(),location[0].toFloat(),3f)
            assertEquals("No native header may consume the reader viewport",insets.top.toFloat(),location[1].toFloat(),3f)
            assertEquals((a.window.decorView.width-insets.left-insets.right).toFloat(),v.width.toFloat(),3f)
            assertEquals((a.window.decorView.height-insets.top-insets.bottom).toFloat(),v.height.toFloat(),3f)
            assertTrue(a.window.decorView.rootWindowInsets.isVisible(android.view.WindowInsets.Type.statusBars()))
            assertTrue(a.window.decorView.rootWindowInsets.isVisible(android.view.WindowInsets.Type.navigationBars()))
            if(dashboard)v.evaluateJavascript("Boolean(document.querySelector('.full-screen.native-reader') && !document.querySelector('.full-screen-bar,.full-screen-wake') && getComputedStyle(document.querySelector('.full-screen')).paddingTop==='0px' && document.querySelector('.dashboard-frame')?.getBoundingClientRect().bottom<=innerHeight+2)"){answer.set(it)}
        }
        if(dashboard){Thread.sleep(100);assertEquals("true",answer.get())}
    }
    private fun readerBoolean(script:String):Boolean {
        val answer=AtomicReference("false")
        onMain{a->webViews(a.window.decorView).last().evaluateJavascript(script){answer.set(it)}}
        Thread.sleep(80);return answer.get()=="true"
    }
    private fun nativeStroke(from:Offset,to:Offset,duration:Int=80) {
        val automation=InstrumentationRegistry.getInstrumentation().uiAutomation
        val start=android.os.SystemClock.uptimeMillis()
        fun send(action:Int,point:Offset) {
            val event=android.view.MotionEvent.obtain(start,android.os.SystemClock.uptimeMillis(),action,point.x,point.y,0)
            event.source=android.view.InputDevice.SOURCE_TOUCHSCREEN
            try{assertTrue(automation.injectInputEvent(event,true))}finally{event.recycle()}
        }
        send(android.view.MotionEvent.ACTION_DOWN,from)
        val steps=(duration/16).coerceAtLeast(1)
        for(i in 1..steps){Thread.sleep(16);send(android.view.MotionEvent.ACTION_MOVE,from+(to-from)*(i.toFloat()/steps))}
        send(android.view.MotionEvent.ACTION_UP,to)
    }
    @Test fun nativeReadersHaveNoDuplicateChromeKeepSafeInsetsAndAcceptFirstTouchAndScroll() {
        val saved=store.load()
        try {
            for((index,id) in listOf(board,"00000000-0000-4000-8000-000000000002").withIndex()) {
                repeat(2) { round ->
                    rule.mainClock.autoAdvance=true
                    rule.onNodeWithTag("home-grid").performScrollToNode(hasTestTag("tile:DASHBOARD:$id"))
                    rule.onNodeWithTag("tile:DASHBOARD:$id").performClick()
                    waitText(if(index==0)"Garden overview" else "Reading overview")
                    assertReaderViewport(true)
                    if(round==0)capture("reader-no-chrome-$index")
                    when {
                        index==0 && round==0 -> back()
                        round==0 -> onMain{a->a.startActivity(android.content.Intent(a,MainActivity::class.java).setAction(android.content.Intent.ACTION_MAIN).addCategory(android.content.Intent.CATEGORY_HOME).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK))}
                        else -> {
                            val actions=rule.onNodeWithTag("full-household").fetchSemanticsNode().config[androidx.compose.ui.semantics.SemanticsActions.CustomActions]
                            rule.runOnIdle{assertTrue(actions.first{it.label=="Return to Home"}.action())}
                        }
                    }
                    rule.mainClock.autoAdvance=true;rule.waitForIdle()
                    rule.waitUntil(10000){rule.onAllNodesWithTag("full-household").fetchSemanticsNodes().isEmpty()}
                    rule.onNodeWithTag("full-household").assertDoesNotExist()
                    assertEquals(saved,store.load())
                }
            }
            val id="00000000-0000-4000-8000-000000000003"
            val html="""<style>body{margin:0;padding:16px 16px 80px;color:#e4efe8;background:#14252b}button{position:fixed;left:16px;top:80px;width:160px;height:48px}nav{position:fixed;bottom:0;left:0;right:0;height:56px;background:#284139;display:flex;align-items:center;justify-content:center}</style><h1>Reader viewport test</h1><button>Tap dashboard</button><div style="height:1000px">Scrollable synthetic dashboard</div><p id="end">End of dashboard content</p><nav>Dashboard navigation</nav><script>document.querySelector('button').addEventListener('click',()=>{document.querySelector('button').textContent='First tap reached dashboard';top.postMessage({fixtureReader:'tap'},'*')});addEventListener('scroll',()=>{const end=document.getElementById('end').getBoundingClientRect(),nav=document.querySelector('nav').getBoundingClientRect();top.postMessage({fixtureReader:'scroll',y:scrollY,endBottom:end.bottom,navTop:nav.top,navBottom:nav.bottom,height:innerHeight},'*')});rhythm.onData(()=>{})</script>"""
            val draft=org.json.JSONObject().put("title","Reader viewport test").put("description","Synthetic touch and insets test").put("html",html)
                .put("feeds",org.json.JSONArray().put(org.json.JSONObject().put("name","progress").put("kind","contents").put("star","second-brain:fixture:note-2")))
            postFixture("/__fixture/create",draft.toString())
            store.save(saved.showPreview(id));onMain{it.recreate()};rule.mainClock.autoAdvance=true
            rule.onNodeWithTag("home-grid").performScrollToNode(hasTestTag("tile:DASHBOARD:$id"));rule.onNodeWithTag("tile:DASHBOARD:$id").performClick()
            waitText("Reader viewport test");assertReaderViewport(true)
            rule.mainClock.autoAdvance=true;rule.waitForIdle();Thread.sleep(1200)
            capture("reader-before-first-touch")
            val point=AtomicReference("")
            var frame=android.graphics.Rect()
            onMain{a->
                val v=webViews(a.window.decorView).last();v.getGlobalVisibleRect(frame)
                // Test-only receiver accepts telemetry from this sealed synthetic iframe.
                // No production bridge, same-origin access or security policy is added.
                v.evaluateJavascript("""(()=>{window.__fixtureReader={taps:0};const f=document.querySelector('.full-screen.native-reader .dashboard-frame');addEventListener('message',e=>{if(e.source!==f.contentWindow[0])return;if(e.data?.fixtureReader==='tap')window.__fixtureReader.taps++;if(e.data?.fixtureReader==='scroll')window.__fixtureReader.scroll=e.data});const r=f.getBoundingClientRect();return JSON.stringify({x:r.left+96,y:r.top+104,w:innerWidth})})()"""){point.set(it)}
            }
            Thread.sleep(100)
            val position=org.json.JSONObject(org.json.JSONTokener(point.get()).nextValue().toString())
            val scale=frame.width()/position.getDouble("w").toFloat()
            val target=Offset(frame.left+position.getDouble("x").toFloat()*scale,frame.top+position.getDouble("y").toFloat()*scale)
            android.util.Log.e("RhythmReader","Synthetic touch point=$target frame=$frame expression=${point.get()}")
            nativeStroke(target,target)
            Thread.sleep(250);capture("reader-after-first-touch")
            rule.waitUntil(10000){readerBoolean("window.__fixtureReader?.taps===1")}
            repeat(6){nativeStroke(Offset(frame.exactCenterX(),frame.top+frame.height()*.75f),Offset(frame.exactCenterX(),frame.top+frame.height()*.25f),300)}
            capture("reader-after-scroll")
            onMain{a->webViews(a.window.decorView).last().evaluateJavascript("JSON.stringify(window.__fixtureReader)"){android.util.Log.e("RhythmReader","Synthetic scroll telemetry=$it")}}
            rule.waitUntil(10000){readerBoolean("Boolean(window.__fixtureReader?.scroll && window.__fixtureReader.scroll.y>0 && window.__fixtureReader.scroll.endBottom<=window.__fixtureReader.scroll.navTop+2 && window.__fixtureReader.scroll.navBottom<=window.__fixtureReader.scroll.height+2)")}
            capture("reader-scrolled-footer");back()
        } finally {store.save(saved);postFixture("/__fixture/reset-created");control("normal");rule.mainClock.autoAdvance=true}
    }

    @Test fun newDashboardAutomaticallyHasPreviewWithoutChangingPinsAndFailuresStayTruthful() {
        val newId="00000000-0000-4000-8000-000000000003"
        val oldLayout=store.load()
        try {
            waitPreviewText("28 min")
            // The real engine's saved-draft shape, validated by cleanDashboard in
            // a synthetic in-memory catalog. This is not a real database write.
            val draft=org.json.JSONObject().put("title","Weekend plan").put("description","Synthetic creation test")
                .put("html","<h1>Weekend plan</h1><script>rhythm.onData(()=>{})</script>")
                .put("feeds",org.json.JSONArray().put(org.json.JSONObject().put("name","progress").put("kind","contents").put("star","second-brain:fixture:note-2")))
            assertEquals(newId,org.json.JSONObject(postFixture("/__fixture/create",draft.toString())).getString("id"))
            assertTrue(runBlocking{HouseholdClient(origin).load().dashboards}.any{it.id==newId})
            onMain{it.recreate()};rule.mainClock.autoAdvance=true;rule.waitForIdle()
            assertEquals(oldLayout,store.load())
            rule.onNodeWithTag("swipe-search").performClick()
            rule.onNodeWithTag("add-preview").performClick()
            rule.waitUntil(15000){rule.onAllNodesWithTag("preview-choice:$newId").fetchSemanticsNodes().isNotEmpty()}
            rule.onNodeWithTag("preview-choice:$newId").performClick()
            val key="DASHBOARD:$newId"
            rule.onNodeWithTag("home-grid").performScrollToNode(hasTestTag("tile:$key"))
            waitPreviewText("Meet at the lake entrance")
            capture("created-preview")
            repeat(2) {
                rule.mainClock.autoAdvance=true
                rule.onNodeWithTag("tile:$key").performClick();waitText("Weekend plan");back()
                rule.onNodeWithTag("home-grid").performScrollToNode(hasTestTag("tile:$key"))
                waitPreviewText("Meet at the lake entrance")
            }
            // Existing dashboards with no preview metadata, missing supported
            // data, a throwing authored renderer and a failed data endpoint.
            store.save(oldLayout)
            for((mode,expected) in listOf("preview-absent" to "Basic preview","preview-broken" to "authored preview failed","preview-error" to "Preview unavailable","preview-denied" to "Sources unavailable")) {
                control(mode);onMain{it.recreate()};waitPreviewText(expected);capture(mode)
            }
            control("normal");onMain{it.recreate()};waitPreviewText("28 min")
            assertEquals(oldLayout,store.load())
        } finally { postFixture("/__fixture/reset-created");control("normal");store.save(oldLayout);rule.mainClock.autoAdvance=true }
    }
    @Test fun mixedLivePreviewsUseTwoCellRowsAndOpenRepeatedly() {
        val newId="00000000-0000-4000-8000-000000000003"
        val saved=store.load()
        try {
            val draft=org.json.JSONObject().put("title","Weekend plan").put("description","Synthetic layout fixture")
                .put("html","<h1>Weekend plan</h1><script>rhythm.onData(()=>{})</script>")
                .put("feeds",org.json.JSONArray().put(org.json.JSONObject().put("name","progress").put("kind","contents").put("star","second-brain:fixture:note-2")))
            postFixture("/__fixture/create",draft.toString())
            val apps=saved.tiles.filter{it.kind==TileKind.APP}.take(4)
            val pins=listOf(Tile(TileKind.PREVIEW,board),Tile(TileKind.PREVIEW,"00000000-0000-4000-8000-000000000002"),Tile(TileKind.PREVIEW,newId))+apps
            store.save(saved.copy(tiles=pins));onMain{it.recreate()}
            waitPreviewText("Meet at the lake entrance");waitPreviewText("28 min")
            val preview=rule.onNodeWithTag("tile:DASHBOARD:$newId").fetchSemanticsNode().boundsInRoot
            val cells=apps.map{rule.onNodeWithTag("tile:"+it.key).fetchSemanticsNode().boundsInRoot}
            assertEquals(preview.top,cells.first().top,1f);assertEquals(preview.bottom,cells.last().bottom,1f)
            rule.onNodeWithText("Open dashboard").assertDoesNotExist()
            capture("live-mixed-cells")
            repeat(2) {
                rule.mainClock.autoAdvance=true
                rule.onNodeWithTag("tile:DASHBOARD:$newId").performClick();waitText("Weekend plan");back()
                waitPreviewText("Meet at the lake entrance")
            }
            assertEquals(pins,store.load().tiles)
        } finally {postFixture("/__fixture/reset-created");store.save(saved);rule.mainClock.autoAdvance=true}
    }
    private fun capture(name: String) {
        if (rule.mainClock.autoAdvance) { rule.mainClock.advanceTimeBy(600); rule.waitForIdle() }
        onMain { a ->
            webViews(a.window.decorView).forEach { v -> v.evaluateJavascript("JSON.stringify({error:document.documentElement.dataset.fixtureError,text:document.body.innerText,hidden:document.hidden,w:innerWidth,h:innerHeight,phone:matchMedia('(max-width:760px) and (min-height:501px)').matches,range:matchMedia('(width<=760px) and (height>=501px)').matches,right:document.querySelector('.explorer-reader')&&getComputedStyle(document.querySelector('.explorer-reader')).right,frame:document.querySelector('.dashboard-card-frame')?.getBoundingClientRect().toJSON(),drawn:Boolean(document.querySelector('[data-preview-ready=true],.launcher-authored.drawn'))})") { android.util.Log.i("RhythmFixture", it) } }
        }
        onMain {
            assertTrue(it.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0)
            assertTrue(it.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0)
        }
        try {
            Thread.sleep(350)
            rule.mainClock.advanceTimeBy(32)
            onMain { it.window.decorView.invalidate() }
            Thread.sleep(100)
            val context = InstrumentationRegistry.getInstrumentation().targetContext
            val values = android.content.ContentValues().apply {
                val phase=InstrumentationRegistry.getArguments().getString("geometryPhase") ?: "v019"
                put(android.provider.MediaStore.Images.Media.DISPLAY_NAME, "$phase-$name.png")
                put(android.provider.MediaStore.Images.Media.MIME_TYPE,"image/png")
                put(android.provider.MediaStore.Images.Media.RELATIVE_PATH,"Pictures/RhythmIntegration")
            }
            val uri = context.contentResolver.insert(android.provider.MediaStore.Images.Media.EXTERNAL_CONTENT_URI,values)!!
            context.contentResolver.openOutputStream(uri)!!.use { InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot().compress(Bitmap.CompressFormat.PNG,100,it) }
        } finally { onMain { assertTrue(it.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE == 0) } }
    }
    @Test fun portraitGeometryEvidenceKeepsPinsAndAllLoadedRecords() {
        val pins=store.load()
        store.still=true
        for((mode,count) in listOf("dense" to 86,"hub" to 86,"normal" to 12,"disconnected" to 66)) {
            control(mode);onMain{it.recreate()}
            waitPreviewText("28 min")
            rule.mainClock.autoAdvance=true
            rule.onNodeWithText("More sky").performClick()
            rule.waitUntil(20000){rule.onAllNodesWithTag("sky-map").fetchSemanticsNodes().any { it.config[androidx.compose.ui.semantics.SemanticsProperties.StateDescription].startsWith("$count loaded") }}
            capture("geometry-$mode-sky")
            if(mode=="hub") {
                rule.onNodeWithText("Stars").performClick()
                rule.onNodeWithText("Garden 1").performClick()
                rule.onNodeWithTag("sky:second-brain:fixture:large-0").assertExists()
                rule.onNodeWithTag("open-sky-selection").assertExists()
                capture("geometry-hub-focused")
                back();rule.onNodeWithContentDescription("Fit sky").performClick()
            }
            rule.onNodeWithText("Less sky").performClick()
            rule.mainClock.advanceTimeBy(1000);waitPreviewText("28 min");capture("geometry-$mode-home")
            assertEquals(pins,store.load())
        }
    }
    @Test fun skyGesturesOpenRealReadersAndReturnHome() {
        // Native facade parses actual HTTPS responses, then full web readers load the built companion.
        assertEquals(10, runBlocking { HouseholdClient(origin).load().stars.size })
        rule.waitUntil(15000) { rule.onAllNodesWithText("Opening household…", substring = true).fetchSemanticsNodes().isEmpty() }
        rule.waitUntil(15000) {
            val drawn = java.util.concurrent.atomic.AtomicInteger(0)
            onMain { a -> webViews(a.window.decorView).forEach { v ->
                v.evaluateJavascript("Boolean(document.querySelector('[data-preview-ready=true],.launcher-authored.drawn') && document.querySelector('#root').getBoundingClientRect().height > 100 && document.querySelector('#root').getBoundingClientRect().width <= innerWidth + 1)") { if (it == "true") drawn.incrementAndGet() }
            } }
            Thread.sleep(100)
            drawn.get() == 2
        }
        capture("home-previews")
        rule.onNodeWithText("More sky").performClick(); rule.waitForIdle()
        capture("small-overview")
        val tile = rule.onNodeWithTag("tile:${previous.tiles.first().key}")
        val top = tile.fetchSemanticsNode().boundsInRoot.top
        val map = rule.onNodeWithTag("sky-map")
        val target = rule.onNodeWithTag("sky:second-brain:fixture:note-2")
        val start = target.fetchSemanticsNode().boundsInRoot.center
        map.performTouchInput { pinch(Offset(width*.25f,height*.4f),Offset(width*.75f,height*.4f),Offset(width*.12f,height*.4f),Offset(width*.88f,height*.4f)) }
        rule.waitForIdle()
        val afterZoom=rule.onAllNodesWithTag("sky:second-brain:fixture:note-2").fetchSemanticsNodes()
        assertTrue(afterZoom.isEmpty() || start!=afterZoom.first().boundsInRoot.center)
        map.performTouchInput { swipe(Offset(width*.6f,height*.6f),Offset(width*.35f,height*.4f),400) }
        rule.waitForIdle()
        assertEquals(top,tile.fetchSemanticsNode().boundsInRoot.top)
        rule.onNodeWithTag("full-household").assertDoesNotExist()
        rule.onNodeWithContentDescription("Fit sky").performClick(); rule.waitForIdle()
        capture("sky")
        rule.onNodeWithTag("sky:$star").performTouchInput { click(center) }; rule.waitForIdle()
        capture("selection")
        rule.onNodeWithTag("open-sky-selection").performClick()
        waitText("Fixture source opened successfully")
        val phone = AtomicReference("false")
        onMain { a -> webViews(a.window.decorView).last().evaluateJavascript("Boolean(document.querySelector('.explorer-reader') && document.querySelector('.explorer-reader').getBoundingClientRect().width >= innerWidth - 2)") { phone.set(it) } }
        Thread.sleep(100); assertEquals("true", phone.get())
        capture("star-reader")
        back(); rule.onNodeWithText("Less sky").assertExists()
        rule.onNodeWithContentDescription("Fit sky").performClick(); rule.waitForIdle()
        rule.onNodeWithTag("sky:$board").performTouchInput { click(center) }; rule.waitForIdle()
        rule.onNodeWithTag("open-sky-selection").performTouchInput { doubleClick(center) }
        waitText("Garden overview"); Thread.sleep(1800); capture("dashboard")
        back(); back(); back(); rule.onNodeWithText("More sky").assertExists()
        // A preview opens the full dashboard through the same validated route.
        rule.onNodeWithTag("tile:DASHBOARD:$board").performTouchInput { click(center) }
        waitText("Garden overview"); back()
    }
    @Test fun navigationKeepsTheSamePreviewDocuments() {
        waitPreviewText("28 min")
        fun cardViews(): List<WebView> {
            var cards = emptyList<WebView>()
            onMain { activity -> cards = webViews(activity.window.decorView).filter { it.url?.contains("launcher-card.html") == true } }
            return cards
        }
        val original = cardViews()
        assertEquals(2, original.size)
        rule.mainClock.autoAdvance = true
        repeat(2) {
            rule.onNodeWithTag("swipe-search").performClick()
            rule.onNodeWithTag("drawer-handle").performClick()
        }
        rule.onNodeWithTag("tile:DASHBOARD:$board").performClick()
        waitText("Garden overview")
        back()
        waitPreviewText("28 min")
        assertEquals("Returning from the reader should preserve both WebViews", original, cardViews())
        rule.mainClock.autoAdvance = true
        rule.activityRule.scenario.moveToState(androidx.lifecycle.Lifecycle.State.CREATED)
        rule.activityRule.scenario.moveToState(androidx.lifecycle.Lifecycle.State.RESUMED)
        waitPreviewText("28 min")
        assertEquals("Backgrounding should pause the same documents", original, cardViews())
    }

    @Test fun readerRetainsItsDocumentAcrossPauseAndChecksAccessOnResume() {
        rule.onNodeWithTag("tile:DASHBOARD:$board").performClick()
        waitText("Garden overview")
        fun reader(): WebView {
            lateinit var page: WebView
            onMain { activity -> page = webViews(activity.window.decorView).last() }
            return page
        }
        val original = reader()
        onMain { original.evaluateJavascript("document.documentElement.dataset.retained = 'yes'", null) }
        rule.mainClock.autoAdvance = true
        rule.activityRule.scenario.moveToState(androidx.lifecycle.Lifecycle.State.CREATED)
        rule.activityRule.scenario.moveToState(androidx.lifecycle.Lifecycle.State.RESUMED)
        waitText("Garden overview")
        assertSame(original, reader())
        assertTrue(readerBoolean("document.documentElement.dataset.retained === 'yes'"))
        rule.mainClock.autoAdvance = true
        onMain { it.recreate() }
        waitText("Garden overview")
        rule.mainClock.autoAdvance = true
        rule.activityRule.scenario.moveToState(androidx.lifecycle.Lifecycle.State.CREATED)
        control("denied")
        rule.activityRule.scenario.moveToState(androidx.lifecycle.Lifecycle.State.RESUMED)
        rule.waitUntil(15000) {
            rule.onAllNodesWithTag("full-household").fetchSemanticsNodes().isEmpty()
        }
        rule.onNodeWithText("Rhythm OS").assertExists()
    }

    @Test fun crashingRendererLeavesLocalAppsUsable() {
        Assume.assumeTrue(android.os.Build.VERSION.SDK_INT >= 29)
        waitPreviewText("28 min")
        var terminated = false
        onMain { activity ->
            val page = webViews(activity.window.decorView).first()
            terminated = page.webViewRenderProcess?.terminate() == true
        }
        assertTrue(terminated)
        rule.mainClock.autoAdvance = true
        rule.waitUntil(15000) {
            rule.onAllNodesWithText("Android System WebView stopped. Your apps are still available.").fetchSemanticsNodes().isNotEmpty()
        }
        rule.onNodeWithTag("swipe-search").performClick()
        rule.onNodeWithTag("search-input").assertExists()
        rule.onNodeWithTag("drawer-handle").performClick()
        rule.onNodeWithText("Rhythm OS").assertExists()
    }

    @Test fun searchFailureKeepsPreviewsButDenialClearsThem() {
        waitPreviewText("28 min")
        rule.mainClock.autoAdvance = true
        control("graph-error")
        rule.onNodeWithTag("swipe-search").performClick()
        rule.onNodeWithTag("search-input").performTextInput("garden")
        rule.waitUntil(15000) { rule.onAllNodesWithText("Rhythm search unavailable").fetchSemanticsNodes().isNotEmpty() }
        rule.onNodeWithTag("drawer-handle").performClick()
        waitPreviewText("28 min")
        rule.mainClock.autoAdvance = true
        control("graph-denied")
        rule.onNodeWithTag("swipe-search").performClick()
        rule.onNodeWithTag("search-input").performTextInput("garden")
        rule.waitUntil(15000) { rule.onAllNodesWithText("Household access unavailable").fetchSemanticsNodes().isNotEmpty() }
        rule.onNodeWithTag("drawer-handle").performClick()
        onMain { assertTrue(webViews(it.window.decorView).isEmpty()) }
    }

    @Test fun driveSheetOpensInBrowserAndBackReturnsToItsReader() {
        val sheet = Tile(TileKind.STAR, "drive:fixture:synthetic-sheet")
        control("drive-preview")
        store.save(previous.copy(tiles = listOf(sheet)))
        onMain { it.recreate() }
        rule.onNodeWithTag("tile:${sheet.key}").performClick()
        waitText("Open original")
        val original = "https://docs.google.com/spreadsheets/d/synthetic-sheet/edit"
        assertTrue(readerBoolean("document.querySelector('.explorer-reader input').value === '$original' && !document.querySelector('.explorer-reader iframe')"))
        val target = AtomicReference<Offset>()
        onMain { activity ->
            val view = webViews(activity.window.decorView).last()
            val frame = android.graphics.Rect()
            view.getGlobalVisibleRect(frame)
            view.evaluateJavascript("""(() => {
              const link = document.querySelector('.explorer-reader a[href="$original"]');
              link.scrollIntoView({block:'center'});
              const r = link.getBoundingClientRect();
              return [r.x + r.width / 2, r.y + r.height / 2, innerWidth];
            })()""") { result ->
                val point = org.json.JSONArray(result)
                val scale = frame.width() / point.getDouble(2)
                target.set(Offset(frame.left + (point.getDouble(0) * scale).toFloat(),
                    frame.top + (point.getDouble(1) * scale).toFloat()))
            }
        }
        rule.waitUntil(5000) { target.get() != null }
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val automation = instrumentation.uiAutomation
        val received = AtomicReference<android.content.Intent>()
        val monitor = object : android.app.Instrumentation.ActivityMonitor() {
            override fun onStartActivity(intent: android.content.Intent): android.app.Instrumentation.ActivityResult? {
                if (intent.action == android.content.Intent.ACTION_VIEW) received.set(intent)
                return null // Let Android launch the actual browser.
            }
        }
        instrumentation.addMonitor(monitor)
        try {
            nativeStroke(target.get(), target.get())
            rule.waitUntil(5000) { received.get() != null }
            val outgoing = received.get()
            assertEquals(original, outgoing.dataString)
            val browser = outgoing.selector!!.resolveActivity(instrumentation.targetContext.packageManager)
            assertNotNull("The test emulator needs a browser", browser)
            rule.waitUntil(15000) { automation.rootInActiveWindow?.packageName == browser.packageName }
            assertTrue(automation.performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_BACK))
            rule.waitUntil(15000) { automation.rootInActiveWindow?.packageName == instrumentation.targetContext.packageName }
            waitText("Open original")
            assertTrue(readerBoolean("document.querySelector('.explorer-reader input').value === '$original'"))
        } finally {
            instrumentation.removeMonitor(monitor)
        }
        back()
        rule.onNodeWithTag("tile:${sheet.key}").assertExists()
    }

    @Test fun failuresLoadingAndInterruptedOpenStayRecoverable() {
        control("slow")
        rule.onNodeWithTag("tile:STAR:$star").performScrollTo().performTouchInput { click(center) }
        rule.onAllNodesWithText("Opening household…").onLast().assertExists(); capture("loading")
        back(); Thread.sleep(2200)
        rule.onNodeWithText("Rhythm OS").assertExists()
        control("old")
        rule.onNodeWithTag("tile:STAR:$star").performTouchInput { click(center) }
        rule.waitUntil(10000) { rule.onAllNodesWithText("Household companion update required").fetchSemanticsNodes().isNotEmpty() }
        capture("server-update-required"); back()
        control("error")
        rule.onNodeWithTag("tile:STAR:$star").performTouchInput { click(center) }
        rule.waitUntil(10000) { rule.onAllNodesWithText("Page unavailable (HTTP 503)").fetchSemanticsNodes().isNotEmpty() }
        capture("error"); control("normal")
        rule.onNode(hasText("Try again") and hasAnyAncestor(hasTestTag("full-household"))).performClick(); waitText("Fixture source opened successfully"); back()
        control("offline")
        rule.onNodeWithTag("tile:STAR:$star").performTouchInput { click(center) }
        rule.waitUntil(12000) { rule.onAllNodesWithText("Cannot verify household companion. Check access, connection, and server update.").fetchSemanticsNodes().isNotEmpty() }
        capture("offline"); back(); rule.onNodeWithText("Rhythm OS").assertExists()
    }
    private fun measureFrames(action: () -> Unit) {
        val samples=java.util.Collections.synchronizedList(mutableListOf<Long>())
        val thread=android.os.HandlerThread("fixture-frame-metrics").apply { start() }
        val listener=android.view.Window.OnFrameMetricsAvailableListener { _,metrics,_ -> samples.add(metrics.getMetric(android.view.FrameMetrics.TOTAL_DURATION)) }
        onMain { it.window.addOnFrameMetricsAvailableListener(listener,android.os.Handler(thread.looper)) }
        try { action();repeat(100) { rule.mainClock.advanceTimeBy(32);Thread.sleep(32) } } finally { onMain { it.window.removeOnFrameMetricsAvailableListener(listener) };thread.quitSafely() }
        val sorted=synchronized(samples){samples.toList().sorted()}
        android.util.Log.i("RhythmPerf","Frame callback count=${sorted.size}")
        assertTrue("Expected rendered frames",sorted.size>10)
        android.util.Log.i("RhythmPerf","360 records; frames=${sorted.size}; p50ms=${sorted[sorted.size/2]/1e6}; p95ms=${sorted[(sorted.size*.95).toInt()]/1e6}; over16ms=${sorted.count { it>16666667 }}; maxms=${sorted.last()/1e6}")
    }
    @Test fun ambientBreathingAndReducedMotionEvidence() {
        rule.mainClock.autoAdvance=false
        control("large");store.still=false;onMain { it.recreate() }
        fun ready() {
            rule.waitUntil(25000) {
                rule.mainClock.advanceTimeBy(100)
                val drawn=java.util.concurrent.atomic.AtomicInteger(0)
                onMain { a -> webViews(a.window.decorView).forEach { v -> v.evaluateJavascript("Boolean(document.querySelector('[data-preview-ready=true],.launcher-authored.drawn'))") { if(it=="true")drawn.incrementAndGet() } } }
                Thread.sleep(100);drawn.get()==2
            }
            repeat(10){rule.mainClock.advanceTimeBy(32);Thread.sleep(32)}
        }
        ready();capture("ambient-a");measureFrames { };capture("ambient-b")
        store.still=true;onMain { it.recreate() };ready();capture("ambient-reduced-a")
        repeat(100){rule.mainClock.advanceTimeBy(32);Thread.sleep(32)}
        capture("ambient-reduced-b")
    }
    @Test fun largeDisconnectedAndAccessLossKeepTheSkyHonest() {
        control("large"); onMain { it.recreate() }
        rule.onNodeWithText("More sky").performClick()
        rule.waitUntil(20000) { rule.onAllNodesWithText("362 loaded · discovery").fetchSemanticsNodes().isNotEmpty() }
        capture("large-graph"); Thread.sleep(2000);rule.mainClock.advanceTimeBy(64); Thread.sleep(50); capture("large-breath")
        val map=rule.onNodeWithTag("sky-map")
        rule.onNodeWithText("Stars").performClick()
        rule.onNodeWithTag("sky-catalogue").performScrollToNode(hasText("Library 360"))
        rule.onNodeWithText("Library 360").assertExists()
        rule.onNodeWithText("Close").performClick()
        measureFrames {
            map.performTouchInput { pinch(Offset(width*.3f,height*.5f),Offset(width*.7f,height*.5f),Offset(width*.1f,height*.5f),Offset(width*.9f,height*.5f)) }
            repeat(4) { map.performTouchInput { swipe(Offset(width*.6f,height*.6f),Offset(width*.4f,height*.4f),350) };Thread.sleep(150) }
        }
        rule.onNodeWithTag("open-sky-selection").assertDoesNotExist()
        map.performTouchInput { down(center);moveBy(Offset(30f,20f));cancel() }
        rule.onNodeWithTag("open-sky-selection").assertDoesNotExist()
        capture("large-zoom")
        rule.onNodeWithContentDescription("Fit sky").performClick()
        rule.onNodeWithText("362 loaded · discovery").performClick()
        rule.onNodeWithText("The server reports more or incomplete results.",substring=true).assertExists()
        rule.onNodeWithText("Close").performClick()
        control("disconnected"); onMain { it.recreate() }
        rule.onNodeWithText("More sky").performClick()
        rule.waitUntil(20000) { rule.onAllNodesWithText("66 loaded · discovery").fetchSemanticsNodes().isNotEmpty() }
        capture("disconnected")
        store.still=true; onMain { it.recreate() }
        rule.onNodeWithText("More sky").performClick()
        rule.waitUntil(20000) { rule.onAllNodesWithText("66 loaded · discovery").fetchSemanticsNodes().isNotEmpty() }
        capture("reduced-motion")
        store.still=false
        control("denied"); onMain { it.recreate() }
        rule.onNodeWithText("More sky").performClick()
        rule.waitUntil(15000) { rule.onAllNodesWithText("0 loaded · discovery").fetchSemanticsNodes().isNotEmpty() }
        rule.onNodeWithTag("sky:second-brain:fixture:large-1").assertDoesNotExist()
        capture("access-loss")
    }

    @Test fun insightFeedbackSurvivesRetryAndIsReadBackFromHousehold() {
        fun open() {
            rule.waitUntil(15000){rule.onAllNodesWithText("A small window for your garden").fetchSemanticsNodes().isNotEmpty()}
            rule.onNodeWithTag("daily-insight").performClick()
        }
        fun tap(text: String) = rule.onNodeWithText(text).performScrollTo().performClick()
        fun saved() = runBlocking { HouseholdClient(origin).insights().single() }
        open()
        tap("Already handled")
        rule.onNodeWithText("Save feedback").assertIsNotEnabled()
        tap("Interesting")
        rule.onNodeWithTag("insight-note").performScrollTo().performTextInput("Try this in the garden")
        control("insight-feedback-error")
        tap("Save feedback")
        rule.waitUntil(10000){rule.onAllNodesWithText("Couldn’t confirm the save.",substring=true).fetchSemanticsNodes().isNotEmpty()}
        rule.onNodeWithTag("insight-note").assertTextContains("Try this in the garden")
        assertNull(saved().feedback)
        control("normal")
        tap("Save feedback")
        rule.waitUntil(10000){rule.onAllNodesWithTag("insight-detail").fetchSemanticsNodes().isEmpty()}
        assertEquals(InsightChoice.Interesting,saved().feedback)
        open()
        rule.onNodeWithText("Interesting").assertIsSelected()
        rule.onNodeWithTag("insight-note").assertTextContains("Try this in the garden")
        tap("Remind me later")
        rule.onNodeWithText("Save feedback").assertIsNotEnabled()
        tap("Tomorrow morning")
        tap("Save feedback")
        rule.waitUntil(15000){rule.onAllNodesWithText("No current insight available").fetchSemanticsNodes().isNotEmpty()}
        val snoozed=saved()
        assertEquals(InsightChoice.Later,snoozed.feedback)
        assertEquals("Try this in the garden",snoozed.feedbackNote)
        assertTrue(requireNotNull(snoozed.until)>System.currentTimeMillis())
    }
    @Test fun insightChoicesSaveTheExpectedOutcome() {
        for (choice in listOf(InsightChoice.Explore, InsightChoice.NotUseful, InsightChoice.Handled, InsightChoice.Later)) {
            control("normal"); onMain { it.recreate() }
            rule.waitUntil(15000){rule.onAllNodesWithText("A small window for your garden").fetchSemanticsNodes().isNotEmpty()}
            rule.onNodeWithTag("daily-insight").performClick()
            rule.onNodeWithText(choice.label).performScrollTo().performClick()
            if (choice == InsightChoice.Handled)
                rule.onNodeWithTag("insight-note").performScrollTo().performTextInput("Finished planting")
            if (choice == InsightChoice.Later) rule.onNodeWithText("Next week").performScrollTo().performClick()
            rule.onNodeWithText("Save feedback").performScrollTo().performClick()
            rule.waitUntil(15000){rule.onAllNodesWithText("No current insight available").fetchSemanticsNodes().isNotEmpty()}
            val saved=runBlocking { HouseholdClient(origin).insights().single() }
            assertEquals(choice,saved.feedback)
            assertEquals(choice.status,saved.status)
            assertEquals(if (choice == InsightChoice.Handled) "Finished planting" else "",saved.feedbackNote)
        }
    }
    @Test fun insightStatesAndRepeatedDetailStayTruthful() {
        val before=store.load()
        rule.waitUntil(15000){rule.onAllNodesWithText("A small window for your garden").fetchSemanticsNodes().isNotEmpty()}
        assertEquals("fixture-daily",runBlocking{HouseholdClient(origin).insights()}.single().id)
        capture("insight-home")
        repeat(3) {
            rule.onNodeWithTag("daily-insight").performTouchInput { click(center) }
            rule.onNodeWithTag("insight-detail").assertExists()
            rule.onNodeWithText("This fixture tests the real insight contract without household data.").assertExists()
            if(it==0)capture("insight-detail")
            back()
        }
        for((mode,text) in listOf("insight-slow" to "A small window for your garden",
            "insight-empty" to "No current insight available","insight-old" to "No current insight available",
            "insight-dismissed" to "No current insight available","insight-error" to "Insight unavailable · try reconnecting",
            "insight-long" to "A little time outside can make room for the next chapter in your garden plan",
            "normal" to "A small window for your garden")) {
            control(mode);onMain{it.recreate()}
            rule.waitUntil(20000){rule.onAllNodesWithText(text).fetchSemanticsNodes().isNotEmpty()}
            assertEquals(before,store.load());capture(mode)
        }
        control("denied");onMain{it.recreate()}
        rule.waitUntil(15000){rule.onAllNodesWithText("Household access unavailable").fetchSemanticsNodes().isNotEmpty()}
        rule.onNodeWithText("A small window for your garden").assertDoesNotExist()
        control("normal");onMain{it.recreate()}
        rule.waitUntil(15000){rule.onAllNodesWithText("A small window for your garden").fetchSemanticsNodes().isNotEmpty()}
    }
    @Test fun holdDragCancelAndSwipeSearchAreDistinct() {
        rule.waitUntil(15000){rule.onAllNodesWithText("A small window for your garden").fetchSemanticsNodes().isNotEmpty()}
        val before=store.load()
        val preview=rule.onNodeWithTag("tile:DASHBOARD:$board")
        val other=rule.onNodeWithTag("tile:DASHBOARD:00000000-0000-4000-8000-000000000002")
        repeat(2) {
            preview.performTouchInput { longClick(center) }
            rule.onNodeWithTag("tile-actions").assertExists();capture("tile-actions")
            rule.onNodeWithText("Add to Home",substring=true).assertDoesNotExist()
            back();assertEquals(before,store.load())
        }
        var delta=other.fetchSemanticsNode().boundsInRoot.center-preview.fetchSemanticsNode().boundsInRoot.center
        preview.performTouchInput{down(center);advanceEventTime(700);moveBy(delta);cancel()}
        rule.waitForIdle();assertEquals(before,store.load())
        preview.performTouchInput{down(center);advanceEventTime(700);moveBy(delta);up()}
        rule.waitForIdle();assertNotEquals(before,store.load());capture("reordered")
        val moved=store.load()
        onMain{it.recreate()};rule.waitForIdle();assertEquals(moved,store.load())
        repeat(3) {
            val dock=rule.onNodeWithTag("home-dock")
            dock.performTouchInput { swipe(center,Offset(center.x,-height*2f),250) }
            rule.onNodeWithTag("search-input").assertExists()
            rule.onNodeWithTag("search-input").performTextInput("Reading")
            rule.onNodeWithTag("tile:DASHBOARD:00000000-0000-4000-8000-000000000002").assertExists()
            if(it==0)capture("search")
            back()
            rule.onNodeWithTag("search-input").assertDoesNotExist()
            assertEquals(moved,store.load())
        }
        // An ordinary preview tap still opens the actual dashboard.
        preview.performTouchInput{click(center)}
        waitText("Garden overview");back()
        store.save(before);onMain{it.recreate()}
    }

}
