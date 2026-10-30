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
        // Every rail the Hook converted on the current map — the candidate set for
        // Snowboarder_UpdateGrinding_Handover. Rebuilt per map; a built-in course
        // never matches it, so the handover is inert there.
        internal static readonly List<Rail> HookRails = new List<Rail>();

        // Builds the game-side segment polyline for a Maker rail.
        //
        // ⚠️ Do NOT go back to MakerRails.GetSegments(). Its GenerateSegments() loops
        // from index 1 and stores TempList[i] as the segment position, so a rail's
        // FIRST point never becomes a segment: the polyline — and the collider we
        // build from it — starts at P1, leaving the whole P0→P1 span dead. That span
        // is the ENTRY end of the rail, so you ride through the visible rail model
        // and nothing grabs. Measured across the shipped ports: a median 3–13 m of
        // ungrindable lead-in on EVERY rail (worst single case 187 m on Pipedream),
        // 6–19% of total rail length. It's the reason a small jump onto the middle
        // of a rail works when riding on from the end doesn't.
        //
        // So: read the PointList directly and emit one segment per point, P0 included.
        // Point positions are read as localPosition exactly as MakerRails does, so
        // every segment from P1 on is bit-identical to the old path; the only change
        // is the prepended P0 (and `distance` becoming true cumulative arc length,
        // which the old path lagged by one span — Snowboarder.railDistance only ever
        // compares it frame-to-frame, so the absolute origin is free).
        private static List<Rail.Segment> BuildRailSegments(MakerRails rail)
        {
            var segments = new List<Rail.Segment>();
            var list = rail.PointList == null ? null : rail.PointList.transform;
            if (list == null || list.childCount < 2)
            {
                // No usable PointList — fall back to whatever the Maker component has.
                // ⚠️ This is the BAD path the comment above exists to prevent: it drops
                // P0 and leaves an ungrindable lead-in. It is kept only so a rail with a
                // malformed PointList still grinds *somewhere* rather than not at all,
                // but it must never be silent — a rider reporting "this rail doesn't
                // grab from the top" needs to find this line in the log rather than
                // re-derive the whole P0 investigation. See CLAUDE.md §6.
                Log.LogWarning($"[Rails] '{rail.gameObject.name}' has no usable PointList "
                    + $"({(list == null ? "null" : list.childCount + " point(s), need 2")}) — "
                    + "falling back to MakerRails.GetSegments(), which DROPS THE FIRST POINT "
                    + "and leaves a dead ungrindable lead-in at the entry end of this rail.");
                foreach (var s in rail.GetSegments())
                {
                    var fb = new Rail.Segment(Vector3.zero, Vector3.up);
                    fb.position = s.Point;
                    fb.rotation = s.Rotation;
                    fb.distance = s.Distance;
                    segments.Add(fb);
                }
                return segments;
            }

            var points = new List<Vector3>(list.childCount);
            for (int i = 0; i < list.childCount; i++)
                points.Add(list.GetChild(i).localPosition);

            float distance = 0f;
            Quaternion rotation = Quaternion.identity;
            for (int i = 0; i < points.Count; i++)
            {
                // Segment i faces along the span that ARRIVES at it (P0 borrows P1's,
                // being the one span it starts rather than ends).
                Vector3 dir = i == 0 ? points[1] - points[0] : points[i] - points[i - 1];
                if (dir.sqrMagnitude > 1e-8f)
                    rotation = Quaternion.LookRotation(dir, Vector3.up); // else keep the last good one
                if (i > 0)
                    distance += (points[i] - points[i - 1]).magnitude;

                var seg = new Rail.Segment(Vector3.zero, Vector3.up);
                seg.position = points[i];
                seg.rotation = rotation;
                seg.distance = distance;
                segments.Add(seg);
            }
            return segments;
        }

        // Builds a solid (non-convex) MeshCollider along the rail's segment line —
        // the built-in-authentic setup: built-in rails are plain solid MeshColliders
        // on the same GameObject as the Rail component. Rail segment positions are
        // rail-GO-LOCAL (Rail.GetClosestPosition works in local space), so the mesh
        // is built in local space too. Each consecutive segment pair becomes a thin
        // box: top face ON the a-b line (the game snaps the rider to
        // GetClosestPosition + up*radius, so the surface must sit exactly there),
        // extruded 0.2 m straight down. Solid is safe: GroundLayers/CastLayerMask
        // exclude layer 9, so ground/side casts can never see these.
        private static bool BuildRailCollider(GameObject railGO, List<Rail.Segment> segments)
        {
            // HalfWidth is the LATERAL catch radius of the grind collider: the
            // game box-casts along rider velocity for a layer-9 collider, so a
            // wider strip is an easier target to land/jump onto. Widened from
            // 0.125 (0.25 m) to 0.25 (0.5 m) at user request. The top face stays
            // pinned to the a-b rail line (do NOT raise it — the rider is snapped
            // to GetClosestPosition + up*radius, so the surface must sit there);
            // only the width grows. Tunable if it feels too grabby / too narrow.
            //
            // SCALE with WorldScale (MECHANICS_DIAGNOSIS Bug 1): the rail MESH is
            // vertex-baked at the map's world scale, but the game's grind box
            // (BoardHalfExtents) is a fixed absolute size — so on a 2.5x map a
            // 0.5 m strip is a 2.5x-too-thin sliver on a rail that looks 2.5x
            // fatter, and a 0.2 m depth can't bridge 2.5x curve-chord sag → most
            // grind approaches miss. Multiplying by WorldScale restores the 1x
            // "target size relative to the visible rail". Inert at scale 1.
            // A map may declare an ABSOLUTE half-width instead (sidecar
            // "railcatch <f>", metres). The WorldScale product below is only a
            // proxy for finished map size and it is wrong wherever the source
            // units are not centimetres — see Plugin.MapRailCatch. Depth keeps
            // the same 0.8x ratio to the width it has always had, so a declaring
            // map gets the identical collider SHAPE, just sized in real metres.
            float HalfWidth = Plugin.MapRailCatch > 0f
                ? Plugin.MapRailCatch : 0.25f * Plugin.WorldScale;
            float Depth = Plugin.MapRailCatch > 0f
                ? 0.8f * Plugin.MapRailCatch : 0.2f * Plugin.WorldScale;

            var verts = new List<Vector3>();
            var tris = new List<int>();
            // Outward-wound faces of one thin box (verts 0-3 top a->b, 4-7 below).
            int[] boxTris = {
                0, 2, 1,  0, 3, 2,   // top (+up)
                4, 5, 6,  4, 6, 7,   // bottom (-up)
                0, 1, 5,  0, 5, 4,   // end cap at a
                3, 6, 2,  3, 7, 6,   // end cap at b
                1, 2, 6,  1, 6, 5,   // +right side
                0, 4, 7,  0, 7, 3    // -right side
            };
            for (int i = 0; i + 1 < segments.Count; i++)
            {
                Vector3 a = segments[i].position;
                Vector3 b = segments[i + 1].position;
                Vector3 d = b - a;
                if (d.magnitude < 0.001f) continue; // degenerate pair (~1 mm)
                Vector3 dir = d.normalized;
                Vector3 right = Mathf.Abs(dir.y) > 0.99f
                    ? Vector3.right // near-vertical: any horizontal width works
                    : Vector3.Cross(Vector3.up, dir).normalized;
                Vector3 r = right * HalfWidth;
                Vector3 down = Vector3.down * Depth;

                int v0 = verts.Count;
                verts.Add(a - r);
                verts.Add(a + r);
                verts.Add(b + r);
                verts.Add(b - r);
                verts.Add(a - r + down);
                verts.Add(a + r + down);
                verts.Add(b + r + down);
                verts.Add(b - r + down);
                for (int t = 0; t < boxTris.Length; t++) tris.Add(v0 + boxTris[t]);
            }
            if (tris.Count == 0) return false;

            var mesh = new Mesh();
            mesh.name = "RailCollider (" + railGO.name + ")";
            if (verts.Count > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            railGO.AddComponent<MeshCollider>().sharedMesh = mesh; // convex stays false
            return true;
        }

        // [Debug] RailColliderProbe: replays the game's grind-detection contract
        // against the first built collider — SphereCast down on layer 9, then
        // GetComponent<Rail>() on the collider's own GameObject.
        private static System.Collections.IEnumerator RailProbe(GameObject railGO, List<Rail.Segment> segments)
        {
            yield return new WaitForFixedUpdate(); // let physics register the collider
            Vector3 localMid = (segments[0].position + segments[1].position) * 0.5f;
            Vector3 world = railGO.transform.TransformPoint(localMid);
            RaycastHit hit;
            if (!Physics.SphereCast(world + Vector3.up * 1f, 0.2f, Vector3.down, out hit, 5f, 1 << 9))
            {
                Log.LogWarning($"[Rails] probe MISS: no layer-9 hit above '{railGO.name}' at {world}");
                yield break;
            }
            var rail = hit.collider.gameObject.GetComponent<Rail>();
            var data = hit.collider.gameObject.GetComponent<RailInstanceData>();
            var segs = data == null ? null : data.segments;
            Log.LogInfo($"[Rails] probe hit '{hit.collider.gameObject.name}' (layer {hit.collider.gameObject.layer}): Rail={(rail != null)}, segments={(segs == null ? 0 : segs.Count)}");
        }

        // The rail a sidecar "rail <name>" line points at, by its GameObject name
        // (the importer names each one after its Splines.json SplineName). The
        // Hook's own converted rails first, then any Rail in the scene, so a rail
        // that shipped with its own collider still resolves.
        private static Rail FindRailByName(string name)
        {
            foreach (var r in HookRails)
                if (r != null && r.gameObject.name == name) return r;
            foreach (var o in FindObjectsByType(typeof(Rail), FindObjectsSortMode.None))
            {
                var r = o as Rail;
                if (r != null && r.gameObject.name == name) return r;
            }
            return null;
        }

        static void ConvertRails(BuildContext ctx)
        {
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
        }
    }

    // Rail-to-rail handover at a rail's end. The game has one already
    // (UpdateGrinding, on MoveOnRail's end flag: OverlapSphere(2*radius) on layer 9,
    // switch rail/railDistance/railDirection) but it misses SSX funnel stubs — short
    // rails that merge INTO a longer one mid-span (R&B's first rail), launching the
    // rider at the merge. This does the same three-field switch a frame or two
    // early, against the Hook's own rail list instead of a physics overlap, so it
    // fires whenever this rail's end lies on another rail travelling the same way.
    // Speed carries over: UpdateGrinding rebuilds velocity from its magnitude.
    [HarmonyPatch(typeof(Snowboarder), "UpdateGrinding")]
    public class Snowboarder_UpdateGrinding_Handover
    {
        static readonly System.Reflection.FieldInfo fRail =
            AccessTools.Field(typeof(Snowboarder), "rail");
        static readonly System.Reflection.FieldInfo fRailDistance =
            AccessTools.Field(typeof(Snowboarder), "railDistance");
        static readonly System.Reflection.FieldInfo fRailDirection =
            AccessTools.Field(typeof(Snowboarder), "railDirection");
        static readonly System.Reflection.FieldInfo fVelocity =
            AccessTools.Field(typeof(Snowboarder), "velocity");

        // A rail meeting another this far off-axis is a crossing, not a merge.
        const float MinAlignment = 0.7f;

        [HarmonyPrefix]
        public static void Prefix(Snowboarder __instance)
        {
            if (Plugin.HookRails.Count < 2) return;
            var rail = fRail.GetValue(__instance) as Rail;
            if (rail == null) return;
            var data = rail.GetComponent<RailInstanceData>();
            if (data == null || data.segments == null || data.segments.Count < 2) return;
            var segs = data.segments;

            float distance = (float)fRailDistance.GetValue(__instance);
            int direction = (int)fRailDirection.GetValue(__instance);
            Vector3 velocity = (Vector3)fVelocity.GetValue(__instance);

            // Only near the end the rider is heading for: within two frames of travel
            // (velocity can grow inside UpdateGrinding before MoveOnRail runs).
            float length = segs[segs.Count - 1].distance;
            float remaining = direction > 0 ? length - distance : distance;
            if (remaining > velocity.magnitude * Time.deltaTime * 2f) return;

            var endSeg = direction > 0 ? segs[segs.Count - 1] : segs[0];
            Vector3 endPos = rail.transform.TransformPoint(endSeg.position);
            Vector3 endFwd = rail.transform.rotation * endSeg.rotation * Vector3.forward * direction;
            float tolerance = 0.25f * Plugin.RouteScale;

            foreach (var other in Plugin.HookRails)
            {
                if (other == null || other == rail) continue;
                Vector3 onOther = other.GetClosestPosition(endPos, out int otherEnd, out Quaternion otherRot, out _);
                if ((onOther - endPos).sqrMagnitude > tolerance * tolerance) continue;
                Vector3 otherFwd = otherRot * Vector3.forward;
                float align = Vector3.Dot(endFwd, otherFwd);
                if (Mathf.Abs(align) < MinAlignment) continue;
                int otherDirection = align < 0f ? -1 : 1;
                // The other rail also ends here, in the direction we'd ride it.
                if (otherEnd == otherDirection) continue;

                // Join at the rider's CURRENT spot, not the end — same line, so seamless.
                other.GetClosestPosition(__instance.transform.position, out _, out _, out float otherDistance);
                fRail.SetValue(__instance, other);
                fRailDistance.SetValue(__instance, otherDistance);
                fRailDirection.SetValue(__instance, otherDirection);
                Plugin.Log.LogInfo($"[Rails] handover '{rail.name}' -> '{other.name}' at {otherDistance:F2} (dir {otherDirection})");
                return;
            }
        }
    }
}
