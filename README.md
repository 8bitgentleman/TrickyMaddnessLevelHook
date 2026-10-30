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

## Building from source

Needs Mono's `mcs` (`brew install mono` on Mac) and an installed copy of the
game with BepInEx 5.4.x — the Hook compiles against the game's own assemblies,
which are not in this repo and never will be.

    ./build.sh --mac        # native Mac build (Steam's default install path)
    ./build.sh              # Windows build under CrossOver

Anywhere else, point it at your install: `GAME=/path/to/Tricky\ Madness ./build.sh`
(or set `MANAGED`, `CORE`, `MAKER` individually). The output DLL is
platform-independent; copy it into `BepInEx/plugins/`.

It also references `TrickyMaddnessLevelMaker.dll` (GlitcherOG's LevelMaker,
which every custom map already needs installed) from your `BepInEx/plugins/`.

## Making a map the Hook understands

A map is `<name>.asset` (a Unity scene AssetBundle) in the game's `Maps/`
folder, plus two optional files next to it with the same stem:

- `<name>.png` — the 1024×576 level-select thumbnail.
- `<name>.aipaths.txt` — the **sidecar**: per-map settings the game has no slot
  for (world scale, gravity, physics rate, platform, level-select group/order,
  display name…) and the AI paths. **The format is specified in full in
  `gen_aipaths.py`'s docstring**, which also generates it from a bundle.

Build one bundle per platform (`mac` and `windows`) and declare it with the
`platform` header — a foreign-platform bundle loads, but its shaders get
swapped for the game's own and that swap is lossy.

## Contributing

Pull requests are welcome. One rule: **only builds from this repo ship under
the Hook's plugin GUID.** BepInEx loads a single plugin per GUID, so a fork
released under the same one means players can't tell which Hook they have and
log reports stop meaning anything. Fork for your own experiments freely, but
either send the change upstream or give your build its own GUID and name.

- Bump `PLUGIN_VERSION` in `PluginInfo.cs` on any change that alters how a map
  plays — it is the version a player's log reports.
- Stay backward-compatible: every map already shipped must load unchanged.
  New sidecar headers must default to the old behaviour when absent, and must
  be added to `SidecarHeaders` in `Plugin.cs` (a missing one fails silently).
- A patch that changes the game's **own** courses must be off by default and
  bail out on any IL mismatch.
- `CLAUDE.md` lists the rules whose violation breaks maps silently — read it
  before touching the area it covers. Some comments and notes reference docs
  from the private conversion workspace this came out of (`HOOK_NOTES.md`,
  `ssx/…`); the rule stated next to them is the part that matters.

## License

The Hook began as a fork of GlitcherOG's
[TrickyMaddnessLevelHook](https://github.com/GlitcherOG/TrickyMaddnessLevelHook),
which has no license. **Licensing is pending GlitcherOG's agreement**; until a
LICENSE file lands here, no license is granted.
