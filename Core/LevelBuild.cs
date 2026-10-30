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
        public static void LoadLevelManager()
        {
            LoadClock.Mark("Unity scene load (bundle -> MakerLevelData.Awake)");
            //Find Main Starting Objects
            var levelManagerMaker = FindAnyObjectByType<MakerLevelData>();

            var MakerSpawnPoint = FindAnyObjectByType<MakerSpawnPoint>();
            var MakerRespawnPoint = FindAnyObjectByType<MakerRespawnPoints>();

            // Playtest start pin: relocate the START before anything reads it.
            // Everything downstream comes off this one transform — the ground-snap
            // below, the starting-gate prop, LevelManager.startingGate, the rider
            // seat and StartLaunchRedirect — so moving it here moves the whole start
            // sequence and nothing else has to know about pins. It deliberately does
            // NOT move the AI paths or the race line: the field still starts at the
            // top, which is why this is a testing tool and not a race setup.
            string pinTrack = menuManager != null ? menuManager.lastLevelEntry.name : null;
            if (TestPin != null && MakerSpawnPoint != null && TestPinMap != null
                && (menuManager == null || !EntryIsTrack(menuManager.lastLevelEntry, TestPinMap)))
            {
                Log.LogInfo($"[TestPin] pin belongs to '{TestPinMap}' and this is '{pinTrack}' — starting at the gate");
            }
            else if (TestPin != null && MakerSpawnPoint != null)
            {
                MakerSpawnPoint.transform.position = TestPin.Value;
                if (TestPinHasYaw)
                    MakerSpawnPoint.transform.rotation = Quaternion.Euler(0f, TestPinYaw, 0f);
                Log.LogWarning($"[TestPin] start moved to {TestPin.Value.ToString("0.##", CultureInfo.InvariantCulture)}"
                    + (TestPinHasYaw ? $" facing {TestPinYaw.ToString("0.#", CultureInfo.InvariantCulture)} deg" : "")
                    + " — this map is NOT starting at its gate");
                RescuePinY(MakerSpawnPoint.transform);
            }

            // --- Spawn / respawn ground-snap -------------------------------------
            // The game seats the rider's CharacterController CENTER on the
            // startingGate transform with NO ground-check for the START (respawns
            // get only a 0.5 m down-probe). Two authoring failure modes result: an
            // authored Y BELOW the terrain (the capsule straddles the floor, PhysX
            // depenetrates DOWNWARD, rider drops through — e.g. SSX3 peak runs B/D
            // stitched from peak-cap + race-body regions), and an authored Y ABOVE
            // it (an immediate drop the instant the run starts). SnapMarkerToGround
            // casts onto the terrain and, per the map's spawnsnap mode, seats each
            // marker exactly on the surface (Both) or only lifts it clear (Raise);
            // Off leaves everything authored (gari's hand-tuned start). This makes
            // XZ the only coordinate a map author has to get right — Y is computed.
            if (Plugin.SpawnSnapMode != Plugin.SpawnSnap.Off)
            {
                Physics.SyncTransforms();
                if (MakerSpawnPoint != null)
                    SnapMarkerToGround(MakerSpawnPoint.transform, "start");
                if (MakerRespawnPoint != null)
                    foreach (Transform rp in MakerRespawnPoint.transform)
                        SnapMarkerToGround(rp, rp.name);
            }
            //Find AI Paths Object

            //Update Level Entry
            // ⚠️ LevelEntry is a STRUCT, so this is a COPY. The threshold ARRAYS are
            // reference fields and the copy shares them with the registered entry, so
            // the six writes below land on the real entry in place — which is the only
            // reason medals have worked at all while the write-back underneath was dead.
            // Do NOT "tidy" these into `new float[] { ... }`: that would silently give
            // every custom map the placeholder medal times from RegisterLevels back.
            // freestyleTimeLimit is a plain float on the struct and does NOT alias.
            // Rescue the medal times, freestyle clock and score thresholds on maps
            // built before the importer learned to derive them (see DeriveBudgets).
            // Runs BEFORE the six writes below, so the rescued values are what lands.
            DeriveBudgets(levelManagerMaker);

            var LevelEntry = menuManager.lastLevelEntry;

            LevelEntry.timeThresholds[2] = levelManagerMaker.GoldTime;
            LevelEntry.timeThresholds[1] = levelManagerMaker.SilverTime;
            LevelEntry.timeThresholds[0] = levelManagerMaker.BronzeTime;

            LevelEntry.scoreThresholds[2] = levelManagerMaker.GoldPoints;
            LevelEntry.scoreThresholds[1] = levelManagerMaker.SilverPoints;
            LevelEntry.scoreThresholds[0] = levelManagerMaker.BronzePoints;

            // The map's own freestyle clock. Held out of 2.7.1 because the baked
            // TimeLeft was `200f * build scale` — a placeholder nobody had ridden,
            // which would have taken a 2.5-scale course from the 150 s every player
            // has actually experienced to 500 s, sight unseen. That reason is now
            // spent: TimeLeft is DERIVED from the measured route (importer) or
            // rescued from it at load (DeriveBudgets), and both floor it at TM's
            // stock 150 s. So no map's clock can get SHORTER than what players ride
            // today — only the long courses that currently run out of time halfway
            // down move at all, and only upward. (The one way past that floor is a
            // course explicitly pinning gameplay.freestyleTime in its Overrides.json,
            // which is a deliberate act by whoever set it.)
            LevelEntry.freestyleTimeLimit = levelManagerMaker.TimeLeft;

            // Seat the copy back through the property's setter. The `Traverse.Field`
            // chain this replaces never resolved (see mSetLastLevelEntry) and said
            // nothing about it, so the entry the menu keeps has always been the one
            // RegisterLevels made up.
            if (mSetLastLevelEntry == null)
            {
                Log.LogError("[Reflection] MenuManager.lastLevelEntry's setter no longer resolves — "
                    + "the level entry is NOT updated; run `python3 tm_re_baseline.py check`.");
            }
            else
            {
                mSetLastLevelEntry.Invoke(menuManager, new object[] { LevelEntry });
            }

            // ...and the freestyle clock separately, because the write-back above is
            // one load too late for it. MenuManager copies lastLevelEntry's limit into
            // its own public `freestyleTimeLimit` during LoadScene, which is already
            // done by the time this Awake postfix runs; LevelManager.Start then reads
            // THAT field (`time = MenuManager.Instance.freestyleTimeLimit`) and Start
            // has not run yet. Without this line the author's clock would only take
            // effect on a second run of the map. The field is public — no reflection,
            // and a TM rename is a compile error rather than a silent miss.
            // ⚠️ The twin of the write above, and BOTH are needed: the write-back
            // above is one load too late on its own, because MenuManager has already
            // copied lastLevelEntry's limit into this public field during LoadScene
            // and LevelManager.Start reads THIS field, not the entry.
            if (levelManagerMaker.TimeLeft > 0f)
            {
                if (!Mathf.Approximately(levelManagerMaker.TimeLeft, menuManager.freestyleTimeLimit))
                    Log.LogInfo($"[LevelEntry] freestyle clock {menuManager.freestyleTimeLimit:0} s "
                        + $"-> {levelManagerMaker.TimeLeft:0} s (this map's own)");
                LoadClock.Mark("LevelEntry");
                menuManager.freestyleTimeLimit = levelManagerMaker.TimeLeft;
            }

            //Insert Starting Gate
            // The gate is NOT a plain bar. Measured off the live prefab
            // (resources.assets, GameObject "StartingGate", one child mesh "gate",
            // 912 verts, 1.5 root scale): SEVEN posts at gate-local x = 0, +/-1.5,
            // +/-3.0, +/-4.5 — pitch exactly 1.5 — plus end caps at +/-5.7/+/-5.963.
            // The posts are the dividers BETWEEN riders, so a rider belongs in a
            // bay at gateX +/- 0.75, 2.25, 3.75, 5.25. EIGHT bays, and that is the
            // whole gate: it cannot hold nine.
            //
            // The lanes are pinned to MakerSpawnPoint, not to this prop — we hand
            // LevelManager.startingGate the spawn point below, and LevelManager.Start
            // seats every rider at startingGate.position + rotation * (x, 0.45, 0):
            // AI i at x = -6.75 + 1.5i (plus another 1.5 once i passes the player's
            // slot) and the player at x = -5.25 + 1.5*slot. Whatever the slot draw,
            // the occupied set is always lanes k = 1..N at x = -6.75 + 1.5k for
            // N = racerEntries.Count.
            //
            // So a bay lands on a lane only when gateX is a MULTIPLE OF 1.5. Mid-field
            // is 0.75N - 6, which is a multiple of 1.5 for EVEN N only; at odd N it
            // lands on a lane and every post spears a rider. Snap it down to the lane
            // midpoint grid instead — exact for even N, one bay left at odd N.
            //
            // ⚠️ At N = 9 the field is 1.5 wider than the gate and lane 9 (x = +6.75)
            // stands off the right end. That is geometry, not a tuning choice: eight
            // bays, nine riders. Snapping DOWN rather than up is what makes the odd
            // one out an AI every time — the player's slot is Random.Range(0,7), so
            // the human is always in lanes 1..7 and never in the one left outside.
            //
            // ⚠️ N = 9 is STOCK behaviour, not something a mod sets. SelectLevel
            // adds the player and THEN Min(8, pool.Count) opponents, so any roster
            // big enough to fill the pool (>= 10 entries — CharacterHook's is)
            // fields nine. TM's own courses never show it because the stock roster
            // is five, which leaves the pool short and the pad-to-8 loop in charge.
            //
            // Non-race modes keep the mode-locked slot 3 (the Random.Range only runs
            // when GetMode() is Race, which is 0 — the game brtrue's past it for
            // Freestyle/TimeTrials) and leave racerEntries holding the PREVIOUS
            // race's list, so the count is only meaningful in Race. A lone rider
            // sits at -5.25 + 1.5*3 = -0.75, which is a bay when gateX is 0.
            float gateX = 0f;   // lone rider at slot 3 (x = -0.75) => the -0.75 bay
            if (menuManager != null && menuManager.GetMode() == MenuManager.Mode.Race)
            {
                var racers = menuManager.racerEntries;
                int lanes = racers != null ? racers.Count : 0;
                if (lanes > 0) gateX = 1.5f * (lanes / 2) - 6f;   // int division: snap down
            }
            var StartingGate = Resources.Load<GameObject>("game/startinggate");
            GameObject StartingGateObject = Instantiate(StartingGate);
            StartingGateObject.transform.position = MakerSpawnPoint.transform.TransformPoint(new Vector3(gateX,0,0.5f));
            StartingGateObject.transform.rotation = MakerSpawnPoint.transform.rotation;

            // Custom-map start-launch fix: steer the human's launch down-course for
            // the first fraction of a second so a sideways start slope can't fling it
            // off the line, and trim the whole field's launch speed evenly (the
            // game's countdown kick is purely vertical — see StartLaunchRedirect).
            // Spawn facing is horizontal by construction (LookRotation, y-zeroed).
            if (MakerSpawnPoint != null)
            {
                Vector3 courseFwd = MakerSpawnPoint.transform.forward; courseFwd.y = 0f;
                if (courseFwd.sqrMagnitude > 1e-4f)
                {
                    var rd = new GameObject("StartLaunchRedirect").AddComponent<StartLaunchRedirect>();
                    rd.CourseForward = courseFwd.normalized;
                }
            }

            //Insert Level Manager
            var LevelManagerObject = Resources.Load<GameObject>("game/levelmanager");
            var gameObject = Instantiate(LevelManagerObject, levelManagerMaker.transform);

            var levelManager = gameObject.GetComponent<LevelManager>();

            //Set Level Manager Data Points
            SetGameField(fStartingGate, "LevelManager.startingGate", levelManager, MakerSpawnPoint.transform);
            SetGameField(fRespawnPointsParent, "LevelManager.respawnPointsParent", levelManager, MakerRespawnPoint.transform);
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

            //Convert From Make Rails to Game Rails
            var RailObjects = FindObjectsByType(typeof(MakerRails), FindObjectsSortMode.None) as MakerRails[];
            int railColliders = 0, railSegments = 0;
            HookRails.Clear();
            GameObject probeRailGO = null;
            List<Rail.Segment> probeSegments = null;
            for (int i = 0; i < RailObjects.Length; i++)
            {
                List<Rail.Segment> NewSegments = BuildRailSegments(RailObjects[i]);

                // Since TM 25714710 a rail's path lives on a sibling RailInstanceData,
                // which Rail.Awake caches with GetComponent — so the data component
                // must exist BEFORE the Rail is added, or Awake caches null and every
                // rail falls back to its transform position (ungrindable, no error).
                var NewData = RailObjects[i].gameObject.AddComponent<RailInstanceData>();
                NewData.segments = NewSegments;
                var NewRail = RailObjects[i].gameObject.AddComponent<RailInvis>();

                // The bool is checked, not discarded: on a miss TryParse yields
                // default(RailMaterial) silently, which is indistinguishable from an
                // authored value and just changes the grind sound/FX for no visible reason.
                Rail.RailMaterial material1;
                if (!Enum.TryParse(RailObjects[i].Materials.ToString(), out material1))
                    Log.LogWarning($"[Rails] '{RailObjects[i].gameObject.name}' declares material "
                        + $"'{RailObjects[i].Materials}', which has no match in Rail.RailMaterial — "
                        + $"using {material1} instead (grind sound/FX will be wrong on this rail).");

                NewRail.material = material1;

                // Grind detection BoxCast/SphereCasts layer 9 ("Rail") and then does
                // GetComponent<Rail>() on the collider's OWN GameObject — so a rail
                // is only grindable with a collider on this exact GO, on layer 9.
                // Maker rails ship with neither. (MakerRails.Start() sets the layer
                // too, but a frame after scene build — set it now.)
                RailObjects[i].gameObject.layer = 9;
                railSegments += NewSegments.Count;
                if (NewSegments.Count < 2)
                {
                    Log.LogDebug($"[Rails] '{RailObjects[i].name}': {NewSegments.Count} segment(s), no collider built");
                    continue;
                }
                if (RailObjects[i].GetComponent<Collider>() != null)
                {
                    Log.LogDebug($"[Rails] '{RailObjects[i].name}': already has a collider, left as-is");
                    continue;
                }
                if (BuildRailCollider(RailObjects[i].gameObject, NewSegments))
                {
                    railColliders++;
                    HookRails.Add(NewRail);
                    if (probeRailGO == null)
                    {
                        probeRailGO = RailObjects[i].gameObject;
                        probeSegments = NewSegments;
                    }
                }
            }
            if (RailObjects.Length > 0)
                Log.LogInfo($"[Rails] converted {RailObjects.Length} rail(s), built {railColliders} collider(s), {railSegments} segment(s) total");
            LoadClock.Mark("Rails");
            if (railColliderProbe.Value && probeRailGO != null && Instance != null)
                Instance.StartCoroutine(RailProbe(probeRailGO, probeSegments));

            // World-space objects hang off LevelData, never levelManager: the game's
            // LevelManager root is a Screen Space - Overlay Canvas, which Unity
            // re-lays-out on any window resize/fullscreen toggle, dragging every
            // child with it (respawns landed 1.35x-scaled about z=0, under the course).
            GameObject AIParent = new GameObject("AI Paths Parent");
            AIParent.transform.parent = levelManagerMaker.transform;

            // MakerAIPath.m_points is a List of a user-defined [Serializable]
            // nested class, and Unity fails to deserialize those for
            // runtime-loaded (BepInEx) assemblies — on custom maps every list
            // comes back empty (engine limitation; lists of engine value types
            // like the race line's List<Vector3> are fine). The maker copy below
            // still wins whenever data DID arrive (future fixed bundles);
            // otherwise the routes are rebuilt from a plain-text sidecar next to
            // the .asset, or, failing that, from the race line.
            var routes = new List<RouteDraft>();
            var MakerAIPaths = FindObjectsByType(typeof(MakerAIPath), FindObjectsSortMode.None) as MakerAIPath[];
            for (int i = 0; i < MakerAIPaths.Length; i++)
            {
                if (MakerAIPaths[i].m_points == null || MakerAIPaths[i].m_points.Count < 2)
                    continue;
                var d = new RouteDraft { Name = "AIPath3D (maker " + routes.Count.ToString(CultureInfo.InvariantCulture) + ")" };
                foreach (var mp in MakerAIPaths[i].m_points)
                {
                    // Maker points are 2D (world XZ), like the old AIPath's.
                    d.Points.Add(new Vector3(mp.position.x, float.NaN, mp.position.y));
                    d.Windups.Add(mp.windup);
                }
                routes.Add(d);
            }

            var raceLineDraft = RaceLineRoute();
            if (routes.Count > 0)
            {
                Log.LogInfo($"[AIPaths] copied {routes.Count} maker path(s) from the bundle (sidecar ignored)");
                LoadClock.Mark("AIPaths");
            }
            else
            {
                // Build BOTH sources into the same pool: SSX branch fragments to race
                // on, and the race line as the one route guaranteed to reach the
                // finish. AIPathNetwork.TryAcquirePath keeps a rider on its current
                // route while inside that route's volume and, once it leaves (a
                // fragment end), re-picks the best-scoring volume it is inside — so
                // the race line is the backstop a stranded rider falls back onto.
                // Toggle MultiPathAI off for race-line-only racing without a rebuild.
                int built = raceLineDraft == null ? 0 : 1;
                if (raceLineDraft != null) routes.Add(raceLineDraft);
                int branches = 0;
                if (multiPathAI.Value || built == 0)
                {
                    var side = SidecarRoutes();
                    branches = side.Count;
                    routes.AddRange(side);
                }
                if (built + branches == 0)
                    Log.LogWarning("[AIPaths] no AI path data (maker points empty, no usable race line, no sidecar); AI will hold the start gate but cannot race");
                else
                    Log.LogInfo($"[AIPaths] AI pool: {built} race-line path + {branches} branch path(s) = {built + branches} total"
                        + (multiPathAI.Value ? "" : " (MultiPathAI off)"));
                    LoadClock.Mark("AIPaths");
            }
            var network = BuildAIPathNetwork(AIParent, routes, raceLineDraft);
            LoadClock.Mark("BuildAIPathNetwork");
            SetGameField(fPathNetwork, "LevelManager.pathNetwork", levelManager, network);

            // ⚠️ A map with no MakerRaceLine is legal — this lookup can and does
            // return null, and dereferencing it threw an NRE straight out of a Unity
            // Awake postfix. That lands in Player.log and NOWHERE in the BepInEx log
            // (root CLAUDE.md, HOOK_NOTES §23), and it aborted the REST of
            // LoadLevelManager: no respawn markers, no glass strip, no wind volumes,
            // no skydome, no destructibles — a half-imported map with a clean log.
            // BuildAIPathFromRaceLine already null-checks the same lookup; this is
            // the other half of that guard.
            var TempMakerRaceLine = FindAnyObjectByType<MakerRaceLine>();
            RaceLine TempRaceLine = null;
            if (TempMakerRaceLine == null || TempMakerRaceLine.m_points == null
                || TempMakerRaceLine.m_points.Count == 0)
            {
                Log.LogWarning("[RaceLine] no usable MakerRaceLine in this map ("
                    + (TempMakerRaceLine == null
                        ? "no MakerRaceLine component anywhere in the scene"
                        : "MakerRaceLine present but its m_points list is empty")
                    + ") — LevelManager.raceLine stays NULL. Rank/position, race-line respawn "
                    + "markers and the AI mod's race terminator are all off for this course; the "
                    + "rest of the level build continues.");
            }
            else
            {
                TempRaceLine = TempMakerRaceLine.gameObject.AddComponent<RaceLine>();

                TempRaceLine.m_points = new List<RaceLine.Point>();

                for (int i = 0;i < TempMakerRaceLine.m_points.Count;i++)
                {
                    var RaceLinePoint = new RaceLine.Point();

                    RaceLinePoint.position = TempMakerRaceLine.m_points[i];

                    TempRaceLine.m_points.Add(RaceLinePoint);
                }

                TempRaceLine.CalculateSegments();
                LoadClock.Mark("RaceLine.CalculateSegments");

                SetGameField(fRaceLine, "LevelManager.raceLine", levelManager, TempRaceLine);
            }

            // Respawn markers ALONG the race line (MECHANICS_DIAGNOSIS Bug 2).
            // The maker respawn points are SSX per-racer branch-line HEADS,
            // scattered off the ridden route; the game's Respawn picks the nearest
            // marker, so a fallen rider gets teleported to an off-route pocket they
            // "have never seen" — worse at 2.5x marker spacing. Replace the parent
            // with markers sampled on the reconstructed race line, each facing
            // down-line (Respawn's down-course 2x penalty reads child.forward), so
            // every respawn drops the rider back ONTO the route facing downhill.
            // The game re-enumerates children live per respawn, so runtime-built
            // markers just work. Falls back to the maker parent if the race line is
            // too short to sample.
            // ⚠️ Seat these on the snow, and DROP the ones with nothing under them.
            // SnapMarkerToGround above only ever sees the MAKER markers, which this
            // block then throws away — so before this, every race-line respawn was
            // used exactly as authored. That is fine for an SSX/SSX3 race line
            // (ground-following AI data) and wrong for one derived from a recorded
            // human run: alps0101's ghost line has 53% of its points more than 5 m
            // up and 13% more than 40 m, so half its respawns were into freefall.
            // Dropping the unreachable ones beats snapping them, because what is
            // under a big jump is not the course (one alps0101 point flies a ravine
            // whose floor is 122 m down); the game picks the NEAREST marker and
            // they sit ~25 m apart, so the neighbours cover the gap. Gated on
            // spawnsnap so a map that opts out (gari) keeps its authored markers.
            // Outside the race-line block on purpose: the wedge watchdog needs no
            // markers, so it must still be cleared on a map that has no race line.
            Snowboarder_AirborneStuckGuard.ResetState();
            AvalancheHazard.ResetState();
            if (TempRaceLine != null && TempRaceLine.m_points != null && TempRaceLine.m_points.Count >= 2)
            {
                var respawnParent = new GameObject("Respawn Points (race line)");
                respawnParent.transform.parent = levelManagerMaker.transform;
                // Static, so it outlives the scene it belongs to: a second map would
                // otherwise escalate riders onto the previous map's markers.
                Plugin.RaceLineRespawns.Clear();
                Snowboarder_Respawn.ResetLoopState();
                // Scene-scoped for the same reason DeriveBudgets is: a global lookup
                // on a second load would cut this line at the PREVIOUS map's finish.
                var finishLine = FindInScene<MakerFinishLine>(levelManagerMaker.gameObject.scene);
                bool seat = Plugin.SpawnSnapMode != Plugin.SpawnSnap.Off;
                var rpts = RespawnLine(TempRaceLine.m_points,
                    MakerSpawnPoint != null ? MakerSpawnPoint.transform : null,
                    finishLine != null ? finishLine.transform : null, seat, out string lineNote);
                if (lineNote != null) Log.LogInfo($"[Respawn] marker line: {lineNote}");
                int made = 0, moved = 0, dropped = 0;
                for (int i = 0; i < rpts.Count; i++)
                {
                    Vector3 p = rpts[i];
                    if (seat)
                    {
                        if (!GroundSeat(p, out float sy, out _, out _)) { dropped++; continue; }
                        if (Plugin.SpawnSnapMode == Plugin.SpawnSnap.Raise) sy = Mathf.Max(p.y, sy);
                        if (Mathf.Abs(sy - p.y) > 0.01f) moved++;
                        p.y = sy;
                    }
                    var go = new GameObject("Respawn " + i.ToString(CultureInfo.InvariantCulture));
                    go.transform.parent = respawnParent.transform;
                    go.transform.position = p;
                    Vector3 fwd = (i + 1 < rpts.Count)
                        ? rpts[i + 1] - rpts[i]
                        : rpts[i] - rpts[i - 1];
                    fwd.y = 0f;
                    if (fwd.sqrMagnitude > 1e-6f)
                        go.transform.rotation = Quaternion.LookRotation(fwd.normalized, Vector3.up);
                    Plugin.RaceLineRespawns.Add(go.transform);
                    made++;
                }
                // Every point airborne means the cast never found the terrain at all
                // (bad ground layers, say) — keep the maker markers rather than hand
                // the game an empty parent, which would strand a fallen rider.
                if (made > 0)
                {
                    SetGameField(fRespawnPointsParent, "LevelManager.respawnPointsParent", levelManager, respawnParent.transform);
                    Log.LogInfo($"[Respawn] built {made} race-line respawn marker(s) ({moved} seated on ground, {dropped} dropped as airborne), replacing maker branch-head markers");
                    LoadClock.Mark("Respawn");
                }
                else
                {
                    UnityEngine.Object.Destroy(respawnParent);
                    // The markers we just recorded are about to be destroyed; leaving
                    // them listed would have the loop guard escalate onto nulls.
                    Plugin.RaceLineRespawns.Clear();
                    Log.LogWarning($"[Respawn] all {rpts.Count} race-line points are airborne — keeping maker branch-head markers");
                }
            }

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

            // Per-level camera draw distance. Naming contract with SSXImporter.BuildLighting:
            // an empty marker GO named "DrawDist_<unity units>" (from Overrides.json
            // lighting.drawDistanceRaw — SSX Tricky's per-course WorldConf terrain cull
            // distance, ssx/NOTES.md). The component lives on the marker, so it dies with
            // the level scene and restores the camera's far clip on the way out. MUST attach
            // before SkydomeFollow below: both hook beginCameraRendering, and subscription
            // order is call order, so the dome fits to the clipped far plane from frame one.
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
                        Log.LogInfo($"[DrawDist] per-level camera far clip {far:F1}");
                        LoadClock.Mark("DrawDist");
                    }
                    else
                        Log.LogWarning($"[DrawDist] marker '{t.name}' not parseable; far clip left alone");
                }
            }

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
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                if (snowDone) break;
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name.StartsWith("SnowSSX_"))
                    {
                        SpawnSsxSnow(t);
                        snowDone = true;
                        break;
                    }
                    if (t.name.StartsWith("WeatherSSX3_"))
                    {
                        if (Ssx3SnowFall.TrySpawn(t, ssx3SnowFlakeSize.Value, out string why))
                            Log.LogInfo($"[SnowFall] SSX 3 weather snow spawned: {why}");
                        else
                            Log.LogWarning($"[SnowFall] SSX 3 weather: {why}");
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
                    if (TrySpawnTmSnow(levelManager, si, out string tmWhy))
                        Log.LogInfo($"[SnowFall] TM's own falling snow wired ({tmWhy})");
                    else
                    {
                        SnowFall.Spawn(si);
                        Log.LogInfo($"[SnowFall] camera-follow snow spawned (intensity {si:F2}); TM snow unavailable: {tmWhy}");
                        LoadClock.Mark("SnowFall");
                    }
                    snowDone = true;
                    break;
                }
            }

            // Engine-drawn lamp halos. Naming contract with SSXImporter.BuildGlowSprites:
            // a "GlowSprites*" MeshRenderer baked DISABLED whose vertex channels carry the
            // halos. GlowSprites rebuilds the camera-facing quads every camera and enables
            // the renderer only once the data reads back whole.
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("GlowSprites") || t.GetComponent<GlowSprites>() != null) continue;
                    var gs = t.gameObject.AddComponent<GlowSprites>();
                    if (!gs.Init()) { UnityEngine.Object.Destroy(gs); continue; }
                    var gm = t.GetComponent<MeshFilter>().sharedMesh;
                    Log.LogInfo($"[Glow] '{t.name}': {gm.vertexCount / 4} halo quads driven");
                    LoadClock.Mark("Glow");
                }
            }

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
            }

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
