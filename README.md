# TrickyMaddnessLevelHook
 
## Custom map thumbnails

A custom map can supply its own level-select thumbnail. Drop a PNG next to the
map's `.asset` in `Maps/`, named to match it — `Maps/MyMap.asset` picks up
`Maps/MyMap.png`. The card art we author is 1024x576; the image is drawn with
its aspect ratio preserved, so other sizes work but anything far off 16:9 will
letterbox inside the card.

This is optional. Without a PNG the map keeps the level-select card's default
placeholder art.
