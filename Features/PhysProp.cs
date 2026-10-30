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
        static void ConvertPhysProps(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
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
            blocks.Add($"physprop={propsConverted}/{propsSkipped}");
            LoadClock.Mark("PhysProps");
        }
    }
}
