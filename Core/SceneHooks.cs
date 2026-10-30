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
        // Does this level entry answer to `track`? Its registered name, or — for a
        // custom map — the Maps/ file stem. A `displayname` sidecar header makes those
        // two differ, and the editor's Playtest From Pin sends the stem (it only knows
        // BuildBundle.DisplayNames), so matching the name alone loses X-Games maps.
        internal static bool EntryIsTrack(LevelEntry entry, string track)
        {
            if (string.IsNullOrEmpty(track)) return false;
            if (string.Equals(entry.name, track, StringComparison.OrdinalIgnoreCase)) return true;
            return entry.sceneName != null && entry.sceneName.StartsWith("$")
                && string.Equals(Path.GetFileNameWithoutExtension(entry.sceneName.Substring(1)),
                                 track, StringComparison.OrdinalIgnoreCase);
        }

        // Scene name of the active custom map, derived from LoadedTrack the same
        // way the LoadScene prefix does (never Path.* — on macOS '\' is an
        // ordinary filename character). Null when no custom bundle is active.
        internal static string CustomSceneName()
        {
            if (string.IsNullOrEmpty(LoadedAssetBundle) || string.IsNullOrEmpty(LoadedTrack))
                return null;
            var parts = LoadedTrack.Split('\\', '/');
            return parts[parts.Length - 1].Split('.')[0];
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (LoadClock.Running && Instance != null)
            {
                LoadClock.Mark("Awake -> sceneLoaded");
                Instance.StartCoroutine(LoadClock.ReportAfterFirstFrame());
            }
            // Material fixing must only run for the custom bundle's own scene.
            // Gating on the flag alone is not enough — returning to the menu doesn't
            // go through MenuManager.LoadScene(LevelEntry), so the flag stays set and
            // we'd rebind MENU materials while the bundle's DX-compiled
            // duplicate-name shaders are still loaded (Shader.Find with duplicate
            // names is unspecified → potential pink menu until restart).
            string customScene = CustomSceneName();
            bool isCustomScene = customScene != null && scene.name == customScene;

            // The custom scene has finished loading: retire the in-flight guard so
            // future selections aren't rejected as "already in progress".
            if (isCustomScene && scene.name == pendingSceneName)
                pendingSceneName = null;

            // Leaving a scaled custom map for anywhere else (menu return doesn't
            // go through the LoadScene prefix) restores stock gravity. The pending
            // guard keeps a possible intermediate loading scene during a custom
            // load from resetting the scale the prefix just read.
            // MapPhysicsHz belongs to the same reset — and must be part of the
            // GUARD too, not just the body: a map that declares only physicshz
            // (no scale, no gravity) would otherwise fail the condition and leave
            // its tick rate applied to every level loaded afterwards.
            if (!isCustomScene && pendingSceneName == null
                && (WorldScale != 1f || MapGravityMultiplier != 1f || MapPhysicsHz != 0f
                    || MapRailCatch != 0f || MapRidePace != 0f || MapRouteScale != 0f))
            {
                WorldScale = 1f;
                MapGravityMultiplier = 1f;
                MapPhysicsHz = 0f;
                MapRailCatch = 0f;
                MapRidePace = 0f;
                MapRouteScale = 0f;
                ApplyPhysicsRate("left custom map");
                Log.LogInfo("[Gravity] left custom map; world scale + gravity multiplier reset to 1");
            }

            // Per-level sky fill must never outlive its map (same lifetime rule as
            // WorldScale); LoadLevelManager re-reads it on the next custom load.
            if (!isCustomScene && pendingSceneName == null)
                SceneSkyFill = null;

            // Same lifetime rule for depth priming: off for our maps, the game's own value
            // back everywhere else (an intermediate loading scene during a custom load
            // leaves it alone).
            if (isCustomScene) SetDepthPriming(false);
            else if (pendingSceneName == null) SetDepthPriming(true);

            // The skybox needs watching on EVERY scene: on built-in levels/menus we
            // only cache a known-good game skybox material (never modify anything);
            // on the custom scene we wait for the bundle's skybox to be bound —
            // empirically RenderSettings.skybox is still null when sceneLoaded fires
            // and only appears during activation — and then fix it.
            if (Instance != null) Instance.StartCoroutine(SkyboxWatch(isCustomScene));

            // Rider telemetry on the game's OWN courses. LoadLevelManager runs for
            // custom bundles only, so without this the instrument can measure the
            // courses we author and none of the four the game ships — and the
            // shipped courses are the calibration the authored ones get sized
            // against ("match forest's rail gap in seconds" is only answerable if
            // forest can be measured). Gated on riderTelemetry ALONE: the respawn
            // half of the harness exists to fix OUR level data's out-of-bounds
            // shell and has no business reporting on a built-in level.
            if (!isCustomScene && riderTelemetry != null && riderTelemetry.Value
                && Instance != null)
                Instance.StartCoroutine(TelemetryOnBuiltIn(scene));

            if (!isCustomScene) return;

            // (The open-top sky-fill for domed maps is applied by SkydomeFollow directly
            // on the render camera in OnBeginCamera — it can't rely on Camera.main here,
            // which is null on these maps, so it doesn't route through SolidColorSky.)

            try
            {
                int scanned = 0, remapped = 0, litFallbacks = 0, pruned = 0;
                var seen = new HashSet<Material>();
                var missing = new HashSet<string>();

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        foreach (var mat in r.sharedMaterials)
                        {
                            if (mat == null || !seen.Add(mat)) continue;
                            scanned++;
                            FixMaterial(mat, missing, ref remapped, ref litFallbacks, ref pruned);
                        }
                    }
                }

                int detailed = 0;
                foreach (var mat in seen) if (ApplyTerrainDetail(mat)) detailed++;
                if (detailed > 0)
                    Log.LogInfo($"[TerrainDetail] scene '{scene.name}': {detailed} material(s) at strength {terrainDetail.Value:0.##}");

                // Clip-only materials: the V2-ported animation flipbooks (crowd cd
                // frames, sign frames) live ONLY inside AnimationClips until their
                // first frame swaps them onto a renderer — the renderer walk above
                // never sees them, so on the foreign-GPU path they'd flip to
                // magenta mid-animation. Sweep every loaded material with the
                // importer's "tex_" name prefix instead; FixMaterial is a no-op on
                // game-origin shaders, so catching a stray game "tex_*" is harmless.
                foreach (var mat in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (mat == null || !mat.name.StartsWith("tex_", StringComparison.Ordinal) || !seen.Add(mat)) continue;
                    scanned++;
                    FixMaterial(mat, missing, ref remapped, ref litFallbacks, ref pruned);
                }

                string miss = missing.Count == 0 ? "none" : string.Join(", ", missing);
                Log.LogInfo($"[ShaderRemap] scene '{scene.name}': scanned {scanned} material(s), remapped {remapped}, lit-fallbacks {litFallbacks}, keyword-pruned {pruned}, unfixable: {miss}");
            }
            catch (Exception e)
            {
                Log.LogError($"[ShaderRemap] failed on scene '{scene.name}': {e}");
            }
        }

        // A built-in level has nothing equivalent to LoadLevelManager to hang the
        // harness off, and the menu is a scene like any other — so wait for a
        // LevelManager to appear and spawn only if one does. No LevelManager, no
        // ride, no CSV. The wait is frames rather than seconds because the harness
        // must be alive before the rider leaves the gate; two seconds of frames is
        // far past when LevelManager.Start has run and still finite, so a menu
        // scene costs 120 no-op frames and then stops asking.
        static System.Collections.IEnumerator TelemetryOnBuiltIn(Scene scene)
        {
            for (int i = 0; i < 120; i++)
            {
                if (UnityEngine.Object.FindObjectOfType<LevelManager>() != null)
                {
                    PerfHarness.Spawn(scene.name);
                    yield break;
                }
                yield return null;
            }
        }

        // After a custom-map swap, verify the scene actually loads. OnSceneLoaded
        // clears pendingSceneName on success; if a newer selection replaced it,
        // pendingSceneName no longer equals ours and we bow out. Only a genuine
        // never-loaded scene (bundle wrong/broken) trips the timeout.
        internal static System.Collections.IEnumerator LoadWatchdog(string sceneName)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (pendingSceneName != sceneName) yield break; // loaded, or superseded
                yield return null;
            }
            if (pendingSceneName == sceneName)
            {
                Log.LogError($"LoadScene prefix: custom scene '{sceneName}' never loaded within 30s — check the bundle");
                pendingSceneName = null;
            }
        }
    }
}
