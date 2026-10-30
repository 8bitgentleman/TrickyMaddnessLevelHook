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
        // Low gravity (tag 20011, "LowGravity"). Up to build 21290881 Integrate did
        // `if (lowGZoneCount > 0) g *= 0.7f` right after picking g = 20 / 17; build
        // 25617730 still counts the zones but only drives the "OuterSpace" RTPC, so
        // every custom map that authored a low-G zone lost it. Re-inserted at the
        // same spot — the instruction after g's store is a branch target (the
        // not-rising path skips the 17), so its labels move onto the new code.
        // Custom maps only: the built-in courses keep the new stock behaviour.
        // ⚠️ Caster.Cast (landing prediction) had the same 0.7 and has no counter
        // any more, so predicted landings in a low-G zone run slightly short.
        static readonly System.Reflection.FieldInfo fLowGZoneCount = AccessTools.Field(typeof(Snowboarder), "lowGZoneCount");

        public static float LowGravityScale(float g, Snowboarder sb)
        {
            if (string.IsNullOrEmpty(LoadedAssetBundle) || sb == null) return g;
            return fLowGZoneCount != null && (int)fLowGZoneCount.GetValue(sb) > 0 ? g * 0.7f : g;
        }

        internal static IEnumerable<CodeInstruction> RestoreLowGravity(List<CodeInstruction> code)
        {
            int at = -1;
            for (int i = 0; i < code.Count && at < 0; i++)
            {
                if (!(code[i].opcode == OpCodes.Ldc_R4 && code[i].operand is float f && f == 17f)) continue;
                for (int j = i + 1; j < code.Count && j < i + 4; j++)
                    if (code[j].IsStloc()) { at = j; break; }
            }
            if (at < 0 || at + 1 >= code.Count)
            {
                Log.LogWarning("[Gravity] Snowboarder.Integrate: g = 17 store not found; low-gravity zones stay at full gravity");
                return code;
            }
            var st = code[at];
            CodeInstruction load;
            if (st.opcode == OpCodes.Stloc_0) load = new CodeInstruction(OpCodes.Ldloc_0);
            else if (st.opcode == OpCodes.Stloc_1) load = new CodeInstruction(OpCodes.Ldloc_1);
            else if (st.opcode == OpCodes.Stloc_2) load = new CodeInstruction(OpCodes.Ldloc_2);
            else if (st.opcode == OpCodes.Stloc_3) load = new CodeInstruction(OpCodes.Ldloc_3);
            else load = new CodeInstruction(OpCodes.Ldloc_S, st.operand);
            var next = code[at + 1];
            load.labels.AddRange(next.labels);
            next.labels.Clear();
            code.InsertRange(at + 1, new[]
            {
                load,
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Plugin), nameof(LowGravityScale))),
                new CodeInstruction(st.opcode, st.operand),
            });
            return code;
        }

        // Gravity for a world WorldScale times bigger. Called (via the transpilers
        // below) everywhere Snowboarder/Caster.Cast used their inlined 20f/17f
        // gravity constants. Returns the input untouched when the map is unscaled
        // or the feature is off — with WorldScale hard-reset to 1 on every
        // built-in selection and menu return, built-in gravity is provably stock.
        public static float ScaledGravity(float baseGravity)
        {
            float g = baseGravity;
            // Legacy opt-in: heavier gravity proportional to a scaled-UP world,
            // to shrink jump arcs back toward original proportions. Off by
            // default; inert at scale 1.
            if (WorldScale != 1f && scaleGravityWithMap != null && scaleGravityWithMap.Value)
                g *= WorldScale;
            // Per-map gravity multiplier from the sidecar (feature 8). Independent
            // of world scale and always applied — so a 1x port can still ask for
            // heavier gravity. 1.0 (the default, and reset on every built-in
            // selection / menu return) leaves gravity untouched, keeping built-in
            // levels provably stock.
            g *= MapGravityMultiplier;
            return g;
        }

        // Routes matching ldc.r4 literals through ScaledGravity. The gravity
        // constants are C# consts (Snowboarder.Gravity=20 / SoftGravity=17), so
        // they're inlined at every call site and can only be caught as literals.
        // Each caller passes ONLY the values that are gravity in that method —
        // e.g. UpdateGrinding also has a 20f windup multiplier, so it patches
        // just its 17f. Known non-bug at large scales: AI cornering gets sloppier
        // (absolute-meter look-ahead caps in AISnowboarder) — expected, not a
        // gravity problem.
        internal static IEnumerable<CodeInstruction> ScaleGravityLiterals(
            IEnumerable<CodeInstruction> instructions, string label, float[] values)
        {
            var scaled = AccessTools.Method(typeof(Plugin), nameof(ScaledGravity));
            int patched = 0;
            foreach (var ins in instructions)
            {
                yield return ins;
                if (ins.opcode == OpCodes.Ldc_R4 && ins.operand is float f
                    && Array.IndexOf(values, f) >= 0)
                {
                    yield return new CodeInstruction(OpCodes.Call, scaled);
                    patched++;
                }
            }
            if (patched == 0)
                Log.LogWarning($"[Gravity] {label}: no gravity literal found to patch — game update changed the method?");
            else
                Log.LogDebug($"[Gravity] {label}: routed {patched} gravity literal(s) through ScaledGravity");
        }
    }

    // Gravity scaling for scaled custom maps. The gravity consts are inlined at
    // their use sites (see Plugin.ScaleGravityLiterals); the verified gravity
    // sites are exactly these three methods — Snowboarder.Integrate (g=20, soft
    // g=17 while rising), Snowboarder.UpdateGrinding (17 pulling the rider onto
    // the rail; its 20f is a windup multiplier and is NOT patched), and
    // Caster.Cast (the 20/17 trajectory prediction, which must agree with
    // Integrate or predicted landings drift). LaunchPad code also inlines 20f
    // but is deliberately untouched (out of scope, native tag mechanic).
    [HarmonyPatch(typeof(Snowboarder), "Integrate")]
    public class Snowboarder_Integrate_Gravity
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var scaled = new List<CodeInstruction>(Plugin.ScaleGravityLiterals(instructions, "Snowboarder.Integrate", new[] { 20f, 17f }));
            return Plugin.RestoreLowGravity(scaled);
        }
    }

    // Build 25617730 added `if (groundNormal.y < 0) velocity -= groundNormal * 20 * dt`
    // to UpdateGrounded — a push off overhangs, and the only new gravity-like
    // literal. UpdateGrounded's two other 20s (windup, particle emission) are not
    // gravity, so match only the 20 loaded right after `ldfld groundNormal`.
    [HarmonyPatch(typeof(Snowboarder), "UpdateGrounded")]
    public class Snowboarder_UpdateGrounded_Gravity
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var scaled = AccessTools.Method(typeof(Plugin), nameof(Plugin.ScaledGravity));
            int patched = 0;
            CodeInstruction prev = null;
            foreach (var ins in instructions)
            {
                yield return ins;
                if (ins.opcode == OpCodes.Ldc_R4 && ins.operand is float f && f == 20f
                    && prev != null && prev.opcode == OpCodes.Ldfld
                    && prev.operand is System.Reflection.FieldInfo fi && fi.Name == "groundNormal")
                {
                    yield return new CodeInstruction(OpCodes.Call, scaled);
                    patched++;
                }
                prev = ins;
            }
            if (patched != 1)
                Plugin.Log.LogWarning($"[Gravity] Snowboarder.UpdateGrounded: expected 1 overhang-push literal, patched {patched} — game update changed the method?");
        }
    }

    [HarmonyPatch(typeof(Snowboarder), "UpdateGrinding")]
    public class Snowboarder_UpdateGrinding_Gravity
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return Plugin.ScaleGravityLiterals(instructions, "Snowboarder.UpdateGrinding", new[] { 17f });
        }
    }

    [HarmonyPatch(typeof(Caster), "Cast")]
    public class Caster_Cast_Gravity
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return Plugin.ScaleGravityLiterals(instructions, "Caster.Cast", new[] { 20f, 17f });
        }
    }
}
