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
    // Drives the game's own Destructible.Break on a smashable pane/prop when the rider
    // rides through it. WHY NOT the game's trigger path: Snowboarder.OnTriggerEnter DOES
    // call Destructible.Break(velocity) — but only when a live trigger fires an enter
    // event for the rider's CharacterController, and that path is unreliable for us: the
    // rider crosses at 150-190 mph (~70-85 m/s → ~1.5 m per fixed tick) so a thin pane
    // trigger is tunnelled between discrete overlap samples, and a bundle-added trigger on
    // the game's glass layer isn't guaranteed to fire against the rider's layer. So we
    // drive Break ourselves with a SWEPT test (segment from the rider's previous origin to
    // its current one vs the box), which can't tunnel. Same collider-free design as Wind/
    // Teleport: the BoxCollider is a geometry carrier, DISABLED at runtime (a live trigger
    // on a cast-included layer would perturb the rider) — but kept present, because
    // Destructible.Break calls GetComponent<Collider>().enabled=false and would NPE without
    // one. One-shot: Break has no re-entry guard (it re-launches every fragment), so we
    // Destroy this component after the first hit.
    public class Smashable : MonoBehaviour
    {
        const float RiderRadiusWorld = 0.6f; // ~capsule radius; catch margin, scale-folded

        Destructible _destr;
        Vector3 _center;                     // box centre, local (unscaled) space
        Vector3 _half = new Vector3(0.15f, 0.15f, 0.15f);
        Snowboarder[] _riders;
        readonly Dictionary<Snowboarder, Vector3> _prev = new Dictionary<Snowboarder, Vector3>();

        void Awake()
        {
            _destr = GetComponent<Destructible>();
            var box = GetComponent<BoxCollider>();
            if (box != null)
            {
                _center = box.center;
                _half = box.size * 0.5f;
                box.enabled = false; // geometry carrier only — never a live collider
            }
            if (_destr == null) enabled = false;
        }

        void FixedUpdate()
        {
            if (_destr == null) { Destroy(this); return; }
            if (_riders == null || _riders.Length == 0)
            {
                _riders = UnityEngine.Object.FindObjectsOfType<Snowboarder>();
                if (_riders.Length == 0) return; // riders not spawned yet
            }
            // Fold the rider's world catch-radius into local (unscaled) units so the margin
            // is a constant ~0.6 m in the world regardless of map WorldScale (the box is
            // authored at unit scale; the transform carries the scale-up).
            float ls = transform.lossyScale.x;
            float m = ls > 1e-4f ? RiderRadiusWorld / ls : RiderRadiusWorld;
            Vector3 half = _half + new Vector3(m, m, m);

            foreach (var sb in _riders)
            {
                if (sb == null) continue;
                Vector3 cur = transform.InverseTransformPoint(sb.transform.position);
                if (!_prev.TryGetValue(sb, out var prev)) prev = cur; // first tick: point test
                _prev[sb] = cur;
                if (!SegmentHitsBox(prev, cur, _center, half)) continue;

                Vector3 vel = Traverse.Create(sb).Field("velocity").GetValue<Vector3>();
                _destr.Break(vel);
                Destroy(this); // one-shot — Break re-launches every fragment if called twice
                return;
            }
        }

        // Slab test: does segment p0->p1 intersect the AABB centred at c with half-extents h?
        // internal because AvalancheHazard runs the identical test in world space.
        internal static bool SegmentHitsBox(Vector3 p0, Vector3 p1, Vector3 c, Vector3 h)
        {
            Vector3 d = p1 - p0;
            float tmin = 0f, tmax = 1f;
            for (int a = 0; a < 3; a++)
            {
                float o = p0[a] - c[a], di = d[a], ha = h[a];
                if (Mathf.Abs(di) < 1e-8f)
                {
                    if (o < -ha || o > ha) return false; // parallel to slab and outside it
                }
                else
                {
                    float t1 = (-ha - o) / di, t2 = (ha - o) / di;
                    if (t1 > t2) { var tmp = t1; t1 = t2; t2 = tmp; }
                    if (t1 > tmin) tmin = t1;
                    if (t2 < tmax) tmax = t2;
                    if (tmin > tmax) return false;
                }
            }
            return true;
        }
    }
}
