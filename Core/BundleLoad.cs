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
    // Wall-clock readout of a custom map's load, one table per load in LogOutput.log
    // under [LoadTime]. Each row is the time since the PREVIOUS mark, so a phase
    // that logged nothing (and so set no mark) is folded into the next row.
    // Marks with the same label sum. Custom maps only: Begin is in the LoadScene
    // prefix, and every other Mark is a no-op until it has run.
    internal static class LoadClock
    {
        static readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
        static readonly List<KeyValuePair<string, double>> rows = new List<KeyValuePair<string, double>>();
        static double last;
        static string map;
        internal static bool Running => watch.IsRunning;

        internal static void Begin(string mapName)
        {
            rows.Clear();
            map = mapName;
            last = 0;
            watch.Restart();
        }

        internal static void Mark(string label)
        {
            if (!watch.IsRunning) return;
            double now = watch.Elapsed.TotalMilliseconds;
            int i = rows.FindIndex(r => r.Key == label);
            if (i < 0) rows.Add(new KeyValuePair<string, double>(label, now - last));
            else rows[i] = new KeyValuePair<string, double>(label, rows[i].Value + now - last);
            last = now;
        }

        internal static System.Collections.IEnumerator ReportAfterFirstFrame()
        {
            yield return null; // Start()s run before this frame's Update
            Mark("Start()s -> first frame");
            yield return null;
            Mark("second frame");
            watch.Stop();
            var sb = new System.Text.StringBuilder();
            sb.Append($"[LoadTime] '{map}' {last / 1000.0:F2} s total (LoadScene prefix -> second frame):");
            foreach (var r in rows)
                sb.Append($"\n[LoadTime]   {r.Value,9:F0} ms  {r.Key}");
            Plugin.Log.LogInfo(sb.ToString());
        }
    }

    // Splits LoadClock's "Start()s -> first frame" row around LevelManager.Start,
    // outermost on both sides so every other mod's Start patch lands inside it.
    [HarmonyPatch(typeof(LevelManager), "Start")]
    public class LevelManager_Start_LoadClock
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix() => LoadClock.Mark("sceneLoaded -> LevelManager.Start");

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        public static void Postfix() => LoadClock.Mark("LevelManager.Start (+ all patches)");
    }

    [HarmonyPatch(typeof(MenuManager), "LoadScene", new System.Type[] { typeof(LevelEntry) })]
    public class MenuManager_SelectLevel
    {
        // Returning false skips MenuManager.LoadScene entirely — the clean no-op
        // abort for any failure: the menu simply stays up instead of loading a
        // black screen. Every abort path logs first. State (myLoadedAssetBundle,
        // LoadedAssetBundle, LoadedTrack) is only committed AFTER a bundle has been
        // confirmed loaded with at least one scene, so a failed load can never
        // leave a null handle for a later selection to NRE on.
        [HarmonyPrefix]
        public static bool Prefix(ref LevelEntry levelEntry)
        {
            if (levelEntry.sceneName == null || !levelEntry.sceneName.Contains("$"))
            {
                // A custom load may still be in flight; unloading its bundle below
                // would rug-pull the pending LoadSceneAsync exactly like switching
                // custom maps would. Same guard semantics as the custom branch.
                if (Plugin.pendingSceneName != null
                    && Time.realtimeSinceStartup - Plugin.pendingSince < 30f)
                {
                    Plugin.Log.LogWarning($"LoadScene prefix: load of '{Plugin.pendingSceneName}' in progress; ignoring builtin selection");
                    return false;
                }

                // Built-in level: no custom bundle should stay loaded. Guard the
                // unload (there may be none) and reset ALL custom state, including
                // any in-flight guard, so the shader-remap/skybox gating (which
                // treats LoadedAssetBundle == "" as "no custom bundle") is correct.
                Plugin.LoadedAssetBundle = "";
                Plugin.LoadedTrack = null;
                if (Plugin.myLoadedAssetBundle != null)
                {
                    Plugin.myLoadedAssetBundle.Unload(true);
                    Plugin.myLoadedAssetBundle = null;
                }
                Plugin.pendingSceneName = null;
                Plugin.WorldScale = 1f;
                Plugin.MapGravityMultiplier = 1f;
                Plugin.MapPhysicsHz = 0f;
                Plugin.MapRailCatch = 0f;
                Plugin.MapRidePace = 0f;
                Plugin.MapRouteScale = 0f;
                Plugin.ApplyPhysicsRate("built-in level selected");
                Plugin.BundleNativePlatform = null;
                Plugin.BundleSource = Plugin.DefaultSource;
                Plugin.BundleMapVersion = "";
                return true;
            }

            try
            {
                string path = levelEntry.sceneName.TrimStart('$');
                Plugin.Log.LogInfo($"LoadScene prefix: loading custom map bundle '{levelEntry.name}' from {path}");
                LoadClock.Begin(levelEntry.name);

                // 1. A previous custom load may still be in flight. Unloading its
                // bundle now (to load another) would pull the rug from a pending
                // LoadSceneAsync. Reject the new selection until it settles.
                if (Plugin.pendingSceneName != null)
                {
                    if (Time.realtimeSinceStartup - Plugin.pendingSince < 30f)
                    {
                        Plugin.Log.LogWarning($"LoadScene prefix: load of '{Plugin.pendingSceneName}' already in progress; ignoring selection");
                        return false;
                    }
                    Plugin.Log.LogError($"LoadScene prefix: previous load of '{Plugin.pendingSceneName}' never completed within 30s; clearing and continuing");
                    Plugin.pendingSceneName = null;
                }

                AssetBundle bundle;
                if (Plugin.LoadedAssetBundle == path && Plugin.myLoadedAssetBundle != null)
                {
                    // 2. Same map re-selected and its bundle is still loaded: reuse it.
                    bundle = Plugin.myLoadedAssetBundle;
                    Plugin.Log.LogInfo($"LoadScene prefix: reusing already-loaded bundle {path}");
                }
                else
                {
                    // 3. Switching maps. The previously loaded bundle is idle here
                    // (no load pending — step 1 guarantees it — and level scenes are
                    // unloaded while in the menu), so unloading it is safe.
                    if (Plugin.myLoadedAssetBundle != null)
                    {
                        Plugin.myLoadedAssetBundle.Unload(true);
                        Plugin.myLoadedAssetBundle = null;
                        Plugin.LoadedAssetBundle = "";
                        Plugin.LoadedTrack = null;
                        Plugin.Log.LogInfo("LoadScene prefix: unloaded previous custom bundle");
                    }

                    // 4. Snapshot the game's shaders exactly once per session,
                    // BEFORE any bundle is ever loaded so nothing bundle-origin can
                    // pollute it. (Once captured it stays valid — the game's shaders
                    // don't change — so no re-snapshot on later switches.)
                    if (Plugin.GameShaders == null)
                        Plugin.SnapshotGameShaders();

                    // 5. Load the bundle. Null means the file is missing/corrupt or
                    // already loaded elsewhere — abort with state still cleared.
                    bundle = AssetBundle.LoadFromFile(path);
                    LoadClock.Mark("AssetBundle.LoadFromFile");
                    if (bundle == null)
                    {
                        Plugin.Log.LogError($"LoadScene prefix: bundle load failed (file missing/corrupt or already loaded): {path}");
                        return false;
                    }

                    // 6. A bundle with no scene is unusable — unload and abort.
                    string[] scenePaths = bundle.GetAllScenePaths();
                    if (scenePaths == null || scenePaths.Length == 0)
                    {
                        Plugin.Log.LogError($"LoadScene prefix: bundle has no scene paths, unusable: {path}");
                        bundle.Unload(true);
                        return false;
                    }

                    // 7. Commit state only now that the load is known good.
                    Plugin.myLoadedAssetBundle = bundle;
                    Plugin.LoadedAssetBundle = path;
                    Plugin.LoadedTrack = scenePaths[0];
                }

                // Per-map world scale from the sidecar header (1.0 when absent).
                // Read on reuse too — the sidecar may have changed on disk.
                Plugin.WorldScale = Plugin.ReadWorldScale(path);
                if (Plugin.WorldScale != 1f)
                    Plugin.Log.LogInfo($"[Gravity] map declares world scale {Plugin.WorldScale.ToString(CultureInfo.InvariantCulture)}; gravity scaling {(Plugin.scaleGravityWithMap.Value ? "enabled" : "disabled by config")}");

                // Per-map gravity multiplier from the same sidecar (1.0 when absent).
                // Read on reuse too — the sidecar may have changed on disk.
                Plugin.MapGravityMultiplier = Plugin.ReadGravityMultiplier(path);
                if (Plugin.MapGravityMultiplier != 1f)
                    Plugin.Log.LogInfo($"[Gravity] map declares gravity multiplier {Plugin.MapGravityMultiplier.ToString(CultureInfo.InvariantCulture)}x (applied on top of any world-scale gravity)");

                // Per-map physics tick rate from the same sidecar (0 = leave the
                // game's rate alone). Applied here rather than at sceneLoaded so
                // the rate is already right for the level's first FixedUpdate.
                Plugin.MapPhysicsHz = Plugin.ReadPhysicsHz(path);
                if (Plugin.MapPhysicsHz > 0f && !Plugin.mapPhysicsRate.Value)
                    Plugin.Log.LogInfo($"[Physics] map declares physicshz {Plugin.MapPhysicsHz.ToString(CultureInfo.InvariantCulture)} but MapPhysicsRate is off in config; keeping the game's rate");
                Plugin.ApplyPhysicsRate($"map '{levelEntry.name}'");

                // Per-map rail catch half-width from the same sidecar (0 = no
                // declaration, keep the 0.25 * WorldScale formula).
                Plugin.MapRailCatch = Plugin.ReadRailCatch(path);
                if (Plugin.MapRailCatch > 0f)
                    Plugin.Log.LogInfo($"[Rails] map declares railcatch {Plugin.MapRailCatch.ToString(CultureInfo.InvariantCulture)} m "
                        + $"(absolute); ignoring the 0.25 * worldScale default of {(0.25f * Plugin.WorldScale).ToString(CultureInfo.InvariantCulture)} m");

                // Per-map route scale (0 = no declaration, route tolerances keep
                // scaling with WorldScale).
                Plugin.MapRouteScale = Plugin.ReadRouteScale(path);
                if (Plugin.MapRouteScale > 0f)
                    Plugin.Log.LogInfo($"[AIPaths] map declares routescale {Plugin.MapRouteScale.ToString(CultureInfo.InvariantCulture)} "
                        + $"(worldScale {Plugin.WorldScale.ToString(CultureInfo.InvariantCulture)}); route/respawn/medal tolerances use it");

                // Per-map gold pace from the same sidecar (0 = no declaration, keep
                // the 45 u/s fallback). Only the medal rescue in DeriveBudgets reads
                // it, and only on a map still carrying the old placeholder times.
                Plugin.MapRidePace = Plugin.ReadRidePace(path);
                if (Plugin.MapRidePace > 0f)
                    Plugin.Log.LogInfo($"[TimeBudget] map declares ridepace {Plugin.MapRidePace.ToString(CultureInfo.InvariantCulture)} u/s");

                // Native-platform declaration from the same sidecar (null when absent).
                // Read on reuse too — the sidecar may have changed on disk.
                Plugin.BundleNativePlatform = Plugin.ReadBundleNativePlatform(path);
                if (Plugin.BundleNativePlatform != null)
                    Plugin.Log.LogInfo($"[ShaderRemap] map declares native platform '{Plugin.BundleNativePlatform}'; its own shaders are trusted (not remapped) when running on that backend");

                // Provenance declaration from the same sidecar (defaults to
                // "tricky_madness" when absent). Read on reuse too — the sidecar
                // may have changed on disk. Informational: nothing keys off it.
                Plugin.BundleSource = Plugin.ReadBundleSource(path);
                Plugin.Log.LogInfo($"[Source] map '{levelEntry.name}' provenance: {Plugin.BundleSource}");
                Plugin.BundleMapVersion = Plugin.ReadBundleMapVersion(path);
                Plugin.Log.LogInfo($"[Source] map '{levelEntry.name}' version: "
                    + (Plugin.BundleMapVersion.Length > 0 ? Plugin.BundleMapVersion : "(unversioned)"));

                // Spawn/respawn ground-snap mode from the same sidecar (Both when
                // absent). Read on reuse too — the sidecar may have changed on disk.
                Plugin.SpawnSnapMode = Plugin.ReadSpawnSnap(path);
                if (Plugin.SpawnSnapMode != Plugin.SpawnSnap.Both)
                    Plugin.Log.LogInfo($"[Spawn] map declares spawnsnap '{Plugin.SpawnSnapMode.ToString().ToLowerInvariant()}'");

                // Derive the bundle-internal scene name from LoadedTrack. NOT Path.*:
                // on macOS '\' is an ordinary filename character, not a separator.
                var parts = Plugin.LoadedTrack.Split('\\', '/');
                string sceneName = parts[parts.Length - 1].Split('.')[0];
                levelEntry.sceneName = sceneName;
                Plugin.pendingSceneName = sceneName;
                Plugin.pendingSince = Time.realtimeSinceStartup;

                if (Plugin.Instance != null)
                    Plugin.Instance.StartCoroutine(Plugin.LoadWatchdog(sceneName));

                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"LoadScene prefix: exception loading custom map: {e}");
                return false;
            }
        }
    }
}
