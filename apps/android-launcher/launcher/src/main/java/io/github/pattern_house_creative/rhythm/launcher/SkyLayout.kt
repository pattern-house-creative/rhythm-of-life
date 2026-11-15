package io.github.pattern_house_creative.rhythm.launcher

import kotlin.math.*

internal data class SkyPoint(val item: RhythmItem, val x: Float, val y: Float, val degree: Int, val group: Int)
internal data class SkyGraph(val points: List<SkyPoint>, val edges: List<Pair<Int, Int>>)

// Settle distances in portrait space BEFORE projecting the normalized canvas.
// A landscape force layout stretched vertically makes a narrow oval/hairball.
private const val PORTRAIT_METRIC = 3.1f

/** Graph-distance neighborhoods with their own room and degree-balanced springs.
 * Visual neighborhoods are not source categories or additional relationships.
 * Every valid input edge survives. Retained anchors prevent refresh reshuffling. */
internal fun layoutSky(items: List<RhythmItem>, links: List<SkyLink>, previous: Map<String, SkyPoint> = emptyMap()): SkyGraph {
    val nodes=items.distinctBy { it.id }.sortedBy { it.id }
    if(nodes.isEmpty())return SkyGraph(emptyList(),emptyList())
    val index=nodes.mapIndexed { i,n -> n.id to i }.toMap()
    val edges=links.mapNotNull { e -> index[e.from]?.let { a -> index[e.to]?.let { b ->
        if(a==b)null else minOf(a,b) to maxOf(a,b)
    } } }.distinct().sortedWith(compareBy<Pair<Int,Int>> { it.first }.thenBy { it.second })
    val adjacent=Array(nodes.size){mutableListOf<Int>()}
    edges.forEach { (a,b)->adjacent[a].add(b);adjacent[b].add(a) }
    val components=mutableListOf<List<Int>>();val seen=BooleanArray(nodes.size)
    nodes.indices.forEach { start -> if(!seen[start]) {
        val queue=ArrayDeque<Int>();val members=mutableListOf<Int>();queue.add(start);seen[start]=true
        while(queue.isNotEmpty()) { val a=queue.removeFirst();members.add(a)
            adjacent[a].forEach { b->if(!seen[b]) { seen[b]=true;queue.add(b) } }
        }
        components.add(members)
    } }
    val neighborhoods=mutableListOf<List<Int>>()
    val capacity=(sqrt(nodes.size.toFloat())*1.25f).toInt().coerceIn(7,18)
    fun partition(members:List<Int>) {
        if(members.size<=capacity) { neighborhoods.add(members);return }
        val allowed=members.toHashSet()
        fun distances(start:Int):IntArray {
            val distance=IntArray(nodes.size){nodes.size+1};distance[start]=0
            val queue=ArrayDeque<Int>();queue.add(start)
            while(queue.isNotEmpty()) { val a=queue.removeFirst()
                adjacent[a].forEach { b->if(b in allowed && distance[b]>distance[a]+1) {
                    distance[b]=distance[a]+1;queue.add(b)
                } }
            }
            return distance
        }
        val hub=members.sortedWith(compareByDescending<Int>{adjacent[it].size}.thenBy{nodes[it].id}).first()
        val fromHub=distances(hub)
        val a=members.sortedWith(compareByDescending<Int>{fromHub[it]}.thenBy{nodes[it].id}).first()
        val da=distances(a)
        val b=members.sortedWith(compareByDescending<Int>{da[it]}.thenBy{nodes[it].id}).first()
        val db=distances(b)
        // Balanced cuts also distribute a hub's leaf-heavy graph. They do not
        // claim that the resulting visual groups are separate source projects.
        val ordered=members.sortedWith(compareBy<Int>{da[it]-db[it]}.thenBy{nodes[it].id.hashCode()}.thenBy{nodes[it].id})
        partition(ordered.take(ordered.size/2));partition(ordered.drop(ordered.size/2))
    }
    components.filter { it.size>1 }.sortedWith(compareByDescending<List<Int>>{it.size}.thenBy{nodes[it.min()].id}).forEach(::partition)
    val loose=components.filter{it.size==1}.flatten()
    loose.sortedBy{nodes[it].id.hashCode()}.chunked(capacity).forEach { neighborhoods.add(it) }
    val x=FloatArray(nodes.size);val y=FloatArray(nodes.size);val group=IntArray(nodes.size){-1}
    data class Area(val left:Float,val top:Float,val width:Float,val height:Float)
    fun place(groups:List<Int>,area:Area) {
        if(groups.size>1) {
            val total=groups.sumOf{neighborhoods[it].size};var sum=0;var split=1
            for(i in 1 until groups.size) { sum+=neighborhoods[groups[i-1]].size;split=i;if(sum>=total/2)break }
            val share=groups.take(split).sumOf{neighborhoods[it].size}.toFloat()/total
            val seed=nodes[neighborhoods[groups.first()].first()].id.hashCode() xor groups.size
            // Unequal, reproducible room boundaries avoid rows of equal circles.
            val ratio=(share+(kotlin.random.Random(seed).nextFloat()-.5f)*.12f)
                .coerceIn(share*.82f,1-(1-share)*.82f)
            if(area.width>area.height) {
                place(groups.take(split),area.copy(width=area.width*ratio))
                place(groups.drop(split),Area(area.left+area.width*ratio,area.top,area.width*(1-ratio),area.height))
            } else {
                place(groups.take(split),area.copy(height=area.height*ratio))
                place(groups.drop(split),Area(area.left,area.top+area.height*ratio,area.width,area.height*(1-ratio)))
            }
            return
        }
        val g=groups.single();val members=neighborhoods[g].sortedWith(compareByDescending<Int>{adjacent[it].size}.thenBy{nodes[it].id})
        val random=kotlin.random.Random(nodes[members.first()].id.hashCode() xor 0x5ea431)
        val rotation=random.nextFloat()*2*PI
        val jitter=if(nodes.size<20).1f else .3f
        val reach=if(nodes.size<20).44f else .42f
        val cx=area.left+area.width*(.5f+(random.nextFloat()-.5f)*jitter)
        val cy=area.top+area.height*(.5f+(random.nextFloat()-.5f)*jitter)
        members.forEachIndexed { slot,i ->
            val radius=sqrt((slot+.5f)/members.size)
            val angle=rotation+slot*2.399963229728653+(random.nextFloat()-.5f)*.3
            x[i]=cx+cos(angle).toFloat()*radius*area.width*reach
            y[i]=cy+sin(angle).toFloat()*radius*area.height*reach
            if(adjacent[i].isNotEmpty())group[i]=g
        }
    }
    place(neighborhoods.indices.toList(),Area(32f,28f*PORTRAIT_METRIC,756f,504f*PORTRAIT_METRIC))
    val ax=x.copyOf();val ay=y.copyOf()
    nodes.forEachIndexed { i,n -> previous[n.id]?.let { x[i]=it.x;y[i]=it.y*PORTRAIT_METRIC } }
    val fixed=BooleanArray(nodes.size){nodes[it].id in previous}
    val spacing=sqrt(756f*504f*PORTRAIT_METRIC/nodes.size)*.56f
    repeat(70) {
        val dx=FloatArray(nodes.size);val dy=FloatArray(nodes.size)
        edges.forEach { (a,b) ->
            val vx=x[b]-x[a];val vy=y[b]-y[a];val d=hypot(vx,vy).coerceAtLeast(.01f)
            // Cross-neighborhood links retain reach instead of pulling to one
            // short distance. Hub degree cannot overpower each area's anchors.
            val rest=if(group[a]==group[b])spacing*1.6f else hypot(ax[b]-ax[a],ay[b]-ay[a])
            val f=(d-rest)*.055f/maxOf(adjacent[a].size,adjacent[b].size,1)
            dx[a]+=vx/d*f;dy[a]+=vy/d*f;dx[b]-=vx/d*f;dy[b]-=vy/d*f
        }
        val cell=maxOf(8f,spacing*1.3f)
        val buckets=HashMap<Pair<Int,Int>,MutableList<Int>>()
        nodes.indices.forEach { i->buckets.getOrPut(floor(x[i]/cell).toInt() to floor(y[i]/cell).toInt()){mutableListOf()}.add(i) }
        nodes.indices.forEach { a ->
            val gx=floor(x[a]/cell).toInt();val gy=floor(y[a]/cell).toInt()
            for(xx in gx-1..gx+1)for(yy in gy-1..gy+1)buckets[xx to yy]?.forEach { b->if(b>a) {
                var vx=x[b]-x[a];var vy=y[b]-y[a]
                if(abs(vx)+abs(vy)<.001f){vx=.1f;vy=.07f}
                val d=hypot(vx,vy);val f=maxOf(0f,spacing*1.15f-d)*.28f
                dx[a]-=vx/d*f;dy[a]-=vy/d*f;dx[b]+=vx/d*f;dy[b]+=vy/d*f
            } }
        }
        nodes.indices.forEach { i->if(!fixed[i]) {
            x[i]=(x[i]+dx[i]+(ax[i]-x[i])*.09f).coerceIn(24f,796f)
            y[i]=(y[i]+dy[i]+(ay[i]-y[i])*.09f).coerceIn(20f*PORTRAIT_METRIC,540f*PORTRAIT_METRIC)
        } }
    }
    return SkyGraph(nodes.mapIndexed { i,n ->
        val old=previous[n.id]
        SkyPoint(n,old?.x?:x[i],old?.y?:y[i]/PORTRAIT_METRIC,adjacent[i].size,group[i])
    },edges)
}

/** Overview lines are a subset of real edges. Bound incidence/length to avoid
 * a hub painting a screen-wide mesh. Focus always restores its loaded edges. */
internal fun skyOverviewEdges(graph:SkyGraph):Set<Int> {
    if(graph.points.size<=18 && graph.edges.size<=24)return graph.edges.indices.toSet()
    val degree=IntArray(graph.points.size);val result=linkedSetOf<Int>()
    graph.edges.indices.sortedWith(compareBy<Int> { i->
        val(a,b)=graph.edges[i];val p=graph.points[a];val q=graph.points[b]
        hypot(p.x-q.x,(p.y-q.y)*PORTRAIT_METRIC)
    }.thenBy{it}).forEach { i->
        val(a,b)=graph.edges[i];val p=graph.points[a];val q=graph.points[b]
        val distance=hypot(p.x-q.x,(p.y-q.y)*PORTRAIT_METRIC)
        if(degree[a]<2 && degree[b]<2 && distance<430f) { result.add(i);degree[a]++;degree[b]++ }
    }
    return result
}

internal fun ambientSkyPosition(point:SkyPoint,width:Float,height:Float)=
    (width*(.035f+.93f*point.x/820f)) to (height*(.025f+.90f*point.y/560f))

internal fun skyVisibleEdges(graph:SkyGraph,overview:Set<Int>,selectedId:String?):Set<Int> {
    if(selectedId==null)return overview
    val selected=graph.points.indexOfFirst{it.item.id==selectedId}
    return overview+graph.edges.indices.filter { val(a,b)=graph.edges[it];a==selected || b==selected }
}

// Shared drift coordinates for label/hit behavior. Reduced motion is the anchor.
internal fun skyDrift(point: SkyPoint, seconds: Float, reduced: Boolean): Pair<Float,Float> {
    if (reduced) return point.x to point.y
    val phase=(point.item.id.hashCode().toLong() and 0xffff).toFloat()*.017f
    return point.x + sin(seconds/5.2f+phase)*2f to point.y + cos(seconds/6.8f+phase)*2.4f
}
