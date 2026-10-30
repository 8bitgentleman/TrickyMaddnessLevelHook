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
        private static void RunDebugBundleLoad()
        {
            Log.LogInfo($"[DebugBundleLoad] Probing {Paths.Count} registered map(s)...");
            for (int i = 0; i < Paths.Count; i++)
            {
                string p = Paths[i];
                try
                {
                    var bundle = AssetBundle.LoadFromFile(p);
                    if (bundle == null)
                    {
                        Log.LogError($"[DebugBundleLoad] LoadFromFile returned NULL for {p}");
                        continue;
                    }
                    string[] scenes = bundle.GetAllScenePaths();
                    if (scenes == null || scenes.Length == 0)
                    {
                        Log.LogError($"[DebugBundleLoad] {Path.GetFileName(p)} loaded but has NO scene paths");
                    }
                    else
                    {
                        Log.LogInfo($"[DebugBundleLoad] {Path.GetFileName(p)} OK, {scenes.Length} scene(s): {string.Join(", ", scenes)}");
                    }
                    bundle.Unload(true);
                }
                catch (Exception e)
                {
                    Log.LogError($"[DebugBundleLoad] Exception loading {p}: {e}");
                }
            }
        }

        // Headless test hook: selects a named track through the game's own
        // SelectLevel(index) — the exact path a real menu click takes (racer
        // setup, LoadScene coroutine, our prefix), so what this exercises is what
        // a player exercises.
        //
        // ⚠️ It is also the only no-menu route in. This comment used to contrast
        // it with a vanilla "attract mode" picking maps at random; there is no such
        // thing — SelectLevel and PlayAgain are LoadScene's only callers in the
        // whole assembly. HOOK_NOTES.md §10 carries the disassembly evidence.
        private static System.Collections.IEnumerator AutoLoadMap(string trackName)
        {
            float deadline = Time.realtimeSinceStartup + 60f;
            while (MenuManager.Instance == null && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (MenuManager.Instance == null)
            {
                Log.LogError("[Debug] AutoLoadMap: MenuManager never appeared within 60s");
                yield break;
            }
            yield return new WaitForSecondsRealtime(5f); // let the menu finish booting

            int index = -1;
            for (int i = 0; i < LevelEntries.GetLevelCount(); i++)
            {
                if (EntryIsTrack(LevelEntries.GetEntry(i), trackName)) { index = i; break; }
            }
            if (index < 0)
            {
                // Name the game's OWN courses on the way out. Custom maps are named
                // by their file in Maps/ and are therefore discoverable on disk; the
                // built-ins are named nowhere outside this list, which makes the one
                // typo you cannot fix by looking at the filesystem also the one this
                // error is least helpful about.
                var builtins = new List<string>();
                for (int i = 0; i < LevelEntries.GetLevelCount(); i++)
                {
                    var e = LevelEntries.GetEntry(i);
                    if (e.sceneName != null && !e.sceneName.StartsWith("$"))
                        builtins.Add("'" + e.name + "'");
                }
                Log.LogError($"[Debug] AutoLoadMap: no registered level named '{trackName}'"
                    + " — the game's own courses are: "
                    + (builtins.Count == 0 ? "(none found)" : string.Join(", ", builtins.ToArray())));
                yield break;
            }
            // MenuManager.difficulty is written ONLY by SelectDifficulty (a menu click)
            // and defaults to 0 = casual, so a menu-less boot raced every COM at half
            // competence (GetDifficulty = 0.5 + 0.25*difficulty) whatever the player
            // normally picks. Apply the saved last-selected difficulty instead.
            int difficulty = -1;
            try
            {
                var getLast = AccessTools.PropertyGetter(typeof(SettingsManager), "lastSelectedDifficulty");
                var fDifficulty = AccessTools.Field(typeof(MenuManager), "difficulty");
                if (getLast != null && fDifficulty != null)
                {
                    difficulty = (int)getLast.Invoke(null, null);
                    fDifficulty.SetValue(MenuManager.Instance, difficulty);
                }
            }
            catch (Exception e) { Log.LogWarning($"[Debug] AutoLoadMap: could not apply the saved difficulty: {e.Message}"); }
            Log.LogInfo($"[Debug] AutoLoadMap: selecting '{trackName}' (index {index}), difficulty "
                + (difficulty < 0 ? "UNSET (casual)" : difficulty.ToString()));
            MenuManager.Instance.SelectLevel(index);
        }
    }
}
