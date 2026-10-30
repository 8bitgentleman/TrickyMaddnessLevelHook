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
        static void SpawnPerfHarnessIfArmed()
        {
            // Respawn / OOB instrumentation. Last, so it belongs to the scene the
            // player will actually ride and dies with it (which is what guarantees
            // its report gets written).
            // Either switch spawns it: the two halves are independent reports, but
            // both need the same rider resolution (LevelManager.player), which is
            // the one expensive thing the harness does.
            if ((perfHarness != null && perfHarness.Value)
                || (riderTelemetry != null && riderTelemetry.Value))
                PerfHarness.Spawn();
        }
    }

    // Respawn / out-of-bounds instrumentation for custom maps (Debug/PerfHarness,
    // off by default). Written for the SSX 3 whole-mountain chain, whose frame rate
    // turned out to be a respawn storm rather than a rendering cost: riders in
    // contact with a converted reset zone were respawned every frame of contact,
    // back into the same volume. See HOOK_NOTES.md §23.
    //
    // What used to live here — the shadow/MSAA/render-scale/cascade toggles, the
    // region streaming, the camera-off and script-census probes, the Ctrl+B sweep
    // and Ctrl+T trend, and the on-screen readout that drove them — is GONE. Every
    // rendering ablation it measured came back inside the noise floor, and the
    // answer was on the physics side. Don't reinstate it to re-chase draw calls;
    // CLAUDE.md records that dead end explicitly.
    //
    // What survives is the part that found the bug and is still the way to tell
    // whether a fix worked: the game's own respawns, split by route, clustered by
    // location, with the offending OOB colliders named so the LEVEL DATA can be
    // fixed at source instead of the Hook papering over it forever.
    public class PerfHarness : MonoBehaviour
    {
        static PerfHarness _inst;

        public static void Spawn() { Spawn(null); }

        // `sceneLabel` names the CSV on a built-in level, where LoadedAssetBundle
        // is empty.
        public static void Spawn(string sceneLabel)
        {
            // One harness per LEVEL, and the level is what the key names. A plain
            // `if (_inst != null) return` is wrong and shipped once: the harness is
            // supposed to die with its scene, but on a ride of forest THEN braid01
            // it did not, so braid01's Spawn silently returned onto the live forest
            // harness and both laps landed in ONE CSV -- 99s of forest and 71s of
            // braid01, under braid01's name and braid01's worldScale. The lap that
            // was supposed to be measured is unrepeatable by then, so this does not
            // trust the scene to have torn the old one down: a different key means
            // flush now. DestroyImmediate, not Destroy, because a deferred destroy
            // leaves two harnesses sampling the same frames and lets the outgoing
            // one's OnDestroy run after the replacement has already registered.
            string key = !string.IsNullOrEmpty(sceneLabel)
                ? sceneLabel
                : (Plugin.LoadedAssetBundle ?? "");
            if (_inst != null)
            {
                if (_inst._ownerKey == key) return;
                UnityEngine.Object.DestroyImmediate(_inst.gameObject);
                _inst = null;
            }
            // Static, so they outlive the harness they belong to: a second map would
            // otherwise open with the previous map's respawns already in the report.
            GameRespawns.Clear();
            OobHits.Clear();
            OobWhere.Clear();
            var go = new GameObject("HookPerfHarness");
            // Deliberately NOT DontDestroyOnLoad: dying with the scene is what
            // guarantees OnDestroy runs and the report is written on level exit.
            _inst = go.AddComponent<PerfHarness>();
            _inst._ownerKey = key;
            // Say so at the START of the run. Otherwise the only evidence recording
            // ever happened is a line printed on the way out, i.e. after the lap
            // that was supposed to be measured is already unrepeatable.
            if (Plugin.riderTelemetry != null && Plugin.riderTelemetry.Value)
                Plugin.Log.LogInfo("[Telemetry] armed — sampling the player every "
                    + "physics step; the CSV is written when you leave this level.");
        }

        void Update()
        {
            HandleKeys();
            TrackRespawns();
            TrackRoutes();
            TrackTricks();
            TrackBoost();
        }

        // Which AIPath3D each AI rider is on, sampled twice a second: the only
        // evidence of whether riders actually spread across the branch routes
        // (a whole-course race line is inside-everywhere, so without the
        // SplitAndLink wiring nobody ever left it).
        static readonly System.Reflection.FieldInfo fController = AccessTools.Field(typeof(Snowboarder), "snowboardController");
        static readonly System.Reflection.FieldInfo fPathLoc = AccessTools.Field(typeof(AISnowboarder), "m_pathLocation");
        readonly Dictionary<Snowboarder, List<string>> _routes = new Dictionary<Snowboarder, List<string>>();
        float _nextRouteSample;

        void TrackRoutes()
        {
            if (Time.time < _nextRouteSample || fController == null || fPathLoc == null) return;
            _nextRouteSample = Time.time + 0.5f;
            foreach (var sb in FindObjectsOfType<Snowboarder>())
            {
                var ai = fController.GetValue(sb) as AISnowboarder;
                if (ai == null) continue;
                var path = ((AIPath3D.PathLocation)fPathLoc.GetValue(ai)).path;
                if (path == null) continue;
                if (!_routes.TryGetValue(sb, out var seq)) _routes[sb] = seq = new List<string>();
                string name = path.gameObject.name;
                if (seq.Count == 0 || seq[seq.Count - 1] != name) seq.Add(name);
            }
        }

        void DumpRoutes()
        {
            if (_routes.Count == 0) return;
            var branches = new HashSet<string>();
            int r = 0;
            foreach (var kv in _routes)
            {
                int onBranch = 0;
                foreach (var n in kv.Value)
                    if (n.Contains("sidecar")) { onBranch++; branches.Add(n); }
                string seq = string.Join(" > ", kv.Value.ConvertAll(n => n.Replace("AIPath3D (", "").Replace(")", "")).ToArray());
                Plugin.Log.LogInfo($"[AIPaths] AI {r++}: {kv.Value.Count} route change(s), {onBranch} on branches: {seq}");
            }
            Plugin.Log.LogInfo($"[AIPaths] route use: {_routes.Count} AI rider(s) rode {branches.Count} distinct branch route(s)");
        }

        // Why the stock COM look passive (no tuck, few grabs). Every gate below is
        // read straight off the game's own decision (AISnowboarder.GetCrouchingInput
        // / GetGrabInput / GetJumpHeldInput, Snowboarder.UpdateGrounded IL_0828–0863):
        // tuck needs the random m_crouchInput roll AND |steeringSpring| < 0.5 AND
        // boardPress == 0 AND !stopping, so time-weighting each gate while grounded
        // says which one is eating the tuck. Rolls happen at spawn and on each
        // landing only, so competence (their odds) is logged too.
        static readonly System.Reflection.FieldInfo fCrouchRoll = AccessTools.Field(typeof(AISnowboarder), "m_crouchInput");
        static readonly System.Reflection.FieldInfo fCompSq = AccessTools.Field(typeof(AISnowboarder), "m_competenceSq");
        static readonly System.Reflection.FieldInfo fGrabRoll = AccessTools.Field(typeof(AISnowboarder), "m_grabInput");
        static readonly System.Reflection.FieldInfo fWindup = AccessTools.Field(typeof(AISnowboarder), "m_windup");
        static readonly System.Reflection.FieldInfo fSteerSpring = AccessTools.Field(typeof(Snowboarder), "steeringSpring");
        static readonly System.Reflection.FieldInfo fCrouching = AccessTools.Field(typeof(Snowboarder), "crouching");
        static readonly System.Reflection.FieldInfo fBoardPress = AccessTools.Field(typeof(Snowboarder), "boardPress");
        static readonly System.Reflection.FieldInfo fStopping = AccessTools.Field(typeof(Snowboarder), "stopping");
        static readonly System.Reflection.FieldInfo fIsGrabbing = AccessTools.Field(typeof(Snowboarder), "isGrabbing");

        class TrickTally
        {
            public float ground, rollOn, steerBlock, pressBlock, stopBlock, crouching;
            public float air, grabRollOn, grabbing, windup, compSq;
            public int grabs, airs; public bool wasGrabbing, wasAir;
        }
        readonly Dictionary<Snowboarder, TrickTally> _tricks = new Dictionary<Snowboarder, TrickTally>();
        bool _trickProbeWarned;

        void TrackTricks()
        {
            if (fController == null || fCrouchRoll == null || fCompSq == null || fGrabRoll == null
                || fWindup == null || fSteerSpring == null || fCrouching == null || fBoardPress == null
                || fStopping == null || fIsGrabbing == null)
            {
                if (!_trickProbeWarned) { Plugin.Log.LogWarning("[AITricks] a probed field is gone — trick probe off"); _trickProbeWarned = true; }
                return;
            }
            float dt = Time.deltaTime;
            foreach (var sb in FindObjectsOfType<Snowboarder>())
            {
                var ai = fController.GetValue(sb) as AISnowboarder;
                if (ai == null || sb.Finished() || sb.Dead()) continue;
                if (!_tricks.TryGetValue(sb, out var t)) _tricks[sb] = t = new TrickTally();
                t.compSq = (float)fCompSq.GetValue(ai);
                bool air = sb.Airborne();
                if (sb.Grounded())
                {
                    t.ground += dt;
                    bool roll = (bool)fCrouchRoll.GetValue(ai);
                    var spring = fSteerSpring.GetValue(sb) as Spring;
                    if (roll)
                    {
                        t.rollOn += dt;
                        if (spring != null && Mathf.Abs(spring.GetValue()) >= 0.5f) t.steerBlock += dt;
                        if ((float)fBoardPress.GetValue(sb) != 0f) t.pressBlock += dt;
                        if ((bool)fStopping.GetValue(sb)) t.stopBlock += dt;
                    }
                    if ((bool)fCrouching.GetValue(sb)) t.crouching += dt;
                    if ((bool)fWindup.GetValue(ai)) t.windup += dt;
                }
                else if (air)
                {
                    t.air += dt;
                    if ((int)fGrabRoll.GetValue(ai) != 0) t.grabRollOn += dt;
                    bool g = (bool)fIsGrabbing.GetValue(sb);
                    if (g) t.grabbing += dt;
                    if (g && !t.wasGrabbing) t.grabs++;
                    t.wasGrabbing = g;
                }
                if (air && !t.wasAir) t.airs++;
                t.wasAir = air;
            }
        }

        static string Pct(float part, float whole) { return whole > 0f ? (100f * part / whole).ToString("0") + "%" : "-"; }

        // Where the pro COM lose 300 m+ to a human who tucks no more than they do:
        // boost is the one speed source a COM can earn but is never scripted to aim
        // for (CashPoints grants 1.75–3.5 s of +6 along velocity at 800/1800/3400/
        // 5000 active points). Logged for EVERY rider, human included, so the gap
        // reads straight off the rows.
        static readonly System.Reflection.FieldInfo fBoostTimer = AccessTools.Field(typeof(Snowboarder), "boostTimer");
        static readonly System.Reflection.FieldInfo fBoostTier = AccessTools.Field(typeof(Snowboarder), "boostTier");
        static readonly System.Reflection.FieldInfo fBanked = AccessTools.Field(typeof(Snowboarder), "bankedPoints");

        class BoostTally
        {
            public bool human, hasPos; public float time, boosting, dist, banked, lastTimer;
            public int grants; public int[] tiers = new int[4]; public Vector3 lastPos;
        }
        readonly Dictionary<Snowboarder, BoostTally> _boosts = new Dictionary<Snowboarder, BoostTally>();

        void TrackBoost()
        {
            if (fController == null || fBoostTimer == null || fBoostTier == null || fBanked == null) return;
            float dt = Time.deltaTime;
            foreach (var sb in FindObjectsOfType<Snowboarder>())
            {
                BoostTally t;
                if (sb.Finished() || sb.Dead()) { if (_boosts.TryGetValue(sb, out t)) t.hasPos = false; continue; }
                if (!_boosts.TryGetValue(sb, out t))
                    _boosts[sb] = t = new BoostTally { human = !(fController.GetValue(sb) is AISnowboarder) };
                t.time += dt;
                float timer = (float)fBoostTimer.GetValue(sb);
                if (timer > 0f) t.boosting += dt;
                // A grant resets the timer upward; natural decay only ever lowers it.
                if (timer > t.lastTimer + 0.01f)
                {
                    t.grants++;
                    t.tiers[Mathf.Clamp((int)fBoostTier.GetValue(sb), 0, 3)]++;
                }
                t.lastTimer = timer;
                t.banked = (float)fBanked.GetValue(sb);
                Vector3 pos = sb.transform.position;
                // Skip respawn teleports: nothing rides 500 u/s.
                float step = (pos - t.lastPos).magnitude;
                if (t.hasPos && dt > 0f && step / dt < 500f) t.dist += step;
                t.lastPos = pos; t.hasPos = true;
            }
        }

        void DumpBoost()
        {
            if (_boosts.Count == 0) return;
            Plugin.Log.LogInfo("[AIBoost] per rider — share of riding time boosting, boosts cashed [by tier 800/1800/3400/5000], banked points, mean speed");
            int r = 0;
            foreach (var t in _boosts.Values)
                Plugin.Log.LogInfo($"[AIBoost] {(t.human ? "PLAYER" : "AI " + r++)}: {t.time:0}s ridden, boosting {Pct(t.boosting, t.time)}, "
                    + $"{t.grants} boost(s) [{t.tiers[0]}/{t.tiers[1]}/{t.tiers[2]}/{t.tiers[3]}], banked {t.banked:0}, "
                    + $"mean speed {(t.time > 0f ? t.dist / t.time : 0f):0.0} u/s");
        }

        void DumpTricks()
        {
            if (_tricks.Count == 0) return;
            Plugin.Log.LogInfo("[AITricks] per AI rider — grounded: tuck-roll on / of which blocked by steering≥0.5, board press, stopping / actually tucking / on a windup knot; airborne: grab-roll on / grabbing");
            int r = 0;
            foreach (var t in _tricks.Values)
                Plugin.Log.LogInfo($"[AITricks] AI {r++}: competence² {t.compSq:0.00} | ground {t.ground:0}s: roll {Pct(t.rollOn, t.ground)}, "
                    + $"blocked steer {Pct(t.steerBlock, t.rollOn)} press {Pct(t.pressBlock, t.rollOn)} stop {Pct(t.stopBlock, t.rollOn)}, "
                    + $"TUCKING {Pct(t.crouching, t.ground)}, windup {Pct(t.windup, t.ground)} | air {t.air:0}s over {t.airs} jump(s): "
                    + $"grab roll {Pct(t.grabRollOn, t.air)}, grabbing {Pct(t.grabbing, t.air)} ({t.grabs} grab(s))");
        }

        void HandleKeys()
        {
            if (!Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl)) return;
            // Ctrl+U dumps the report mid-ride. OnDestroy dumps it again on exit, so
            // this is only for reading it without ending the run.
            if (Input.GetKeyDown(KeyCode.U)) DumpRespawns();
        }

        // Every rider, plus which index is the human.
        //
        // ⚠️ The human is `LevelManager.player`, read off the game — NOT the rider
        // nearest the camera. The camera heuristic this replaced is wrong exactly
        // when it matters: the whole field is bunched at the start gate when the
        // watch list locks, so it can name any of them, and a report that says
        // "rider 0" without knowing whether rider 0 is the player is unreadable.
        List<Snowboarder> FindRiders(out int human)
        {
            var all = new List<Snowboarder>(FindObjectsOfType<Snowboarder>());
            human = -1;
            var lm = FindObjectOfType<LevelManager>();
            var player = lm != null
                ? Traverse.Create(lm).Field("player").GetValue() as Snowboarder
                : null;
            if (player != null)
                for (int i = 0; i < all.Count; i++)
                    if (ReferenceEquals(all[i], player)) { human = i; break; }
            return all;
        }

        // ---- respawn tracking ---------------------------------------------------
        // Where riders respawn, and whether the loops cluster. A respawn is detected
        // as a POSITION JUMP — a discontinuity far larger than the rider could have
        // travelled in one frame — so this needs to know nothing about the game's
        // respawn code and cannot be broken by an update renaming it.
        //
        // This exists to answer one question before any fix is written: if the loops
        // pile up at two or three places, they are bad respawn nodes in OUR merged
        // chain data and the fix is the data. If they are scattered, it is the
        // game's respawn logic and the fix is escalation at runtime.
        //
        // ⚠️ This detector UNDER-counts badly and has twice agreed with a wrong
        // conclusion: it reported 4 events against a run that turned out to have
        // 3231 real respawns. Believe the game-truth list below over this one; it
        // is kept only as the calibration the threshold never had.
        struct Respawn { public int rider; public Vector3 to; public float t; }

        Transform[] _watch;
        Snowboarder[] _sb;      // parallel to _watch — the wedge probe reads their state
        int _human = -1;        // index into _watch of LevelManager.player
        Vector3[] _watchPrev;
        float[] _watchStep;
        float _watchRetry;
        bool _watchLocked;
        readonly List<Respawn> _respawns = new List<Respawn>();

        // GAME-TRUTH respawns, recorded by Snowboarder_Respawn's postfix. The game
        // telling us it respawned somebody cannot be wrong.
        // `from` is where the rider was STUCK, which is the location a fix has to act
        // on; `to` is only where the game put them. `crashed` distinguishes the two
        // routes into Respawn, which need completely different fixes:
        //   crashed=true  -> Snowboarder.FixedUpdate, gated on CanRespawn() (state
        //                    Dead + deathTimer > 1.5). The rider wrecked on geometry.
        //   crashed=false -> either Snowboarder.OnControllerColliderHit on a layer-10
        //                    contact (NO cooldown at all — fires every frame of
        //                    contact, and Respawn picks the NEAREST marker, which for
        //                    a rider inside an OOB volume is the one that puts them
        //                    back in it), or AISnowboarder.GetSteeringInput after
        //                    1.5 s of finding no AI path. The OOB tally below is what
        //                    separates those two, by measurement rather than by
        //                    deduction from the state field.
        public struct GameRespawn
        { public Transform who; public Vector3 from, to; public float t; public bool crashed; }
        public static readonly List<GameRespawn> GameRespawns = new List<GameRespawn>();

        // Which OOB collider each respawn loop is actually firing on. The whole point
        // is to name the objects: the level data can then be fixed at source instead
        // of the Hook papering over a misplaced volume forever.
        public static readonly Dictionary<string, int> OobHits = new Dictionary<string, int>();
        public static readonly Dictionary<string, Vector3> OobWhere = new Dictionary<string, Vector3>();

        public static void NoteOobHit(string name, Vector3 at)
        {
            if (_inst == null || name == null) return;
            int n;
            OobHits.TryGetValue(name, out n);
            // Bounded: a level with pathological naming must not grow this without end.
            if (n == 0 && OobHits.Count >= 512) return;
            OobHits[name] = n + 1;
            OobWhere[name] = at;
        }

        static Vector3 _preFrom;
        static bool _preCrashed;

        public static void NotePreRespawn(Vector3 from, bool crashed)
        { _preFrom = from; _preCrashed = crashed; }

        public static void NoteRespawn(Transform who, Vector3 to)
        {
            // Nothing to report to when the harness is off; don't accumulate for a
            // reader that will never run.
            if (_inst == null) return;
            // Bounded: a pathological loop must not turn a diagnostic into a leak.
            if (GameRespawns.Count < 8192)
                GameRespawns.Add(new GameRespawn
                {
                    who = who, from = _preFrom, to = to,
                    t = Time.unscaledTime, crashed = _preCrashed,
                });
        }

        // ---- stuck / looping discrimination -------------------------------------
        // The jump detector answers "where do they respawn". It cannot answer the
        // question the first report raised: riders visibly stop racing far more
        // often than they teleport, so most of them are not respawning at all.
        //
        // Over a fixed window, compare each rider's PATH LENGTH against their NET
        // DISPLACEMENT. Healthy: both large. Wedged against geometry: both ~zero.
        // Looping (crash -> recover -> crash the same rock): path large, net ~zero.
        // Those three are indistinguishable to any single-position probe, which is
        // exactly why the stall column read them all as "stalled".
        const float StuckWindow = 10f;      // s
        const float StuckNet = 50f;         // u of net travel below which a window is "not racing"
        const float LoopPath = 150f;        // u of path length above which "not racing" means looping

        struct Stuck { public int rider; public Vector3 at; public float t; public bool looping; }

        Vector3[] _winStart;
        float[] _winPath;
        float _winClock;
        readonly List<Stuck> _stuck = new List<Stuck>();

        void TrackRespawns()
        {
            if (!_watchLocked)
            {
                // Riders are spawned after the scene builds, so retry until they
                // exist — then LOCK and never scan again.
                //
                // ⚠️ This scan is a FindObjectsOfType over a 59k-object scene. It first
                // shipped on a fixed 5 s timer, which put a periodic hitch and steady GC
                // pressure into every ride and read as "the map got slower" — the
                // instrument degrading the thing it was measuring. It may run while
                // resolving, and after that only if a rider is destroyed.
                _watchRetry -= Time.unscaledDeltaTime;
                if (_watchRetry > 0f) return;
                _watchRetry = 2f;
                int human;
                var all = FindRiders(out human);
                if (all.Count == 0) return;
                // Don't lock until LevelManager.player resolves. Locking early would
                // save a watch list that cannot say which rider is the player, which
                // is the one question the wedge report has to answer.
                if (human < 0) return;
                _human = human;
                _sb = all.ToArray();
                _watch = new Transform[_sb.Length];
                for (int i = 0; i < _sb.Length; i++) _watch[i] = _sb[i].transform;
                _watchPrev = new Vector3[_watch.Length];
                _watchStep = new float[_watch.Length];
                _winStart = new Vector3[_watch.Length];
                _winPath = new float[_watch.Length];
                for (int i = 0; i < _watch.Length; i++)
                {
                    _watchPrev[i] = _watch[i] != null ? _watch[i].position : Vector3.zero;
                    _winStart[i] = _watchPrev[i];
                }
                _winClock = 0f;
                _watchLocked = true;
                return;     // no previous frame to compare against yet
            }

            bool endWindow = (_winClock += Time.unscaledDeltaTime) >= StuckWindow;

            for (int i = 0; i < _watch.Length; i++)
            {
                var tr = _watch[i];
                if (tr == null) { _watchLocked = false; return; }
                Vector3 p = tr.position;
                float step = (p - _watchPrev[i]).magnitude;
                // A teleport is a jump both absolutely large AND far bigger than the
                // rider's own recent per-frame step, so a genuinely fast rider on a
                // slow frame does not read as a respawn. 40 units and 5x are guesses
                // sized off the ~60 u/s descent speed seen in the trend runs — not
                // calibrated against a known respawn, which is why NoteRespawn now
                // records the game's own respawns alongside this.
                bool jump = step > 40f && step > _watchStep[i] * 5f && _watchStep[i] > 0f;
                if (jump)
                {
                    _respawns.Add(new Respawn { rider = i, to = p, t = Time.unscaledTime });
                    // A teleport is not travel: counting it as path length would make
                    // a respawning rider look like a healthy one.
                    _winStart[i] = p;
                    _winPath[i] = 0f;
                }
                else _winPath[i] += step;

                if (endWindow)
                {
                    float net = (p - _winStart[i]).magnitude;
                    if (net < StuckNet)
                    {
                        bool looping = _winPath[i] > LoopPath;
                        _stuck.Add(new Stuck
                        {
                            rider = i, at = p, t = Time.unscaledTime,
                            looping = looping,
                        });
                        if (!looping) ProbeWedge(i, p);
                    }
                    _winStart[i] = p;
                    _winPath[i] = 0f;
                }

                _watchStep[i] = step;
                _watchPrev[i] = p;
            }

            if (endWindow) _winClock = 0f;
        }

        // ---- wedge probe --------------------------------------------------------
        // A WEDGED rider (net travel ~0, path length ~0) is a different bug from a
        // looping one and the respawn report is blind to it: it generates no
        // respawns at all, so nothing in GameRespawns names it and the loop guard
        // never sees it. This says what the rider IS and what it is touching.
        //
        // The three readings that separate the candidate causes, per the IL:
        //   * HUMAN vs AI — the crash respawn is `CanRespawn() &&
        //     snowboardController.GetRespawnInput()`. The AI's GetRespawnInput
        //     self-arms on a 0.5–1 s / competence timer, but the player's is a
        //     button, so a wedged player generating no respawns is the game working
        //     as designed and NOT something the Hook should be fixing.
        //   * state / deathTimer / finished — CanRespawn() needs state Dead (4),
        //     !finished, deathTimer > 1.5. A rider stuck in Riding never qualifies
        //     however long it sits there, and one that has `finished` is parked on
        //     purpose.
        //   * what it is touching — a solid collider holding the rider in place. If
        //     these come back named `Mdl_Resetzone*` on layer 10 this is the same
        //     bug as §23; anything else is new.
        const float ProbeRadius = 12f;      // u — a rider capsule is a couple of units
        const int ProbeMaxPerRider = 3;     // a whole-run wedge must not flood the log
        int[] _probed;

        void ProbeWedge(int i, Vector3 p)
        {
            if (_probed == null || _probed.Length != _watch.Length)
                _probed = new int[_watch.Length];
            if (_probed[i] >= ProbeMaxPerRider) return;
            _probed[i]++;

            var sb = _sb != null && i < _sb.Length ? _sb[i] : null;
            string who = i == _human ? "HUMAN" : "AI";
            string detail = "state ?";
            if (sb != null)
            {
                var tv = Traverse.Create(sb);
                object st = tv.Field("state").GetValue();
                object dt = tv.Field("deathTimer").GetValue();
                object fin = tv.Field("finished").GetValue();
                var cc = sb.GetComponent<CharacterController>();
                detail = $"state {st} deathTimer {dt} finished {fin}"
                    + (cc != null ? $" grounded {cc.isGrounded} vel {cc.velocity.magnitude:F1}" : "");
            }
            Plugin.Log.LogInfo($"[Perf] WEDGED rider {i} ({who}) at "
                + $"({p.x:F0},{p.y:F0},{p.z:F0})  {detail}  "
                + $"[probe {_probed[i]}/{ProbeMaxPerRider}]");

            // QueryTriggerInteraction.Collide on purpose: a trigger cannot hold a
            // rider, but knowing one is overlapping is exactly what distinguishes
            // "wedged in geometry" from "sat in a volume that keeps acting on it".
            var hits = Physics.OverlapSphere(p, ProbeRadius, ~0, QueryTriggerInteraction.Collide);
            if (hits == null || hits.Length == 0)
            {
                Plugin.Log.LogInfo($"[Perf]   nothing within {ProbeRadius:F0} u — "
                    + "not held by a collider; look at the rider's own state instead");
                return;
            }
            Array.Sort(hits, (a, b) =>
                (a.ClosestPoint(p) - p).sqrMagnitude.CompareTo((b.ClosestPoint(p) - p).sqrMagnitude));
            int shown = Math.Min(hits.Length, 8);
            Plugin.Log.LogInfo($"[Perf]   {hits.Length} collider(s) within {ProbeRadius:F0} u, nearest {shown}:");
            for (int h = 0; h < shown; h++)
            {
                var c = hits[h];
                if (c == null) continue;
                float d = (c.ClosestPoint(p) - p).magnitude;
                Plugin.Log.LogInfo($"[Perf]     {d,6:F1} u  layer {c.gameObject.layer,2}"
                    + $"{(c.isTrigger ? " trigger" : " solid  ")}  {c.gameObject.name}");
            }
        }

        // Cluster events by proximity and report the hot spots. Greedy single-pass
        // clustering: good enough to answer "two spots or twenty", which is the only
        // question being asked of it.
        void ReportClusters(string label, List<Vector3> pos, List<int> rider, List<float> time)
        {
            if (pos.Count == 0)
            {
                Plugin.Log.LogInfo($"[Perf] {label}: none");
                return;
            }
            const float R = 200f;
            var centres = new List<Vector3>();
            var counts = new List<int>();
            var who = new List<HashSet<int>>();
            var first = new List<float>();
            var last = new List<float>();
            for (int e = 0; e < pos.Count; e++)
            {
                int hit = -1;
                for (int c = 0; c < centres.Count; c++)
                    if ((centres[c] - pos[e]).sqrMagnitude < R * R) { hit = c; break; }
                if (hit < 0)
                {
                    centres.Add(pos[e]); counts.Add(1);
                    who.Add(new HashSet<int> { rider[e] });
                    first.Add(time[e]); last.Add(time[e]);
                }
                else
                {
                    counts[hit]++; who[hit].Add(rider[e]); last[hit] = time[e];
                }
            }
            var idx = new List<int>();
            for (int i = 0; i < centres.Count; i++) idx.Add(i);
            idx.Sort((a, b) => counts[b].CompareTo(counts[a]));

            Plugin.Log.LogInfo($"[Perf] {label}: {pos.Count} event(s) in "
                + $"{centres.Count} cluster(s) (radius {R:F0} u), worst first:");
            foreach (int c in idx)
            {
                var ids = new List<int>(who[c]); ids.Sort();
                var names = new List<string>();
                // The player is marked, because "rider 0" means nothing on its own —
                // whether a hot spot is the human's or the AI field's decides
                // whether it is a bug worth fixing at all.
                foreach (int id in ids)
                    names.Add(id < 0 ? "?" : (id == _human ? id + "(P)" : id.ToString()));
                Plugin.Log.LogInfo($"[Perf]   {counts[c],4} x near ({centres[c].x,8:F0},"
                    + $"{centres[c].y,7:F0},{centres[c].z,8:F0})  rider(s) {string.Join(",", names.ToArray())}"
                    + $"  over {last[c] - first[c],5:F0}s");
            }
        }

        int WatchIndex(Transform tr)
        {
            if (_watch == null || tr == null) return -1;
            for (int i = 0; i < _watch.Length; i++)
                if (ReferenceEquals(_watch[i], tr)) return i;
            return -1;
        }

        // ---- rider motion telemetry ---------------------------------------------
        // What the rider DOES, sampled every physics step and written to a CSV.
        // Everything else in this class records what goes wrong; this records what
        // normal riding looks like, which turns out to be the number nobody had.
        //
        // It exists because every threshold in `authoring/tools/make_course.py` —
        // rail length, rail gap, lateral jog, arch height — was fitted to the static
        // GEOMETRY of TM's own four courses and none of it to the MOTION of a rider
        // through them. "The rails are too close together for how fast we're going"
        // cannot be answered from a level file: rail spacing is a distance, riding is
        // a distance per second, and nothing here had ever measured the second.
        // `authoring/tools/ride_report.py` turns this CSV into those seconds.
        //
        // ⚠️ Sampled in FixedUpdate, NOT Update. Snowboarder.FixedUpdate is what
        // writes the state transitions (Grounded -> Airborne -> Grinding), so a
        // render-rate sampler misses short airs outright and quantises the ones it
        // does catch to the frame rate — which is the one quantity this exists for.
        //
        // ⚠️ The header carries gravity, world scale and the physics step, and the
        // reader must use them. An air time is only a jump arc under a known g, and
        // this repo ships maps at four world scales with per-map gravity multipliers
        // on top (see ScaledGravity) — an analysis that assumes 20 is silently wrong
        // on most of the library. The chains also run at 50 Hz, not 100.
        //
        // ⚠️ The PLAYER only. An AI rider is driven by a speed model that brakes for
        // its own profiled limits (`AI_BRAIN_PLAN.md`), so its speed measures the AI,
        // not the course. Sizing course geometry off it would close a loop between
        // two of our own guesses.
        struct Sample
        {
            public float t;
            public Vector3 p, v;
            public int state;
        }

        // 100 Hz x 40 min. A telemetry run is one descent, not a session; this is
        // here so the key left set overnight cannot turn a diagnostic into a leak.
        const int MaxSamples = 240000;
        readonly List<Sample> _tel = new List<Sample>(16384);
        bool _telFull;

        // The header's physics constants, snapshotted on this level's first sample
        // rather than read when the CSV is written. See FixedUpdate.
        float _hdrWorldScale = 1f, _hdrGravity = 20f, _hdrGravityMult = 1f, _hdrFixedDt = 0.02f;

        // Which level this harness belongs to: the scene name on a built-in level,
        // the bundle path on a custom map. It names the CSV *and* decides whether a
        // later Spawn is the same level (ignore) or the next one (flush first), and
        // it is captured at Spawn rather than read at write time -- OnDestroy runs
        // while the following scene is loading, so by then the globals it would
        // otherwise read already describe the wrong course.
        string _ownerKey;

        static readonly System.Reflection.FieldInfo fTelState =
            AccessTools.Field(typeof(Snowboarder), "state");
        static readonly System.Reflection.FieldInfo fTelVel =
            AccessTools.Field(typeof(Snowboarder), "velocity");

        void FixedUpdate()
        {
            if (Plugin.riderTelemetry == null || !Plugin.riderTelemetry.Value) return;
            if (_telFull || !_watchLocked || _sb == null) return;
            if (_human < 0 || _human >= _sb.Length) return;
            // A reflection miss records nothing rather than a column of zeroes: a CSV
            // of plausible-looking constants is worse than no CSV at all.
            if (fTelState == null || fTelVel == null) return;
            var sb = _sb[_human];
            if (sb == null) return;

            object st = fTelState.GetValue(sb);
            int state = st == null ? -1 : Convert.ToInt32(st);
            // Starting (0) is the grid before the gun and Done (5) is a finished
            // rider coasting out. Both are minutes of near-zero speed that would land
            // in the speed percentiles as a fake slow tail.
            if (state == 0 || state == 5 || state < 0) return;

            // Snapshot the level's own constants the first time this level is
            // actually being ridden. They are all globals that the NEXT level
            // overwrites, and OnDestroy can now run while that next level is
            // loading, so reading them at write time stamps a lap with the wrong
            // course's scale and gravity -- which is precisely how 99s of forest
            // came to be filed under worldScale 2.5.
            if (_tel.Count == 0)
            {
                _hdrWorldScale = Plugin.WorldScale;
                _hdrGravity = Plugin.ScaledGravity(20f);
                _hdrGravityMult = Plugin.MapGravityMultiplier;
                _hdrFixedDt = Time.fixedDeltaTime;
            }

            object v = fTelVel.GetValue(sb);
            _tel.Add(new Sample
            {
                t = Time.time,
                p = sb.transform.position,
                v = v is Vector3 ? (Vector3)v : Vector3.zero,
                state = state,
            });
            if (_tel.Count >= MaxSamples)
            {
                _telFull = true;
                Plugin.Log.LogWarning($"[Telemetry] sample cap ({MaxSamples}) hit — recording "
                    + "stopped; the CSV holds the first part of this run only.");
            }
        }

        static string StateName(int s)
        {
            return s == 1 ? "grounded" : s == 2 ? "airborne" : s == 3 ? "grinding"
                 : s == 4 ? "dead" : s.ToString(CultureInfo.InvariantCulture);
        }

        void WriteTelemetry()
        {
            if (Plugin.riderTelemetry == null || !Plugin.riderTelemetry.Value) return;
            if (_tel.Count == 0)
            {
                Plugin.Log.LogWarning("[Telemetry] armed but recorded 0 samples — the player "
                    + "was never resolved, or the run never left the start gate.");
                return;
            }
            try
            {
                string dir = Path.Combine(BepInEx.Paths.GameRootPath, "Telemetry");
                Directory.CreateDirectory(dir);
                // Named from the key captured at Spawn, NEVER from
                // Plugin.LoadedAssetBundle read here: a harness flushed because the
                // NEXT level is already loading would take that level's name and
                // file the lap under a course it was not ridden on.
                string map = !string.IsNullOrEmpty(_ownerKey)
                    ? Path.GetFileNameWithoutExtension(_ownerKey)
                    : "unknown";
                string file = Path.Combine(dir,
                    map + " " + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");

                // InvariantCulture everywhere. The game runs on whatever locale the
                // machine has, and a European decimal comma would silently produce a
                // CSV whose every field is two columns.
                var inv = CultureInfo.InvariantCulture;
                using (var w = new StreamWriter(file, false))
                {
                    w.WriteLine("# tm-rider-telemetry v1");
                    w.WriteLine("# map=" + map);
                    w.WriteLine("# worldScale=" + _hdrWorldScale.ToString("R", inv));
                    w.WriteLine("# gravity=" + _hdrGravity.ToString("R", inv));
                    w.WriteLine("# gravityMultiplier=" + _hdrGravityMult.ToString("R", inv));
                    w.WriteLine("# fixedDeltaTime=" + _hdrFixedDt.ToString("R", inv));
                    w.WriteLine("# samples=" + _tel.Count.ToString(inv)
                        + (_telFull ? " (TRUNCATED at the cap)" : ""));
                    w.WriteLine("t,x,y,z,vx,vy,vz,speed,state");
                    for (int i = 0; i < _tel.Count; i++)
                    {
                        var s = _tel[i];
                        w.WriteLine(string.Concat(
                            s.t.ToString("F4", inv), ",",
                            s.p.x.ToString("F3", inv), ",", s.p.y.ToString("F3", inv), ",",
                            s.p.z.ToString("F3", inv), ",",
                            s.v.x.ToString("F3", inv), ",", s.v.y.ToString("F3", inv), ",",
                            s.v.z.ToString("F3", inv), ",",
                            s.v.magnitude.ToString("F3", inv), ",", StateName(s.state)));
                    }
                }
                Plugin.Log.LogInfo($"[Telemetry] {_tel.Count} sample(s) over "
                    + $"{_tel[_tel.Count - 1].t - _tel[0].t:F1}s written to {file}");
            }
            catch (Exception e)
            {
                // A diagnostic must never take the level exit down with it.
                Plugin.Log.LogError("[Telemetry] could not write the CSV: " + e);
            }
        }

        void DumpRespawns()
        {
            DumpRoutes();
            DumpTricks();
            DumpBoost();
            // Several reports, deliberately separate, because the first ride showed they
            // do NOT measure the same thing: 4 position jumps against riders that
            // visibly stop racing for most of a run. Whichever of these is large is
            // the one the fix has to address.
            var pos = new List<Vector3>();
            var rid = new List<int>();
            var tim = new List<float>();

            Plugin.Log.LogInfo($"[Perf] {(_watch != null ? _watch.Length : 0)} rider(s) watched; "
                + (_human >= 0 ? $"rider {_human} is the player (marked (P) below)"
                               : "player NOT identified — LevelManager.player never resolved"));

            // Split by route, because they are different bugs. Reported by the FROM
            // position: where the rider was stuck is what a fix has to act on.
            int crashed = 0;
            foreach (var g in GameRespawns) if (g.crashed) crashed++;

            foreach (var g in GameRespawns)
            {
                if (!g.crashed) continue;
                pos.Add(g.from); rid.Add(WatchIndex(g.who)); tim.Add(g.t);
            }
            ReportClusters($"CRASHED respawns ({crashed}) — wrecked on geometry, from", pos, rid, tim);

            pos.Clear(); rid.Clear(); tim.Clear();
            foreach (var g in GameRespawns)
            {
                if (g.crashed) continue;
                pos.Add(g.from); rid.Add(WatchIndex(g.who)); tim.Add(g.t);
            }
            // NOT "pathless". The first split labelled every non-Dead respawn as the
            // AI's pathless route; that was an inference and it was wrong. Both the
            // OOB-contact route and the pathless route leave state != Dead, and the
            // pathless one is hard-gated at one respawn per 1.5 s per rider — so it
            // cannot account for 2817 events in 114 s from a single rider. The OOB
            // tally below is what separates them, by measurement rather than by
            // deduction from the state field.
            ReportClusters($"NON-CRASH respawns ({GameRespawns.Count - crashed}) — OOB "
                + "contact or AI pathless, from", pos, rid, tim);

            if (OobHits.Count > 0)
            {
                var names = new List<string>(OobHits.Keys);
                names.Sort((a, b) => OobHits[b].CompareTo(OobHits[a]));
                int shown = Math.Min(names.Count, 15);
                Plugin.Log.LogInfo($"[Perf] OOB collider contacts: {OobHits.Count} distinct "
                    + $"collider(s), worst {shown}:");
                for (int i = 0; i < shown; i++)
                {
                    Vector3 w = OobWhere[names[i]];
                    Plugin.Log.LogInfo($"[Perf]   {OobHits[names[i]],7} x  {names[i]}"
                        + $"  at ({w.x:F0}, {w.y:F0}, {w.z:F0})");
                }
            }
            else Plugin.Log.LogInfo("[Perf] OOB collider contacts: none");

            pos.Clear(); rid.Clear(); tim.Clear();
            foreach (var g in GameRespawns)
            { pos.Add(g.to); rid.Add(WatchIndex(g.who)); tim.Add(g.t); }
            ReportClusters("respawn DESTINATIONS (both routes)", pos, rid, tim);

            pos.Clear(); rid.Clear(); tim.Clear();
            foreach (var r in _respawns)
            { pos.Add(r.to); rid.Add(r.rider); tim.Add(r.t); }
            ReportClusters("respawns (position-jump detector)", pos, rid, tim);

            pos.Clear(); rid.Clear(); tim.Clear();
            int loops = 0;
            foreach (var s in _stuck)
            {
                if (s.looping) loops++;
                pos.Add(s.at); rid.Add(s.rider); tim.Add(s.t);
            }
            ReportClusters($"not-racing {StuckWindow:F0}s window(s) "
                + $"[{loops} looping / {_stuck.Count - loops} wedged]", pos, rid, tim);
        }

        void OnDestroy()
        {
            // Both halves report on the way out — the scene dying is what guarantees
            // this runs at all — but each only when its own switch armed it, so a
            // telemetry run does not bury its CSV line under a respawn report.
            if (Plugin.perfHarness != null && Plugin.perfHarness.Value)
                DumpRespawns();   // free, and the whole reason the run was ridden
            WriteTelemetry();
            if (_inst == this) _inst = null;
        }
    }
}
