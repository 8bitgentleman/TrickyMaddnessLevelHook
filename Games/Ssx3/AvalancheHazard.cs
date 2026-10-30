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
    // Debris that WRECKS you — the consequence half of the SSX 3 avalanche.
    //
    // Attached to a MoveSurf_<idx> mover that carries a "MoverHazard" marker child
    // (SSXImporter.BuildMovingSurfaces, from Overrides.json
    // gameplay.persistentMovers[].hazard). Sweeps each rider's previous->current
    // position against every part's world AABB and, on a hit, calls the game's own
    // private Snowboarder.Die(): ragdoll, whole trick combo dropped, character and DJ
    // both call the hit. Nothing bespoke — the wreck a rider already knows.
    //
    // ⚠️ THIS CONSEQUENCE IS AUTHORED, NOT RECOVERED. SSX 3 keeps its avalanche logic
    // in chunk-16 bytecode nobody has decoded, so the original game's answer to "what
    // happens when the rocks reach you" is unavailable. This is our answer standing in
    // for it. See HOOK_NOTES.md.
    //
    // Non-solid on purpose: the debris instances ship PlayerCollision:false, so the
    // rocks pass THROUGH the rider rather than shoving them. A solid non-convex mesh
    // sweeping the race line is a moving wall, which wrecks you by accident and in a
    // way that reads as a bug; this wrecks you on contact and reads as an avalanche.
    //
    // The loop guard needs no wiring here: Die() sets state=Dead, so the next
    // Respawn() takes Snowboarder_Respawn's CRASH route and the existing escalation
    // (three wrecks in the same stretch -> shunt down-course) already covers a rider
    // who respawns underneath the slide.
    public class AvalancheHazard : MonoBehaviour
    {
        const float RiderRadiusWorld = 0.6f;  // ~capsule radius, same catch margin as Smashable
        // Long enough to outlast the game's own respawn gate (deathTimer > 1.5 s, so a
        // real respawn lands at ~1.5-2.4 s). Shorter and a rider who gets up while the
        // slide is still on top of them is re-wrecked on the very next frame, before
        // the loop guard has seen enough hits to move them.
        const float ReHitCooldown = 3f;

        public Animation target;              // the mover's own clip — the arming gate

        static readonly System.Reflection.MethodInfo mDie =
            AccessTools.Method(typeof(Snowboarder), "Die");
        static readonly System.Reflection.FieldInfo fState =
            AccessTools.Field(typeof(Snowboarder), "state");
        static string _loggedScene;

        Renderer[] _parts;
        AnimationState _st;
        Snowboarder[] _riders;
        readonly Dictionary<int, Vector3> _prev = new Dictionary<int, Vector3>();
        readonly Dictionary<int, float> _hitAt = new Dictionary<int, float>();

        // Closest-approach telemetry. A hazard that never fires is indistinguishable
        // from a hazard that is switched off, and the difference matters: a 2 m miss
        // is a tuning number, a 90 m miss means the debris and the rider are simply
        // never in the same place and no radius will fix it. Reported once per run.
        bool _wasArmed;
        float _closest = float.MaxValue;
        bool _everHit;
        static string _loggedMissScene;

        public static void ResetState() { _loggedScene = null; _loggedMissScene = null; }

        void Awake()
        {
            // Any reflection miss and the component removes itself, rather than run a
            // hazard that can detect a hit and not deliver one.
            if (mDie == null || fState == null)
            {
                Plugin.Log.LogWarning("[Avalanche] Snowboarder.Die()/state did not resolve — hazard disabled");
                Destroy(this);
                return;
            }
            if (target == null) target = GetComponent<Animation>();
            if (target != null && target.clip != null) _st = target[target.clip.name];
            // Bounds come off the RENDERERS, not the colliders: hazard debris is
            // deliberately non-solid, so it has no colliders to measure.
            _parts = GetComponentsInChildren<Renderer>(true);
            if (_parts.Length == 0 || target == null) { Destroy(this); return; }
        }

        // Armed only while the clip is actually travelling. isPlaying alone is NOT
        // enough: these clips are WrapMode.ClampForever, which by definition never
        // stops playing — it holds the last frame forever — so gating on isPlaying
        // would leave a permanent kill zone parked wherever the slide came to rest.
        // Hence the explicit time-vs-length test as well.
        bool Armed()
        {
            if (target == null || !target.isPlaying) return false;
            if (_st == null) return true;                 // no state to read: trust isPlaying
            return _st.time < _st.length;
        }

        void FixedUpdate()
        {
            if (!Armed())
            {
                _prev.Clear();                           // stale prevs would sweep a huge phantom segment
                if (_wasArmed)
                {
                    _wasArmed = false;
                    if (!_everHit && _closest < float.MaxValue && _loggedMissScene != Plugin.CustomSceneName())
                    {
                        _loggedMissScene = Plugin.CustomSceneName();
                        Plugin.Log.LogInfo($"[Avalanche] {name} finished without wrecking anyone — "
                            + $"closest any rider came to the debris was {_closest:F1} m");
                    }
                    _closest = float.MaxValue;
                }
                return;
            }
            _wasArmed = true;
            if (_riders == null || _riders.Length == 0)
            {
                _riders = UnityEngine.Object.FindObjectsOfType<Snowboarder>();
                if (_riders.Length == 0) return;          // riders not spawned yet
            }

            float now = Time.time;
            foreach (var sb in _riders)
            {
                if (sb == null) continue;
                int id = sb.GetInstanceID();
                Vector3 cur = sb.transform.position;
                Vector3 prev;
                if (!_prev.TryGetValue(id, out prev)) prev = cur;   // first armed tick: point test
                _prev[id] = cur;

                // Starting (0) is the grid before the gun, Dead (4) is already wrecked
                // and Done (5) has crossed the line — none of the three can be
                // meaningfully wrecked, and wrecking a finished rider is just rude.
                object so = fState.GetValue(sb);
                int state = so == null ? -1 : Convert.ToInt32(so);
                if (state == 0 || state == 4 || state == 5 || state < 0) continue;

                float last;
                if (_hitAt.TryGetValue(id, out last) && now - last < ReHitCooldown) continue;

                Renderer hit = null;
                foreach (var r in _parts)
                {
                    if (r == null) continue;
                    var b = r.bounds;                     // world AABB, follows the baked clip
                    var h = b.extents + new Vector3(RiderRadiusWorld, RiderRadiusWorld, RiderRadiusWorld);
                    float gap = Vector3.Distance(b.ClosestPoint(cur), cur);
                    if (gap < _closest) _closest = gap;
                    if (Smashable.SegmentHitsBox(prev, cur, b.center, h)) { hit = r; break; }
                }
                if (hit == null) continue;

                _everHit = true;
                _hitAt[id] = now;
                mDie.Invoke(sb, null);

                string scene = Plugin.CustomSceneName();
                if (_loggedScene != scene)
                {
                    _loggedScene = scene;
                    Plugin.Log.LogInfo($"[Avalanche] debris wrecked a rider on '{scene}' — "
                        + $"{name} part '{hit.name}' caught {sb.name} [{id}] at {cur}");
                }
                else
                    Plugin.Log.LogDebug($"[Avalanche] {name} wrecked {sb.name} [{id}] at {cur}");
            }
        }
    }
}
