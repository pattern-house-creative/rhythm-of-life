package io.github.pattern_house_creative.rhythm.launcher

internal data class SkyCamera(val x: Float=410f,val y: Float=280f,val zoom: Float=1f)

// A constellation is not a geographic map. Its stable graph coordinates use the
// available portrait space; zoom and pan must invert each axis independently.
internal class SkyViewport(width: Float,height: Float,spanX: Float=820f,spanY: Float=560f) {
    private val w=width.coerceAtLeast(1f)
    private val h=height.coerceAtLeast(1f)
    private val sx=w/spanX.coerceAtLeast(1f)
    private val sy=h/spanY.coerceAtLeast(1f)
    fun x(value: Float,camera: SkyCamera)=(value-camera.x)*sx*camera.zoom+w/2
    fun y(value: Float,camera: SkyCamera)=(value-camera.y)*sy*camera.zoom+h/2
    fun transform(camera: SkyCamera,cx: Float,cy: Float,dx: Float,dy: Float,factor: Float): SkyCamera {
        val z=(camera.zoom*factor).coerceIn(.75f,8f)
        return SkyCamera(
            (camera.x+(cx-w/2)/(sx*camera.zoom)-(cx+dx-w/2)/(sx*z)).coerceIn(-100f,920f),
            (camera.y+(cy-h/2)/(sy*camera.zoom)-(cy+dy-h/2)/(sy*z)).coerceIn(-80f,640f),z)
    }
}

// Match the motion study's 58vh reveal while reserving native controls and one
// usable pin row on shorter windows. No system/gesture inset is consumed by it.
internal fun skyRevealDp(windowHeight: Float)=minOf(windowHeight*.58f,570f,(windowHeight-400f).coerceAtLeast(0f))
