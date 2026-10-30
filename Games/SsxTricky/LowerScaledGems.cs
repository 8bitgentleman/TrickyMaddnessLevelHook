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
        // Gem heights on scaled SSX Tricky maps. The importer scales the whole world
        // by WorldScale (2.5 on most Tricky ports) but rider physics stays stock, so
        // gems SSX placed for its own jumps sit too high. Each gem's height above
        // the ground under it is divided by min(GemHeightDivisor, WorldScale).
        //   * The divisor is FITTED, not WorldScale: dividing by the full 2.5 put the
        //     626 Tricky gems' median at 16 u against TM's own flakes' 22, and a
        //     playtest called it too low. 1.75 gives p10/median/p90 10.0/22.8/49.6 vs
        //     TM's 10.5/22.0/52.5.
        //   * Gems move as STACKS. SSX stacks a rarer gem 17-41 u over a common one
        //     at the same spot (40 such pairs); moved one at a time, the lower gem hit
        //     the ground clearance and the upper one was pulled down onto it. Gems
        //     whose footprints overlap seen from above form one group, the group
        //     takes the lowest gem's drop, so the spacing inside it is preserved.
        //   * Only ever lowers, and never into the ground: every gem's bottom stays
        //     GemClearance above the HIGHEST ground under its footprint, so a slope
        //     can't swallow the uphill half.
        //   * A group is left alone if any gem in it has nothing solid under it, has
        //     OOB first under it, or sits over a pit/gap (the ring around it mostly
        //     misses, or the ground under it is GemPitDepth below the ring's median)
        //     -- dividing would drop it into the hole.
        // ⚠️ GemClearance, GemPitDepth and the ring margin are JUDGMENT CALLS (the pit
        // depth is the measuring script's cut-off; it moved the gap count 101..191
        // over -20..-3 u). Only BundleSource "ssx_tricky" -- WildTangent rides at
        // WorldScale 100 and SSX 3 was not measured.
        const float GemClearance = 2f, GemPitDepth = 10f, GemRingMargin = 6f, GemProbeReach = 1000f;

        // Everything a gem can rest over: excludes pickups(2), 3, rails(9), 13, 15 --
        // the same non-ground set the measurement skipped. OOB(10) stays IN so it
        // can be detected and refused.
        const int GemGroundMask = ~((1 << 2) | (1 << 3) | (1 << 9) | (1 << 13) | (1 << 15));

        static bool GemGroundY(float x, float z, float top, out float y)
        {
            y = 0f;
            if (!Physics.Raycast(new Vector3(x, top, z), Vector3.down, out var hit, GemProbeReach,
                                 GemGroundMask, QueryTriggerInteraction.Ignore)) return false;
            if (hit.collider.gameObject.layer == Snowboarder_OnControllerColliderHit.OobLayer) return false;
            y = hit.point.y;
            return true;
        }

        class GemProbe
        {
            public Transform t;
            public Bounds b;
            public bool gap;
            public float drop;      // <= 0: how far this gem alone would move down
            public float maxDrop;   // <= 0: the most it may move before it hits its clearance
            public int group;
        }

        // Measures one gem where it stands; null if it has no renderer to size from.
        static GemProbe ProbeGem(Snowflake flake, float divisor, List<float> ring)
        {
            var rend = flake.GetComponent<Renderer>();
            if (rend == null) return null;
            Bounds b = rend.bounds;
            if (b.size == Vector3.zero) return null;
            var p = new GemProbe { t = flake.transform, b = b };
            float top = b.max.y + 0.5f;

            if (!GemGroundY(b.center.x, b.center.z, top, out float groundC)) { p.gap = true; return p; }
            ring.Clear();
            float rr = Mathf.Max(b.extents.x, b.extents.z) + GemRingMargin;
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4f;
                if (GemGroundY(b.center.x + rr * Mathf.Cos(a), b.center.z + rr * Mathf.Sin(a), top, out float ry))
                    ring.Add(ry);
            }
            if (ring.Count < 4) { p.gap = true; return p; }
            ring.Sort();
            if (groundC < ring[ring.Count / 2] - GemPitDepth) { p.gap = true; return p; }

            float groundMax = groundC;
            for (int cx = -1; cx <= 1; cx += 2)
                for (int cz = -1; cz <= 1; cz += 2)
                    if (GemGroundY(b.center.x + cx * b.extents.x, b.center.z + cz * b.extents.z, top, out float fy))
                        groundMax = Mathf.Max(groundMax, fy);

            float h = b.center.y - groundC;
            p.maxDrop = Mathf.Min(0f, groundMax + b.extents.y + GemClearance - b.center.y);
            p.drop = h <= 0f ? 0f : Mathf.Max(p.maxDrop, Mathf.Min(0f, groundC + h / divisor - b.center.y));
            return p;
        }

        static int GemRoot(int[] parent, int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }

        static partial void LowerScaledGems(UnityEngine.SceneManagement.Scene scene)
        {
            if (lowerScaledGems == null || !lowerScaledGems.Value) return;
            if (BundleSource != "ssx_tricky" || WorldScale <= 1f) return;
            float divisor = Mathf.Min(Mathf.Max(1f, gemHeightDivisor.Value), WorldScale);
            if (divisor <= 1f) return;
            Physics.SyncTransforms();

            var gems = new List<GemProbe>();
            var ring = new List<float>(8);
            foreach (var sceneRoot in scene.GetRootGameObjects())
                foreach (var flake in sceneRoot.GetComponentsInChildren<Snowflake>(true))
                {
                    var p = ProbeGem(flake, divisor, ring);
                    if (p != null) gems.Add(p);
                }

            // Group gems whose footprints overlap seen from above (union-find).
            var parent = new int[gems.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            for (int i = 0; i < gems.Count; i++)
                for (int j = i + 1; j < gems.Count; j++)
                {
                    Bounds a = gems[i].b, c = gems[j].b;
                    float reach = Mathf.Max(a.extents.x, a.extents.z) + Mathf.Max(c.extents.x, c.extents.z);
                    float dx = a.center.x - c.center.x, dz = a.center.z - c.center.z;
                    if (dx * dx + dz * dz < reach * reach) parent[GemRoot(parent, i)] = GemRoot(parent, j);
                }
            for (int i = 0; i < gems.Count; i++) gems[i].group = GemRoot(parent, i);

            int lowered = 0, groups = 0, stacked = 0, gapGems = 0, unchanged = 0;
            foreach (var grp in gems.GroupBy(g => g.group))
            {
                var members = grp.ToList();
                if (members.Any(g => g.gap)) { gapGems += members.Count; continue; }
                // The lowest gem sets the drop; no member may pass its own clearance.
                var lowest = members.OrderBy(g => g.b.center.y).First();
                float drop = Mathf.Max(lowest.drop, members.Max(g => g.maxDrop));
                if (drop >= -0.01f) { unchanged += members.Count; continue; }
                foreach (var g in members) g.t.position += Vector3.up * drop;
                lowered += members.Count;
                groups++;
                if (members.Count > 1) stacked++;
            }
            if (gems.Count > 0)
                Log.LogInfo($"[Gems] scale {WorldScale.ToString(CultureInfo.InvariantCulture)}, divisor {divisor.ToString(CultureInfo.InvariantCulture)}: " +
                            $"lowered {lowered} gem(s) in {groups} group(s) ({stacked} multi-gem), " +
                            $"left {gapGems} over a gap/pit/OOB and {unchanged} already low enough");
        }
    }
}
