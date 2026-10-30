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
