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
        // Private game members the custom-map rebuild seats, held as AccessTools
        // handles rather than `Traverse.Create(x).Field("name")` string chains.
        // Two reasons, both about SILENT failure:
        //   1. `tm_re_baseline.py check`'s binding scan greps for AccessTools.* — a
        //      Traverse string chain is INVISIBLE to it, so a TM rename reports
        //      "clean" while the Hook quietly stops wiring the field. That is the
        //      exact community-DLL failure mode the root CLAUDE.md describes.
        //   2. Traverse.SetValue on a name that no longer resolves is a no-op with
        //      no log at all; SetGameField/GetGameField below say so, loudly.
        // `raceLine` is the one that costs most: AIMod's race terminator and its
        // progress measurement both read LevelManager.raceLine and only warn in
        // THEIR log when it is null, so an unwired field here reads as a broken AI
        // mod several layers away.
        static readonly System.Reflection.FieldInfo fStartingGate =
            AccessTools.Field(typeof(LevelManager), "startingGate");

        static readonly System.Reflection.FieldInfo fRespawnPointsParent =
            AccessTools.Field(typeof(LevelManager), "respawnPointsParent");

        static readonly System.Reflection.FieldInfo fSnowflakes =
            AccessTools.Field(typeof(LevelManager), "snowflakes");

        static readonly System.Reflection.FieldInfo fSpeedLabel =
            AccessTools.Field(typeof(LevelManager), "speedLabel");

        // TM build 25638975 added weather snow. Null on older builds — TrySpawnTmSnow
        // then falls back to the Hook's own SnowFall, so neither is ERROR-logged.
        static readonly System.Reflection.FieldInfo fFallingSnow =
            AccessTools.Field(typeof(LevelManager), "fallingSnow");

        static readonly System.Reflection.FieldInfo fFallingSnowPrefab =
            AccessTools.Field(typeof(LevelManager), "fallingSnowPrefab");

        // TM build 25617730 replaced pathsParent/dividersParent (flat 2D AIPath
        // polylines under a transform) with one AIPathNetwork of 3D spline volumes.
        static readonly System.Reflection.FieldInfo fPathNetwork =
            AccessTools.Field(typeof(LevelManager), "pathNetwork");

        static readonly System.Reflection.FieldInfo fSampleSpacing =
            AccessTools.Field(typeof(AIPath3D), "m_sampleSpacing");

        static readonly System.Reflection.FieldInfo fRaceLine =
            AccessTools.Field(typeof(LevelManager), "raceLine");

        // NOT a field. `MenuManager.lastLevelEntry` is an auto-property with a
        // private setter, so there is no member of that name to bind — only the
        // accessors and a mangled `<lastLevelEntry>k__BackingField`. The old
        // `Traverse.Create(menuManager).Field("lastLevelEntry")` therefore resolved
        // to nothing and its SetValue was a no-op that logged nothing, for as long
        // as it has been in the file. See the write-back site for what that cost.
        static readonly System.Reflection.MethodInfo mSetLastLevelEntry =
            AccessTools.PropertySetter(typeof(MenuManager), "lastLevelEntry");

        static readonly System.Reflection.FieldInfo fPropSoundName =
            AccessTools.Field(typeof(PhysicsProp), "soundName");

        static readonly System.Reflection.FieldInfo fDestrFragmentList =
            AccessTools.Field(typeof(Destructible), "fragmentList");

        static readonly System.Reflection.FieldInfo fDestrDisableList =
            AccessTools.Field(typeof(Destructible), "disableList");

        static readonly System.Reflection.FieldInfo fDestrHideSelf =
            AccessTools.Field(typeof(Destructible), "hideSelf");

        static readonly System.Reflection.FieldInfo fDestrSoundName =
            AccessTools.Field(typeof(Destructible), "soundName");

        // Assign through a handle, and say something if the handle is dead. Returns
        // false so a caller with a fallback can take it. Logged as an ERROR on
        // purpose: every one of these is load-bearing for a custom map, and a
        // half-wired LevelManager is not a warning-level event.
        static bool SetGameField(System.Reflection.FieldInfo f, string label, object target, object value)
        {
            if (f == null)
            {
                Log.LogError($"[Reflection] {label} no longer resolves — NOT wired. "
                    + "TM has almost certainly renamed it; run `python3 tm_re_baseline.py check`.");
                return false;
            }
            f.SetValue(target, value);
            return true;
        }

        static object GetGameField(System.Reflection.FieldInfo f, string label, object target)
        {
            if (f == null)
            {
                Log.LogError($"[Reflection] {label} no longer resolves — read as null. "
                    + "TM has almost certainly renamed it; run `python3 tm_re_baseline.py check`.");
                return null;
            }
            return f.GetValue(target);
        }
    }
}
