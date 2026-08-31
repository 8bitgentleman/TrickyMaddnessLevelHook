# LevelHook — runtime invariants

<!-- tm-build-stamp -->
> **RE'd against Tricky Madness build `21290881`** (v3.0, Unity 2022.3.62f1, `Assembly-CSharp.dll` sha256 `88636067…55efa4`).
> Findings below are true of **that** build. TM is in active development — run `python3 tm_re_baseline.py check`
> to see what a newer build moved before trusting any of it (`tm_baseline/README.md`).
<!-- /tm-build-stamp -->

The BepInEx plugin that rebuilds custom levels at runtime. Build commands,
install paths and the shell gotchas are in the **root `CLAUDE.md`**; this file is
only the rules whose violation breaks a map *silently*.

Full mechanism, IL evidence and dead ends for each §N: `../HOOK_NOTES.md`, same
numbering.

- §3 Never `Path.*` to split scene names — on macOS `\` is an ordinary filename
  char. Use `Split('\\','/')`.
- §4 Bundle shaders are DISTRUSTED on Metal|Vulkan|D3D11|D3D12 (`isSupported`
  lies for foreign bytecode) **unless** the sidecar's `platform` is native to the
  running GPU. `PruneKeywords` and the Lit fallback both need the `SafeKeywords`
  allow-list, or you get "blocky trees".
- §6 The rail collider lives on the Rail GO **itself**, and segments come from
  `BuildRailSegments` — **never** `MakerRails.GetSegments()`, which drops each
  rail's first point.
- §7 `List<T>` of user-defined nested serializables arrives **EMPTY** under
  BepInEx runtime load — hence the plain-text sidecar.
- §11 `WindVolume`'s BoxCollider is a geometry carrier ONLY, disabled at runtime.
  `AirStream`/`LowGravity` are the game's own tags — zero Hook code.
- §12 Teleport volumes: do NOT reintroduce a rotation-comparison "has facing"
  sentinel — it both false-positives and false-negatives.
- §13 Skydome follow runs in `beginCameraRendering`, NOT LateUpdate (else the
  dome edge swims). `[Skydome] Scale` is a residual multiplier, not absolute.
- §14 UV-scroll overrides key by the **original** instance name; parse only the
  first three `_`-segments.
- §15 Destructibles are **Hook-driven** (swept segment-vs-OBB). The
  `OnTriggerEnter` paths never fire — the rider tunnels thin panes. Don't revert.
- §18 `SurfaceType` picks the **layer**, it is not an inclusion test. No
  `DoubleSidedCollider` on a moving surface. `gameplay.persistentMovers` is opt-in
  **by name, never by shape**. TM has **zero** moving-platform code, so a surface
  moving *away* can never carry the rider.
- §18/§19 Classify movers and pickups by **wiring and shape, never by name** —
  community courses use arbitrary names (Snow Belline shipped 39 gems as scenery).
- §20 The `Rail.GetClosestPosition` transpiler is the **only** Hook patch that
  changes built-in levels — hence config-gated + bails out unchanged on any
  mismatch. ⚠️ Every future base-game fix gets those same two properties, or a
  Hook install silently alters the game everyone already has.
- §21 `group`/`order` lay out the level-select grid and the Hook holds **no**
  table of map names — a map declares its own section heading and global sort
  position, which is why the grid works for somebody else's levels too. Sections
  are runs of *adjacent* maps sharing a `group`, so a third group ordered between
  two maps of one group splits that group into two identically-titled sections.
- §22 `physicshz` is a **hand-set** header — a `gen_aipaths.py` regen that doesn't
  repeat it drops it silently. Chains only (50); never the per-peak courses.
  ⚠️ A heavy map's frame rate is the physics iteration count, **not** draw calls —
  the same sweep found every render ablation inside the noise floor. Don't chase
  it through rendering.
- §23 `Snowboarder.OnControllerColliderHit` respawns on **any** layer-10 contact
  with **no cooldown**. The layer-10 population that bites is SSX 3's own reset
  shell — `BuildResetVolumes` reading `ResetVolumes.json`, **not** `EmitResetZone`
  (1217 on the chain vs a peak's 9–330) — so **the "bad frame rate" bug and the
  "OOB conflicts" bug are the same bug**. Testing containment against
  `Overrides.json`'s hand-drawn OOB boxes does NOT clear a map. ⚠️ "`Respawn` picks
  the nearest marker and puts you back in the box" is **refuted** — 0 of 501
  markers sit inside a volume and `Respawn`'s teleport is correct in IL; don't
  re-derive the loop from marker placement. `RespawnLoopGuard` is a backstop; the
  fix is the level data. ⚠️ Its postfix needs `bool __runOriginal` (postfixes run
  even when a prefix returned false) and must toggle `CharacterController.enabled`
  around the teleport.
- §24 The chain's time-of-day ramp (`lighting.stops` → `LightStop_*` markers →
  `LightingRamp`) is **chain-only, and derived** — authored in the five source
  courses, emitted by `merge_chain_overrides.py`, so hand-editing the chain's
  copy is overwritten. ⚠️ Anchor elevations are **measured at import** from the
  patches; never write a Y into the JSON. ⚠️ Terrain tint sweeps `terr_`/`terrlit_`
  **only** — `LightmapOverlay_<ID>`'s `_BaseColor` is a load-bearing `(2,2,2,1)`.
  ⚠️ Skydome tint is **relative to stop 0** (the dome ships as an untinted daylight
  panorama); terrain tint is absolute. ⚠️ `LevelOverridesWindow`'s chain Capture
  must `StripStops` before comparing, or every capture forks `lighting` into
  `Overrides.extra.json` and pins a stale ramp over the merge.

- §25 The playtest start pin is **`--tm-pin` on the command line and nothing else** —
  no config key, ever. A pin is wrong for every ordinary launch, and a config key
  outlives the session that set it (next launch starts halfway down a course, cause
  unfindable); a launch flag dies with the process. `--tm-map` scopes it to one track.
  It moves `MakerSpawnPoint` BEFORE the ground-snap — that ordering is what makes the
  gate prop, `startingGate`, the rider seat and `StartLaunchRedirect` all follow from
  one write. ⚠️ It moves the START only: AI paths and the race line still begin at the
  top, so the field rides down without you. `RescuePinY` is pin-only on purpose —
  never widen `GroundSeat`'s narrow band, which is what keeps a bad respawn from
  silently teleporting 500 m.

- §26 The starting-gate offset is **computed from the field, not hardcoded** —
  lanes are `x = -6.75 + 1.5k` for `k = 1..racerEntries.Count`, so mid-field is
  `0.75N - 6` and the bar (11.94 units, one mesh, no bays) must be centred there.
  ⚠️ Read `racerEntries.Count` **only when `GetMode() == Race`**: the other two
  modes spawn the player alone at the mode-locked slot 3 and leave the list
  holding the *previous* race's field, so an unguarded read shifts the bar
  sideways on a freestyle run taken after a race. ⚠️ A full field is **9, not 8**
  (stock `SelectLevel` adds the player and *then* `Min(8, pool.Count)`), which
  only shows up on a roster of ≥ 10 — i.e. never in TM, always with CharacterHook.

## Version drift

`MenuManager.SelectLevel(LevelEntry)` was renamed to **`LoadScene(LevelEntry)`**
and `LevelEntry` became a **struct**, so the old community Harmony patch silently
fails to bind → custom levels load to a black screen. Patch `LoadScene`, with a
`ref LevelEntry` prefix. If a future update breaks it again, disassemble
`Assembly-CSharp.dll` (`monodis`/`ikdasm`) and re-check `MenuManager`'s load
method + `LevelEntry`'s shape against `Plugin.cs`.

⚠️ Don't start that by hand. `python3 tm_re_baseline.py check` (from the repo root)
resolves **every** `[HarmonyPatch]`/`AccessTools` target in all five mods against the
installed game and names the ones that no longer bind, plus what else in the build
moved. `tm_baseline/README.md`.
