using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace TrickyMaddnessLevelHook
{
    public partial class Plugin
    {
        // What LoadLevelManager's blocks share: the three maker objects found up
        // front, the LevelManager once it is instantiated, and the race line once
        // built (the respawn markers are sampled off it). Nothing else crosses blocks.
        internal sealed class BuildContext
        {
            public MakerLevelData Maker;
            public MakerSpawnPoint SpawnPoint;
            public MakerRespawnPoints RespawnPoints;
            public LevelManager LevelManager;
            public RaceLine RaceLine;
        }

        public static void LoadLevelManager()
        {
            LoadClock.Mark("Unity scene load (bundle -> MakerLevelData.Awake)");
            var ctx = FindMakers();
            var levelManagerMaker = ctx.Maker;

            // Order: pin -> ground-snap -> gate -> launch redirect. The pin moves the start before anything reads it (§25).
            ApplyTestPin(ctx);

            SnapStartMarkers(ctx);
            ApplyLevelEntry(ctx);

            PlaceStartingGate(ctx);

            AddStartLaunchRedirect(ctx);

            //Insert Level Manager
            var LevelManagerObject = Resources.Load<GameObject>("game/levelmanager");
            var gameObject = Instantiate(LevelManagerObject, ctx.Maker.transform);

            var levelManager = gameObject.GetComponent<LevelManager>();
            ctx.LevelManager = levelManager;

            //Set Level Manager Data Points
            SetGameField(fStartingGate, "LevelManager.startingGate", levelManager, ctx.SpawnPoint.transform);
            SetGameField(fRespawnPointsParent, "LevelManager.respawnPointsParent", levelManager, ctx.RespawnPoints.transform);
            SetGameField(fSnowflakes, "LevelManager.snowflakes", levelManager, new GameObject("Temp")); //FIX to get all snowflakes and apply

            //Create UI Hooks
            var TempObject = GetGameField(fSpeedLabel, "LevelManager.speedLabel", levelManager) as TextMeshProUGUI;
            var CanvasObjects = FindObjectsByType(typeof(MakerCanvas), FindObjectsSortMode.None) as MakerCanvas[];
            for (int i = 0; i < CanvasObjects.Length; i++)
            {
                GameObject gameObject1 = Instantiate(TempObject.transform.gameObject, levelManager.transform.GetChild(0));
                gameObject1.SetActive(true);
                gameObject1.GetComponent<TextMeshProUGUI>().text = "NewText";
                CanvasObjects[i].Generate(gameObject1.GetComponent<TextMeshProUGUI>());
            }

            ConvertRails(ctx);

            BuildAIPaths(ctx);

            // Before the respawn markers, which are sampled off ctx.RaceLine.
            BuildRaceLine(ctx);

            BuildRaceLineRespawns(ctx);

            // Kill the "bouncy glass" (MECHANICS_DIAGNOSIS Bug 4). SSX glass panes
            // import as SOLID thin double-sided MeshColliders on layer 8; a
            // CharacterController hitting the thin quad depenetrates along the
            // contact normal while KEEPING its speed = a bounce (no PhysicMaterial
            // is involved). Strip the collider from any near-flat layer-8
            // MeshCollider whose thin axis is roughly HORIZONTAL (a vertical
            // wall/pane the rider slams into sideways) so the rider rides straight
            // through it — the user is fine with glass not breaking as long as it
            // isn't bouncy. Panes whose thin axis is VERTICAL (floors, and the
            // glass RAMPS you ride ON, e.g. merquer) are left solid so they stay
            // rideable. Convex colliders (PhysProps) are excluded.
            //
            // This predicate is pure geometry, and layer 8 is "solid, not ground"
            // for every non-terrain prop, not just glass — a thin, roughly-vertical,
            // non-convex collider is exactly what a chain-link fence, sign stand,
            // billboard or bridge support looks like too. That silently rode the
            // player through gari's Fnc_FenceChainLink_NoLogo fence (HOOK_NOTES.md
            // "Ride-through fences" addendum). Require "glass" or "window" in the
            // instance name (case-insensitive) to gate the strip to actual SSX
            // glass; scanning the full Maps/ library with ssx/audit_glass_strip.py,
            // "glass" alone caught Tricky's one glass family (Pipedream's
            // Mdl_Barrier_SideWallGlass) but missed SSX3's window-named glass
            // (merquer's Mdl_WindowsA3_1002, already a documented glow object;
            // Peak 2's mdl_DRA4_window_lit / mdl_CRA3_window) — "window" closes
            // that gap. Everything else this predicate used to catch (fences,
            // signs, billboards, bridge/wall/barrier/checkpoint props, ~14,000
            // instances across all 159 shipped maps) is a false positive this
            // name gate saves.
            // One count per scene-scan block, logged unconditionally at the end in call
            // order, so a block that stops running (or moves) shows in a log diff even on
            // a map where it finds nothing. The per-block lines above only fire on > 0.
            var blocks = new List<string>();
            int glassStripped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var mc in sceneRoot.GetComponentsInChildren<MeshCollider>(true))
                {
                    var go = mc.gameObject;
                    if (go.layer != 8 || mc.convex || mc.sharedMesh == null) continue;
                    bool looksLikeGlass =
                        go.name.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        go.name.IndexOf("window", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!looksLikeGlass) continue;
                    // A door pane is geometrically INDISTINGUISHABLE from a glass
                    // pane here: it is thin, layer 8, and vertical by definition.
                    // Without this exemption the strip deletes every AnimDoor part
                    // collider (48 of 48 on megaple) at load, long before
                    // AnimDoorTrigger ever fires, so the doors animate perfectly and
                    // the rider drives straight through them. MoveSurf parts are
                    // exempted for the same reason (megaple's closest is only 0.24
                    // off the horizontal-thin-axis threshold today).
                    if (IsAnimatedSurfacePart(go.transform)) continue;
                    Vector3 ext = mc.sharedMesh.bounds.size;
                    int thin = 0;
                    if (ext.y < ext[thin]) thin = 1;
                    if (ext.z < ext[thin]) thin = 2;
                    // MUST be Min, not Max. A PANE is thin relative to BOTH other
                    // axes; a POLE is thin relative to only its long one. Under Max
                    // a tree trunk (0.24 x 0.26 x 4.5) tested 0.24 > 0.3*4.5 = false
                    // and read as a sheet, so this stripped every trunk, flagpole and
                    // lamp post in gari/elysium/mesa/untrack (454 in Untracked alone,
                    // 5030 across a full session) — leaving trees with no collision at
                    // all, since the leaf meshes never get a collider by design.
                    // Min keeps genuine panes stripped: those are thin vs their min too.
                    float med = Mathf.Min(ext[(thin + 1) % 3], ext[(thin + 2) % 3]);
                    if (med < 1e-3f || ext[thin] > 0.3f * med) continue; // not a thin sheet
                    Vector3 thinLocal = thin == 0 ? Vector3.right : thin == 1 ? Vector3.up : Vector3.forward;
                    Vector3 thinWorld = go.transform.TransformDirection(thinLocal).normalized;
                    if (Mathf.Abs(thinWorld.y) > 0.7f) continue; // thin axis vertical -> floor/ramp, keep solid
                    UnityEngine.Object.Destroy(mc);
                    glassStripped++;
                }
            }
            if (glassStripped > 0)
                Log.LogInfo($"[Glass] stripped {glassStripped} thin vertical layer-8 'glass'-named collider(s) (bounce fix)");
            blocks.Add($"glass={glassStripped}");
            LoadClock.Mark("Glass");

            //Convert pipeline-marked props into crashable PhysicsProps
            // Naming contract with the map pipeline: any GameObject named
            // "PhysProp_*" becomes a game PhysicsProp (the crash-through-it prop:
            // rider contact calls Launch()). Order matters — PhysicsProp.Awake
            // immediately does GetComponent<Rigidbody>().Sleep(), so the Rigidbody
            // must exist BEFORE PhysicsProp is added. Layer 15 ("PhysicsProp") is
            // excluded from both CastLayerMask and GroundLayers, so these can
            // never read as ground. soundName is a private serialized string that
            // arrives null on runtime-added components; null passes the game's
            // != "" check and would PostEvent(null) — set it to "" explicitly.
            // A dynamic Rigidbody needs a convex collider: log-and-skip anything
            // with a non-convex MeshCollider (or none at all).
            int propsConverted = 0, propsSkipped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("PhysProp_")) continue;
                    var propGO = t.gameObject;
                    if (propGO.GetComponent<PhysicsProp>() != null) continue; // already a prop
                    var col = propGO.GetComponent<Collider>();
                    var meshCol = col as MeshCollider;
                    if (col == null || (meshCol != null && !meshCol.convex))
                    {
                        propsSkipped++;
                        Log.LogDebug($"[PhysProps] '{propGO.name}' skipped: {(col == null ? "no collider" : "non-convex MeshCollider")}");
                        continue;
                    }
                    propGO.AddComponent<Rigidbody>();
                    var prop = propGO.AddComponent<PhysicsProp>();
                    propGO.layer = 15;
                    SetGameField(fPropSoundName, "PhysicsProp.soundName", prop, "");
                    propsConverted++;
                }
            }
            if (propsConverted > 0 || propsSkipped > 0)
                Log.LogInfo($"[PhysProps] converted {propsConverted} crashable prop(s), skipped {propsSkipped}");
            blocks.Add($"physprop={propsConverted}/{propsSkipped}");
            LoadClock.Mark("PhysProps");

            // Trick-multiplier pickups: hand the flakes to the GAME's own snowflake
            // behaviour instead of the Maker's generic one.
            //
            // The map pipeline can only emit Maker components, so an imported flake
            // ships with MakerSnowflake (tags the GO — that's all it does) plus a
            // MakerObjectAnimation spinning it 60 deg/s about its LOCAL Y. The game's
            // built-in courses instead carry `Snowflake`, which sets an ABSOLUTE
            // world rotation of 3 rad/s (~172 deg/s) about Y — verified in level1,
            // where every stock flake is: layer 2, tag Snowflake<n>, `Snowflake`, and
            // a SPHERE trigger of radius 10 (its mesh is a 14.6 x 16.8 x 1.1 panel,
            // so the sphere covers the flake's face AND stands ~10 units proud of it
            // on either side).
            //
            // Two things follow, and both are why ported pickups feel off:
            //   * spin is at the mercy of a Maker component we don't control (Molokai
            //     ships a bundle structurally identical to maps that do spin, yet
            //     doesn't) — the game's own component removes that variable, and
            //     matches the built-in courses' speed while it's there.
            //   * our trigger is the flake's own convex hull, i.e. ~2 units thick
            //     along the panel normal. The rider crosses that in a single frame at
            //     speed, so pickups get missed. Stock's grab depth is the radius, not
            //     the panel: a sphere of max(10, largest extent) is never smaller than
            //     stock's absolute grab and always encloses the (bigger, 2.5x-scaled)
            //     ported flake, so its corners stay collectable.
            int flakes = 0, flakeSpheres = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var mf in sceneRoot.GetComponentsInChildren<MakerSnowflake>(true))
                {
                    var flakeGO = mf.gameObject;
                    if (flakeGO.GetComponent<Snowflake>() != null) continue; // already converted
                    var makerAnim = flakeGO.GetComponent<MakerObjectAnimation>();
                    if (makerAnim != null) UnityEngine.Object.Destroy(makerAnim);
                    flakeGO.AddComponent<Snowflake>();
                    flakes++;

                    // Swap the mesh-tight trigger for the stock sphere. Keep the mesh
                    // collider only if there's no renderer to size a sphere from.
                    var meshCol = flakeGO.GetComponent<MeshCollider>();
                    var filter = flakeGO.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    var b = filter.sharedMesh.bounds;
                    var sphere = flakeGO.AddComponent<SphereCollider>();
                    sphere.isTrigger = true;
                    sphere.center = b.center;
                    // Stock parity is a flat 10 (TM's own forest flakes all carry
                    // m_Radius 10), and §19 justified it by noting the sphere still
                    // ENCLOSES the flake. That held for every gem in the corpus until
                    // WildTangent's coin, whose largest extent is 1.72 -- there the
                    // floor is 5.8x the coin's own diameter, so a rider on the snow
                    // sits entirely inside a trigger centred well overhead and grabs
                    // coins by riding under them. Keep the floor, but don't let it
                    // dwarf the flake: 3x the flake's own extent, capped at the stock
                    // 10 and never smaller than the flake itself.
                    // Measured, so this is a no-op everywhere it already worked:
                    // TM forest flake extent 8.39 -> 10 (unchanged), merquer/megaple
                    // gem 13.06 -> 13.06 (unchanged), dusk coin 1.72 -> 5.15.
                    // ⚠️ The 3x is a JUDGMENT CALL -- it is the reach-vs-precision
                    // dial, and nothing in the game's data pins it.
                    // ⚠️ Do NOT scale this by Plugin.WorldScale: rider physics is
                    // absolute (§19), and WildTangent rides at WorldScale 100, which
                    // would make this a 1000-unit grab sphere.
                    float ext = Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z));
                    sphere.radius = Mathf.Max(ext, Mathf.Min(10f, ext * 3f));
                    if (meshCol != null) UnityEngine.Object.Destroy(meshCol);
                    flakeSpheres++;
                }
            }
            if (flakes > 0)
                Log.LogInfo($"[Snowflakes] converted {flakes} pickup(s) to the game's Snowflake behaviour, {flakeSpheres} sphere trigger(s)");
            blocks.Add($"flake={flakes}/{flakeSpheres}");
            LoadClock.Mark("Snowflakes");
            LowerScaledGems(levelManagerMaker.gameObject.scene);

            // Phase 4 custom items — Wind volumes. Naming contract with SSXImporter.BuildItems:
            // a GO named "Wind_<accel>" carries a BoxCollider (geometry only) and is oriented
            // so its forward (+Z) is the world push direction. We attach a WindVolume behavior
            // that, while a Snowboarder is inside the box, adds accel*dt along the push direction
            // to that rider's velocity every physics step (Snowboarder.velocity is the exact field
            // UpdateGrounded feeds to CharacterController.Move). Containment is done analytically
            // (OBB test in WindVolume) and the collider is DISABLED at runtime — so a wind box
            // never blocks the rider and is never mistaken for ground by the game's ground
            // SphereCast (which hits triggers by default). The number is an ACCELERATION in Unity
            // units/s^2, parsed invariant-culture, matching the importer — it is SSX's own
            // BoostAmount passed through 1:1 (WindVolume's header has the why).
            int windAttached = 0, windSkipped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Wind_")) continue;
                    var windGO = t.gameObject;
                    if (windGO.GetComponent<WindVolume>() != null) continue; // already attached
                    if (windGO.GetComponent<BoxCollider>() == null)
                    { windSkipped++; Log.LogDebug($"[Wind] '{windGO.name}' skipped: no BoxCollider"); continue; }
                    float accel;
                    string tail = t.name.Substring("Wind_".Length);
                    if (!float.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out accel))
                    {
                        // Shouldn't happen — the importer always bakes a number. 60 is SSX's own
                        // conveyor rating, i.e. about 3x gravity, so a malformed name degrades to
                        // something mild rather than to a no-op or a rocket.
                        accel = 60f;
                        Log.LogDebug($"[Wind] '{windGO.name}' name has no parseable acceleration -> default {accel}");
                    }
                    var wv = windGO.AddComponent<WindVolume>();
                    // Push direction: an optional "Dir" child (importer emits it for SSX boost
                    // fields, whose push vector is independent of the box's own orientation — the
                    // box is oriented to the source volume so its OBB containment matches). No
                    // Dir child (editor-drawn wind volumes) => the box's own forward, as before.
                    var windDir = t.Find("Dir");
                    wv.WorldDir = windDir != null ? windDir.forward : t.forward;
                    wv.Accel = accel;
                    windAttached++;
                }
            }
            if (windAttached > 0 || windSkipped > 0)
                Log.LogInfo($"[Wind] attached {windAttached} wind volume(s), skipped {windSkipped}");
            blocks.Add($"wind={windAttached}/{windSkipped}");
            LoadClock.Mark("Wind");

            // Phase 4 custom items — Teleport volumes. Naming contract with
            // SSXImporter.BuildItems: a GO named "Teleport_<i>" carries a BoxCollider (the
            // source region) and a child "Target" transform (the destination pos + facing).
            // TeleportVolume repositions any rider that ENTERS the box to the Target, using
            // the game's own CharacterController-teleport idiom (disable CC -> set transform
            // -> re-enable CC -> zero velocity, RE'd from Snowboarder.Respawn). Edge-triggered
            // (once per entry). Same collider-free OBB containment as WindVolume.
            int tpAttached = 0, tpSkipped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Teleport")) continue;
                    var tpGO = t.gameObject;
                    if (tpGO.GetComponent<TeleportVolume>() != null) continue; // already attached
                    if (tpGO.GetComponent<BoxCollider>() == null)
                    { tpSkipped++; Log.LogDebug($"[Teleport] '{tpGO.name}' skipped: no BoxCollider"); continue; }
                    var target = t.Find("Target");
                    if (target == null)
                    { tpSkipped++; Log.LogDebug($"[Teleport] '{tpGO.name}' skipped: no Target child"); continue; }
                    tpGO.AddComponent<TeleportVolume>();   // reads box + Target child in Awake
                    tpAttached++;
                }
            }
            if (tpAttached > 0 || tpSkipped > 0)
                Log.LogInfo($"[Teleport] attached {tpAttached} teleport volume(s), skipped {tpSkipped}");
            blocks.Add($"teleport={tpAttached}/{tpSkipped}");
            LoadClock.Mark("Teleport");

            // Phase 4 custom items — Launch volumes. Naming contract with
            // SSXImporter.BuildItems: a GO named "Launch_<i>_<speed>" (plus an optional
            // "_lap<N>") carries a BoxCollider (geometry only), AXIS-ALIGNED like SSX's own
            // trigger, with the launch direction on a "Dir" child. LaunchVolume SETS the
            // rider's velocity to that direction * speed the moment they cross in — the one-shot throw SSX Tricky's own
            // SubType 15/18/24 verbs perform, and what makes megaple's fan shaft a lap
            // course. "_lap<N>" caps it at N launches per rider so the final descent rides
            // through to the finish; absent, the volume fires every time. Speed is parsed
            // invariant-culture, matching the importer.
            int launchAttached = 0, launchSkipped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Launch_")) continue;
                    var lnGO = t.gameObject;
                    if (lnGO.GetComponent<LaunchVolume>() != null) continue; // already attached
                    if (lnGO.GetComponent<BoxCollider>() == null)
                    { launchSkipped++; Log.LogDebug($"[Launch] '{lnGO.name}' skipped: no BoxCollider"); continue; }

                    var seg = t.name.Split('_');
                    float speed;
                    if (seg.Length < 3 ||
                        !float.TryParse(seg[2], NumberStyles.Float, CultureInfo.InvariantCulture, out speed) ||
                        speed <= 0f)
                    { launchSkipped++; Log.LogWarning($"[Launch] '{lnGO.name}' skipped: no parseable speed in the name"); continue; }

                    // Trailing option tokens are SCANNED, not indexed by position — "_lap<N>"
                    // and "_ctr" are independent and either may be absent, so seg[3] is not
                    // a fixed slot. (The editor-side parsers scan the same way.)
                    int laps = 0;      // 0 = fires every time
                    bool center = false;
                    for (int si = 3; si < seg.Length; si++)
                    {
                        if (seg[si] == "ctr") { center = true; continue; }
                        if (!seg[si].StartsWith("lap")) continue;
                        if (!int.TryParse(seg[si].Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out laps) || laps < 1)
                        { laps = 0; Log.LogWarning($"[Launch] '{lnGO.name}': '{seg[si]}' is not a lap count — firing every time"); }
                    }

                    var lv = lnGO.AddComponent<LaunchVolume>();
                    // Launch direction: an optional "Dir" child overrides the box's own
                    // forward, same channel WindVolume uses for the SSX boost fields whose
                    // push vector is independent of the volume's orientation.
                    var lnDir = t.Find("Dir");
                    lv.WorldDir = lnDir != null ? lnDir.forward : t.forward;
                    lv.Speed = speed;
                    lv.Laps = laps;
                    lv.Center = center;
                    launchAttached++;
                }
            }
            if (launchAttached > 0 || launchSkipped > 0)
                Log.LogInfo($"[Launch] attached {launchAttached} launch volume(s), skipped {launchSkipped}");
            blocks.Add($"launch={launchAttached}/{launchSkipped}");
            LoadClock.Mark("Launch");

            // Phase 4 custom items — TriggerAnim ("door") volumes. Naming contract with
            // SSXImporter.BuildItems: a GO named "TriggerAnim_*" ships fully authored —
            // LIVE trigger BoxCollider (layer 2), legacy Animation with the baked
            // closed→open clip (ClampForever), and the door prop reparented underneath.
            // We only attach the GAME'S OWN PlayAnimationOnTriggerEnter (HOOK_NOTES §11,
            // the SSX-Tokyo tree-trigger mechanism): any Snowboarder entering plays the
            // Animation. Its Start() self-heals m_animation and the audio locations, but
            // OnTriggerEnter posts startEvent with NO null check — a component born via
            // AddComponent (not deserialization) has null Wwise event fields, so blank
            // AK.Wwise.Event instances are Traverse-set (Event.Post is IsValid()-gated:
            // a blank event no-ops silently instead of NRE'ing). Its private `triggered`
            // one-shot flag is checked but never set by the game, so the anim replays on
            // every entry — harmless: ClampForever means an open door just stays open.
            int taAttached = 0, taSkipped = 0;
            var patType = AccessTools.TypeByName("PlayAnimationOnTriggerEnter");
            var wwiseEventType = AccessTools.TypeByName("AK.Wwise.Event");
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("TriggerAnim_")) continue;
                    var taGO = t.gameObject;
                    if (patType == null || wwiseEventType == null)
                    { taSkipped++; Log.LogWarning("[TriggerAnim] game types missing — skipped"); continue; }
                    if (taGO.GetComponent(patType) != null) continue;   // already attached
                    if (taGO.GetComponent<Animation>() == null)
                    { taSkipped++; Log.LogDebug($"[TriggerAnim] '{taGO.name}' skipped: no Animation"); continue; }
                    var pat = taGO.AddComponent(patType);
                    var tr = Traverse.Create(pat);
                    tr.Field("startEvent").SetValue(Activator.CreateInstance(wwiseEventType));
                    tr.Field("endEvent").SetValue(Activator.CreateInstance(wwiseEventType));
                    // Start() would set these anyway; set them here too in case a rider is
                    // already inside the box when physics wakes up before Start runs.
                    tr.Field("m_animation").SetValue(taGO.GetComponent<Animation>());
                    tr.Field("audioLocation1").SetValue(taGO);
                    tr.Field("audioLocation2").SetValue(taGO);
                    taAttached++;
                }
            }
            if (taAttached > 0 || taSkipped > 0)
                Log.LogInfo($"[TriggerAnim] attached {taAttached} door trigger(s), skipped {taSkipped}");
            blocks.Add($"triggeranim={taAttached}/{taSkipped}");
            LoadClock.Mark("TriggerAnim");

            // AnimDoor (feature: gameplay.animDoors). Naming contract with
            // SSXImporter.BuildAnimDoors:
            //   AnimDoor_<doorIdx>            — door GO with a legacy Animation (baked open clip).
            //   AnimDoorTrig_<doorIdx>_<trig> — trigger box relaying into that door.
            // Unlike TriggerAnim above (1 trigger reparents + plays 1 prop on its OWN GO),
            // SSX doors are N triggers -> M doors (merquer: one trigger opens two gates), and
            // the game's PlayAnimationOnTriggerEnter force-binds m_animation to its own GO in
            // Start() — so a shared door can't ride under a trigger. The door instead carries
            // its own Animation; each trigger references it by parsing <doorIdx> from its name.
            // AnimDoorTrigger Play()s the resolved door on rider entry (analytic OBB
            // containment like Teleport/Wind — no trigger-callback reliance for thin volumes).
            int adDoors = 0, adTrig = 0, adSkip = 0;
            var animDoorByIdx = new Dictionary<string, Animation>();
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("AnimDoor_")) continue;
                    var anim = t.GetComponent<Animation>();
                    if (anim == null) continue;   // static door (no animated part) — nothing to drive
                    animDoorByIdx[t.name.Substring("AnimDoor_".Length)] = anim;
                    adDoors++;
                }
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("AnimDoorTrig_")) continue;
                    var trigGO = t.gameObject;
                    if (trigGO.GetComponent<AnimDoorTrigger>() != null) continue;   // already attached
                    // name = AnimDoorTrig_<doorIdx>_<trigIdx>; the door idx is segment [1].
                    var segs = t.name.Split('_');
                    if (segs.Length < 3 || !animDoorByIdx.TryGetValue(segs[1], out var doorAnim))
                    { adSkip++; Log.LogDebug($"[AnimDoor] trigger '{t.name}' has no matching AnimDoor_ — skipped"); continue; }
                    var rel = trigGO.AddComponent<AnimDoorTrigger>();
                    rel.target = doorAnim;
                    rel.clipName = "AnimDoor_" + segs[1];
                    adTrig++;
                }
            if (adDoors > 0 || adTrig > 0 || adSkip > 0)
                Log.LogInfo($"[AnimDoor] attached {adTrig} trigger(s) driving {adDoors} door(s), skipped {adSkip}");
            blocks.Add($"animdoor={adTrig}/{adDoors}/{adSkip}");
            LoadClock.Mark("AnimDoor");

            // MovingSurface (feature: gameplay.movingColliders). Same relay as AnimDoor with one
            // difference that is the whole point of the feature: keepColliders. A door disables its
            // colliders on fire so a non-convex mesh never moves while colliding; a rideable kicker
            // must keep them and let the animation drag the collider. Naming contract with
            // SSXImporter.BuildMovingSurfaces:
            //   MoveSurf_<idx>              — mover GO (legacy Animation, layer 0, live colliders).
            //   MoveSurfTrig_<idx>_<trig>   — trigger box relaying into it.
            int msSurf = 0, msTrig = 0, msSkip = 0, msHaz = 0;
            var moveSurfByIdx = new Dictionary<string, Animation>();
            var moveSurfPlayerOnly = new HashSet<string>();
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("MoveSurf_")) continue;
                    var anim = t.GetComponent<Animation>();
                    if (anim == null) continue;   // static mover (no animated part) — nothing to drive
                    moveSurfByIdx[t.name.Substring("MoveSurf_".Length)] = anim;
                    msSurf++;
                    // playerOnly:true -> only the human fires this mover's triggers. Same
                    // marker-child contract as MoverHazard below, for the same reason.
                    if (t.Find("MoverPlayerOnly") != null)
                        moveSurfPlayerOnly.Add(t.name.Substring("MoveSurf_".Length));
                    // Contact with this mover wrecks the rider. Flagged by a marker CHILD
                    // rather than by the mover's own name, because the trigger join below
                    // parses "MoveSurfTrig_<mover>_<trig>" positionally — decorating
                    // "MoveSurf_<idx>" would silently unwire every trigger on the course.
                    if (t.Find("MoverHazard") != null && t.GetComponent<AvalancheHazard>() == null)
                    {
                        t.gameObject.AddComponent<AvalancheHazard>().target = anim;
                        msHaz++;
                    }
                }
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("MoveSurfTrig_")) continue;
                    var trigGO = t.gameObject;
                    if (trigGO.GetComponent<AnimDoorTrigger>() != null) continue;   // already attached
                    // name = MoveSurfTrig_<surfIdx>_<trigIdx>; the mover idx is segment [1].
                    var segs = t.name.Split('_');
                    if (segs.Length < 3 || !moveSurfByIdx.TryGetValue(segs[1], out var surfAnim))
                    { msSkip++; Log.LogDebug($"[MoveSurf] trigger '{t.name}' has no matching MoveSurf_ — skipped"); continue; }
                    var rel = trigGO.AddComponent<AnimDoorTrigger>();
                    rel.target = surfAnim;
                    rel.clipName = "MoveSurf_" + segs[1];
                    rel.keepColliders = true;   // the collider MOVES — that is the feature
                    rel.playerOnly = moveSurfPlayerOnly.Contains(segs[1]);
                    msTrig++;
                }
            if (msSurf > 0 || msTrig > 0 || msSkip > 0)
                Log.LogInfo($"[MoveSurf] attached {msTrig} trigger(s) driving {msSurf} mover(s), skipped {msSkip}"
                    + (msHaz > 0 ? $", {msHaz} hazard(s)" : ""));
            blocks.Add($"movesurf={msTrig}/{msSurf}/{msSkip}/{msHaz}");
            LoadClock.Mark("MoveSurf");

            // ParticleTrigger (feature: lighting.particleTriggers). Contract on the class.
            int ptTrig = 0, ptSys = 0, ptSkip = 0, ptImp = 0, ptHide = 0;
            var pfxShotByKey = new Dictionary<string, Transform>();
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                    if (t.name.StartsWith("PfxShot_")) pfxShotByKey[t.name.Substring("PfxShot_".Length)] = t;
            if (pfxShotByKey.Count > 0)
                foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
                    foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                    {
                        if (!t.name.StartsWith("ParticleTrig_")) continue;
                        if (t.GetComponent<ParticleTrigger>() != null) continue;   // already attached
                        var rel = t.gameObject.AddComponent<ParticleTrigger>();
                        foreach (Transform c in t)
                        {
                            if (c.name.StartsWith("PfxImpact_"))
                            {
                                rel.impact = true;
                                string ra = c.name.Substring("PfxImpact_".Length);
                                rel.rearm = ra == "once" ? -1f
                                    : float.TryParse(ra, System.Globalization.NumberStyles.Float,
                                                     System.Globalization.CultureInfo.InvariantCulture, out var rs) ? rs : -1f;
                                continue;
                            }
                            if (c.name == "PfxHideSelf")
                            {
                                // Meshes only: the PfxShot_ burst lives under the same mover part.
                                var surf = t.parent;
                                while (surf != null && !surf.name.StartsWith("MoveSurf_")) surf = surf.parent;
                                if (surf != null)
                                {
                                    foreach (var r in surf.GetComponentsInChildren<Renderer>(true))
                                        if (!(r is ParticleSystemRenderer)) rel.hideRenderers.Add(r);
                                    ptHide++;
                                }
                                else { ptSkip++; Log.LogDebug($"[ParticleTrig] '{t.name}' PfxHideSelf has no MoveSurf_ ancestor — skipped"); }
                                continue;
                            }
                            if (c.name.StartsWith("PfxHide_"))
                            {
                                var hideGO = FindSceneGOByInstanceName(t.gameObject.scene, c.name.Substring("PfxHide_".Length));
                                if (hideGO != null) { rel.hides.Add(hideGO); ptHide++; }
                                else { ptSkip++; Log.LogDebug($"[ParticleTrig] '{t.name}' hide '{c.name}' resolved to nothing — skipped"); }
                                continue;
                            }
                            if (!c.name.StartsWith("PfxTarget_")) continue;
                            if (!pfxShotByKey.TryGetValue(c.name.Substring("PfxTarget_".Length), out var shot))
                            { ptSkip++; Log.LogDebug($"[ParticleTrig] '{t.name}' target '{c.name}' has no matching PfxShot_ — skipped"); continue; }
                            rel.targets.AddRange(shot.GetComponentsInChildren<ParticleSystem>(true));
                        }
                        ptSys += rel.targets.Count;
                        ptTrig++;
                        if (rel.impact) ptImp++;
                    }
            if (pfxShotByKey.Count > 0 || ptSkip > 0)
                Log.LogInfo($"[ParticleTrig] attached {ptTrig} trigger(s) ({ptImp} impact, {ptHide} hide) over {pfxShotByKey.Count} group(s), {ptSys} emitter bind(s), skipped {ptSkip}");
            blocks.Add($"particletrig={ptTrig}/{pfxShotByKey.Count}/{ptSkip}");
            LoadClock.Mark("ParticleTrig");

            // Per-level sky-fill override. Naming contract with SSXImporter.BuildLighting:
            // an empty marker GO named "SkyFill_<RRGGBB>" (from Overrides.json
            // lighting.skyFillColor). MUST be read before the SkydomeFollow attach below —
            // its OnEnable samples the fill once. Always assigned (found or null) so a
            // previous map's override can't leak onto the next one.
            SceneSkyFill = null;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("SkyFill_")) continue;
                    string hex = "#" + t.name.Substring("SkyFill_".Length);
                    if (ColorUtility.TryParseHtmlString(hex, out var fill))
                    {
                        SceneSkyFill = fill;
                        Log.LogInfo($"[Skydome] per-level sky fill {hex} (overrides [Skydome] FillColor)");
                        LoadClock.Mark("Skydome");
                    }
                    else
                        Log.LogWarning($"[Skydome] sky-fill marker '{t.name}' not parseable; using [Skydome] FillColor");
                }
            }
            blocks.Add($"skyfill={(SceneSkyFill.HasValue ? 1 : 0)}");

            // Per-level camera draw distance. Naming contract with SSXImporter.BuildLighting:
            // an empty marker GO named "DrawDist_<unity units>" (from Overrides.json
            // lighting.drawDistanceRaw — SSX Tricky's per-course WorldConf terrain cull
            // distance, ssx/NOTES.md). The component lives on the marker, so it dies with
            // the level scene and restores the camera's far clip on the way out. MUST attach
            // before SkydomeFollow below: both hook beginCameraRendering, and subscription
            // order is call order, so the dome fits to the clipped far plane from frame one.
            int drawDists = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("DrawDist_")) continue;
                    if (t.GetComponent<DrawDistance>() != null) continue;
                    if (float.TryParse(t.name.Substring("DrawDist_".Length), NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out float far) && far > 1f)
                    {
                        t.gameObject.AddComponent<DrawDistance>().far = far;
                        drawDists++;
                        Log.LogInfo($"[DrawDist] per-level camera far clip {far:F1}");
                        LoadClock.Mark("DrawDist");
                    }
                    else
                        Log.LogWarning($"[DrawDist] marker '{t.name}' not parseable; far clip left alone");
                }
            }
            blocks.Add($"drawdist={drawDists}");

            // Skydome. Naming contract with SSXImporter.BuildSkybox: a GO named
            // "Skydome" carries the level's unlit, Background-queue, ZWrite-off dome
            // mesh (no collider). We attach SkydomeFollow so it stays centered on the
            // camera — the dome is baked at map scale (only tens of units across), so
            // without following, the rider would ride out of it and lose the sky. Only
            // position follows; rotation stays world-fixed so the sky is compass-stable.
            int skydomes = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name != "Skydome") continue;
                    if (t.GetComponent<SkydomeFollow>() != null) continue; // already attached
                    t.gameObject.AddComponent<SkydomeFollow>();
                    skydomes++;
                }
            }
            if (skydomes > 0)
                Log.LogInfo($"[Skydome] attached camera-follow to {skydomes} skydome(s)");
            blocks.Add($"skydome={skydomes}");
            LoadClock.Mark("Skydome");

            // Animated textures (feature 14). Naming contract with SSXImporter.BuildProps:
            // a GO named "UVScroll_<u>_<v>_<originalName>" (rates written with the
            // invariant-culture round-trip format) renders SSX geometry whose material
            // UVs scroll — LCD scanlines, jumbotrons, boost-pad chevrons, river water.
            // The original name may itself contain underscores, so only the first three
            // '_'-separated segments are parsed. UVScrollBehaviour instantiates the
            // renderers' materials once and scrolls their base-map offset per frame.
            int uvAttached = 0, uvSkipped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("UVScroll_")) continue;
                    if (t.GetComponent<UVScrollBehaviour>() != null) continue; // already attached
                    string[] seg = t.name.Split(new[] { '_' }, 4);
                    float ru, rv;
                    if (seg.Length < 4 ||
                        !float.TryParse(seg[1], NumberStyles.Float, CultureInfo.InvariantCulture, out ru) ||
                        !float.TryParse(seg[2], NumberStyles.Float, CultureInfo.InvariantCulture, out rv))
                    {
                        uvSkipped++;
                        Log.LogDebug($"[UVScroll] '{t.name}' skipped: unparseable rates");
                        continue;
                    }
                    var us = t.gameObject.AddComponent<UVScrollBehaviour>();
                    us.RatePerFrame = new Vector2(ru, rv);
                    uvAttached++;
                }
            }
            if (uvAttached > 0 || uvSkipped > 0)
                Log.LogInfo($"[UVScroll] attached {uvAttached} UV-scroller(s), skipped {uvSkipped}");
            blocks.Add($"uvscroll={uvAttached}/{uvSkipped}");
            LoadClock.Mark("UVScroll");

            // Smashable glass / breakables. Naming contract with SSXImporter.BuildProps:
            // a GO named "Destructible_<orig>" carries the intact model (render mesh, NO
            // collider) plus inactive "Frag_<i>" children (per-shard meshes) and optional
            // "DisableRef_<name>" markers. We wire the game's own Destructible on it, plus a
            // Hook-side Smashable that DRIVES the break. WHY not rely on the game's trigger:
            // Snowboarder.OnTriggerEnter does call Destructible.Break, but only if the pane's
            // trigger fires an enter event for the rider's CharacterController — unreliable
            // for us (the rider crosses at 150-190 mph → ~1.5 m/tick, tunnelling a thin pane;
            // and a bundle-added trigger on the glass layer isn't guaranteed to fire against
            // the rider's layer). Smashable does a SWEPT test instead (can't tunnel) and calls
            // the public Break directly. The BoxCollider (render-mesh bounds, min 0.3 m thick)
            // is a geometry carrier for Smashable and for Break's GetComponent<Collider>() —
            // Smashable DISABLES it at runtime; layer 2 (the game's glass layer, excluded from
            // the ground SphereCast) keeps an intact pane from ever reading as ground. Private
            // serialized List<T>/string fields arrive null on runtime-added components
            // (feature 7): Traverse-set all four — lists may be empty but NEVER null
            // (Break foreach-es fragmentList; soundName null would PostEvent(null)).
            int smashWired = 0, smashFrags = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Destructible_")) continue;
                    var dGO = t.gameObject;
                    if (dGO.GetComponent<Destructible>() != null) continue; // already wired
                    var box = dGO.AddComponent<BoxCollider>();
                    var dmf = dGO.GetComponent<MeshFilter>();
                    if (dmf != null && dmf.sharedMesh != null)
                    {
                        var b = dmf.sharedMesh.bounds;
                        box.center = b.center;
                        box.size = Vector3.Max(b.size, new Vector3(0.3f, 0.3f, 0.3f));
                    }
                    box.isTrigger = true;
                    dGO.layer = 2;
                    var frags = new List<DestructibleFragment>();
                    var disables = new List<GameObject>();
                    foreach (Transform child in t)
                    {
                        if (child.name.StartsWith("Frag_"))
                        {
                            child.gameObject.SetActive(false);
                            child.gameObject.layer = 0;
                            frags.Add(child.gameObject.AddComponent<DestructibleFragment>());
                        }
                        else if (child.name.StartsWith("DisableRef_"))
                        {
                            // Resolve by original instance name; the scene GO may carry a
                            // pipeline prefix (UVScroll_/PhysProp_). Unresolved refs are
                            // fine — megaple's invisible Glass_Surface proxies are dropped
                            // at import, and the pane needs no wall (it's a pure trigger).
                            string target = child.name.Substring("DisableRef_".Length);
                            var found = FindSceneGOByInstanceName(levelManagerMaker.gameObject.scene, target);
                            if (found != null) disables.Add(found);
                        }
                    }
                    var destr = dGO.AddComponent<Destructible>();
                    SetGameField(fDestrFragmentList, "Destructible.fragmentList", destr, frags);
                    SetGameField(fDestrDisableList, "Destructible.disableList", destr, disables);
                    SetGameField(fDestrHideSelf, "Destructible.hideSelf", destr, true);
                    SetGameField(fDestrSoundName, "Destructible.soundName", destr, "Play_GlassSmash_Large");
                    dGO.AddComponent<Smashable>(); // drives Break via swept detection; disables the box
                    smashWired++; smashFrags += frags.Count;
                }
            }
            if (smashWired > 0)
                Log.LogInfo($"[Destructible] wired {smashWired} smashable(s) with {smashFrags} shard fragment(s)");
            blocks.Add($"destructible={smashWired}/{smashFrags}");
            LoadClock.Mark("Destructible");

            // Atmospheric falling snow. Naming contract with SSXImporter.BuildLighting:
            // an empty GO "SnowFX_<intensity>". That gets TM's own FallingSnow when the
            // build has it (TrySpawnTmSnow), else a camera-follow ParticleSystem built at
            // runtime (SnowFall) — a bundle-baked system would be engine-module-stripped
            // and, world-simulated, couldn't stay wrapped around the chase cam. One system
            // total (there's only ever one marker); intensity scales emission + cap.
            // "SnowSSX_<chance>_<intensity>_<unitsPerMetre>" is SSX Tricky's decoded
            // SnowFallMan instead (SsxSnowFall, BakeSsxSnowMarker); a failed per-race roll
            // still consumes the marker. "WeatherSSX3_<S>_<x0>_<y0>_<rawPerPixel>" is SSX 3's
            // zone-mapped weather (Ssx3SnowFall, BakeSsx3WeatherMarker).
            bool snowDone = SnowFall.Active || SsxSnowFall.Active || Ssx3SnowFall.Active;
            string snowKind = snowDone ? "active" : "none";
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                if (snowDone) break;
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name.StartsWith("SnowSSX_"))
                    {
                        SpawnSsxSnow(t);
                        snowKind = "ssx";
                        snowDone = true;
                        break;
                    }
                    if (t.name.StartsWith("WeatherSSX3_"))
                    {
                        if (Ssx3SnowFall.TrySpawn(t, ssx3SnowFlakeSize.Value, out string why))
                            Log.LogInfo($"[SnowFall] SSX 3 weather snow spawned: {why}");
                        else
                            Log.LogWarning($"[SnowFall] SSX 3 weather: {why}");
                        snowKind = "ssx3";
                        snowDone = true;
                        break;
                    }
                    if (!t.name.StartsWith("SnowFX_")) continue;
                    float si;
                    if (!float.TryParse(t.name.Substring("SnowFX_".Length), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out si))
                    {
                        si = 1f;
                        Log.LogDebug($"[SnowFall] '{t.name}' has no parseable intensity -> default {si:F2}");
                    }
                    snowKind = "tm";
                    if (TrySpawnTmSnow(levelManager, si, out string tmWhy))
                        Log.LogInfo($"[SnowFall] TM's own falling snow wired ({tmWhy})");
                    else
                    {
                        snowKind = "cam";
                        SnowFall.Spawn(si);
                        Log.LogInfo($"[SnowFall] camera-follow snow spawned (intensity {si:F2}); TM snow unavailable: {tmWhy}");
                        LoadClock.Mark("SnowFall");
                    }
                    snowDone = true;
                    break;
                }
            }
            blocks.Add($"snow={snowKind}");

            // Engine-drawn lamp halos. Naming contract with SSXImporter.BuildGlowSprites:
            // a "GlowSprites*" MeshRenderer baked DISABLED whose vertex channels carry the
            // halos. GlowSprites rebuilds the camera-facing quads every camera and enables
            // the renderer only once the data reads back whole.
            int glows = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("GlowSprites") || t.GetComponent<GlowSprites>() != null) continue;
                    var gs = t.gameObject.AddComponent<GlowSprites>();
                    if (!gs.Init()) { UnityEngine.Object.Destroy(gs); continue; }
                    glows++;
                    var gm = t.GetComponent<MeshFilter>().sharedMesh;
                    Log.LogInfo($"[Glow] '{t.name}': {gm.vertexCount / 4} halo quads driven");
                    LoadClock.Mark("Glow");
                }
            }
            blocks.Add($"glow={glows}");

            // Post-process bloom. Naming contract: "Bloom_<intensity>_<threshold>". PostFX
            // creates a global URP Volume with a Bloom override and flips the render
            // camera's post-processing on (feature: PostFX). One volume total.
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Bloom_")) continue;
                    if (PostFX.Active) break;
                    string[] seg = t.name.Split('_');
                    float bi = 0.7f, bt = 0.9f, parsed;
                    // ⚠️ Parse into a scratch float, NOT straight into bi/bt. TryParse
                    // zeroes its out param on failure, so passing bi directly turned a
                    // malformed "Bloom_x_y" into intensity 0 — bloom silently OFF — while
                    // the log line below cheerfully reported the 0 as if authored.
                    if (seg.Length >= 2)
                    {
                        if (float.TryParse(seg[1], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) bi = parsed;
                        else Log.LogDebug($"[PostFX] '{t.name}' has no parseable intensity -> default {bi:F2}");
                    }
                    if (seg.Length >= 3)
                    {
                        if (float.TryParse(seg[2], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) bt = parsed;
                        else Log.LogDebug($"[PostFX] '{t.name}' has no parseable threshold -> default {bt:F2}");
                    }
                    PostFX.Enable(bi, bt);
                    Log.LogInfo($"[PostFX] bloom enabled (intensity {bi:F2}, threshold {bt:F2})");
                    LoadClock.Mark("PostFX");
                }
                if (PostFX.Active) break;
            }
            blocks.Add($"bloom={(PostFX.Active ? 1 : 0)}");

            // Time-of-day ramp. Naming contract with SSXImporter.BuildLightStops: empty GOs
            // "LightStop_<region>_<y>_<sunI>_<sunHex>_<ambR>_<ambG>_<ambB>_<fillHex>_
            // <fogHex>_<fogDen>_<terrHex>", one per source course of a whole-mountain chain.
            // A Unity scene bakes ONE lighting grade; SSX 3's final descent is daylight at
            // the summit and night by Metro-City, so LightingRamp blends between the stops
            // by rider elevation. Needs at least two — one stop is just the baked grade.
            // Attached after the skydome and terrain exist, because it drives their
            // materials.
            // SSX 3 per-zone fog + ScreenTint (WorldPainter slots 5/7) from the plain-text
            // "<map>.painter.txt" sidecar — a sidecar so it needs no bundle rebuild. Absent
            // file or [Visual] Ssx3WorldPainter = false: the bundle's baked fog, no tint.
            if (ssx3WorldPainter == null || ssx3WorldPainter.Value)
            {
                string painterPath = Path.ChangeExtension(LoadedAssetBundle, ".painter.txt");
                if (File.Exists(painterPath))
                {
                    try
                    {
                        if (Ssx3Painter.TrySpawn(painterPath, WorldScale, ssx3FogReach == null ? 0f : ssx3FogReach.Value,
                                                 levelManagerMaker.gameObject.scene, out string why))
                            Log.LogInfo($"[Painter] {why}");
                        else
                            Log.LogWarning($"[Painter] '{Path.GetFileName(painterPath)}': {why}; keeping the baked fog, no tint");
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning($"[Painter] '{Path.GetFileName(painterPath)}' unreadable ({e.Message}); keeping the baked fog, no tint");
                    }
                }
                else if (BundleSource == "ssx3")
                    Log.LogInfo($"[Painter] no '{Path.GetFileName(painterPath)}' next to this SSX 3 map; baked fog, no per-zone fog or tint");
                LoadClock.Mark("Painter");
            }

            int rampStops = -1;   // -1 = [Visual] LightingRamp off
            if (lightingRamp == null || lightingRamp.Value)
            {
                var stops = new List<LightingRamp.Stop>();
                int badStops = 0;
                foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
                    foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                    {
                        if (!t.name.StartsWith("LightStop_")) continue;
                        LightingRamp.Stop s;
                        if (LightingRamp.TryParse(t.name, out s)) stops.Add(s);
                        else
                        {
                            badStops++;
                            Log.LogWarning($"[LightingRamp] marker '{t.name}' is malformed; ignored");
                        }
                    }
                // A partly-parsed ramp is worse than none: it would blend across the stops
                // that dropped out, so a whole course's stretch of mountain silently takes
                // its neighbours' grade. Refuse the whole thing and say so.
                if (badStops > 0 && stops.Count > 0)
                {
                    Log.LogWarning($"[LightingRamp] {badStops} of {stops.Count + badStops} stop marker(s) "
                        + "unreadable — ramp DISABLED; the map keeps its single baked grade. Rebuild it.");
                }
                else if (stops.Count >= 2)
                {
                    LightingRamp.Spawn(stops);
                }
                else if (stops.Count == 1)
                {
                    Log.LogWarning("[LightingRamp] only 1 stop marker found; a ramp needs 2 to "
                        + "interpolate, so the map keeps its single baked grade.");
                }
                rampStops = stops.Count;
            }
            blocks.Add($"ramp={rampStops}");
            Log.LogInfo("[LevelBuild] blocks: " + string.Join(" ", blocks));

            // Respawn / OOB instrumentation. Last, so it belongs to the scene the
            // player will actually ride and dies with it (which is what guarantees
            // its report gets written).
            // Either switch spawns it: the two halves are independent reports, but
            // both need the same rider resolution (LevelManager.player), which is
            // the one expensive thing the harness does.
            if ((perfHarness != null && perfHarness.Value)
                || (riderTelemetry != null && riderTelemetry.Value))
                PerfHarness.Spawn();

            // NB: the open-top sky-fill is started from OnSceneLoaded (not here) —
            // SolidColorSky captures the ACTIVE scene name, which isn't the custom scene
            // yet at LoadLevelManager time, so starting it here exits immediately.
            LoadClock.Mark("LoadLevelManager tail");
        }
    }
}
