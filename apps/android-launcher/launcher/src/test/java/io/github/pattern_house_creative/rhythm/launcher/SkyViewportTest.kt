package io.github.pattern_house_creative.rhythm.launcher

import org.junit.Test
import org.junit.Assert.*

class SkyViewportTest {
    @Test fun revealMatchesPhoneViewportButReservesForegroundOnShortWindows() {
        assertEquals(515f,skyRevealDp(915f),.01f)
        assertEquals(12f,skyRevealDp(412f),.01f)
        assertEquals(0f,skyRevealDp(240f),.01f)
        assertEquals(570f,skyRevealDp(1400f),.01f)
    }
    @Test fun fitUsesActualGraphExtentsWithStableCameraCoordinates() {
        val v=SkyViewport(364f,480f,400f,280f);val c=SkyCamera(420f,260f)
        assertEquals(0f,v.x(220f,c),.001f);assertEquals(364f,v.x(620f,c),.001f)
        assertEquals(0f,v.y(120f,c),.001f);assertEquals(480f,v.y(400f,c),.001f)
    }
    @Test fun portraitGraphUsesAvailableHeight() {
        val v=SkyViewport(364f,480f);val c=SkyCamera()
        assertEquals(0f,v.x(0f,c),.001f);assertEquals(364f,v.x(820f,c),.001f)
        assertEquals(0f,v.y(0f,c),.001f);assertEquals(480f,v.y(560f,c),.001f)
    }
    @Test fun pinchKeepsTouchedWorldPointAnchoredAcrossAspectRatios() {
        for ((w,h) in listOf(364f to 480f,800f to 280f)) {
            val v=SkyViewport(w,h);val a=SkyCamera();val px=v.x(350f,a);val py=v.y(210f,a)
            val b=v.transform(a,px,py,0f,0f,1.8f)
            assertEquals(px,v.x(350f,b),.001f);assertEquals(py,v.y(210f,b),.001f)
        }
    }
    @Test fun panMovesBothAxesByTheFingerDeltaAndZoomIsBounded() {
        val v=SkyViewport(364f,480f);val a=SkyCamera();val b=v.transform(a,182f,240f,30f,-40f,1f)
        assertEquals(30f,v.x(410f,b)-v.x(410f,a),.001f)
        assertEquals(-40f,v.y(280f,b)-v.y(280f,a),.001f)
        assertEquals(8f,v.transform(a,182f,240f,0f,0f,100f).zoom,0f)
        assertEquals(.75f,v.transform(a,182f,240f,0f,0f,.01f).zoom,0f)
    }
}
