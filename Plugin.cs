using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection.Emit;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace TrickyMaddnessLevelHook
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static Harmony harmony;
        public static MenuManager menuManager;

        public static AssetBundle myLoadedAssetBundle;
        public static string LoadedAssetBundle = "";
        public static string LoadedTrack;
        public static string LoadedTrackName;

        // Custom-scene load in flight. pendingSceneName is the bundle-internal
        // scene name we told MenuManager to load; null once it has loaded (or the
        // load was aborted). pendingSince timestamps the swap so a load that never
        // arrives can be detected and cleared instead of wedging future selections.
        internal static string pendingSceneName;
        internal static float pendingSince;

        // When false, the level-select card strip is left exactly as vanilla (no
        // scrolling behaviour attached). Default on.
        internal static ConfigEntry<bool> levelSelectScroll;

        // Opt-in feel-tuning knob (default OFF): multiply the game's rider/caster
        // gravity constants by the active map's declared world scale. Scaled-up
        // maps deliberately keep stock gravity — the rider's jumps are tuned for
        // big courses and over-clear a 1x map, so a bigger world with the SAME
        // jump is the point; heavier gravity would shrink the arcs right back.
        // Inert at scale 1 and on built-in levels regardless of the flag.
        internal static ConfigEntry<bool> scaleGravityWithMap;
        // World scale of the active custom map, from the optional "scale <f>"
        // header line in its .aipaths.txt sidecar (gen_aipaths.py --scale).
        // 1.0 for built-in levels, sidecar-less maps, and maps with no scale line
        // — i.e. everything that exists today.
        internal static float WorldScale = 1f;

        public static List<string> TrackNames = new List<string>();
        public static List<string> Paths = new List<string>();

        // Resolved absolute path to the Maps directory (cross-platform).
        public static string MapsDir;
        // Static log sink so the Harmony-patched static methods can log too.
        internal static ManualLogSource Log;
        // When true, every registered map is opened at Awake and its scene paths
        // logged, to verify a Windows-built bundle even loads on this platform
        // without needing to navigate menus. Off in shipped builds.
        private static ConfigEntry<bool> debugBundleLoad;
        // When true, [ShaderRemap] logs one line per scanned material (shader name,
        // origin, isSupported, keywords, action taken). Off in shipped builds.
        private static ConfigEntry<bool> shaderDiagnostics;
        // Name of a registered track to auto-select shortly after boot, for
        // deterministic headless testing (attract mode picks maps at random).
        // Empty in shipped builds.
        private static ConfigEntry<string> debugAutoLoadMap;
        // When true, after building rail colliders a SphereCast probe verifies the
        // game-side grind-detection contract (layer-9 hit + GetComponent<Rail>).
        // Off in shipped builds.
        private static ConfigEntry<bool> railColliderProbe;

        // Snapshot of the GAME's shaders, taken in the LoadScene prefix right
        // before the map bundle is loaded (so nothing bundle-origin can be in it).
        // Used to (a) resolve duplicate shader names to the game's platform-correct
        // copy — Shader.Find is unspecified with duplicates — and (b) tell whether
        // a material's shader shipped with the game or with the bundle.
        internal static Dictionary<string, Shader> GameShaders;
        internal static HashSet<int> GameShaderIds;
        // Per game-shader union of keywords observed on game materials. Bundle
        // material keywords outside this set select shader variants the game build
        // likely stripped (magenta at draw time), so we prune to this set.
        internal static Dictionary<int, HashSet<string>> GameShaderKeywords;

        internal static Plugin Instance;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            // Plugin startup logic
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");

            debugBundleLoad = Config.Bind("Debug", "DebugBundleLoad", false,
                "Load every registered map bundle at startup and log its scene paths. " +
                "Diagnostic only; leave false for normal play.");
            shaderDiagnostics = Config.Bind("Debug", "ShaderDiagnostics", false,
                "Log one line per material scanned by the shader-remap pass. " +
                "Diagnostic only; leave false for normal play.");
            debugAutoLoadMap = Config.Bind("Debug", "AutoLoadMap", "",
                "Name of a registered track to auto-select shortly after boot, for " +
                "deterministic headless testing. Diagnostic only; leave empty for " +
                "normal play.");
            railColliderProbe = Config.Bind("Debug", "RailColliderProbe", false,
                "After building rail colliders on a custom map, SphereCast-probe the " +
                "first rail to verify grind detection would hit it. Diagnostic only; " +
                "leave false for normal play.");
            levelSelectScroll = Config.Bind("UI", "LevelSelectScroll", true,
                "Scroll the level-select card strip when more cards exist than fit " +
                "on screen, auto-scrolling to the selected card. When all cards fit, " +
                "the menu is left pixel-identical to vanilla. Set false to disable.");
            scaleGravityWithMap = Config.Bind("Gameplay", "ScaleGravityWithMap", false,
                "Opt-in feel tuning: on custom maps that declare a world scale (a " +
                "'scale' line in their .aipaths.txt sidecar), multiply the " +
                "rider/caster gravity by that scale, shrinking jump arcs back " +
                "toward original proportions. Off by default — scaled-up maps are " +
                "meant to keep stock gravity so jumps cover proportionally less " +
                "ground. No effect on built-in levels or unscaled maps.");

            MapsDir = ResolveMapsDir();
            Logger.LogInfo($"Maps directory resolved to: {MapsDir}");
            if (!Directory.Exists(MapsDir))
            {
                Directory.CreateDirectory(MapsDir);
            }
            AddTracks();

            // Cross-platform shader fix: Windows-built bundles ship DX-compiled
            // shaders that won't run on Metal. Re-bind each material to the game's
            // own (platform-correct) shader of the same name once the scene loads.
            SceneManager.sceneLoaded += OnSceneLoaded;

            if (debugBundleLoad.Value)
            {
                RunDebugBundleLoad();
            }
            if (!string.IsNullOrEmpty(debugAutoLoadMap.Value))
            {
                StartCoroutine(AutoLoadMap(debugAutoLoadMap.Value));
            }

            DoPatching();
        }

        // GameRootPath points at the executable's folder. On macOS that is
        // TrickyMadness.app/Contents/MacOS; walk up to the folder that holds the
        // .app (next to run_bepinex.sh) so Maps sits where it does on Windows.
        private static string ResolveMapsDir()
        {
            string root = BepInEx.Paths.GameRootPath;
            int idx = root.IndexOf(".app" + Path.DirectorySeparatorChar + "Contents",
                StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                idx = root.IndexOf(".app/Contents", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int slash = root.LastIndexOfAny(new[] { '/', '\\' }, idx);
                if (slash > 0) root = root.Substring(0, slash);
            }
            return Path.Combine(root, "Maps");
        }

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
        // setup, LoadScene coroutine, our prefix), unlike attract mode which
        // picks maps at random.
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
                if (LevelEntries.GetEntry(i).name == trackName) { index = i; break; }
            }
            if (index < 0)
            {
                Log.LogError($"[Debug] AutoLoadMap: no registered level named '{trackName}'");
                yield break;
            }
            Log.LogInfo($"[Debug] AutoLoadMap: selecting '{trackName}' (index {index})");
            MenuManager.Instance.SelectLevel(index);
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
            if (!isCustomScene && pendingSceneName == null && WorldScale != 1f)
            {
                WorldScale = 1f;
                Log.LogInfo("[Gravity] left scaled custom map; world scale reset to 1");
            }

            // The skybox needs watching on EVERY scene: on built-in levels/menus we
            // only cache a known-good game skybox material (never modify anything);
            // on the custom scene we wait for the bundle's skybox to be bound —
            // empirically RenderSettings.skybox is still null when sceneLoaded fires
            // and only appears during activation — and then fix it.
            if (Instance != null) Instance.StartCoroutine(SkyboxWatch(isCustomScene));

            if (!isCustomScene) return;

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

                string miss = missing.Count == 0 ? "none" : string.Join(", ", missing);
                Log.LogInfo($"[ShaderRemap] scene '{scene.name}': scanned {scanned} material(s), remapped {remapped}, lit-fallbacks {litFallbacks}, keyword-pruned {pruned}, unfixable: {miss}");
            }
            catch (Exception e)
            {
                Log.LogError($"[ShaderRemap] failed on scene '{scene.name}': {e}");
            }
        }

        // A shader is fine if it shipped with the game (it runs there by
        // definition). A bundle-origin shader is broken if Unity says so — but
        // isSupported can report true for foreign-platform bytecode, so we also
        // distrust any bundle-origin shader outright when the running GPU backend
        // differs from the one the bundle was built for. We now build every bundle
        // on macOS (Metal bytecode) and ship it to BOTH platforms, so a bundle run
        // on Metal is native (its own shaders would work) yet a bundle run on D3D
        // (Windows) or Vulkan carries foreign Metal bytecode that reports
        // isSupported=true but renders magenta — those MUST be remapped to the
        // game's platform-correct shaders. Distrusting bundle shaders on every
        // backend except… nothing: the game has same-named copies of everything we
        // use, so remapping is always safe. (Historically this was Metal/Vulkan-only
        // because community bundles were Windows-built; that assumption no longer
        // holds now that we author the bundles ourselves on Mac — see CLAUDE.md
        // Hook feature 4. Verified on real D3D via CrossOver 2026-07-13.)
        private static bool ShaderLooksBroken(Shader s, bool gameOrigin)
        {
            if (gameOrigin) return false;
            if (!s.isSupported) return true;
            var gpu = SystemInfo.graphicsDeviceType;
            return gpu == UnityEngine.Rendering.GraphicsDeviceType.Metal
                || gpu == UnityEngine.Rendering.GraphicsDeviceType.Vulkan
                || gpu == UnityEngine.Rendering.GraphicsDeviceType.Direct3D11
                || gpu == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12;
        }

        private static void Diag(Material mat, string action)
        {
            if (shaderDiagnostics == null || !shaderDiagnostics.Value) return;
            var sh = mat.shader;
            bool gameOrigin = GameShaderIds != null && sh != null && GameShaderIds.Contains(sh.GetInstanceID());
            string kw = sh == null ? "" : string.Join(" ", mat.shaderKeywords);
            Log.LogInfo($"[ShaderDiag] mat '{mat.name}' shader '{(sh == null ? "<null>" : sh.name)}' gameOrigin={gameOrigin} isSupported={(sh != null && sh.isSupported)} keywords=[{kw}] -> {action}");
        }

        private static void FixMaterial(Material mat, HashSet<string> missing,
            ref int remapped, ref int litFallbacks, ref int pruned)
        {
            var sh = mat.shader;
            if (sh == null) { Diag(mat, "null shader, skipped"); return; }
            bool gameOrigin = GameShaderIds != null && GameShaderIds.Contains(sh.GetInstanceID());

            // Error shaders baked into the bundle at build time render magenta by
            // design on every platform — route them to the Lit fallback too.
            bool bakedError = sh.name.Contains("FallbackError");

            if (!bakedError && !ShaderLooksBroken(sh, gameOrigin))
            {
                Diag(mat, "ok, untouched");
                return;
            }

            Shader replacement;
            if (!bakedError && GameShaders != null && GameShaders.TryGetValue(sh.name, out replacement))
            {
                mat.shader = replacement;
                remapped++;
                List<string> removed = PruneKeywords(mat, replacement);
                if (removed != null) pruned++;
                Diag(mat, removed == null
                    ? $"remapped to game '{replacement.name}'"
                    : $"remapped to game '{replacement.name}', pruned stripped-variant keywords [{string.Join(" ", removed)}]");
                return;
            }

            // No same-name shader in the game build: substitute the game's URP Lit.
            // Property values (incl. _BaseMap/_BaseColor/_BumpMap and tiling) live on
            // the material and survive the shader swap; keywords are reset so we get
            // the base variant, which every URP build has. Plain but not pink.
            if (GameShaders != null && GameShaders.TryGetValue("Universal Render Pipeline/Lit", out replacement))
            {
                mat.shader = replacement;
                mat.shaderKeywords = new string[0];
                litFallbacks++;
                Diag(mat, "lit-fallback (keywords cleared)");
            }
            else
            {
                missing.Add(sh.name);
                Diag(mat, "UNFIXABLE: no game shader with this name and no game URP Lit");
            }
        }

        // Drop material keywords never observed on any game material using the same
        // shader: the game build only compiles the shader variants its own content
        // uses, and a bundle keyword combination outside that set can select a
        // stripped variant (magenta at draw time). Losing a keyword costs a feature
        // (e.g. emission); keeping a stripped combination costs the whole material.
        // Returns the list of removed keywords, or null if nothing was pruned.
        private static List<string> PruneKeywords(Material mat, Shader gameShader)
        {
            HashSet<string> union = null;
            if (GameShaderKeywords != null)
                GameShaderKeywords.TryGetValue(gameShader.GetInstanceID(), out union);

            var kws = mat.shaderKeywords;
            if (kws == null || kws.Length == 0) return null;
            var kept = new List<string>();
            var removed = new List<string>();
            foreach (var k in kws)
            {
                if (union != null && union.Contains(k)) kept.Add(k);
                else removed.Add(k);
            }
            if (removed.Count == 0) return null;
            mat.shaderKeywords = kept.ToArray();
            return removed;
        }

        // The game's own skybox shaders/materials (Skybox/Cubemap + SkyboxDay1 etc.)
        // live inside built-in level scenes, NOT in AlwaysIncludedShaders or
        // Resources — so from a cold boot straight into a custom map there is no
        // way to Shader.Find them. Whenever any scene shows a working game skybox,
        // remember it so a later custom map can borrow it.
        private static Material CachedGameSkybox;

        // Waits for RenderSettings.skybox to be bound (it is still null at
        // sceneLoaded time; the scene's lighting settings bind during activation).
        // On non-custom scenes this only caches a known-good game skybox material;
        // on the custom scene it fixes a broken one.
        private static System.Collections.IEnumerator SkyboxWatch(bool isCustomScene)
        {
            float deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (isCustomScene && string.IsNullOrEmpty(LoadedAssetBundle)) yield break; // bundle gone
                var sky = RenderSettings.skybox;
                if (sky != null && sky.shader != null)
                {
                    bool gameOrigin = GameShaderIds == null // pre-snapshot: nothing bundle-origin can be loaded
                        || GameShaderIds.Contains(sky.shader.GetInstanceID());
                    if (!ShaderLooksBroken(sky.shader, gameOrigin) && !sky.shader.name.Contains("FallbackError"))
                    {
                        if (gameOrigin && sky.shader.isSupported && !ReferenceEquals(sky, CachedGameSkybox))
                        {
                            CachedGameSkybox = sky;
                            Diag(sky, "working game skybox cached for custom maps");
                        }
                        if (isCustomScene) Log.LogInfo($"[ShaderRemap] skybox '{sky.shader.name}' is usable, untouched");
                        yield break;
                    }
                    if (!isCustomScene) yield break; // broken sky outside our scene is not ours to touch
                    FixSkybox(sky);
                    yield break;
                }
                yield return null;
            }
            if (isCustomScene) Log.LogInfo("[ShaderRemap] skybox: RenderSettings.skybox never appeared within 15s");
        }

        private static void FixSkybox(Material sky)
        {
            var sh = sky.shader;

            // 1) Same-name shader from the game build.
            Shader replacement;
            if (GameShaders != null && GameShaders.TryGetValue(sh.name, out replacement))
            {
                sky.shader = replacement;
                Log.LogInfo($"[ShaderRemap] skybox remapped to game '{replacement.name}'");
                return;
            }

            // 2) Another skybox shader the game has loaded; copy matching properties.
            string[] candidates = { "Skybox/6 Sided", "Skybox/Cubemap", "Skybox/Panoramic", "Skybox/Procedural" };
            foreach (var candName in candidates)
            {
                if (GameShaders == null || !GameShaders.TryGetValue(candName, out replacement)) continue;
                var newMat = new Material(replacement);
                CopySharedProperties(sky, newMat);
                RenderSettings.skybox = newMat;
                Log.LogInfo($"[ShaderRemap] skybox '{sh.name}' not in game build; rebuilt on game '{candName}'");
                return;
            }

            // 3) Any working game skybox material currently loaded, or one cached
            // from an earlier scene this session. A wrong-but-real sky beats pink.
            Material donor = null;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null) continue;
                if (!m.shader.name.StartsWith("Skybox/")) continue;
                if (GameShaderIds == null || !GameShaderIds.Contains(m.shader.GetInstanceID())) continue;
                if (!m.shader.isSupported) continue;
                donor = m;
                break;
            }
            if (donor == null) donor = CachedGameSkybox;
            if (donor != null)
            {
                var newMat = new Material(donor); // copy — never mutate the game's own material
                CopySharedProperties(sky, newMat);
                RenderSettings.skybox = newMat;
                Log.LogInfo($"[ShaderRemap] skybox '{sh.name}' unfixable by name; borrowed game skybox material '{donor.name}' ({donor.shader.name})");
                return;
            }

            // 4) Nothing skybox-shaped exists in memory (cold boot straight into the
            // custom map). Hide the broken skybox behind a solid-color camera clear
            // — a plain sky beats a pink one. RenderSettings.skybox is deliberately
            // left alone: the scene's baked ambient probe is unaffected by it at
            // runtime, and clearing it could black out ambient lighting.
            Color skyColor = sky.HasProperty("_Tint") ? sky.GetColor("_Tint") : new Color(0.53f, 0.70f, 0.86f);
            Log.LogInfo($"[ShaderRemap] skybox '{sh.name}' unfixable (no game skybox available); falling back to solid sky color {skyColor}");
            if (Instance != null) Instance.StartCoroutine(SolidColorSky(skyColor));
        }

        // Applies a solid-color clear to whatever the main camera currently is, and
        // keeps re-applying it while this custom map stays loaded — the game
        // re-instantiates 'Main Camera(Clone)' on run start/restart, and each new
        // camera comes back with skybox clear flags.
        private static System.Collections.IEnumerator SolidColorSky(Color c)
        {
            bool announced = false;
            var waitHalfSecond = new WaitForSecondsRealtime(0.5f);
            // Stop when the player leaves this scene — LoadedAssetBundle alone is
            // not enough (returning to the menu doesn't clear it), and the menu
            // camera must never be recolored.
            string sceneName = SceneManager.GetActiveScene().name;
            while (!string.IsNullOrEmpty(LoadedAssetBundle)
                && SceneManager.GetActiveScene().name == sceneName)
            {
                var cam = Camera.main;
                if (cam != null && (cam.clearFlags != CameraClearFlags.SolidColor || cam.backgroundColor != c))
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = c;
                    if (!announced)
                    {
                        Log.LogInfo($"[ShaderRemap] solid sky applied to camera '{cam.name}'");
                        announced = true;
                    }
                }
                yield return waitHalfSecond;
            }
        }

        // Copy texture (plus tiling/offset) and a few common scalar/color properties
        // from the bundle's material onto the replacement, where names match.
        private static void CopySharedProperties(Material from, Material to)
        {
            foreach (var prop in from.GetTexturePropertyNames())
            {
                if (!to.HasProperty(prop)) continue;
                to.SetTexture(prop, from.GetTexture(prop));
                to.SetTextureScale(prop, from.GetTextureScale(prop));
                to.SetTextureOffset(prop, from.GetTextureOffset(prop));
            }
            string[] colorProps = { "_Tint", "_Color", "_SkyTint", "_GroundColor" };
            foreach (var prop in colorProps)
                if (from.HasProperty(prop) && to.HasProperty(prop)) to.SetColor(prop, from.GetColor(prop));
            string[] floatProps = { "_Exposure", "_Rotation" };
            foreach (var prop in floatProps)
                if (from.HasProperty(prop) && to.HasProperty(prop)) to.SetFloat(prop, from.GetFloat(prop));
        }

        // Called from the LoadScene prefix immediately BEFORE the bundle is loaded,
        // so everything captured here is guaranteed game-origin.
        public static void SnapshotGameShaders()
        {
            GameShaders = new Dictionary<string, Shader>();
            GameShaderIds = new HashSet<int>();
            foreach (var sh in Resources.FindObjectsOfTypeAll<Shader>())
            {
                if (sh == null) continue;
                GameShaderIds.Add(sh.GetInstanceID());
                if (!GameShaders.ContainsKey(sh.name)) GameShaders[sh.name] = sh;
            }
            GameShaderKeywords = new Dictionary<int, HashSet<string>>();
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null) continue;
                int id = m.shader.GetInstanceID();
                if (!GameShaderIds.Contains(id)) continue;
                HashSet<string> set;
                if (!GameShaderKeywords.TryGetValue(id, out set))
                {
                    set = new HashSet<string>();
                    GameShaderKeywords[id] = set;
                }
                foreach (var k in m.shaderKeywords) set.Add(k);
            }
            Log.LogInfo($"[ShaderRemap] snapshotted {GameShaderIds.Count} game shader(s), keyword unions for {GameShaderKeywords.Count}");
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

        public void AddTracks()
        {
            var List = Directory.GetFiles(MapsDir, "*.asset");
            for (int i = 0; i < List.Length; i++)
            {
                Paths.Add(List[i]);
                TrackNames.Add(Path.GetFileNameWithoutExtension(List[i]));
            }
            Logger.LogInfo($"AddTracks: {Paths.Count} track(s) registered from {MapsDir}");
        }

        public static void DoPatching()
        {
            harmony = new Harmony("com.glitcherog.patch");
            //Level appears
            var mOriginal = AccessTools.Method(typeof(MenuManager), "RegisterLevels"); // if possible use nameof() here
            var mPrefix = SymbolExtensions.GetMethodInfo(() => RegisterLevels());

            harmony.Patch(mOriginal, null, new HarmonyMethod(mPrefix));

            mOriginal = AccessTools.Method(typeof(MakerLevelData), "Awake"); // if possible use nameof() here
            mPrefix = SymbolExtensions.GetMethodInfo(() => LoadLevelManager());
            harmony.Patch(mOriginal, null, new HarmonyMethod(mPrefix));

            harmony.PatchAll();
        }

        public static void RegisterLevels()
        {
            menuManager = MenuManager.Instance;

            for (int i = 0; i < Paths.Count; i++)
            {
                LevelEntries.RegisterLevel(new LevelEntry
                {
                    name = TrackNames[i],
                    sceneName = "$" + Paths[i],
                    scoreThresholds = new float[]
                    {
                    100000f,
                    150000f,
                    200000f
                    },
                    timeThresholds = new float[]
                    {
                    110f,
                    100f,
                    95f
                    },
                    freestyleTimeLimit = 150f
                });
            }
        }

        public static void LoadLevelManager()
        {
            //Find Main Starting Objects
            var levelManagerMaker = FindAnyObjectByType<MakerLevelData>();

            var MakerSpawnPoint = FindAnyObjectByType<MakerSpawnPoint>();
            var MakerRespawnPoint = FindAnyObjectByType<MakerRespawnPoints>();

            // --- Spawn ground-snap ------------------------------------------------
            // The game seats the rider's CharacterController CENTER on the
            // startingGate transform with NO ground-check for the START (respawns
            // get a 0.5 m down-probe; the start does not). SSX3 peak runs (B/D) are
            // stitched from peak-cap + race-body + connector regions, so the authored
            // start Y can land at/below the merged terrain surface -> the capsule
            // straddles the floor, PhysX depenetrates it DOWNWARD, and the rider drops
            // straight through. Cast onto the terrain (layer 0 ground, layer 6 ice)
            // and RAISE the start so the capsule clears the surface. max() only ever
            // raises, so a course that already spawns clean (gari, SSX3 E) is a no-op;
            // gari terrain sits on its own mapped layers, so the mask usually misses
            // it entirely and leaves the sensitive gari start exactly as authored.
            if (MakerSpawnPoint != null)
            {
                const int TerrainMask = (1 << 0) | (1 << 6);
                const float Clearance = 2f; // > half a rider capsule; a small drop is harmless, an embed is fatal
                Physics.SyncTransforms();
                Vector3 sp = MakerSpawnPoint.transform.position;
                if (Physics.Raycast(sp + Vector3.up * 20f, Vector3.down, out var groundHit, 120f, TerrainMask, QueryTriggerInteraction.Ignore))
                {
                    float lifted = Mathf.Max(sp.y, groundHit.point.y + Clearance);
                    if (lifted - sp.y > 0.01f)
                    {
                        MakerSpawnPoint.transform.position = new Vector3(sp.x, lifted, sp.z);
                        Log.LogInfo($"[Spawn] ground-snapped start {sp.y:F2} -> {lifted:F2} (terrain y={groundHit.point.y:F2}, +{lifted - sp.y:F2})");
                    }
                    else Log.LogInfo($"[Spawn] start already clears terrain (start y={sp.y:F2}, terrain y={groundHit.point.y:F2})");
                }
                else Log.LogWarning($"[Spawn] no terrain under start {sp} on layers 0|6 — left as authored");
            }
            //Find AI Paths Object

            //Update Level Entry
            var LevelEntry = menuManager.lastLevelEntry;

            LevelEntry.timeThresholds[2] = levelManagerMaker.GoldTime;
            LevelEntry.timeThresholds[1] = levelManagerMaker.SilverTime;
            LevelEntry.timeThresholds[0] = levelManagerMaker.BronzeTime;

            LevelEntry.scoreThresholds[2] = levelManagerMaker.GoldPoints;
            LevelEntry.scoreThresholds[1] = levelManagerMaker.SilverPoints;
            LevelEntry.scoreThresholds[0] = levelManagerMaker.BronzePoints;

            LevelEntry.freestyleTimeLimit = levelManagerMaker.TimeLeft;

            Traverse.Create(menuManager).Field("lastLevelEntry").SetValue(LevelEntry);

            //Insert Starting Gate
            var StartingGate = Resources.Load<GameObject>("game/startinggate");
            GameObject StartingGateObject = Instantiate(StartingGate);
            StartingGateObject.transform.position = MakerSpawnPoint.transform.TransformPoint(new Vector3(-0.5f,0,0.5f));
            StartingGateObject.transform.rotation = MakerSpawnPoint.transform.rotation;

            //Insert Level Manager
            var LevelManagerObject = Resources.Load<GameObject>("game/levelmanager");
            var gameObject = Instantiate(LevelManagerObject, levelManagerMaker.transform);

            var levelManager = gameObject.GetComponent<LevelManager>();

            //Set Level Manager Data Points
            Traverse.Create(levelManager).Field("startingGate").SetValue(MakerSpawnPoint.transform);
            Traverse.Create(levelManager).Field("respawnPointsParent").SetValue(MakerRespawnPoint.transform);
            Traverse.Create(levelManager).Field("snowflakes").SetValue(new GameObject("Temp")); //FIX to get all snowflakes and apply

            //Create UI Hooks
            var TempObject = Traverse.Create(levelManager).Field("speedLabel").GetValue() as TextMeshProUGUI;
            var CanvasObjects = FindObjectsByType(typeof(MakerCanvas), FindObjectsSortMode.None) as MakerCanvas[];
            for (int i = 0; i < CanvasObjects.Length; i++)
            {
                GameObject gameObject1 = Instantiate(TempObject.transform.gameObject, levelManager.transform.GetChild(0));
                gameObject1.SetActive(true);
                gameObject1.GetComponent<TextMeshProUGUI>().text = "NewText";
                CanvasObjects[i].Generate(gameObject1.GetComponent<TextMeshProUGUI>());
            }

            //Convert From Make Rails to Game Rails
            var RailObjects = FindObjectsByType(typeof(MakerRails), FindObjectsSortMode.None) as MakerRails[];
            int railColliders = 0, railSegments = 0;
            GameObject probeRailGO = null;
            List<Rail.Segment> probeSegments = null;
            for (int i = 0; i < RailObjects.Length; i++)
            {
                var NewRail = RailObjects[i].gameObject.AddComponent<RailInvis>();

                var Segments = RailObjects[i].GetSegments();
                List<Rail.Segment> NewSegments = new List<Rail.Segment>();

                for (int j = 0; j < Segments.Count; j++)
                {
                    Rail.Segment NewSegment = new Rail.Segment(Vector3.zero, Vector3.up);

                    NewSegment.position = Segments[j].Point;
                    NewSegment.rotation = Segments[j].Rotation;
                    NewSegment.distance = Segments[j].Distance;

                    NewSegments.Add(NewSegment);
                }

                Enum.TryParse(RailObjects[i].Materials.ToString(), out Rail.RailMaterial material1);

                NewRail.material = material1;

                Traverse.Create(NewRail).Field("segments").SetValue(NewSegments);

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
                    if (probeRailGO == null)
                    {
                        probeRailGO = RailObjects[i].gameObject;
                        probeSegments = NewSegments;
                    }
                }
            }
            if (RailObjects.Length > 0)
                Log.LogInfo($"[Rails] converted {RailObjects.Length} rail(s), built {railColliders} collider(s), {railSegments} segment(s) total");
            if (railColliderProbe.Value && probeRailGO != null && Instance != null)
                Instance.StartCoroutine(RailProbe(probeRailGO, probeSegments));

            GameObject AIParent = new GameObject("AI Paths Parent");
            AIParent.transform.parent = levelManager.transform;

            // MakerAIPath.m_points is a List of a user-defined [Serializable]
            // nested class, and Unity fails to deserialize those for
            // runtime-loaded (BepInEx) assemblies — on custom maps every list
            // comes back empty (engine limitation; lists of engine value types
            // like the race line's List<Vector3> are fine). The maker copy below
            // still wins whenever data DID arrive (future fixed bundles);
            // otherwise the paths are rebuilt from a plain-text sidecar next to
            // the .asset, or, failing that, from the race line. AI enumerates
            // DIRECT children of pathsParent, so every AIPath must sit
            // immediately under AIParent.
            var MakerAIPaths = FindObjectsByType(typeof(MakerAIPath), FindObjectsSortMode.None) as MakerAIPath[];
            int copied = 0;
            for (int i = 0; i < MakerAIPaths.Length; i++)
            {
                // An empty maker path would become an empty AIPath, which is what
                // sends AI into the 1.5s respawn loop — skip it entirely.
                if (MakerAIPaths[i].m_points == null || MakerAIPaths[i].m_points.Count == 0)
                    continue;

                var NewPath = MakerAIPaths[i].gameObject.AddComponent<AIPath>();

                MakerAIPaths[i].transform.parent = AIParent.transform;

                NewPath.m_points = new List<AIPath.Point>();

                for (int j = 0; j < MakerAIPaths[i].m_points.Count; j++)
                {
                    var TempPoint = new AIPath.Point();
                    TempPoint.position = MakerAIPaths[i].m_points[j].position;
                    TempPoint.windup = MakerAIPaths[i].m_points[j].windup;
                    NewPath.m_points.Add(TempPoint);
                }

                NewPath.CalculateSegments();
                copied++;
            }

            if (copied > 0)
            {
                Log.LogInfo($"[AIPaths] copied {copied} maker path(s) from the bundle (sidecar ignored)");
            }
            else
            {
                // AI needs ONE complete start->finish route. The sidecar holds SSX
                // branch FRAGMENTS — no single fragment spans the course, so the AI
                // runs off a fragment end and (especially on a scaled map, where the
                // gaps exceed FindNewPath's absolute 50-unit reacquire threshold)
                // loses its path, hits the 1.5s no-path timeout, and respawn-loops
                // without ever finishing (MECHANICS_DIAGNOSIS Bug 3). The race line
                // IS a complete ordered route, so prefer it as the AI path; fall
                // back to the sidecar fragments only if there's no usable race line.
                int built = BuildAIPathFromRaceLine(AIParent.transform);
                if (built == 0) built = BuildAIPathsFromSidecar(AIParent.transform);
                if (built == 0)
                    Log.LogWarning("[AIPaths] no AI path data (maker points empty, no usable race line, no sidecar); AI will hold the start gate but cannot race");
            }
            Traverse.Create(levelManager).Field("pathsParent").SetValue(AIParent.transform);


            GameObject AIPDividerParent = new GameObject("AI Paths Divider Parent");
            AIPDividerParent.transform.parent = levelManager.transform;
            var MakerAIPathsDivider = FindObjectsByType(typeof(MakerAIPathDivider), FindObjectsSortMode.None) as MakerAIPathDivider[];
            for (int i = 0; i < MakerAIPathsDivider.Length; i++)
            {
                var NewPath = MakerAIPathsDivider[i].gameObject.AddComponent<AIPathDivider>();

                // The game enumerates DIRECT children of dividersParent — parent
                // dividers under the transform actually registered as
                // dividersParent (they used to go under AIParent and were never
                // found).
                MakerAIPathsDivider[i].transform.parent = AIPDividerParent.transform;

                NewPath.m_points = MakerAIPathsDivider[i].m_points;
            }
            Traverse.Create(levelManager).Field("dividersParent").SetValue(AIPDividerParent.transform);

            var TempMakerRaceLine = FindAnyObjectByType<MakerRaceLine>();
            var TempRaceLine = TempMakerRaceLine.gameObject.AddComponent<RaceLine>();

            TempRaceLine.m_points = new List<RaceLine.Point>();

            for (int i = 0;i < TempMakerRaceLine.m_points.Count;i++)
            {
                var RaceLinePoint = new RaceLine.Point();

                RaceLinePoint.position = TempMakerRaceLine.m_points[i];

                TempRaceLine.m_points.Add(RaceLinePoint);
            }

            TempRaceLine.CalculateSegments();

            Traverse.Create(levelManager).Field("raceLine").SetValue(TempRaceLine);

            // Respawn markers ALONG the race line (MECHANICS_DIAGNOSIS Bug 2).
            // The maker respawn points are SSX per-racer branch-line HEADS,
            // scattered off the ridden route; the game's Respawn picks the nearest
            // marker, so a fallen rider gets teleported to an off-route pocket they
            // "have never seen" — worse at 2.5x marker spacing. Replace the parent
            // with markers sampled on the reconstructed race line, each facing
            // down-line (Respawn's down-course 2x penalty reads child.forward), so
            // every respawn drops the rider back ONTO the route facing downhill.
            // The game re-enumerates children live per respawn, so runtime-built
            // markers just work. Falls back to the maker parent if the race line is
            // too short to sample.
            if (TempRaceLine.m_points != null && TempRaceLine.m_points.Count >= 2)
            {
                var respawnParent = new GameObject("Respawn Points (race line)");
                respawnParent.transform.parent = levelManager.transform;
                var rpts = TempRaceLine.m_points;
                for (int i = 0; i < rpts.Count; i++)
                {
                    var go = new GameObject("Respawn " + i.ToString(CultureInfo.InvariantCulture));
                    go.transform.parent = respawnParent.transform;
                    go.transform.position = rpts[i].position;
                    Vector3 fwd = (i + 1 < rpts.Count)
                        ? rpts[i + 1].position - rpts[i].position
                        : rpts[i].position - rpts[i - 1].position;
                    if (fwd.sqrMagnitude > 1e-6f)
                        go.transform.rotation = Quaternion.LookRotation(fwd.normalized, Vector3.up);
                }
                Traverse.Create(levelManager).Field("respawnPointsParent").SetValue(respawnParent.transform);
                Log.LogInfo($"[Respawn] built {rpts.Count} race-line respawn marker(s), replacing maker branch-head markers");
            }

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
            int glassStripped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var mc in sceneRoot.GetComponentsInChildren<MeshCollider>(true))
                {
                    var go = mc.gameObject;
                    if (go.layer != 8 || mc.convex || mc.sharedMesh == null) continue;
                    Vector3 ext = mc.sharedMesh.bounds.size;
                    int thin = 0;
                    if (ext.y < ext[thin]) thin = 1;
                    if (ext.z < ext[thin]) thin = 2;
                    float med = Mathf.Max(ext[(thin + 1) % 3], ext[(thin + 2) % 3]);
                    if (med < 1e-3f || ext[thin] > 0.3f * med) continue; // not a thin sheet
                    Vector3 thinLocal = thin == 0 ? Vector3.right : thin == 1 ? Vector3.up : Vector3.forward;
                    Vector3 thinWorld = go.transform.TransformDirection(thinLocal).normalized;
                    if (Mathf.Abs(thinWorld.y) > 0.7f) continue; // thin axis vertical -> floor/ramp, keep solid
                    UnityEngine.Object.Destroy(mc);
                    glassStripped++;
                }
            }
            if (glassStripped > 0)
                Log.LogInfo($"[Glass] stripped {glassStripped} thin vertical layer-8 collider(s) (bounce fix)");

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
                    Traverse.Create(prop).Field("soundName").SetValue("");
                    propsConverted++;
                }
            }
            if (propsConverted > 0 || propsSkipped > 0)
                Log.LogInfo($"[PhysProps] converted {propsConverted} crashable prop(s), skipped {propsSkipped}");
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
            float HalfWidth = 0.25f * Plugin.WorldScale;
            float Depth = 0.2f * Plugin.WorldScale;

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
            var segs = rail == null ? null
                : Traverse.Create(rail).Field("segments").GetValue() as List<Rail.Segment>;
            Log.LogInfo($"[Rails] probe hit '{hit.collider.gameObject.name}' (layer {hit.collider.gameObject.layer}): Rail={(rail != null)}, segments={(segs == null ? 0 : segs.Count)}");
        }

        // Rebuilds AI paths from "<map>.aipaths.txt" next to the .asset
        // (generated by gen_aipaths.py from the bundle's own data). Plain text,
        // NOT JsonUtility — that is the same serializer that loses the nested
        // class in the first place. Format: '#' comments; a line "path" starts a
        // new path; point lines are "x z windup" (two invariant-culture floats,
        // world XZ, then 0/1). Returns the number of paths built.
        private static int BuildAIPathsFromSidecar(Transform parent)
        {
            if (string.IsNullOrEmpty(LoadedAssetBundle)) return 0;
            string sidecarPath = Path.ChangeExtension(LoadedAssetBundle, ".aipaths.txt");
            if (!File.Exists(sidecarPath)) return 0;
            try
            {
                var paths = new List<List<AIPath.Point>>();
                List<AIPath.Point> cur = null;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.StartsWith("scale")) continue; // world-scale header, read at load time (ReadWorldScale)
                    if (line == "path")
                    {
                        cur = new List<AIPath.Point>();
                        paths.Add(cur);
                        continue;
                    }
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length < 3) continue;
                    if (cur == null)
                    {
                        cur = new List<AIPath.Point>();
                        paths.Add(cur);
                    }
                    var p = new AIPath.Point();
                    p.position = new Vector2(
                        float.Parse(t[0], CultureInfo.InvariantCulture),
                        float.Parse(t[1], CultureInfo.InvariantCulture));
                    p.windup = t[2] == "1";
                    cur.Add(p);
                }

                int built = 0, points = 0;
                for (int i = 0; i < paths.Count; i++)
                {
                    if (paths[i].Count < 2) continue; // a path needs a direction
                    var go = new GameObject("AIPath (sidecar " + built.ToString(CultureInfo.InvariantCulture) + ")");
                    go.transform.parent = parent;
                    var path = go.AddComponent<AIPath>();
                    path.m_points = paths[i];
                    path.CalculateSegments();
                    built++;
                    points += paths[i].Count;
                }
                Log.LogInfo($"[AIPaths] built {built} path(s) ({points} points) from sidecar {Path.GetFileName(sidecarPath)}");
                return built;
            }
            catch (Exception e)
            {
                Log.LogError($"[AIPaths] sidecar {sidecarPath} failed to load: {e}");
                return 0;
            }
        }

        // Last-ditch path source: the race line's List<Vector3> DOES deserialize,
        // so one AIPath along it (world XZ, no windups) keeps AI racing instead of
        // respawn-looping.
        private static int BuildAIPathFromRaceLine(Transform parent)
        {
            var raceLine = FindAnyObjectByType<MakerRaceLine>();
            if (raceLine == null || raceLine.m_points == null || raceLine.m_points.Count < 2)
                return 0;
            var go = new GameObject("AIPath (race-line fallback)");
            go.transform.parent = parent;
            var path = go.AddComponent<AIPath>();
            path.m_points = new List<AIPath.Point>();
            for (int i = 0; i < raceLine.m_points.Count; i++)
            {
                var p = new AIPath.Point();
                Vector3 v = raceLine.m_points[i];
                p.position = new Vector2(v.x, v.z);
                p.windup = false;
                path.m_points.Add(p);
            }
            path.CalculateSegments();
            Log.LogInfo($"[AIPaths] built 1 complete path from the race line ({path.m_points.Count} points)");
            return 1;
        }

        // Reads the optional "scale <f>" header line from a map's .aipaths.txt
        // sidecar (written by gen_aipaths.py --scale). Returns 1.0 for a missing
        // sidecar, no scale line, or a non-positive value — every path through
        // here that isn't an explicitly scaled map yields exactly 1.0.
        internal static float ReadWorldScale(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return 1f;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length != 2 || t[0] != "scale") continue;
                    float s = float.Parse(t[1], CultureInfo.InvariantCulture);
                    if (s > 0f) return s;
                    Log.LogWarning($"[Gravity] sidecar {Path.GetFileName(sidecarPath)} declares scale {t[1]} (must be > 0); ignoring");
                    return 1f;
                }
                return 1f;
            }
            catch (Exception e)
            {
                Log.LogError($"[Gravity] failed reading world scale sidecar for {bundlePath}: {e}");
                return 1f;
            }
        }

        // Gravity for a world WorldScale times bigger. Called (via the transpilers
        // below) everywhere Snowboarder/Caster.Cast used their inlined 20f/17f
        // gravity constants. Returns the input untouched when the map is unscaled
        // or the feature is off — with WorldScale hard-reset to 1 on every
        // built-in selection and menu return, built-in gravity is provably stock.
        public static float ScaledGravity(float baseGravity)
        {
            if (WorldScale == 1f || scaleGravityWithMap == null || !scaleGravityWithMap.Value)
                return baseGravity;
            return baseGravity * WorldScale;
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
                return true;
            }

            try
            {
                string path = levelEntry.sceneName.TrimStart('$');
                Plugin.Log.LogInfo($"LoadScene prefix: loading custom map bundle '{levelEntry.name}' from {path}");

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

    // On custom maps AISnowboarder.GetSteeringInput calls Snowboarder.Respawn()
    // directly after 1.5s of pathlessness, bypassing CanRespawn() — during the
    // countdown that launches AI off the start line. Block Respawn while the
    // custom scene is in its Starting state; built-in levels
    // (LoadedAssetBundle == "") are untouched, and once the countdown releases
    // (state != Starting) everything behaves as stock. Deliberately mode-blind.
    [HarmonyPatch(typeof(Snowboarder), "Respawn")]
    public class Snowboarder_Respawn
    {
        private static string loggedScene;

        [HarmonyPrefix]
        public static bool Prefix(Snowboarder __instance)
        {
            if (string.IsNullOrEmpty(Plugin.LoadedAssetBundle)) return true;
            string custom = Plugin.CustomSceneName();
            if (custom == null || SceneManager.GetActiveScene().name != custom) return true;
            object state = Traverse.Create(__instance).Field("state").GetValue();
            if (state == null || Convert.ToInt32(state) != 0) return true; // 0 = State.Starting
            if (loggedScene != custom)
            {
                loggedScene = custom;
                Plugin.Log.LogDebug($"[AIPaths] blocked pre-start Respawn on custom scene '{custom}' (state=Starting)");
            }
            return false;
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
            return Plugin.ScaleGravityLiterals(instructions, "Snowboarder.Integrate", new[] { 20f, 17f });
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

    // Custom thumbnails. The game sets each level's menu thumbnail via
    // Resources.Load<Sprite>(thumbnailPath), which can't read a PNG off disk and
    // runs before the map bundle is loaded. So instead, after the menu is built,
    // we override the thumbnail Image for any custom ("$"-marked) level with a PNG
    // dropped next to its .asset (e.g. Maps/Garibaldi Beta V2.png).
    [HarmonyPatch(typeof(LevelSelectMenu), "Populate")]
    public class LevelSelectMenu_Populate
    {
        // Cache so we don't re-read the file / leak textures every time the menu opens.
        private static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();

        [HarmonyPostfix]
        public static void Postfix(LevelSelectMenu __instance)
        {
            var entries = Traverse.Create(__instance).Field("levelSelectEntries")
                .GetValue() as List<LevelSelectEntry>;
            if (entries == null) return;

            int count = LevelEntries.GetLevelCount();
            for (int i = 0; i < entries.Count && i < count; i++)
            {
                var entry = LevelEntries.GetEntry(i);
                if (string.IsNullOrEmpty(entry.sceneName) || !entry.sceneName.StartsWith("$"))
                    continue; // built-in level, leave its Resources thumbnail alone

                string assetPath = entry.sceneName.TrimStart('$');
                string pngPath = Path.ChangeExtension(assetPath, ".png");

                var sprite = LoadSprite(pngPath);
                if (sprite == null) continue;

                var img = entries[i].thumbnail;
                if (img == null) continue;
                img.sprite = sprite;
                img.preserveAspect = true; // never distort whatever aspect the user provides
            }

            // Attach the scroller once (it no-ops itself when all cards fit). Put it
            // on the Scroll View GameObject — never on Content (whose children the
            // menu indexes by position) or the Scroll View's RectTransform (it has
            // an Animation component we must not disturb).
            if (Plugin.levelSelectScroll != null && !Plugin.levelSelectScroll.Value) return;
            var scrollRect = __instance.GetComponentInChildren<ScrollRect>(true);
            if (scrollRect == null)
            {
                Plugin.Log.LogWarning("[LevelSelect] no ScrollRect found under LevelSelectMenu; scrolling disabled");
                return;
            }
            if (scrollRect.gameObject.GetComponent<LevelSelectScroller>() == null)
            {
                var scroller = scrollRect.gameObject.AddComponent<LevelSelectScroller>();
                scroller.Init(scrollRect);
                Plugin.Log.LogInfo("[LevelSelect] attached card-strip scroller");
            }
        }

        private static Sprite LoadSprite(string pngPath)
        {
            if (cache.TryGetValue(pngPath, out var cached)) return cached;
            cache[pngPath] = null; // remember misses too, so we don't retry disk every menu open
            if (!File.Exists(pngPath)) return null;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(File.ReadAllBytes(pngPath))) return null;
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                cache[pngPath] = sprite;
                return sprite;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LevelHook] thumbnail load failed for " + pngPath + ": " + e.Message);
                return null;
            }
        }
    }

    // Makes the level-select card strip scroll when more cards exist than fit,
    // auto-scrolling to the selected card. Lives on the "Scroll View" GameObject.
    //
    // Vanilla layout keeps Content stretch-filled to the viewport with the
    // ContentSizeFitter horizontally Unconstrained, so Content never overflows and
    // the ScrollRect has nothing to scroll — the cards are simply centred and any
    // beyond the edges are clipped. When the cards don't fit we switch Content to a
    // preferred-width, left-anchored layout so it overflows and the ScrollRect can
    // move it; when they DO fit we touch nothing, leaving the menu pixel-identical
    // to vanilla.
    public class LevelSelectScroller : MonoBehaviour
    {
        private ScrollRect scrollRect;
        private RectTransform content;
        private RectTransform viewport;
        // Set in OnEnable when all cards fit: LateUpdate then does nothing and no
        // layout is changed, so the vanilla look is preserved exactly.
        private bool disabled;

        // Card sizeDelta springs between these (LevelSelectEntry.FixedUpdate).
        private const float IdleCardWidth = 640f;
        private const float SelectedCardWidth = 768f;
        // Keep-in-view inset from each viewport edge (px).
        private const float EdgeMargin = 32f;

        // Vanilla Content layout, captured the first time overflow layout is
        // applied so a later all-fits OnEnable can restore it instead of leaving
        // the mutated layout stuck under a disabled scroller.
        private bool overflowApplied;
        private ContentSizeFitter.FitMode origFit;
        private bool origHadFitter;
        private Vector2 origAnchorMin;
        private Vector2 origAnchorMax;
        private float origPivotX;
        private float origAnchoredX;

        public void Init(ScrollRect sr)
        {
            scrollRect = sr;
        }

        private void OnEnable()
        {
            // AddComponent fires OnEnable before Init runs, but we're on the
            // ScrollRect's own GameObject, so GetComponent always resolves it.
            if (scrollRect == null) scrollRect = GetComponent<ScrollRect>();
            if (scrollRect == null) { disabled = true; return; }

            content = scrollRect.content;
            viewport = scrollRect.viewport != null
                ? scrollRect.viewport
                : scrollRect.transform.GetChild(0) as RectTransform;
            if (content == null || viewport == null) { disabled = true; return; }

            // An unbuilt/zero rect would make the fit test meaningless (everything
            // "overflows" a 0-wide viewport) — fail safe into vanilla for this
            // enable; the next menu open re-evaluates against a real rect.
            if (viewport.rect.width < 1f) { disabled = true; return; }

            // Child 0 is the inactive card template; count only active cards.
            int n = 0;
            for (int i = 0; i < content.childCount; i++)
                if (content.GetChild(i).gameObject.activeSelf) n++;

            // Use the MAX (selected) width for the one card so the fit decision
            // never flickers as widths spring frame to frame.
            float needed = (n - 1) * IdleCardWidth + SelectedCardWidth;
            if (needed <= viewport.rect.width)
            {
                // Everything fits — vanilla layout. If a previous enable applied
                // overflow layout, put the original back rather than leaving the
                // mutated anchors/fitter stuck under a disabled scroller.
                if (overflowApplied) RestoreVanillaLayout();
                disabled = true;
                return;
            }
            disabled = false;

            // Overflow layout: preferred-width content, left-anchored, pinned to the
            // viewport's left edge. Idempotent — safe to re-apply every menu open.
            var fitter = content.GetComponent<ContentSizeFitter>();
            if (!overflowApplied)
            {
                origHadFitter = fitter != null;
                origFit = origHadFitter ? fitter.horizontalFit : ContentSizeFitter.FitMode.Unconstrained;
                origAnchorMin = content.anchorMin;
                origAnchorMax = content.anchorMax;
                origPivotX = content.pivot.x;
                origAnchoredX = content.anchoredPosition.x;
                overflowApplied = true;
            }
            if (fitter != null)
                fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            content.anchorMin = new Vector2(0f, 0f);
            content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, content.pivot.y);
            content.anchoredPosition = new Vector2(0f, content.anchoredPosition.y);
        }

        private void RestoreVanillaLayout()
        {
            var fitter = content.GetComponent<ContentSizeFitter>();
            if (origHadFitter && fitter != null) fitter.horizontalFit = origFit;
            content.anchorMin = origAnchorMin;
            content.anchorMax = origAnchorMax;
            content.pivot = new Vector2(origPivotX, content.pivot.y);
            content.anchoredPosition = new Vector2(origAnchoredX, content.anchoredPosition.y);
            overflowApplied = false;
        }

        private void LateUpdate()
        {
            if (disabled || content == null || viewport == null) return;

            var es = EventSystem.current;
            GameObject sel = es == null ? null : es.currentSelectedGameObject;
            if (sel == null || !sel.transform.IsChildOf(content)) return;

            // Walk up to the card root (the child whose parent is Content).
            Transform card = sel.transform;
            while (card != null && card.parent != content) card = card.parent;
            var cardRt = card as RectTransform;
            if (cardRt == null) return;

            // Card horizontal extent in viewport-local space. Content is a child of
            // the viewport, so shifting content.anchoredPosition.x by d shifts these
            // by d too — the delta to bring an edge in-bounds is a direct target.
            Vector3[] corners = new Vector3[4];
            cardRt.GetWorldCorners(corners);
            float left = float.MaxValue, right = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 lp = viewport.InverseTransformPoint(corners[i]);
                if (lp.x < left) left = lp.x;
                if (lp.x > right) right = lp.x;
            }

            Rect vr = viewport.rect;
            float vLeft = vr.xMin + EdgeMargin;
            float vRight = vr.xMax - EdgeMargin;

            float targetX = content.anchoredPosition.x;
            bool needMove = false;
            if (left < vLeft) { targetX += vLeft - left; needMove = true; }       // card past left edge → move right
            else if (right > vRight) { targetX += vRight - right; needMove = true; } // past right edge → move left
            if (!needMove) return; // card fully visible — don't fight a mouse drag

            // Never scroll past the ends. content.rect.width tracks the springing
            // card widths, so recompute every frame.
            float minX = Mathf.Min(0f, viewport.rect.width - content.rect.width);
            targetX = Mathf.Clamp(targetX, minX, 0f);

            float cur = content.anchoredPosition.x;
            float nx = Mathf.Lerp(cur, targetX, 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime));
            content.anchoredPosition = new Vector2(nx, content.anchoredPosition.y);
        }
    }
}
