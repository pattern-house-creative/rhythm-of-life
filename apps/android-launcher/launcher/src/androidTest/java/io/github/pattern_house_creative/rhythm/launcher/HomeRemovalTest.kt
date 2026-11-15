package io.github.pattern_house_creative.rhythm.launcher

import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.*
import org.junit.Assert.*

class HomeRemovalTest {
    @get:Rule val rule=createAndroidComposeRule<MainActivity>()
    private val context get()=InstrumentationRegistry.getInstrumentation().targetContext
    private lateinit var store:LauncherStore
    private lateinit var saved:HomeLayout
    private var origin=""
    private var priorMarker:String?=null
    private var priorRemovals:String?=null
    @Before fun prepare(){assertTrue(android.os.Build.MODEL.contains("sdk"));store=LauncherStore(context);rule.waitUntil(15000){store.load().tiles.isNotEmpty()};saved=store.load();origin=store.origin
        val prefs=context.getSharedPreferences("phone-home-v1",0)
        priorMarker=prefs.getString("preview-setup-origin",null);priorRemovals=prefs.getString("removed-previews-v1",null)
    }
    @After fun restore(){if(::saved.isInitialized){store.save(saved);store.origin=origin
        context.getSharedPreferences("phone-home-v1",0).edit().apply{
            if(priorMarker==null)remove("preview-setup-origin")else putString("preview-setup-origin",priorMarker)
            if(priorRemovals==null)remove("removed-previews-v1")else putString("removed-previews-v1",priorRemovals)
        }.apply()
    }}
    @Test fun editHomeExposesRemoveForAppAndPreviewAndUndoRestoresOnlyRemovedItem(){
        val app=saved.tiles.first{it.kind==TileKind.APP}
        val preview=Tile(TileKind.PREVIEW,"synthetic-removal-preview")
        val pins=HomeLayout(listOf(app,preview)+saved.tiles.filter{it.key!=app.key}.take(2),saved.dock)
        store.save(pins);rule.activityRule.scenario.recreate()
        rule.onNodeWithContentDescription("Settings").performClick()
        rule.onNodeWithText("Edit Home").performClick()
        capture("edit-home")
        rule.onNodeWithTag("remove-home:${app.key}").assertIsDisplayed().performClick()
        assertFalse(store.load().tiles.contains(app));assertEquals(pins.dock,store.load().dock)
        rule.onNodeWithText("Undo").performClick();assertEquals(pins,store.load())
        rule.onNodeWithTag("remove-home:${preview.key}").assertIsDisplayed().performClick()
        assertEquals(pins.tiles.filterNot{it.key==preview.key},store.load().tiles)
        assertEquals(pins.dock,store.load().dock)
        capture("removed-preview-undo")
        rule.activityRule.scenario.recreate()
        assertEquals(pins.tiles.filterNot{it.key==preview.key},LauncherStore(context).load().tiles)
    }
    @Test fun holdReleaseMenuAndDirectAccessibilityRemovalDoNotDeleteOrUndock(){
        val app=saved.tiles.first{it.kind==TileKind.APP}
        rule.onNodeWithTag("tile:${app.key}").performTouchInput{longClick()}
        rule.onNodeWithText("−  Remove from Home").assertIsDisplayed().performClick()
        assertFalse(store.load().tiles.any{it.key==app.key});assertEquals(saved.dock,store.load().dock)
        val component=android.content.ComponentName.unflattenFromString(app.target)!!
        assertNotNull(context.packageManager.getActivityInfo(component,0))
        rule.onNodeWithText("Undo").performClick()
        val action=rule.onNodeWithTag("tile:${app.key}").fetchSemanticsNode().config[SemanticsActions.CustomActions].first{it.label=="Remove from Home"}
        rule.runOnIdle{assertTrue(action.action())}
        assertFalse(store.load().tiles.any{it.key==app.key});assertEquals(saved.dock,store.load().dock)
    }
    @Test fun removedPreviewRemainsRemovedWhenOnboardingOrReconnectRunsAgain(){
        val preview="synthetic-persistently-removed-preview"
        store.origin="https://10.0.2.2:9443"
        store.recordPreviewRemoval(preview)
        val layout=store.load().copy(tiles=store.load().tiles.filterNot{it.target==preview})
        store.save(layout)
        store.origin="https://127.0.0.1:9443"
        store.initialPreviews(store.load(),listOf("synthetic-other-household-preview"))
        store.origin="https://10.0.2.2:9443"
        val loaded=LauncherStore(context)
        val result=loaded.initialPreviews(loaded.load(),listOf(preview,"synthetic-other-preview"))
        assertFalse(result.tiles.any{it.target==preview})
        assertEquals(layout.dock,result.dock)
        loaded.allowPreview(preview)
        loaded.save(result.showPreview(preview))
        assertTrue(LauncherStore(context).load().tiles.any{it.target==preview})
    }
    private fun capture(name:String) {
        assertTrue(store.origin.isBlank() || store.origin in listOf("https://10.0.2.2:9443","https://127.0.0.1:9443"))
        val values=android.content.ContentValues().apply{
            val prefix=InstrumentationRegistry.getArguments().getString("capturePrefix") ?: "v012"
            put(android.provider.MediaStore.Images.Media.DISPLAY_NAME,"$prefix-$name.png")
            put(android.provider.MediaStore.Images.Media.MIME_TYPE,"image/png")
            put(android.provider.MediaStore.Images.Media.RELATIVE_PATH,"Pictures/Rhythm012")
        }
        val uri=context.contentResolver.insert(android.provider.MediaStore.Images.Media.EXTERNAL_CONTENT_URI,values)!!
        context.contentResolver.openOutputStream(uri)!!.use{InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot().compress(android.graphics.Bitmap.CompressFormat.PNG,100,it)}
    }
}
