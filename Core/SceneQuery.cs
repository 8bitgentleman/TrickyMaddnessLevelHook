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
        // Resolves a Destructible DisableRef_ marker back to the live scene GO. The
        // importer records the SSX instance's ORIGINAL name, but the pipeline may have
        // prefixed the scene GO (UVScroll_<u>_<v>_<name>, PhysProp_<name>) — so accept
        // an exact match or any name ending in "_<name>". Dropped instances (megaple's
        // invisible Glass_Surface proxies) simply resolve to null and are skipped.
        private static GameObject FindSceneGOByInstanceName(UnityEngine.SceneManagement.Scene scene, string instName)
        {
            string suffix = "_" + instName;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                {
                    // A marker naming the instance ("DisableRef_<name>", "PfxHide_<name>") also
                    // ends in "_<name>" — never hand one back as its own target.
                    if (tr.name.StartsWith("DisableRef_", StringComparison.Ordinal)
                        || tr.name.StartsWith("PfxHide_", StringComparison.Ordinal)) continue;
                    if (tr.name == instName || tr.name.EndsWith(suffix, StringComparison.Ordinal))
                        return tr.gameObject;
                }
            return null;
        }

        // ---- time budget rescue -------------------------------------------------
        // Medal times and the freestyle clock are baked into MakerLevelData by the
        // importer. Until 2026-09-19 it baked `105/120/135/200 * (S*100)` — the BUILD
        // SCALE, which is a source-unit conversion factor and says nothing about how
        // long the course is. Every 2.5x map therefore shipped the same 337 s bronze
        // whether its route was 610 units or 84,667; the X-Games maps (S ~ 1.4) shipped
        // a 5.2-hour gold. Both failures are silent — an unreachable medal and an
        // automatic one look identical from the menu — and they are baked into 150-odd
        // ALREADY DEPLOYED bundles.
        //
        // So derive the same numbers here, at load, from the scene's own race line.
        // That fixes the deployed maps with a DLL drop and no rebuilds; as maps are
        // rebuilt the importer's values arrive correct and this stops firing, because
        // the trigger is the old placeholder's RATIO SIGNATURE (silver/gold = 120/105,
        // bronze/gold = 135/105, free/gold = 200/105) and the derived values do not
        // have it. A hand-tuned map does not have it either, so this cannot stomp one.
        //
        // The constants are the importer's, deliberately duplicated rather than shared
        // (the Hook does not reference the editor assembly). Keep them in step with
        // SSXImporter.BuildMakers — a divergence shows up as a map whose medals change
        // when it is rebuilt.
        // Scene-scoped component lookups. See the warning in DeriveBudgets: the global
        // Find* family is unsafe here because loaded scenes stack and never unload.
        private static T FindInScene<T>(Scene scene) where T : Component
        {
            if (!scene.IsValid()) return null;
            foreach (var go in scene.GetRootGameObjects())
            {
                var hit = go.GetComponentInChildren<T>(true);
                if (hit != null) return hit;
            }
            return null;
        }

        private static List<T> FindAllInScene<T>(Scene scene) where T : Component
        {
            var all = new List<T>();
            if (!scene.IsValid()) return all;
            foreach (var go in scene.GetRootGameObjects())
                all.AddRange(go.GetComponentsInChildren<T>(true));
            return all;
        }
    }
}
