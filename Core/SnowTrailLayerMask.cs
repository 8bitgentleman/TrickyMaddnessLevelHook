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
    // SnowTrail (the board's ground-snapped ribbon decal) ground-snaps via a
    // raycast that only hits Unity layer "Default" (IL-verified: SnowTrail.Start
    // sets its static layerMask to just 1 << LayerMask.NameToLayer("Default")).
    // Our rideable ground can legitimately be Default(0)/Ice(6)/Rock(7)/Metal(14)
    // (see Snowboarder's own layer semantics), so on any SSX/SSX3/2012 terrain
    // typed onto Ice/Rock/Metal the raycast misses every frame and the trail
    // never renders. Widen the mask once, right after Start() sets it.
    [HarmonyPatch(typeof(SnowTrail), "Start")]
    public class SnowTrail_Start_LayerMask
    {
        // AccessTools, not a Traverse string chain, so tm_re_baseline.py's binding
        // scan can see it — Plugin.SetGameField has the why.
        static readonly System.Reflection.FieldInfo fLayerMask =
            AccessTools.Field(typeof(SnowTrail), "layerMask");

        [HarmonyPostfix]
        public static void Postfix()
        {
            if (fLayerMask == null)
            {
                Plugin.Log.LogError("[Reflection] SnowTrail.layerMask no longer resolves — snow trails "
                    + "left on the game's own layers; run `python3 tm_re_baseline.py check`.");
                return;
            }
            int rideableMask = (1 << 0) | (1 << 6) | (1 << 7) | (1 << 14);
            fLayerMask.SetValue(null, rideableMask);   // private STATIC field
        }
    }
}
