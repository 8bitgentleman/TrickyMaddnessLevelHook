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
        // True for anything living under an AnimDoor_* / MoveSurf_* subtree. Those
        // GOs are emitted by the importer's animDoors / movingColliders passes and
        // their part colliders ARE the gameplay: the door is meant to block the
        // rider until its trigger opens it. The bundle nests parts one level deep,
        // but walk the whole chain so a future importer change can't quietly
        // reintroduce the pass-through.
        static bool IsAnimatedSurfacePart(Transform t)
        {
            for (var cur = t; cur != null; cur = cur.parent)
                if (cur.name.StartsWith("AnimDoor_") || cur.name.StartsWith("MoveSurf_"))
                    return true;
            return false;
        }

        static partial void StripGlassColliders(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
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
            blocks.Add($"glass={glassStripped}");
            LoadClock.Mark("Glass");
        }
    }
}
