# LevelHook — running custom maps in Tricky Madness

The BepInEx/Harmony mod that lets custom map AssetBundles (converted from other
games' levels) load and run correctly in Tricky Madness. Full feature histories,
IL evidence and dead ends: [`../HOOK_NOTES.md`](../HOOK_NOTES.md). Runtime
invariants (the things that break silently if violated): [`CLAUDE.md`](CLAUDE.md).

Commit tag: **`HOOK`**. Build: `build.sh --mac` (see the root
[`CLAUDE.md`](../CLAUDE.md) § Building — always `--mac`, the opposite default
from `CharacterHook-src`/`AIMod-src`). Does not auto-deploy; copy the DLL to
`BepInEx/plugins/` by hand.

## Features

**Map loading & compatibility**
- Loads custom map AssetBundles into TM's menu/level flow, replacing a vanilla
  hook that a game update broke.
- Works on both the native Mac build and the Windows/CrossOver build.
- Reads a sidecar settings file per map for things the game itself has no slot
  for — world scale, gravity, physics rate, gate direction, rail grab distance,
  display name, source game, level-select grouping.
- Debug auto-load of a named map straight to boot, for testing without menu
  navigation.

**Visuals & shading**
- Auto-fixes the pink/blocky shader problem caused by materials compiled for
  the wrong graphics API (Mac vs Windows) — the reason a Windows-built bundle
  still loads cleanly on the native Mac build.
- Skybox handling: solid-color fallback when there's no real sky, plus full
  custom multi-piece sky domes (including a layered variant for non-dome
  shapes).
- Animated/scrolling textures (waterfalls, banners, screens) play at the
  correct authored speed.
- A day/night-style lighting ramp for long descents, so lighting isn't one
  flat bake for the whole run.

**Gameplay & physics**
- Builds working grind rails for custom maps (they ship with no collision by
  default).
- Simulates wind zones, teleport zones, and launch pads placed in the level
  editor.
- Wires up breakable props/glass, moving surfaces (kickers, pinball bumpers),
  and an avalanche hazard so they actually interact with the rider.
- Optional fix for a base-game rail-grinding physics bug (off by default so it
  doesn't touch stock levels unless asked).
- Optional per-map gravity/physics-rate and world-scale adjustment for maps
  built at different sizes than the original game.
- Guards against respawn loops and riders getting stuck in the air; fixes
  starting-gate alignment for races with large fields.
- Optional "jump to a spot on the course" playtest shortcut.

**AI**
- Builds AI racing paths for custom maps from sidecar data (the game's own
  path data doesn't survive being loaded as a mod), with an optional
  multi-route mode so AI riders don't all follow one line.

**Level-select / dev tooling**
- Custom per-map thumbnail images.
- Optional grid-style level-select with per-source-game section headers,
  instead of one long scrolling row.
- Debug toggles (shader logging, rail-collider checks, telemetry export, perf
  harness) — all off by default.
