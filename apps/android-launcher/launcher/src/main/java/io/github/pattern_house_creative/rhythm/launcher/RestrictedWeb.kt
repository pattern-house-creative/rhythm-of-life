package io.github.pattern_house_creative.rhythm.launcher

import android.net.http.SslError
import android.webkit.*
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import kotlinx.coroutines.*
import java.io.ByteArrayInputStream

// No JS bridge, SSL bypass, native intents or cross-origin web navigation.
@Composable
internal fun RestrictedWeb(origin: String, url: String, modifier: Modifier, active:Boolean=true, accessLost: () -> Unit) {
    val lost by rememberUpdatedState(accessLost)
    var attempt by remember(url) { mutableIntStateOf(0) }
    var ready by remember(url, attempt) { mutableStateOf(false) }
    var loading by remember(url, attempt) { mutableStateOf(true) }
    var error by remember(url, attempt) { mutableStateOf<String?>(null) }
    val companion = url != origin
    LaunchedEffect(url, attempt) {
        if (companion) {
            try {
                val requiredBuiltin = if (java.net.URI(url).fragment in listOf("builtin=glucose", "rhythm-launcher=builtin:glucose")) "glucose" else null
                if (!HouseholdClient(origin).companionReady(requiredBuiltin)) {
                    error = "Household companion update required"; loading = false; return@LaunchedEffect
                }
            } catch (e: CancellationException) { throw e }
            catch (_: Exception) {
                error = "Cannot verify household companion. Check access, connection, and server update."
                loading = false; return@LaunchedEffect
            }
        }
        ready = true
    }
    LaunchedEffect(ready, loading, attempt) {
        if (ready && loading) {
            delay(25_000)
            error = "Page could not start. Check access and update Android System WebView."; loading = false
        }
    }
    Box(modifier) {
        if (ready && error == null) key(url, attempt) {
            val alive = remember { java.util.concurrent.atomic.AtomicBoolean(true) }
            AndroidView(factory = { viewContext -> WebView(viewContext).apply {
                setBackgroundColor(android.graphics.Color.TRANSPARENT)
                settings.javaScriptEnabled = true; settings.domStorageEnabled = true
                // Honor the companion's device-width meta viewport for media queries,
                // including the 170dp card viewport and the phone reader's sheet layout.
                settings.useWideViewPort = true; settings.loadWithOverviewMode = true
                settings.allowFileAccess = false; settings.allowContentAccess = false
                settings.cacheMode = WebSettings.LOAD_NO_CACHE
                settings.mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
                settings.javaScriptCanOpenWindowsAutomatically = false
                CookieManager.getInstance().setAcceptThirdPartyCookies(this, false)
                webChromeClient = object : WebChromeClient() {
                    override fun onPermissionRequest(request: PermissionRequest) { request.deny() }
                }
                webViewClient = object : WebViewClient() {
                    override fun onPageCommitVisible(view: WebView, destination: String) { if (!companion) loading = false }
                    private fun confirmRendered(view: WebView) {
                        if (!alive.get() || !loading || error != null) return
                        // Fixed readiness query only: no source content, bridge or privilege.
                        view.evaluateJavascript("Boolean(document.getElementById('root')?.firstElementChild)") {
                            if (!alive.get()) return@evaluateJavascript
                            if (it == "true") loading = false
                            else view.postDelayed({ confirmRendered(view) }, 250)
                        }
                    }
                    override fun onPageFinished(view: WebView, destination: String) {
                        if (companion && sameOrigin(origin, destination)) confirmRendered(view)
                    }
                    override fun onReceivedError(view: WebView, request: WebResourceRequest, detail: WebResourceError) {
                        if (request.isForMainFrame) { error = "Page unavailable. Check your connection and try again."; loading = false }
                    }
                    override fun onReceivedSslError(view: WebView, handler: SslErrorHandler, detail: SslError) {
                        handler.cancel(); error = "Secure connection could not be verified"; loading = false
                    }
                    override fun onReceivedHttpError(view: WebView, request: WebResourceRequest, response: WebResourceResponse) {
                        if (response.statusCode == 401 || response.statusCode == 403) {
                            error = "Household access unavailable. Open household sign-in in settings."; loading = false; lost()
                        } else if (request.isForMainFrame) {
                            error = "Page unavailable (HTTP ${response.statusCode})"; loading = false
                        }
                    }
                    override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                        if (sameOrigin(origin, request.url.toString())) return false
                        if (request.isForMainFrame) {
                            error = "This sign-in or link leaves your household. External navigation is not supported."; loading = false
                        }
                        return true
                    }
                    override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
                        val destination = request.url.toString()
                        return if (sameOrigin(origin, destination) || destination == "about:blank") null
                        else WebResourceResponse("text/plain", "utf-8", ByteArrayInputStream(ByteArray(0)))
                    }
                }
                loadUrl(url)
                if(!active){visibility=android.view.View.INVISIBLE;onPause()}
            } }, update={view->
                val visibility=if(active)android.view.View.VISIBLE else android.view.View.INVISIBLE
                if(view.visibility!=visibility){view.visibility=visibility;if(active)view.onResume()else view.onPause()}
            }, modifier = Modifier.fillMaxSize().testTag("household-web"), onReset = null,
                onRelease = { view -> alive.set(false); view.stopLoading(); view.loadUrl("about:blank"); view.clearCache(true); view.destroy() })
        }
        if (loading || error != null) Column(Modifier.fillMaxSize().background(Color(0xff101b20)).padding(12.dp),
            horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
            if (loading) {
                CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp)
                Text("Opening household…", fontSize = 12.sp, modifier = Modifier.padding(top = 12.dp))
            } else {
                Text(error.orEmpty(), fontSize = 12.sp)
                TextButton(onClick = { attempt++ }) { Text("Try again") }
            }
        }
    }
}
