# TrickyMaddnessLevelHook
 
## Custom maps across platforms

A map's `.asset` bundle stores its shaders as compiled bytecode for whichever
platform it was built on. The bundle still loads on the other platforms, but
none of that bytecode runs there, so the whole map renders magenta.

The hook fixes this by rebinding the map's materials onto the game's own
shaders, which are the same ones by name and are always correct for the
platform you are actually running. Some shader features can be lost in the
process, so it only does this when it has reason to think the map is foreign.

Config entry `[Rendering] ShaderRemap` (`BepInEx/config/TrickyMaddnessLevelHook.cfg`):

- `Auto` (default) — remap only when the game is *not* running on Windows.
  Custom maps are overwhelmingly built on Windows, so Windows players keep
  their maps exactly as authored and macOS/Linux players get maps that work.
- `Always` — also remap on Windows. Use this if a map built on macOS or Linux
  renders magenta for you.
- `Never` — disable it entirely.

Map authors: nothing in a bundle records the platform it was built for, so the
setting above is a guess based on where the game is running. If you publish a
map built on macOS or Linux, say so on the download page, so Windows players
know to switch to `Always`.
