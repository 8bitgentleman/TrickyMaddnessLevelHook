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
        // The race-line respawn markers built in BuildScene, in course order, so the
        // loop guard can escalate a wedged rider FORWARD by index. The game itself
        // only ever picks the nearest marker, which is exactly why it can't break a
        // loop: the nearest marker to a rider stuck in an OOB volume is the one that
        // keeps putting them back in it.
        internal static readonly List<Transform> RaceLineRespawns = new List<Transform>();

        private const float TB_RIDE_SPEED   = 45f;    // u/s over the whole route

        private const float TB_SILVER_RATIO = 1.06f;  // measured on TM's four courses

        private const float TB_BRONZE_RATIO = 1.15f;

        private const float TB_FREE_RATIO   = 1.5f;

        private const float TB_MIN_GOLD     = 45f;    // s, floor for arena-sized maps

        private const float TB_MIN_FREE     = 150f;   // s, TM's stock freestyle clock

        private const float TB_SINUOSITY    = 1.15f;  // chord -> ridden distance

        private const float TB_POINT_RATE   = 1500f;  // freestyle pts/s of clock at TM's pickup density

        private const float TB_TM_GEM_DENS  = 42f;    // weighted pickup value per 1000 u (TM: 41-83)

        private const float TB_MIN_CONTENT  = 0.5f;   // floor for a map with no pickups at all

        private const float TB_SILVER_PTS   = 0.75f;  // measured, exact on TM's four courses

        private const float TB_BRONZE_PTS   = 0.5f;   // measured, exact on TM's four courses

        private const float TB_MIN_GOLD_PTS = 50000f;

        private const float TB_PTS_ROUND    = 25000f;

        private static bool LooksLikeScaledPlaceholder(MakerLevelData ld)
        {
            if (ld == null || ld.GoldTime <= 0f) return false;
            return Mathf.Abs(ld.SilverTime / ld.GoldTime - 120f / 105f) < 0.01f
                && Mathf.Abs(ld.BronzeTime / ld.GoldTime - 135f / 105f) < 0.01f
                && Mathf.Abs(ld.TimeLeft   / ld.GoldTime - 200f / 105f) < 0.01f;
        }

        // Arc position along a polyline of the point closest to q, plus how far q sits
        // off the line. The distance is what lets the caller REFUSE to clip to a marker
        // that is not on the route. Mirrors SSXImporter.ProjectOnRoute.
        private static void ProjectOnRoute(List<Vector3> pts, Vector3 q, out float arc, out float off)
        {
            arc = 0f; off = float.MaxValue;
            float run = 0f;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector3 a = pts[i], ab = pts[i + 1] - a;
                float seg = ab.magnitude;
                float t = seg > 1e-6f ? Mathf.Clamp01(Vector3.Dot(q - a, ab) / (seg * seg)) : 0f;
                float d = Vector3.Distance(a + ab * t, q);
                if (d < off) { off = d; arc = run + seg * t; }
                run += seg;
            }
        }

        // The polyline the race-line respawn markers are built along: the race line
        // cut short of the finish, with its long gaps filled in.
        //
        // ⚠️ Cut at the finish because a race line need not stop there (avalanche
        // crs01's runs 247 u on, into a run-out that ends at a collision wall). A
        // marker past the gate is one no rider can finish from, and the crash-loop
        // guard escalated COM riders onto exactly those: they hit the end wall, died,
        // respawned at the nearest marker — the same one — forever. Stops a clip
        // tolerance short of the finish rather than on it: that clears the importer's
        // 12 * scale-deep finish trigger (15 * scale > 6 * scale), so a respawn never
        // lands inside the gate and finishes the race for free. Same on-route test as
        // DeriveBudgets — an off-route or lap-course finish leaves the line whole.
        //
        // Densified because the game and both guards count in MARKERS, not distance:
        // with crs01's 14 points one segment is 45% of the course, so "3 markers on"
        // skipped the finish entirely. Any segment over twice the median is split
        // into median-length pieces. A fill is a straight chord, not the course: on
        // crs01 five of them leave the mesh where the course bends away. Only the
        // ground-seat pass downstream makes them safe (it drops a point with no
        // terrain in its band), so `densify` is off when spawnsnap is off.
        private static List<Vector3> RespawnLine(List<RaceLine.Point> rpts, Transform spawn,
            Transform finish, bool densify, out string note)
        {
            note = null;
            var pts = new List<Vector3>(rpts.Count);
            foreach (var rp in rpts) pts.Add(rp.position);
            float len = 0f;
            for (int i = 1; i < pts.Count; i++) len += Vector3.Distance(pts[i - 1], pts[i]);

            if (finish != null && len > 1f)
            {
                float tol = Mathf.Max(15f * RouteScale, 0.02f * len);
                ProjectOnRoute(pts, finish.position, out float sFinish, out float dFinish);
                float sSpawn = 0f, dSpawn = float.MaxValue;
                if (spawn != null) ProjectOnRoute(pts, spawn.position, out sSpawn, out dSpawn);
                float a0 = dSpawn <= tol ? sSpawn : 0f;
                float cut = sFinish - tol;
                if (dFinish <= tol && cut > a0 && sFinish < len - 1f)
                {
                    var kept = new List<Vector3> { pts[0] };
                    float run = 0f;
                    for (int i = 1; i < pts.Count; i++)
                    {
                        float seg = Vector3.Distance(pts[i - 1], pts[i]);
                        if (run + seg >= cut)
                        {
                            float t = seg > 1e-6f ? (cut - run) / seg : 0f;
                            kept.Add(Vector3.Lerp(pts[i - 1], pts[i], t));
                            break;
                        }
                        kept.Add(pts[i]);
                        run += seg;
                    }
                    note = $"cut {len - cut:0} u short of the line's end ({len - sFinish:0} u past the finish, {tol:0} u before it)";
                    pts = kept;
                }
            }

            if (densify && pts.Count >= 3)
            {
                var segs = new List<float>(pts.Count - 1);
                for (int i = 1; i < pts.Count; i++) segs.Add(Vector3.Distance(pts[i - 1], pts[i]));
                var sorted = new List<float>(segs);
                sorted.Sort();
                float median = sorted[sorted.Count / 2];
                if (median > 1e-3f)
                {
                    var dense = new List<Vector3>(pts.Count) { pts[0] };
                    int added = 0;
                    for (int i = 1; i < pts.Count; i++)
                    {
                        if (segs[i - 1] > 2f * median)
                        {
                            int n = Mathf.Min(Mathf.CeilToInt(segs[i - 1] / median), 64);
                            for (int k = 1; k < n; k++) dense.Add(Vector3.Lerp(pts[i - 1], pts[i], k / (float)n));
                            added += n - 1;
                        }
                        dense.Add(pts[i]);
                    }
                    if (added > 0)
                    {
                        note = (note == null ? "" : note + "; ") + $"{added} point(s) filled into gaps over {2f * median:0} u";
                        pts = dense;
                    }
                }
            }
            return pts;
        }

        private static void DeriveBudgets(MakerLevelData ld)
        {
            if (ld == null) return;
            bool wantTimes = LooksLikeScaledPlaceholder(ld);
            bool wantScore = ld.GoldPoints <= 0f && ld.SilverPoints <= 0f && ld.BronzePoints <= 0f;
            if (!wantTimes && !wantScore) return;

            // ⚠️ Scene-SCOPED, not FindAnyObjectByType. MenuManager.LoadScene stacks
            // scenes additively and never unloads the outgoing one (see
            // LevelHook-src/CLAUDE.md), so a global lookup on a second load hands back
            // the PREVIOUS course's race line, spawn and finish — and would derive
            // this map's medals off the last map's route, silently. Everything here is
            // looked up inside the LevelData's OWN scene, the same way the snowflake
            // conversion in LoadLevel already does it. (BuildAIPathFromRaceLine below
            // still uses the global lookup; it predates this and is untouched here.)
            var scene = ld.gameObject.scene;
            var raceLine = FindInScene<MakerRaceLine>(scene);
            if (raceLine == null || raceLine.m_points == null || raceLine.m_points.Count < 2)
            {
                Log.LogWarning("[TimeBudget] this map carries the old placeholder medal times "
                    + "and/or zeroed score thresholds, and has no race line to measure — leaving "
                    + "them alone. Rebuild it, or pin gameplay.goldTime / goldPoints (and friends) "
                    + "in its Overrides.json.");
                return;
            }

            float len = 0f;
            for (int i = 1; i < raceLine.m_points.Count; i++)
                len += Vector3.Distance(raceLine.m_points[i - 1], raceLine.m_points[i]);

            var spawn = FindInScene<MakerSpawnPoint>(scene);
            var finish = FindInScene<MakerFinishLine>(scene);

            // Clip the polyline to the stretch actually ridden. Mirrors the block in
            // SSXImporter.BuildMakers — a race line is not required to start at the
            // spawn or stop at the finish, and on several source games it demonstrably
            // does neither: SSX 3 events start 38-60% of the way down their line, and
            // an SSX 2012 ghost that ran the lap twice made africa0302 measure a 253 s
            // course at 480 s. Timing the whole polyline hands those courses a gold
            // nobody can reach.
            //
            // ⚠️ Each end is clipped ONLY if the marker is actually ON the line. A
            // course whose spawn sits hundreds of units off-route (megaple) would
            // otherwise project onto some arbitrary nearest point and lose 80% of its
            // route to a clip that measured nothing. The tolerance is the same
            // proportional one the importer uses, with Plugin.WorldScale standing in
            // for the importer's S * 100 — they are the same number, which is what
            // lets both halves agree without a shared constant.
            float clipped = len;
            if (spawn != null && finish != null && len > 1f)
            {
                float clipTol = Mathf.Max(15f * RouteScale, 0.02f * len);
                float sSpawn, dSpawn, sFinish, dFinish;
                ProjectOnRoute(raceLine.m_points, spawn.transform.position, out sSpawn, out dSpawn);
                ProjectOnRoute(raceLine.m_points, finish.transform.position, out sFinish, out dFinish);
                float a0 = dSpawn  <= clipTol ? sSpawn  : 0f;
                float b0 = dFinish <= clipTol ? sFinish : len;
                // Wrap when the finish projects BEFORE the spawn — that is a lap
                // course, where the ridden stretch runs off the end and back.
                float c = b0 >= a0 ? (b0 - a0) : (len - a0) + b0;
                if (c > 1f && c < len - 1f)
                {
                    Log.LogInfo($"[TimeBudget] route clip: {len:0} u -> {c:0} u "
                        + $"(spawn {dSpawn:0} u off route, finish {dFinish:0} u, tol {clipTol:0})");
                    clipped = c;
                }
            }
            len = clipped;

            // Same floor the importer applies: the rider has to get from the start to
            // the finish trigger whatever the race line says, so the straight line
            // between those two markers bounds the ridden distance from below (times
            // SINUOSITY, since no ridden line is straight). Raises the budget only, and
            // on a course whose race line is intact the polyline already dwarfs it.
            float chord = 0f;
            if (spawn != null && finish != null)
            {
                chord = Vector3.Distance(spawn.transform.position, finish.transform.position);
                len = Mathf.Max(len, chord * TB_SINUOSITY);
            }

            // Per-course pace when the sidecar declares one, else the old flat 45 u/s.
            float pace = MapRidePace > 0f ? MapRidePace : TB_RIDE_SPEED;
            float gold = Mathf.Max(TB_MIN_GOLD, len / pace);
            if (wantTimes)
            {
                ld.GoldTime   = gold;
                ld.SilverTime = gold * TB_SILVER_RATIO;
                ld.BronzeTime = gold * TB_BRONZE_RATIO;
                ld.TimeLeft   = Mathf.Max(TB_MIN_FREE, gold * TB_FREE_RATIO);

                Log.LogInfo($"[TimeBudget] old scale-derived times replaced: route {len:0} u "
                    + $"({raceLine.m_points.Count} pts, spawn->finish {chord:0} u) at {pace:0.0} u/s "
                    + $"=> gold {ld.GoldTime:0.0}s / silver {ld.SilverTime:0.0}s / bronze "
                    + $"{ld.BronzeTime:0.0}s, freestyle {ld.TimeLeft:0}s. Rebuild the map to bake these in.");
            }

            // Freestyle score thresholds. A map built before 2026-09-19 baked 0/0/0,
            // which LevelManager reads as "any score is gold" — so every freestyle run
            // on every custom map has auto-golded off the start gate. Zero is also an
            // unambiguous marker: no real threshold ladder starts at nothing, so this
            // cannot stomp an authored one. Mirrors SSXImporter.BuildMakers' block —
            // see it for what here is measured (the 0.75/0.5 ratios, exact on TM's
            // four courses) and what is fitted (everything else).
            if (wantScore)
            {
                int gemValue = 0, gemCount = 0;
                foreach (var flake in FindAllInScene<MakerSnowflake>(scene))
                {
                    gemCount++;
                    gemValue += flake.snowFlakeType == MakerSnowflake.SnowFlakeType.Snowflake5 ? 5
                              : flake.snowFlakeType == MakerSnowflake.SnowFlakeType.Snowflake3 ? 3 : 2;
                }
                float gemDensity = len > 1f ? gemValue * 1000f / len : 0f;
                float content = Mathf.Clamp(gemDensity / TB_TM_GEM_DENS, TB_MIN_CONTENT, 1f);
                // Off the map's OWN clock, whichever it ended up with above, so the
                // bar tracks how long the rider actually gets to bank points.
                float goldPts = Mathf.Round(TB_POINT_RATE * ld.TimeLeft * content / TB_PTS_ROUND) * TB_PTS_ROUND;
                goldPts = Mathf.Max(TB_MIN_GOLD_PTS, goldPts);

                ld.GoldPoints   = goldPts;
                ld.SilverPoints = goldPts * TB_SILVER_PTS;
                ld.BronzePoints = goldPts * TB_BRONZE_PTS;

                Log.LogInfo($"[TimeBudget] zeroed freestyle score thresholds replaced (they golded "
                    + $"on any score): {gemCount} pickup(s) worth {gemValue} over {len:0} u = "
                    + $"{gemDensity:0.0}/1000u => content x{content:0.00} on a {ld.TimeLeft:0} s clock "
                    + $"=> gold {ld.GoldPoints:0} / silver {ld.SilverPoints:0} / bronze {ld.BronzePoints:0}.");
            }
        }
    }

    // The game's OOB contact handler, and the reason this map's frame rate collapses
    // at one spot. Snowboarder.OnControllerColliderHit reads (IL-verified):
    //
    //     if (state == Dead) return;
    //     if (hit.collider.gameObject.layer == LayerMask.NameToLayer("OOB")) {
    //         Respawn(); OnOOB?.Invoke(); return;
    //     }
    //
    // Respawn() sets state = Riding, so that early-out can never engage on the rider
    // it just moved — and Respawn picks the NEAREST respawn marker, which for a rider
    // standing inside an OOB volume is the marker that puts them back inside it. There
    // is no cooldown, so the whole cycle re-runs on the next contact, i.e. every
    // rendered frame. Measured: 2817 respawns in 114 s for a single rider.
    //
    // SSX3 reset zones are what they hit: SSXImporter.EmitResetZone emits every
    // Mdl_Resetzone* instance as a SOLID (isTrigger = false) layer-10 BoxCollider,
    // 1234 of them on the chain.
    //
    // This prefix records only — it names the collider so the misplaced volumes can be
    // fixed in the map data, and flags the respawn about to happen as OOB-triggered so
    // the loop guard below can tell it apart from a crash.
    [HarmonyPatch(typeof(Snowboarder), "OnControllerColliderHit")]
    public class Snowboarder_OnControllerColliderHit
    {
        internal const int OobLayer = 10;

        // Set immediately before the game's own Respawn() call on the OOB branch and
        // consumed by Snowboarder_Respawn's prefix on the same call stack, so no
        // frame-boundary staleness is possible.
        // ⚠️ The game returns before its OOB branch when the rider is Dead (state 4,
        //   first instruction of the method), so a wrecked rider sliding into a reset
        //   volume is NOT respawned by it. Flagging that contact anyway left the flag
        //   stale, and the rider's own death-timer Respawn then read as OOB — the 1 s
        //   window — so the crash route never counted a single death of that loop.
        internal static string PendingOobCollider;

        [HarmonyPrefix]
        public static void Prefix(Snowboarder __instance, ControllerColliderHit hit)
        {
            if (hit == null || __instance == null) return;
            var col = hit.collider;
            if (col == null || col.gameObject.layer != OobLayer) return;
            object st = Traverse.Create(__instance).Field("state").GetValue();
            if (st != null && Convert.ToInt32(st) == 4) return;   // 4 = State.Dead
            PendingOobCollider = col.gameObject.name;
            PerfHarness.NoteOobHit(PendingOobCollider, col.transform.position);
        }
    }

    // On custom maps AISnowboarder.GetSteeringInput calls Snowboarder.Respawn()
    // directly after 1.5s of pathlessness, bypassing CanRespawn() — during the
    // countdown that launches AI off the start line. Block Respawn while the
    // custom scene is in its Starting state; built-in levels
    // (LoadedAssetBundle == "") are untouched, and once the countdown releases
    // (state != Starting) everything behaves as stock. Deliberately mode-blind.
    [HarmonyPatch(typeof(Snowboarder), "Respawn")]
    public class Snowboarder_Respawn
    {
        private static string loggedScene;

        // Set by the prefix and consumed by the postfix on the same call stack, exactly
        // like PendingOobCollider — Respawn resets state to Riding, so by the time the
        // postfix runs the rider no longer looks dead and the route is unrecoverable.
        internal static bool PendingCrash;

        [HarmonyPrefix]
        public static bool Prefix(Snowboarder __instance)
        {
            // Snapshot BEFORE the game moves them — Respawn resets state to Riding and
            // deathTimer to 0, so a postfix can no longer tell why it was called.
            if (__instance != null)
            {
                object st = Traverse.Create(__instance).Field("state").GetValue();
                PendingCrash = st != null && Convert.ToInt32(st) == 4;   // 4 = State.Dead
                PerfHarness.NotePreRespawn(__instance.transform.position, PendingCrash);
            }
            if (string.IsNullOrEmpty(Plugin.LoadedAssetBundle)) return true;
            string custom = Plugin.CustomSceneName();
            if (custom == null || SceneManager.GetActiveScene().name != custom) return true;
            object state = Traverse.Create(__instance).Field("state").GetValue();
            if (state == null || Convert.ToInt32(state) != 0) return true; // 0 = State.Starting
            if (loggedScene != custom)
            {
                loggedScene = custom;
                Plugin.Log.LogDebug($"[AIPaths] blocked pre-start Respawn on custom scene '{custom}' (state=Starting)");
            }
            return false;
        }

        // Ground truth for the perf harness's respawn report: the game telling us it
        // respawned somebody, rather than the harness inferring it from a position
        // jump big enough to clear a guessed threshold. Diagnostic only — it reads
        // position and records it, and changes nothing.
        [HarmonyPostfix]
        public static void Postfix(Snowboarder __instance, bool __runOriginal)
        {
            // Harmony runs postfixes even when a prefix skipped the original, so a
            // respawn WE blocked above must not be recorded as one that happened. Its
            // OOB flag is spent all the same, or the next real respawn inherits it.
            if (!__runOriginal || __instance == null)
            { Snowboarder_OnControllerColliderHit.PendingOobCollider = null; return; }
            bool wasOob = Snowboarder_OnControllerColliderHit.PendingOobCollider != null;
            Snowboarder_OnControllerColliderHit.PendingOobCollider = null;
            bool wasCrash = PendingCrash;
            PendingCrash = false;
            // Before NoteRespawn, so the perf harness records where the rider really
            // ended up rather than the marker the game picked and we overrode.
            if (!wasCrash) FallClamp(__instance);
            PerfHarness.NoteRespawn(__instance.transform, __instance.transform.position);
            // A rider can be both dead AND touching an OOB volume. OOB wins: it is the
            // faster loop (every frame, vs. every ~2.4 s) and the one with the tighter
            // window, so counting it on the crash tally would only slow the response.
            if (wasOob) LoopGuard(__instance, false);
            else if (wasCrash) LoopGuard(__instance, true);
        }

        // ---- respawn loop guard ----------------------------------------------
        // Escalate a rider FORWARD along the race line when respawning has stopped
        // making progress. The game can't break the loop itself because it always
        // picks the nearest marker, and the nearest marker is what put them there.
        //
        // Two routes reach here, and they loop at completely different rates:
        //   OOB   — OnControllerColliderHit respawns on every frame of contact.
        //   CRASH — the rider wrecks on geometry, respawns onto a marker sitting on
        //           that same geometry, and wrecks again. Rate-limited by the game's
        //           CanRespawn() (deathTimer > 1.5), so it cycles every ~2.4 s.
        // Hence a counter per route: one window cannot cover both.
        class Loop
        {
            public float oobT, crashT;
            public int oobHits, crashHits;
            // Marker index the last crash-respawn landed near, so the crash route can
            // tell a loop from a rider who is simply crashing a lot on the way down.
            public int crashNear = -1;
            // Same idea for the fall clamp: the marker the last clamped fall was
            // taken from, and how many falls in a row have come from around it.
            public int fallNear = -1, fallHits;
        }
        static readonly Dictionary<int, Loop> _loops = new Dictionary<int, Loop>();
        static string _guardLoggedOob, _guardLoggedCrash;

        // Where each rider last had the course under them, written every physics
        // tick by Snowboarder_LastGround, and moved by every teleport of ours so the
        // clamp never undoes a shunt the loop guard just made.
        internal static readonly Dictionary<int, Vector3> LastGround = new Dictionary<int, Vector3>();

        // Consecutive OOB respawns within this window count as one loop. The real
        // loop fires every frame, so anything above a couple of frames is generous;
        // a genuine crash-respawn can't repeat this fast because CanRespawn() needs
        // deathTimer > 1.5 — which is exactly why the crash route needs its own,
        // much wider window (Plugin.respawnCrashLoopWindow).
        const float LoopWindow = 1.0f;
        const int LoopTrigger = 3;

        // How far down the marker list a crashing rider must get before the crash
        // counter treats it as progress rather than as the same wreck again. It has
        // to exceed RespawnLoopAdvance, or the guard's own shunt would read as
        // progress and reset the counter it just fired.
        const int CrashProgressBand = 2;

        public static void ResetLoopState()
        { _loops.Clear(); LastGround.Clear(); _guardLoggedOob = _guardLoggedCrash = null; }

        // ---- fall clamp --------------------------------------------------------
        // A non-crash respawn lands on the marker nearest to wherever the rider
        // ended up, and for a rider who fell THROUGH the terrain that is under the
        // mountain — nearest to a marker far down-course. Measured on SSX3 The
        // Throne: dropped through at the start line (-9023, 18495, 7980), fell
        // 30.7 s to y 12746, respawned 92% of the way down, finished in ~46 s,
        // in >=5 races. Pull them back to where they last rode.
        //
        // How far past the last contact a respawn may land before it counts as
        // course the rider never rode. A judgement call, not measured: it matches
        // RespawnLoopAdvance's default, and at ~25-35 m a marker it leaves alone a
        // rider who goes out of bounds just past a lip.
        const int FallSlack = 3;
        // A fall costs the whole drop each time (~30 s on The Throne), so the loop
        // is declared on the SECOND fall from the same place, not the third.
        const int FallLoopTrigger = 2;

        static void FallClamp(Snowboarder sb)
        {
            if (!Plugin.respawnFallClamp.Value) return;
            if (string.IsNullOrEmpty(Plugin.LoadedAssetBundle)) return;
            var marks = Plugin.RaceLineRespawns;
            if (marks.Count < 2) return;

            int id = sb.GetInstanceID();
            Vector3 ground;
            if (!LastGround.TryGetValue(id, out ground)) return;
            int from = NearestMarker(ground);
            int landed = NearestMarker(sb.transform.position);
            if (from < 0 || landed < 0 || landed - from <= FallSlack) return;

            // Progress-based, not time-based like the other two routes: a fall loop
            // repeats once per drop, which no fixed window can be sized for across
            // courses of different heights. Two falls from around the same marker is
            // a loop however long they took. Applies to the player too, on the crash
            // route's reasoning — a rider who keeps dropping through the same hole is
            // not making progress there is anything to protect.
            int adv = Mathf.Max(1, Plugin.respawnLoopAdvance.Value);
            Loop st;
            if (!_loops.TryGetValue(id, out st)) { st = new Loop(); _loops[id] = st; }
            bool sameSpot = st.fallNear >= 0
                && Mathf.Abs(from - st.fallNear) <= adv + CrashProgressBand;
            st.fallHits = sameSpot ? st.fallHits + 1 : 1;
            st.fallNear = from;

            int dest = from;
            string how = "back to last contact";
            if (st.fallHits >= FallLoopTrigger)
            {
                // Forward, but never past where the game itself wanted them. Stay
                // armed at the new spot so a hole there escalates on its first fall.
                dest = Mathf.Min(from + adv, landed);
                st.fallNear = dest;
                st.fallHits = FallLoopTrigger - 1;
                how = $"fall loop x{FallLoopTrigger}, stepped {dest - from} forward";
            }
            var target = marks[dest];
            if (target == null) return;
            Vector3 was = sb.transform.position;
            Teleport(sb, target);
            // Info, not Debug, and every time: a fall means the rider left the world,
            // which is a collision bug worth finding, and it happens at most once a
            // drop — nothing like the OOB loop's once per frame.
            Plugin.Log.LogInfo($"[Respawn] fall clamp: {sb.name} [{id}] respawned at marker "
                + $"{landed} of {marks.Count} {was} but last rode near marker {from} {ground} "
                + $"-> marker {dest} ({how})");
        }

        static int NearestMarker(Vector3 p)
        {
            var marks = Plugin.RaceLineRespawns;
            int near = -1;
            float best = float.MaxValue;
            for (int i = 0; i < marks.Count; i++)
            {
                if (marks[i] == null) continue;
                float d = (marks[i].position - p).sqrMagnitude;
                if (d < best) { best = d; near = i; }
            }
            return near;
        }

        // A CharacterController caches its own position and will drag the rider
        // back to it, so a bare transform write does not stick — toggle it around
        // the move.
        static void Teleport(Snowboarder sb, Transform target)
        {
            var cc = sb.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            sb.transform.position = target.position;
            sb.transform.rotation = target.rotation;
            if (cc != null) cc.enabled = true;
            LastGround[sb.GetInstanceID()] = target.position;
        }

        static void LoopGuard(Snowboarder sb, bool crashRoute)
        {
            if (!Plugin.respawnLoopGuard.Value) return;
            // Custom maps only, and only when BuildScene actually produced race-line
            // markers — with no ordered list there is no "forward" to escalate to, so
            // the guard fails to stock rather than guessing at a destination.
            if (string.IsNullOrEmpty(Plugin.LoadedAssetBundle)) return;
            var marks = Plugin.RaceLineRespawns;
            if (marks.Count < 2) return;

            // The OOB route leaves the human alone by default: being moved down-course
            // is a real advantage in a race, and a CPU rider losing it costs nothing.
            // The crash route has no such trade to weigh — the rider is not going
            // anywhere at all — so it always applies to the player and the CPU alike.
            bool isAI = Traverse.Create(sb).Field("snowboardController").GetValue() is AISnowboarder;
            if (!crashRoute && !isAI && !Plugin.respawnLoopGuardPlayer.Value) return;

            int adv = Mathf.Max(1, Plugin.respawnLoopAdvance.Value);

            // Nearest marker to where the game just dropped them, then step forward.
            // Escalating from the CURRENT nearest (rather than a remembered index)
            // is what makes repeated firings compound: each one starts from wherever
            // the last one landed. Computed before the trigger test because the crash
            // route needs it to judge progress; the scan is linear over the marker
            // list, which even the pathological OOB case can afford.
            Vector3 p = sb.transform.position;
            int near = NearestMarker(p);
            if (near < 0) return;

            int id = sb.GetInstanceID();
            Loop st;
            if (!_loops.TryGetValue(id, out st)) { st = new Loop(); _loops[id] = st; }

            float now = Time.unscaledTime;
            int hits;
            if (crashRoute)
            {
                // Dying repeatedly is only a loop if it keeps happening in the same
                // place. A rider who wrecks three times while still working down the
                // mountain is having a bad run, not stuck, and moving them forward
                // would be handing out course for free.
                bool sameSpot = st.crashNear >= 0
                    && Mathf.Abs(near - st.crashNear) <= adv + CrashProgressBand;
                bool inWindow = now - st.crashT <= Plugin.respawnCrashLoopWindow.Value;
                st.crashHits = (inWindow && sameSpot) ? st.crashHits + 1 : 1;
                st.crashT = now;
                st.crashNear = near;
                hits = st.crashHits;
            }
            else
            {
                st.oobHits = (now - st.oobT <= LoopWindow) ? st.oobHits + 1 : 1;
                st.oobT = now;
                hits = st.oobHits;
            }
            if (hits < LoopTrigger) return;
            if (crashRoute) st.crashHits = 0; else st.oobHits = 0;

            int dest = Mathf.Min(near + adv, marks.Count - 1);
            // Already at the last marker, which RespawnLine stops just short of the
            // finish: shunting them further is meaningless. Leave them to the game.
            if (dest == near) return;
            var target = marks[dest];
            if (target == null) return;
            Teleport(sb, target);

            // Named per route, because "the guard is active" means two different
            // things to whoever is reading the log to decide what to fix: an OOB
            // loop points at a misplaced reset volume, a crash loop at geometry
            // sitting on the race line.
            string route = crashRoute ? "crash" : "OOB";
            string scene = Plugin.CustomSceneName();
            string logged = crashRoute ? _guardLoggedCrash : _guardLoggedOob;
            if (logged != scene)
            {
                if (crashRoute) _guardLoggedCrash = scene; else _guardLoggedOob = scene;
                Plugin.Log.LogInfo($"[Respawn] {route} loop guard active on '{scene}' — "
                    + $"escalating wedged riders {adv} marker(s) down-course "
                    + $"(first: {sb.name} [{id}] at {p} -> marker {dest} of {marks.Count})");
            }
            Plugin.Log.LogDebug($"[Respawn] {route} loop guard moved {sb.name} [{id}] "
                + $"marker {near} -> {dest} ({route} respawn x{LoopTrigger})");
        }
    }

    // Feeds Snowboarder_Respawn's fall clamp: the last place each rider had the
    // course under them. ⚠️ Read off the state field, and only states that mean
    // contact — a rider falling through the terrain reads Airborne for the whole
    // drop (SmartAI's jump log saw 30.7 s of it on The Throne), so the fall can
    // never overwrite the spot they fell from. Grounded also needs the controller
    // to agree, because a wedged rider can read Grounded with nothing under it
    // (the perf harness's WEDGED probe: "state Grounded ... grounded False").
    [HarmonyPatch(typeof(Snowboarder), "FixedUpdate")]
    public class Snowboarder_LastGround
    {
        static readonly System.Reflection.FieldInfo fState =
            AccessTools.Field(typeof(Snowboarder), "state");
        static readonly System.Reflection.FieldInfo fCC =
            AccessTools.Field(typeof(Snowboarder), "characterController");

        // Runs before Snowboarder_AirborneStuckGuard, whose forced Land() could
        // otherwise be what this records. Without a priority the two run in
        // source-file order, which a file split would silently change.
        [HarmonyPostfix, HarmonyPriority(Priority.HigherThanNormal)]
        public static void Postfix(Snowboarder __instance)
        {
            if (!Plugin.respawnFallClamp.Value || __instance == null || fState == null) return;
            if (string.IsNullOrEmpty(Plugin.LoadedAssetBundle)) return;
            object s = fState.GetValue(__instance);
            if (s == null) return;
            int state = Convert.ToInt32(s);
            if (state == 1)             // Grounded
            {
                var cc = fCC == null ? null : fCC.GetValue(__instance) as CharacterController;
                if (cc == null || !cc.enabled || !cc.isGrounded) return;
            }
            // Starting (0) is the grid, which is where a start-line faller came from;
            // Grinding (3) is a rail, which is course.
            else if (state != 0 && state != 3) return;
            Snowboarder_Respawn.LastGround[__instance.GetInstanceID()] = __instance.transform.position;
        }
    }

    // The rider who is standing still and doing tricks.
    //
    // Snowboarder.OnControllerColliderHit is the ONLY caller of Land() in the whole
    // assembly, and neither of the two ways a rider gets pinned reaches it. Rest on
    // a Wall-layer (8) prop and the Wall branch returns early — it can deflect you,
    // and it can Die() you on a velocity·normal < -30 impact, but it has no path
    // that lands you. Get pinned against ANOTHER RIDER (layer 3) and there is no
    // ground contact to land on at all. Either way the state machine stays in
    // Airborne — which is also the game's trick state — so the rider flips in place
    // forever with no input that escapes.
    //
    // ⚠️ Detect this by POSITION, never by the rider's own state fields. The first
    // cut of this guard tested "Airborne AND characterController.isGrounded AND
    // speed ≈ 0", taken from an AI wedged on terrain, and it never fired once: the
    // player's wedge was against another rider, where the contact normal is
    // horizontal, so isGrounded reads FALSE and gravity winds the velocity field up
    // to 47 u/s while Move is fully blocked. The one thing both wedges share — and
    // the only thing the perf harness's own WEDGED probe tests — is that the
    // transform stops moving. Everything else disagrees between the two cases.
    //
    // Break the deadlock in two steps: force the landing first, because a rider who
    // can simply ride off keeps their run; only if that does not take do we wreck
    // them and let Respawn (and the crash loop guard above) carry them clear.
    [HarmonyPatch(typeof(Snowboarder), "FixedUpdate")]
    public class Snowboarder_AirborneStuckGuard
    {
        class Stuck { public Vector3 anchor; public float stuckT; public int stage; public bool init; }
        static readonly Dictionary<int, Stuck> _stuck = new Dictionary<int, Stuck>();
        static string _loggedScene;

        // NET travel over a whole window, measured the way PerfHarness measures it
        // (StuckNet/StuckWindow), just faster. It has to be net rather than a "did
        // it move this tick" test, because the wedge that matters is not motionless:
        // the player's read 47 u/s of CharacterController velocity while going
        // nowhere at all. A rider bouncing inside a small region defeats any
        // per-tick or small-radius test, and defeats a re-anchoring one too — every
        // excursion past the radius resets the clock and it never accumulates.
        // A healthy descent covers ~60 u/s, so 25 u over the default 2 s window is
        // ~5x clear of anything actually racing.
        const float StuckNet = 25f;

        // Cached rather than Traverse'd: this runs for every rider every physics
        // tick, which on the chain is 9 riders x 50 Hz.
        static readonly System.Reflection.FieldInfo fState =
            AccessTools.Field(typeof(Snowboarder), "state");
        static readonly System.Reflection.FieldInfo fVel =
            AccessTools.Field(typeof(Snowboarder), "velocity");
        static readonly System.Reflection.FieldInfo fCC =
            AccessTools.Field(typeof(Snowboarder), "characterController");
        static readonly System.Reflection.FieldInfo fPoints =
            AccessTools.Field(typeof(Snowboarder), "activePoints");
        static readonly System.Reflection.MethodInfo mLand =
            AccessTools.Method(typeof(Snowboarder), "Land", new[] { typeof(float) });
        static readonly System.Reflection.MethodInfo mDie =
            AccessTools.Method(typeof(Snowboarder), "Die");

        // Static, so it outlives the scene: instance ids are not reused across a
        // load, but the dictionary would otherwise grow by a full grid per map.
        public static void ResetState() { _stuck.Clear(); _loggedScene = null; }

        [HarmonyPostfix]
        public static void Postfix(Snowboarder __instance)
        {
            if (!Plugin.airborneStuckGuard.Value || __instance == null) return;
            // Custom maps only, for the same reason the rail projection fix is
            // config-gated: a Hook install must not change how the levels everybody
            // already has play.
            if (string.IsNullOrEmpty(Plugin.LoadedAssetBundle)) return;
            // Any reflection miss and we do nothing at all, rather than run half a
            // watchdog that can wreck a rider but never land one.
            if (fState == null || mLand == null || mDie == null) return;

            int id = __instance.GetInstanceID();
            object s = fState.GetValue(__instance);
            int state = s == null ? -1 : Convert.ToInt32(s);
            // Starting (0) is the grid before the gun, Dead (4) is already mid-
            // respawn and Done (5) is a finished rider coasting out — all three are
            // motionless on purpose and none of them can be helped by this.
            if (state == 0 || state == 4 || state == 5 || state < 0)
            {
                _stuck.Remove(id);
                return;
            }

            Vector3 p = __instance.transform.position;
            Stuck st;
            if (!_stuck.TryGetValue(id, out st)) { st = new Stuck(); _stuck[id] = st; }
            if (!st.init) { st.init = true; st.anchor = p; st.stuckT = 0f; return; }

            // Fixed windows, closed only at the end: sampling net displacement once
            // per window is what makes an oscillating rider readable at all.
            st.stuckT += Time.fixedDeltaTime;
            if (st.stuckT < Plugin.airborneStuckSeconds.Value) return;
            float net = (p - st.anchor).magnitude;
            st.anchor = p;
            st.stuckT = 0f;
            if (net >= StuckNet)
            {
                st.stage = 0;       // they are racing; forget any escalation
                return;
            }

            // Not moving is only a BUG while the rider is airborne — stopping on a
            // flat section is a thing players do on purpose, and a Grounded rider
            // parked in the snow must never be touched. Once we have intervened
            // though, stage keeps us armed through the state the intervention put
            // them in: a forced landing makes them Grounded, and if that did not
            // free them the escalation still has to reach the wreck.
            if (state != 2 && st.stage == 0) return;      // 2 = State.Airborne

            string what;
            if (st.stage == 0)
            {
                st.stage = 1;
                // Land() cashes the trick pot before it flips the state (it calls
                // CashPoints() one instruction earlier), and a rider who has been
                // spinning in place for two seconds is sitting on a fortune in spin
                // points. Handing that over would turn the bug into a scoring
                // exploit, so the pot is emptied first.
                if (fPoints != null) fPoints.SetValue(__instance, 0f);
                mLand.Invoke(__instance, new object[] { 0f });
                what = "forced landing";
            }
            else
            {
                st.stage = 0;
                mDie.Invoke(__instance, null);
                what = "wrecked (landing did not take)";
            }

            // The state fields go in the log rather than in the test: they are what
            // told us the first cut was testing the wrong thing, and if a third
            // flavour of wedge turns up this is the line that will name it.
            string detail = Describe(__instance, state) + " net " + net.ToString("F1")
                + " near " + Nearby(__instance, p);
            string scene = Plugin.CustomSceneName();
            if (_loggedScene != scene)
            {
                _loggedScene = scene;
                Plugin.Log.LogInfo($"[Stuck] wedge watchdog active on '{scene}' — riders "
                    + $"who stop moving for {Plugin.airborneStuckSeconds.Value}s while "
                    + $"airborne are landed, then wrecked if that does not free them "
                    + $"(first: {__instance.name} [{id}] at {p} {detail})");
            }
            Plugin.Log.LogInfo($"[Stuck] {what}: {__instance.name} [{id}] at {p} {detail}");
        }

        // Diagnostic only — never a gate. See the ⚠️ on the class.
        static string Describe(Snowboarder sb, int state)
        {
            string nm = state == 1 ? "Grounded" : state == 2 ? "Airborne"
                      : state == 3 ? "Grinding" : state.ToString();
            var cc = fCC == null ? null : fCC.GetValue(sb) as CharacterController;
            object v = fVel == null ? null : fVel.GetValue(sb);
            float speed = v is Vector3 ? ((Vector3)v).magnitude : -1f;
            float ccv = cc != null ? cc.velocity.magnitude : -1f;
            return $"[state {nm} grounded {(cc != null && cc.enabled && cc.isGrounded)} "
                 + $"vel {speed:F1} ccvel {ccv:F1}]";
        }

        // ⚠️ EXCLUDES THE RIDER'S OWN COLLIDERS. PerfHarness.ProbeWedge does not,
        // and that cost a wrong diagnosis: its list is dominated by the rider's own
        // capsule (layer 3, named "Snowboarder(Clone)" like every other rider), its
        // own board and its own chase camera, all at ~0 u, which reads exactly like
        // being pinned against another rider. Only runs when the guard fires, so
        // the allocation and the sphere cast are off the hot path.
        //
        // Deliberately does NOT sort by ClosestPoint: that throws on the non-convex
        // MeshColliders the terrain is made of, and the resulting error storm is
        // what buries the useful lines in Player.log.
        static string Nearby(Snowboarder sb, Vector3 p)
        {
            var hits = Physics.OverlapSphere(p, 12f, ~0, QueryTriggerInteraction.Collide);
            if (hits == null || hits.Length == 0) return "nothing within 12u";
            Transform root = sb.transform.root;
            var names = new List<string>();
            int own = 0;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h == null) continue;
                if (h.transform.root == root) { own++; continue; }
                if (names.Count < 5)
                    names.Add($"{h.name}(L{h.gameObject.layer}{(h.isTrigger ? ",trig" : "")})");
            }
            if (names.Count == 0)
                return $"nothing within 12u but its own {own} collider(s) — not held by "
                     + "geometry, look at the rider's own state";
            return $"{string.Join(", ", names.ToArray())} (+{own} own)";
        }
    }
}
