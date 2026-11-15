package io.github.pattern_house_creative.rhythm.launcher
import org.junit.Test
import org.junit.Assert.*
class DrawerReleaseTest {
    @Test fun deliberateShortSwipeCommitsButSmallPullAndJitterReturnToPreviousTarget(){
        assertFalse(drawerReleaseTarget(.1f,0f,50f,2f,false))
        assertTrue(drawerReleaseTarget(.9f,0f,-50f,2f,true))
        assertTrue(drawerReleaseTarget(.15f,0f,120f,2f,false))
        assertFalse(drawerReleaseTarget(.85f,0f,-120f,2f,true))
        assertFalse(drawerReleaseTarget(.1f,-2000f,2f,2f,false))
        assertTrue(drawerReleaseTarget(.9f,2000f,-2f,2f,true))
    }
    @Test fun flingAndMidflightReversalRespectDirectionDensityAndEndpoints(){
        assertTrue(drawerReleaseTarget(.2f,-1600f,60f,2.625f,false))
        assertFalse(drawerReleaseTarget(.8f,1600f,-60f,2.625f,true))
        assertTrue(drawerReleaseTarget(.4f,0f,180f,2.625f,false))
        assertFalse(drawerReleaseTarget(.6f,0f,-180f,2.625f,true))
        assertTrue(drawerReleaseTarget(1f,0f,0f,2f,false))
        assertFalse(drawerReleaseTarget(0f,0f,0f,2f,true))
    }
}
