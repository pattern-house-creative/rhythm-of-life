package io.github.pattern_house_creative.rhythm.launcher

internal data class HomeCell(val tile:Tile,val index:Int,val column:Int,val row:Int) {
    val columns get()=tile.span
    val rows get()=if(tile.kind==TileKind.PREVIEW)2 else 1
}

/** Ordered packing, without rewriting the saved list. Only the starting cell
 * advances; cells under a 2x2 preview remain occupied while following icons
 * fill the neighboring upper and lower rows. We never backfill before an
 * earlier pin's starting cell, which would silently change reading order. */
internal fun packHomeCells(tiles:List<Tile>):List<HomeCell> {
    val occupied=HashSet<Int>();var cursor=0
    return tiles.mapIndexed { index,tile ->
        val w=tile.span;val h=if(tile.kind==TileKind.PREVIEW)2 else 1
        while(cursor%4+w>4 || (0 until h).any{y->(0 until w).any{x->cursor+y*4+x in occupied}})cursor++
        val cell=HomeCell(tile,index,cursor%4,cursor/4)
        repeat(h){y->repeat(w){x->occupied.add(cursor+y*4+x)}}
        cursor+=w
        cell
    }
}
