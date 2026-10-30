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
        static void AttachTriggerAnims(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
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
        }

        static void AttachAnimDoors(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
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
        }

        static void AttachMovingSurfaces(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
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
                    AttachGameMoverParts(t, anim, ref msHaz);
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
        }
    }

    // Relay for a trigger-driven animated door (feature: gameplay.animDoors). The
    // importer emits the door GO's Animation separately (N triggers can share one door,
    // which the game's own PlayAnimationOnTriggerEnter can't express — it force-binds to
    // its own GO). While a Snowboarder's origin enters this box we Play() the referenced
    // door once per entry (edge-triggered). Analytic OBB containment (box disabled) like
    // TeleportVolume/WindVolume — thin trigger volumes tunnel physics trigger callbacks.
    // ClampForever clips: the door opens and stays open; a replay on re-entry is harmless.
    public class AnimDoorTrigger : MonoBehaviour
    {
        public Animation target;
        public string clipName;
        // Doors clear the path by switching their colliders OFF on fire. A moving RIDEABLE
        // surface (gameplay.movingColliders) must keep them so the animation drags the
        // collider — that motion IS the feature. See ssx/MOVING_COLLIDER_FEASIBILITY.md.
        public bool keepColliders;
        // Fire for the HUMAN rider only (persistentMovers playerOnly:true, via the mover's
        // MoverPlayerOnly marker). A set piece meant to be SEEN -- SSX 3's EBC3 plane crash --
        // would otherwise be fired by a CPU rider ahead of you, out of your sight.
        public bool playerOnly;

        Vector3 _halfLocal = new Vector3(0.5f, 0.5f, 0.5f);
        Vector3 _center = Vector3.zero;   // box.center: model-bounds offset from the instance origin
        Snowboarder[] _riders;
        readonly HashSet<Snowboarder> _inside = new HashSet<Snowboarder>();
        readonly Dictionary<Snowboarder, Vector3> _prev = new Dictionary<Snowboarder, Vector3>();
        bool _opened;   // door colliders disabled once (idempotent), and log emitted once

        void Awake()
        {
            var box = GetComponent<BoxCollider>();
            if (box != null)
            {
                _halfLocal = box.size * 0.5f;   // local half-extents; containment is in local space
                _center = box.center;
                box.enabled = false;            // geometry carrier only
            }
            // NOTE: do NOT self-disable on a null target here. AddComponent runs Awake
            // synchronously, BEFORE the wiring assigns `target` on the next line — so target is
            // ALWAYS null at this point and disabling would switch off every relay permanently
            // (the "attached N, fired 0" bug). FixedUpdate already early-returns on a null target.
        }

        void FixedUpdate()
        {
            if (target == null) return;
            if (_riders == null || _riders.Length == 0)
            {
                _riders = UnityEngine.Object.FindObjectsOfType<Snowboarder>();
                if (_riders.Length == 0) return; // riders not spawned yet
                if (playerOnly)
                    _riders = System.Array.FindAll(_riders, r => r != null &&
                        !(Traverse.Create(r).Field("snowboardController").GetValue() is AISnowboarder));
            }
            foreach (var sb in _riders)
            {
                if (sb == null) continue;
                // Swept test: the trigger boxes are only ~6 m across and the rider crosses at
                // 70-85 m/s (>1 box-width per fixed tick), so a point-in-box sample tunnels clean
                // through and never fires. Test the segment from the rider's PREVIOUS origin to its
                // current one against the box (local-space slab test) — this can't tunnel. Mirrors
                // the Smashable swept design for the same reason.
                Vector3 cur = transform.InverseTransformPoint(sb.transform.position);
                Vector3 prev = _prev.TryGetValue(sb, out var p) ? p : cur;
                _prev[sb] = cur;
                bool hit = SegmentHitsBox(prev, cur, _center, _halfLocal);
                if (!hit) { _inside.Remove(sb); continue; }
                if (!_inside.Add(sb)) continue;   // still crossing — edge-trigger only
                Fire();
            }
        }

        void Fire()
        {
            if (!string.IsNullOrEmpty(clipName)) target.Play(clipName);
            else target.Play();
            if (_opened) return;
            _opened = true;
            if (keepColliders)
            {
                Plugin.Log.LogInfo($"[MoveSurf] trigger '{name}' fired -> moving '{target.name}'");
                return;
            }
            // Open the path independent of how far the baked animation actually travels: disable the
            // closed door's blocking colliders. The trigger sits ahead of the door, so this clears
            // before the rider arrives; the animation then plays purely for show. No deadlock.
            foreach (var col in target.GetComponentsInChildren<Collider>())
                col.enabled = false;
            Plugin.Log.LogInfo($"[AnimDoor] trigger '{name}' fired -> opened '{target.name}'");
        }

        // Segment p0->p1 vs axis-aligned box (center, half) in the box's own local space. Slab
        // method: clamp the [0,1] parametric interval against each axis' slab; empty => miss.
        internal static bool SegmentHitsBox(Vector3 p0, Vector3 p1, Vector3 center, Vector3 half)
        {
            Vector3 d = p1 - p0;
            float tmin = 0f, tmax = 1f;
            for (int a = 0; a < 3; a++)
            {
                float o = p0[a] - center[a];
                float di = d[a];
                float mn = -half[a], mx = half[a];
                if (Mathf.Abs(di) < 1e-8f)
                {
                    if (o < mn || o > mx) return false; // parallel to slab and outside it
                }
                else
                {
                    float t1 = (mn - o) / di, t2 = (mx - o) / di;
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    if (t1 > tmin) tmin = t1;
                    if (t2 < tmax) tmax = t2;
                    if (tmin > tmax) return false;
                }
            }
            return true;
        }
    }
}
