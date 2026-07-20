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
        // Fine-tune multiplier on the skydome's auto-fit (feature 13). SkydomeFollow
        // sizes the dome to ~95% of the camera far-clip plane each frame so it envelops
        // the whole visible course; this scales that target (1 = the fit). Tunable, no
        // rebuild.
        internal static ConfigEntry<float> skydomeScale;
        // SSX domes only reach ~+20 deg elevation — an open-topped bowl. Above the rim
        // the camera shows its clear color, which is black by default (feature-5's blue
        // clear only fires for skybox-remapped maps, and a domed map's RenderSettings
        // .skybox is null → that fallback stays dormant). So for a domed map SkydomeFollow
        // clears the camera to this sky color so looking up past the rim reads as sky, not
        // a black void. Hex; tunable without a rebuild.
        internal static ConfigEntry<string> skydomeFillColor;
        // Per-level sky-fill override, baked into the bundle by SSXImporter as an empty
        // marker GO named "SkyFill_<RRGGBB>" (from Overrides.json lighting.skyFillColor).
        // Parsed in LoadLevelManager before SkydomeFollow attaches; wins over the global
        // [Skydome] FillColor config wherever the Hook clears the camera. Null = no
        // override on the active map.
        internal static Color? SceneSkyFill;
        // Animated-texture playback speed (feature 14). The importer decodes SSX's
        // UVScrolling effect (SSFLogic MainType 0 / SubType 10) and bakes the two
        // scroll rates into the GO name ("UVScroll_<u>_<v>_<orig>"); those rates are
        // UV-units PER FRAME at the PS2's 60 Hz, so UVScrollBehaviour multiplies by
        // this to get UV-units/second. 60 = authentic speed. Tunable, no rebuild.
        internal static ConfigEntry<float> uvScrollFrameRate;
        // World scale of the active custom map, from the optional "scale <f>"
        // header line in its .aipaths.txt sidecar (gen_aipaths.py --scale).
        // 1.0 for built-in levels, sidecar-less maps, and maps with no scale line
        // — i.e. everything that exists today.
        internal static float WorldScale = 1f;
        // Per-map gravity multiplier from the optional "gravity <f>" header line in
        // the active custom map's .aipaths.txt sidecar. INDEPENDENT of WorldScale:
        // applies at 1x too, so a course can ask for heavier (>1) or lighter (<1)
        // gravity regardless of its size. 1.0 for built-in levels, sidecar-less
        // maps, and maps with no gravity line — i.e. everything that exists today.
        // Reset to 1 on every built-in selection and menu return (same lifetime
        // rule as WorldScale) so built-in gravity stays provably stock.
        internal static float MapGravityMultiplier = 1f;
        // Platform the active custom map's bundle was authored native to, from an
        // optional "platform <windows|mac>" line in its .aipaths.txt sidecar. null
        // (every existing map, no header) means "unknown — assume the bytecode is
        // foreign to whatever GPU we're on", the historical always-remap behavior.
        // Declaring it lets ShaderLooksBroken trust the bundle's own shaders when
        // it runs on that platform: the original Windows-built Garibaldi ships
        // "platform windows" so its native D3D shaders are left alone on a Windows
        // box (restoring the pre-D3D-distrust look) yet still remapped on Metal,
        // where a Windows bundle genuinely is foreign. Reset to null on non-custom loads.
        internal static string BundleNativePlatform;

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
            skydomeScale = Config.Bind("Skydome", "Scale", 1f,
                "Fine-tune multiplier on the skydome's automatic fit. The dome is " +
                "auto-sized each frame so its radius is ~90% of the camera's far-clip " +
                "plane — big enough to envelop the whole visible course so it reads as a " +
                "distant sky, not a nearby wall. 1 = the auto fit; raise slightly to push " +
                "it out further (values >1.1 risk the far plane clipping the dome), lower " +
                "to pull it in. No rebuild needed, just relaunch.");
            skydomeFillColor = Config.Bind("Skydome", "FillColor", "#3373C3",
                "Camera clear color (hex) for maps with a skydome. SSX domes are open " +
                "at the top (~+20 deg), so above the rim the camera would otherwise show " +
                "black; this fills it with sky. Default matches the dome's blue so the " +
                "rim seam blends. No rebuild needed, just relaunch.");
            uvScrollFrameRate = Config.Bind("Visual", "UVScrollFrameRate", 60f,
                "Playback speed for animated (UV-scrolling) textures on custom maps — " +
                "LCD scanlines, jumbotrons, boost-pad chevrons, river water. The " +
                "authored rates are per-frame at the PS2's 60 Hz, so 60 = authentic " +
                "speed, 0 = frozen. No rebuild needed, just relaunch.");

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
            if (!isCustomScene && pendingSceneName == null && (WorldScale != 1f || MapGravityMultiplier != 1f))
            {
                WorldScale = 1f;
                MapGravityMultiplier = 1f;
                Log.LogInfo("[Gravity] left custom map; world scale + gravity multiplier reset to 1");
            }

            // Per-level sky fill must never outlive its map (same lifetime rule as
            // WorldScale); LoadLevelManager re-reads it on the next custom load.
            if (!isCustomScene && pendingSceneName == null)
                SceneSkyFill = null;

            // The skybox needs watching on EVERY scene: on built-in levels/menus we
            // only cache a known-good game skybox material (never modify anything);
            // on the custom scene we wait for the bundle's skybox to be bound —
            // empirically RenderSettings.skybox is still null when sceneLoaded fires
            // and only appears during activation — and then fix it.
            if (Instance != null) Instance.StartCoroutine(SkyboxWatch(isCustomScene));

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
        // The one exception to the always-distrust default above: a bundle may
        // declare (via a "platform" header in its .aipaths.txt) the platform it
        // was authored native to. When it runs on THAT GPU family its own shaders
        // are the platform-correct bytecode — remapping them is not just needless
        // but LOSSY for hand-authored materials (the original Windows-built
        // Garibaldi's cutout trees flattened and its skybox vanished on a
        // Windows/D3D box the moment the D3D distrust was added, because the
        // keyword-prune dropped variants the game's own build never compiled).
        // Undeclared maps (every Mac-built bundle we author) keep the default.
        private static bool ShaderLooksBroken(Shader s, bool gameOrigin)
        {
            if (gameOrigin) return false;
            if (!s.isSupported) return true;
            var gpu = SystemInfo.graphicsDeviceType;
            if (BundleIsNativeToCurrentGpu()) return false;
            return gpu == UnityEngine.Rendering.GraphicsDeviceType.Metal
                || gpu == UnityEngine.Rendering.GraphicsDeviceType.Vulkan
                || gpu == UnityEngine.Rendering.GraphicsDeviceType.Direct3D11
                || gpu == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12;
        }

        // True when the active custom bundle declared (via its .aipaths.txt
        // "platform" header) that it was built native to the running GPU family:
        // "windows" → Direct3D11/12, "mac" → Metal. Undeclared (BundleNativePlatform
        // null — every existing map) returns false, so nothing is trusted by
        // default and the remap fires exactly as it did before this opt-out existed.
        private static bool BundleIsNativeToCurrentGpu()
        {
            var gpu = SystemInfo.graphicsDeviceType;
            if (BundleNativePlatform == "windows")
                return gpu == UnityEngine.Rendering.GraphicsDeviceType.Direct3D11
                    || gpu == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12;
            if (BundleNativePlatform == "mac")
                return gpu == UnityEngine.Rendering.GraphicsDeviceType.Metal;
            return false;
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

            // Assigning Material.shader RESETS renderQueue to the new shader's
            // default (documented Unity behavior). Our importer's transparent
            // materials rely on m_CustomRenderQueue (2450 cutout / 3000 blend);
            // losing it drops a ZWrite-off blend material into the opaque pass,
            // where later same-queue draws (terrain) overwrite it — fences/foliage
            // rendered invisible except against sky/buildings (2026-07-16 report).
            int queue = mat.renderQueue;

            Shader replacement;
            if (!bakedError && GameShaders != null && GameShaders.TryGetValue(sh.name, out replacement))
            {
                mat.shader = replacement;
                mat.renderQueue = queue;
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
                mat.renderQueue = queue;
                // Keep the safe-listed feature keywords (alpha clip / transparent
                // surface) — clearing them un-cuts cutout foliage; the base Lit
                // variant plus these is still a variant the game build compiles.
                var keep = new List<string>();
                foreach (var k in mat.shaderKeywords)
                    if (SafeKeywords.Contains(k)) keep.Add(k);
                mat.shaderKeywords = keep.ToArray();
                litFallbacks++;
                Diag(mat, "lit-fallback (keywords cleared)");
            }
            else
            {
                missing.Add(sh.name);
                Diag(mat, "UNFIXABLE: no game shader with this name and no game URP Lit");
            }
        }

        // Feature keywords the game build is KNOWN to compile even though the
        // menu-time snapshot can't see them: the materials that use them live in
        // built-in level scenes (ForestBush01/MapleTree01 carry _ALPHATEST_ON,
        // glass carries _SURFACE_TYPE_TRANSPARENT — verified in sharedassets1),
        // which aren't loaded when the union is taken. Without this safe-list the
        // prune strips alpha clip from every cutout material and foliage/fences
        // render as opaque blocks (2026-07-16 gari_25x report: exactly the 13
        // cutout + 4 blend mats were the 17 pruned).
        private static readonly HashSet<string> SafeKeywords = new HashSet<string>
        {
            "_ALPHATEST_ON", "_SURFACE_TYPE_TRANSPARENT",
        };

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
                if (SafeKeywords.Contains(k) || (union != null && union.Contains(k))) kept.Add(k);
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
            Color skyColor = SceneSkyFill
                ?? (sky.HasProperty("_Tint") ? sky.GetColor("_Tint") : new Color(0.53f, 0.70f, 0.86f));
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

        // Desired level-selector order: SSX Tricky courses (user's chosen sequence),
        // then SSX3 A-E. Matched case-insensitively against each map's file name via
        // StartsWith, so scale variants ("Garibaldi 2.5x") and the "Rebuilt" suffix
        // ("Ssx3_Acourse_Tricky Rebuilt") share their base's rank. Any map matching
        // nothing here (e.g. the "Tricky" bonus arena, or a future drop-in) sorts
        // after all known courses, alphabetically. Note "Tricky" must never be a key:
        // it's a substring of the SSX3 "..._Tricky" names, and StartsWith on the file
        // name keeps them apart, but a bare "Tricky" key would still be redundant.
        private static readonly string[] TrackOrder =
        {
            "Garibaldi",
            "Snowdream",
            "Elysium Alps",
            "Mesablanca",
            "Merqury",
            "Tokyo",
            "Aloha",
            "Alaska",
            "Untracked",
            "Pipedream",
            "Ssx3_Acourse",
            "Ssx3_Bcourse",
            "Ssx3_Ccourse",
            "Ssx3_Dcourse",
            "Ssx3_Ecourse",
        };

        private static int TrackRank(string name)
        {
            for (int i = 0; i < TrackOrder.Length; i++)
                if (name.StartsWith(TrackOrder[i], StringComparison.OrdinalIgnoreCase))
                    return i;
            return int.MaxValue; // unranked maps go last
        }

        public void AddTracks()
        {
            var List = Directory.GetFiles(MapsDir, "*.asset");
            // Directory.GetFiles order is filesystem-defined (roughly alphabetical),
            // so sort into the curated course order; scale variants of one course sit
            // adjacent (same rank) and fall back to name order (base before "2x").
            Array.Sort(List, (a, b) =>
            {
                string na = Path.GetFileNameWithoutExtension(a);
                string nb = Path.GetFileNameWithoutExtension(b);
                int cmp = TrackRank(na).CompareTo(TrackRank(nb));
                return cmp != 0 ? cmp : string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
            });
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

            // Phase 4 custom items — Wind volumes. Naming contract with SSXImporter.BuildItems:
            // a GO named "Wind_<strength>" carries a BoxCollider (geometry only) and is oriented
            // so its forward (+Z) is the world push direction. We attach a WindVolume behavior
            // that, while a Snowboarder is inside the box, adds forward*strength*dt to that
            // rider's private Snowboarder.velocity each physics step (the exact field
            // UpdateGrounded feeds to CharacterController.Move). Containment is done analytically
            // (OBB test in WindVolume) and the collider is DISABLED at runtime — so a wind box
            // never blocks the rider and is never mistaken for ground by the game's ground
            // SphereCast (which hits triggers by default). Strength (m/s^2) is parsed from the
            // name, invariant culture, matching the importer.
            int windAttached = 0, windSkipped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Wind_")) continue;
                    var windGO = t.gameObject;
                    if (windGO.GetComponent<WindVolume>() != null) continue; // already attached
                    if (windGO.GetComponent<BoxCollider>() == null)
                    { windSkipped++; Log.LogDebug($"[Wind] '{windGO.name}' skipped: no BoxCollider"); continue; }
                    float strength;
                    string tail = t.name.Substring("Wind_".Length);
                    if (!float.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out strength))
                    {
                        strength = 10f; // default when the name has no parseable strength
                        Log.LogDebug($"[Wind] '{windGO.name}' name has no parseable strength -> default {strength}");
                    }
                    var wv = windGO.AddComponent<WindVolume>();
                    // Push direction: an optional "Dir" child (importer emits it for SSX boost
                    // fields, whose push vector is independent of the box's own orientation — the
                    // box is oriented to the source volume so its OBB containment matches). No
                    // Dir child (editor-drawn wind volumes) => the box's own forward, as before.
                    var windDir = t.Find("Dir");
                    wv.WorldDir = windDir != null ? windDir.forward : t.forward;
                    wv.Strength = strength;
                    windAttached++;
                }
            }
            if (windAttached > 0 || windSkipped > 0)
                Log.LogInfo($"[Wind] attached {windAttached} wind volume(s), skipped {windSkipped}");

            // Phase 4 custom items — Teleport volumes. Naming contract with
            // SSXImporter.BuildItems: a GO named "Teleport_<i>" carries a BoxCollider (the
            // source region) and a child "Target" transform (the destination pos + facing).
            // TeleportVolume repositions any rider that ENTERS the box to the Target, using
            // the game's own CharacterController-teleport idiom (disable CC -> set transform
            // -> re-enable CC -> zero velocity, RE'd from Snowboarder.Respawn). Edge-triggered
            // (once per entry). Same collider-free OBB containment as WindVolume.
            int tpAttached = 0, tpSkipped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Teleport")) continue;
                    var tpGO = t.gameObject;
                    if (tpGO.GetComponent<TeleportVolume>() != null) continue; // already attached
                    if (tpGO.GetComponent<BoxCollider>() == null)
                    { tpSkipped++; Log.LogDebug($"[Teleport] '{tpGO.name}' skipped: no BoxCollider"); continue; }
                    var target = t.Find("Target");
                    if (target == null)
                    { tpSkipped++; Log.LogDebug($"[Teleport] '{tpGO.name}' skipped: no Target child"); continue; }
                    tpGO.AddComponent<TeleportVolume>();   // reads box + Target child in Awake
                    tpAttached++;
                }
            }
            if (tpAttached > 0 || tpSkipped > 0)
                Log.LogInfo($"[Teleport] attached {tpAttached} teleport volume(s), skipped {tpSkipped}");

            // Phase 4 custom items — TriggerAnim ("door") volumes. Naming contract with
            // SSXImporter.BuildItems: a GO named "TriggerAnim_*" ships fully authored —
            // LIVE trigger BoxCollider (layer 2), legacy Animation with the baked
            // closed→open clip (ClampForever), and the door prop reparented underneath.
            // We only attach the GAME'S OWN PlayAnimationOnTriggerEnter (HOOK_NOTES §11,
            // the SSX-Tokyo tree-trigger mechanism): any Snowboarder entering plays the
            // Animation. Its Start() self-heals m_animation and the audio locations, but
            // OnTriggerEnter posts startEvent with NO null check — a component born via
            // AddComponent (not deserialization) has null Wwise event fields, so blank
            // AK.Wwise.Event instances are Traverse-set (Event.Post is IsValid()-gated:
            // a blank event no-ops silently instead of NRE'ing). Its private `triggered`
            // one-shot flag is checked but never set by the game, so the anim replays on
            // every entry — harmless: ClampForever means an open door just stays open.
            int taAttached = 0, taSkipped = 0;
            var patType = AccessTools.TypeByName("PlayAnimationOnTriggerEnter");
            var wwiseEventType = AccessTools.TypeByName("AK.Wwise.Event");
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("TriggerAnim_")) continue;
                    var taGO = t.gameObject;
                    if (patType == null || wwiseEventType == null)
                    { taSkipped++; Log.LogWarning("[TriggerAnim] game types missing — skipped"); continue; }
                    if (taGO.GetComponent(patType) != null) continue;   // already attached
                    if (taGO.GetComponent<Animation>() == null)
                    { taSkipped++; Log.LogDebug($"[TriggerAnim] '{taGO.name}' skipped: no Animation"); continue; }
                    var pat = taGO.AddComponent(patType);
                    var tr = Traverse.Create(pat);
                    tr.Field("startEvent").SetValue(Activator.CreateInstance(wwiseEventType));
                    tr.Field("endEvent").SetValue(Activator.CreateInstance(wwiseEventType));
                    // Start() would set these anyway; set them here too in case a rider is
                    // already inside the box when physics wakes up before Start runs.
                    tr.Field("m_animation").SetValue(taGO.GetComponent<Animation>());
                    tr.Field("audioLocation1").SetValue(taGO);
                    tr.Field("audioLocation2").SetValue(taGO);
                    taAttached++;
                }
            }
            if (taAttached > 0 || taSkipped > 0)
                Log.LogInfo($"[TriggerAnim] attached {taAttached} door trigger(s), skipped {taSkipped}");

            // AnimDoor (feature: gameplay.animDoors). Naming contract with
            // SSXImporter.BuildAnimDoors:
            //   AnimDoor_<doorIdx>            — door GO with a legacy Animation (baked open clip).
            //   AnimDoorTrig_<doorIdx>_<trig> — trigger box relaying into that door.
            // Unlike TriggerAnim above (1 trigger reparents + plays 1 prop on its OWN GO),
            // SSX doors are N triggers -> M doors (merquer: one trigger opens two gates), and
            // the game's PlayAnimationOnTriggerEnter force-binds m_animation to its own GO in
            // Start() — so a shared door can't ride under a trigger. The door instead carries
            // its own Animation; each trigger references it by parsing <doorIdx> from its name.
            // AnimDoorTrigger Play()s the resolved door on rider entry (analytic OBB
            // containment like Teleport/Wind — no trigger-callback reliance for thin volumes).
            int adDoors = 0, adTrig = 0, adSkip = 0;
            var animDoorByIdx = new Dictionary<string, Animation>();
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("AnimDoor_")) continue;
                    var anim = t.GetComponent<Animation>();
                    if (anim == null) continue;   // static door (no animated part) — nothing to drive
                    animDoorByIdx[t.name.Substring("AnimDoor_".Length)] = anim;
                    adDoors++;
                }
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("AnimDoorTrig_")) continue;
                    var trigGO = t.gameObject;
                    if (trigGO.GetComponent<AnimDoorTrigger>() != null) continue;   // already attached
                    // name = AnimDoorTrig_<doorIdx>_<trigIdx>; the door idx is segment [1].
                    var segs = t.name.Split('_');
                    if (segs.Length < 3 || !animDoorByIdx.TryGetValue(segs[1], out var doorAnim))
                    { adSkip++; Log.LogDebug($"[AnimDoor] trigger '{t.name}' has no matching AnimDoor_ — skipped"); continue; }
                    var rel = trigGO.AddComponent<AnimDoorTrigger>();
                    rel.target = doorAnim;
                    rel.clipName = "AnimDoor_" + segs[1];
                    adTrig++;
                }
            if (adDoors > 0 || adTrig > 0 || adSkip > 0)
                Log.LogInfo($"[AnimDoor] attached {adTrig} trigger(s) driving {adDoors} door(s), skipped {adSkip}");

            // Per-level sky-fill override. Naming contract with SSXImporter.BuildLighting:
            // an empty marker GO named "SkyFill_<RRGGBB>" (from Overrides.json
            // lighting.skyFillColor). MUST be read before the SkydomeFollow attach below —
            // its OnEnable samples the fill once. Always assigned (found or null) so a
            // previous map's override can't leak onto the next one.
            SceneSkyFill = null;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("SkyFill_")) continue;
                    string hex = "#" + t.name.Substring("SkyFill_".Length);
                    if (ColorUtility.TryParseHtmlString(hex, out var fill))
                    {
                        SceneSkyFill = fill;
                        Log.LogInfo($"[Skydome] per-level sky fill {hex} (overrides [Skydome] FillColor)");
                    }
                    else
                        Log.LogWarning($"[Skydome] sky-fill marker '{t.name}' not parseable; using [Skydome] FillColor");
                }
            }

            // Skydome. Naming contract with SSXImporter.BuildSkybox: a GO named
            // "Skydome" carries the level's unlit, Background-queue, ZWrite-off dome
            // mesh (no collider). We attach SkydomeFollow so it stays centered on the
            // camera — the dome is baked at map scale (only tens of units across), so
            // without following, the rider would ride out of it and lose the sky. Only
            // position follows; rotation stays world-fixed so the sky is compass-stable.
            int skydomes = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name != "Skydome") continue;
                    if (t.GetComponent<SkydomeFollow>() != null) continue; // already attached
                    t.gameObject.AddComponent<SkydomeFollow>();
                    skydomes++;
                }
            }
            if (skydomes > 0)
                Log.LogInfo($"[Skydome] attached camera-follow to {skydomes} skydome(s)");

            // Animated textures (feature 14). Naming contract with SSXImporter.BuildProps:
            // a GO named "UVScroll_<u>_<v>_<originalName>" (rates written with the
            // invariant-culture round-trip format) renders SSX geometry whose material
            // UVs scroll — LCD scanlines, jumbotrons, boost-pad chevrons, river water.
            // The original name may itself contain underscores, so only the first three
            // '_'-separated segments are parsed. UVScrollBehaviour instantiates the
            // renderers' materials once and scrolls their base-map offset per frame.
            int uvAttached = 0, uvSkipped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("UVScroll_")) continue;
                    if (t.GetComponent<UVScrollBehaviour>() != null) continue; // already attached
                    string[] seg = t.name.Split(new[] { '_' }, 4);
                    float ru, rv;
                    if (seg.Length < 4 ||
                        !float.TryParse(seg[1], NumberStyles.Float, CultureInfo.InvariantCulture, out ru) ||
                        !float.TryParse(seg[2], NumberStyles.Float, CultureInfo.InvariantCulture, out rv))
                    {
                        uvSkipped++;
                        Log.LogDebug($"[UVScroll] '{t.name}' skipped: unparseable rates");
                        continue;
                    }
                    var us = t.gameObject.AddComponent<UVScrollBehaviour>();
                    us.RatePerFrame = new Vector2(ru, rv);
                    uvAttached++;
                }
            }
            if (uvAttached > 0 || uvSkipped > 0)
                Log.LogInfo($"[UVScroll] attached {uvAttached} UV-scroller(s), skipped {uvSkipped}");

            // Smashable glass / breakables. Naming contract with SSXImporter.BuildProps:
            // a GO named "Destructible_<orig>" carries the intact model (render mesh, NO
            // collider) plus inactive "Frag_<i>" children (per-shard meshes) and optional
            // "DisableRef_<name>" markers. We wire the game's own Destructible on it, plus a
            // Hook-side Smashable that DRIVES the break. WHY not rely on the game's trigger:
            // Snowboarder.OnTriggerEnter does call Destructible.Break, but only if the pane's
            // trigger fires an enter event for the rider's CharacterController — unreliable
            // for us (the rider crosses at 150-190 mph → ~1.5 m/tick, tunnelling a thin pane;
            // and a bundle-added trigger on the glass layer isn't guaranteed to fire against
            // the rider's layer). Smashable does a SWEPT test instead (can't tunnel) and calls
            // the public Break directly. The BoxCollider (render-mesh bounds, min 0.3 m thick)
            // is a geometry carrier for Smashable and for Break's GetComponent<Collider>() —
            // Smashable DISABLES it at runtime; layer 2 (the game's glass layer, excluded from
            // the ground SphereCast) keeps an intact pane from ever reading as ground. Private
            // serialized List<T>/string fields arrive null on runtime-added components
            // (feature 7): Traverse-set all four — lists may be empty but NEVER null
            // (Break foreach-es fragmentList; soundName null would PostEvent(null)).
            int smashWired = 0, smashFrags = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Destructible_")) continue;
                    var dGO = t.gameObject;
                    if (dGO.GetComponent<Destructible>() != null) continue; // already wired
                    var box = dGO.AddComponent<BoxCollider>();
                    var dmf = dGO.GetComponent<MeshFilter>();
                    if (dmf != null && dmf.sharedMesh != null)
                    {
                        var b = dmf.sharedMesh.bounds;
                        box.center = b.center;
                        box.size = Vector3.Max(b.size, new Vector3(0.3f, 0.3f, 0.3f));
                    }
                    box.isTrigger = true;
                    dGO.layer = 2;
                    var frags = new List<DestructibleFragment>();
                    var disables = new List<GameObject>();
                    foreach (Transform child in t)
                    {
                        if (child.name.StartsWith("Frag_"))
                        {
                            child.gameObject.SetActive(false);
                            child.gameObject.layer = 0;
                            frags.Add(child.gameObject.AddComponent<DestructibleFragment>());
                        }
                        else if (child.name.StartsWith("DisableRef_"))
                        {
                            // Resolve by original instance name; the scene GO may carry a
                            // pipeline prefix (UVScroll_/PhysProp_). Unresolved refs are
                            // fine — megaple's invisible Glass_Surface proxies are dropped
                            // at import, and the pane needs no wall (it's a pure trigger).
                            string target = child.name.Substring("DisableRef_".Length);
                            var found = FindSceneGOByInstanceName(levelManagerMaker.gameObject.scene, target);
                            if (found != null) disables.Add(found);
                        }
                    }
                    var destr = dGO.AddComponent<Destructible>();
                    var trav = Traverse.Create(destr);
                    trav.Field("fragmentList").SetValue(frags);
                    trav.Field("disableList").SetValue(disables);
                    trav.Field("hideSelf").SetValue(true);
                    trav.Field("soundName").SetValue("Play_GlassSmash_Large");
                    dGO.AddComponent<Smashable>(); // drives Break via swept detection; disables the box
                    smashWired++; smashFrags += frags.Count;
                }
            }
            if (smashWired > 0)
                Log.LogInfo($"[Destructible] wired {smashWired} smashable(s) with {smashFrags} shard fragment(s)");

            // Atmospheric falling snow. Naming contract with SSXImporter.BuildLighting:
            // an empty GO "SnowFX_<intensity>". We build a camera-follow ParticleSystem
            // at runtime (SnowFall) — a bundle-baked system would be engine-module-stripped
            // and, world-simulated, couldn't stay wrapped around the chase cam. One system
            // total (there's only ever one marker); intensity scales emission + cap.
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("SnowFX_")) continue;
                    if (SnowFall.Active) break;
                    float si;
                    if (!float.TryParse(t.name.Substring("SnowFX_".Length), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out si)) si = 1f;
                    SnowFall.Spawn(si);
                    Log.LogInfo($"[SnowFall] camera-follow snow spawned (intensity {si:F2})");
                }
                if (SnowFall.Active) break;
            }

            // Post-process bloom. Naming contract: "Bloom_<intensity>_<threshold>". PostFX
            // creates a global URP Volume with a Bloom override and flips the render
            // camera's post-processing on (feature: PostFX). One volume total.
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Bloom_")) continue;
                    if (PostFX.Active) break;
                    string[] seg = t.name.Split('_');
                    float bi = 0.7f, bt = 0.9f;
                    if (seg.Length >= 2) float.TryParse(seg[1], NumberStyles.Float, CultureInfo.InvariantCulture, out bi);
                    if (seg.Length >= 3) float.TryParse(seg[2], NumberStyles.Float, CultureInfo.InvariantCulture, out bt);
                    PostFX.Enable(bi, bt);
                    Log.LogInfo($"[PostFX] bloom enabled (intensity {bi:F2}, threshold {bt:F2})");
                }
                if (PostFX.Active) break;
            }

            // NB: the open-top sky-fill is started from OnSceneLoaded (not here) —
            // SolidColorSky captures the ACTIVE scene name, which isn't the custom scene
            // yet at LoadLevelManager time, so starting it here exits immediately.
        }

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
                    if (tr.name == instName || tr.name.EndsWith(suffix, StringComparison.Ordinal))
                        return tr.gameObject;
            return null;
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
                    if (line.StartsWith("platform")) continue; // native-platform header, read at load time (ReadBundleNativePlatform)
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

        // Reads the optional "gravity <f>" header from a map's .aipaths.txt
        // sidecar. Returns 1.0 (no change) for a missing sidecar, no gravity
        // line, or a non-positive value. Independent of the "scale" header —
        // applies at any world scale (see ScaledGravity).
        internal static float ReadGravityMultiplier(string bundlePath)
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
                    if (t.Length != 2 || t[0] != "gravity") continue;
                    float g = float.Parse(t[1], CultureInfo.InvariantCulture);
                    if (g > 0f) return g;
                    Log.LogWarning($"[Gravity] sidecar {Path.GetFileName(sidecarPath)} declares gravity {t[1]} (must be > 0); ignoring");
                    return 1f;
                }
                return 1f;
            }
            catch (Exception e)
            {
                Log.LogError($"[Gravity] failed reading gravity multiplier sidecar for {bundlePath}: {e}");
                return 1f;
            }
        }

        // Reads the optional "platform <windows|mac>" header from a map's
        // .aipaths.txt sidecar. Returns null (no declaration) for a missing
        // sidecar, no platform line, or an unrecognized value — the safe default
        // that keeps the always-remap behavior. Only "windows" and "mac" are
        // honored; they let the shader remap trust the bundle's own shaders when
        // it runs on that platform (see BundleIsNativeToCurrentGpu).
        internal static string ReadBundleNativePlatform(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return null;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length != 2 || t[0] != "platform") continue;
                    string p = t[1].ToLowerInvariant();
                    if (p == "windows" || p == "mac") return p;
                    Log.LogWarning($"[ShaderRemap] sidecar {Path.GetFileName(sidecarPath)} declares platform '{t[1]}' (expected 'windows' or 'mac'); ignoring");
                    return null;
                }
                return null;
            }
            catch (Exception e)
            {
                Log.LogError($"[ShaderRemap] failed reading platform sidecar for {bundlePath}: {e}");
                return null;
            }
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
                Plugin.BundleNativePlatform = null;
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

                // Per-map gravity multiplier from the same sidecar (1.0 when absent).
                // Read on reuse too — the sidecar may have changed on disk.
                Plugin.MapGravityMultiplier = Plugin.ReadGravityMultiplier(path);
                if (Plugin.MapGravityMultiplier != 1f)
                    Plugin.Log.LogInfo($"[Gravity] map declares gravity multiplier {Plugin.MapGravityMultiplier.ToString(CultureInfo.InvariantCulture)}x (applied on top of any world-scale gravity)");

                // Native-platform declaration from the same sidecar (null when absent).
                // Read on reuse too — the sidecar may have changed on disk.
                Plugin.BundleNativePlatform = Plugin.ReadBundleNativePlatform(path);
                if (Plugin.BundleNativePlatform != null)
                    Plugin.Log.LogInfo($"[ShaderRemap] map declares native platform '{Plugin.BundleNativePlatform}'; its own shaders are trusted (not remapped) when running on that backend");

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

    // Keeps a skydome mesh centered on the camera and sized to envelop the whole visible
    // course, so a finite (map-scaled) dome reads as a distant sky. The importer emits an
    // unlit, Background-queue, ZWrite-off, cull-off dome named "Skydome" with no collider.
    //
    // Two jobs, both in URP's beginCameraRendering (fires AFTER every Update/LateUpdate,
    // right before the camera culls — the exact render viewpoint, zero lag):
    //
    // 1) POSITION: snap the dome onto the camera every render. A plain LateUpdate follow
    //    lags a frame behind the chase-cam rig; at 200 mph that ~1.5 m offset swims the
    //    dome edge / makes the near ground "emerge like water." beginCameraRendering has
    //    no lag. Only position follows; rotation stays world-fixed (compass-stable sky).
    //
    // 2) SCALE: the dome is a FINITE bowl baked at map scale (tens of units). If its radius
    //    is smaller than the distance to far course terrain, that terrain pokes past the
    //    dome edge and the dome reads as a nearby wall (user report). Centering alone can't
    //    fix this — the course is a fixed size the dome must ENVELOP. So auto-fit the dome
    //    radius to ~90% of the camera's far-clip plane each frame: as distant as possible
    //    while staying inside the frustum (overshoot → the far plane clips the dome into
    //    chunks). [Skydome] Scale is a residual multiplier on that fit (default 1).
    //
    // Also clears the camera to [Skydome] FillColor so looking up past the open rim (~+20
    // deg) reads as sky, not black. Done on the pipeline-supplied camera, NOT Camera.main
    // (null on these maps — the game camera isn't the enabled MainCamera the whole time).
    public class SkydomeFollow : MonoBehaviour
    {
        Transform _cam;
        Color _fill;
        bool _fillValid;
        float _baseRadius = -1f;   // unscaled dome radius (map units), computed once
        bool _fitLogged;

        void OnEnable()
        {
            // Per-level override (Overrides.json lighting.skyFillColor, via the SkyFill_
            // marker Plugin.LoadLevelManager parses before attaching us) wins over the
            // global [Skydome] FillColor config.
            if (Plugin.SceneSkyFill.HasValue) { _fill = Plugin.SceneSkyFill.Value; _fillValid = true; }
            else
            {
                string hex = Plugin.skydomeFillColor != null ? Plugin.skydomeFillColor.Value : "#3373C3";
                if (ColorUtility.TryParseHtmlString(hex, out _fill)) _fillValid = true;
                else { _fill = new Color(0.20f, 0.45f, 0.76f); _fillValid = true;
                       Plugin.Log.LogWarning($"[Skydome] FillColor '{hex}' not parseable; using #3373C3"); }
            }

            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }
        void OnDisable()
        {
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        }

        // Farthest dome vertex from the pivot, in the dome's UNSCALED local frame — the
        // InverseTransformPoint round-trip cancels the root's own localScale, so this is
        // scale-independent and only needs computing once.
        float ComputeBaseRadius()
        {
            float maxSq = 0f;
            foreach (var mf in GetComponentsInChildren<MeshFilter>())
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                var b = mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + new Vector3(
                        (i & 1) == 0 ? b.extents.x : -b.extents.x,
                        (i & 2) == 0 ? b.extents.y : -b.extents.y,
                        (i & 4) == 0 ? b.extents.z : -b.extents.z);
                    Vector3 rootLocal = transform.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    float sq = rootLocal.sqrMagnitude;
                    if (sq > maxSq) maxSq = sq;
                }
            }
            return Mathf.Sqrt(maxSq);
        }

        void OnBeginCamera(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == null || !cam.CompareTag("MainCamera")) return;

            transform.position = cam.transform.position;

            if (_baseRadius < 0f) _baseRadius = ComputeBaseRadius();
            if (_baseRadius > 0.001f && cam.farClipPlane > 1f)
            {
                float mult = Plugin.skydomeScale != null ? Mathf.Max(0.01f, Plugin.skydomeScale.Value) : 1f;
                float targetR = cam.farClipPlane * 0.95f * mult;
                float s = targetR / _baseRadius;
                transform.localScale = Vector3.one * s;
                if (!_fitLogged)
                {
                    Plugin.Log.LogInfo($"[Skydome] auto-fit: baseR={_baseRadius:F1} farClip={cam.farClipPlane:F0} mult={mult:F2} -> scale={s:F1}");
                    _fitLogged = true;
                }
            }

            if (_fillValid && (cam.clearFlags != CameraClearFlags.SolidColor || cam.backgroundColor != _fill))
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = _fill;
            }
        }

        // First-frame / built-in-pipeline fallback; harmless under URP where OnBeginCamera
        // repositions before the render.
        void LateUpdate()
        {
            if (_cam == null)
            {
                var c = Camera.main;
                if (c == null) return;
                _cam = c.transform;
            }
            transform.position = _cam.position;
        }
    }

    // Camera-follow atmospheric snowfall. The importer bakes an empty "SnowFX_<intensity>"
    // marker; we build the ParticleSystem here at runtime because (a) a bundle-baked
    // ParticleSystem is silently stripped unless the module is in the import project, and
    // (b) it must stay wrapped around the chase cam. Simulated in WORLD space so flakes
    // fall past a turning camera naturally (a camera-parented/local system would rotate the
    // whole snowfield with the view); the emitter box just re-centres on the camera each
    // LateUpdate so there is always fresh snow around the rider even at 190 mph. Geometry
    // is fixed and playtest-tuned; intensity scales only emission rate + particle cap.
    public class SnowFall : MonoBehaviour
    {
        public static bool Active;
        Transform _cam;

        public static void Spawn(float intensity)
        {
            var go = new GameObject("HookSnowFall");
            go.AddComponent<SnowFall>().Build(Mathf.Clamp(intensity, 0.05f, 8f));
            Active = true;
        }

        void Build(float intensity)
        {
            var ps = gameObject.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4f, 6.5f);  // desync the fall
            main.startSpeed = 0f;                    // motion comes from velocityOverLifetime
            // Wide size spread so flakes read as near/far, not one uniform grade — some fine
            // spindrift (0.05), a few fatter foreground flakes (0.55; 0.8 read as dust bunnies).
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.55f);
            main.startColor = new Color(0.93f, 0.96f, 1f, 0.85f);
            main.maxParticles = Mathf.CeilToInt(3400 * intensity);
            main.gravityModifier = 0f;
            main.playOnAwake = false;

            var emission = ps.emission;
            emission.rateOverTime = 650f * intensity;   // ~2x the first pass

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(70f, 45f, 70f); // band around the camera (world units)

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(-1.5f, 1.5f);  // drift
            vel.y = new ParticleSystem.MinMaxCurve(-7f, -4f);     // fall
            vel.z = new ParticleSystem.MinMaxCurve(-1.5f, 1.5f);

            // Non-uniform density: soft low-frequency turbulence swirls flakes into clumps
            // and gaps instead of an even rain grid — the "make it not so uniform" fix.
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 1.6f;      // world-unit drift amplitude
            noise.frequency = 0.22f;    // large soft swirls, not jitter
            noise.scrollSpeed = 0.35f;
            noise.damping = true;
            noise.quality = ParticleSystemNoiseQuality.Medium;

            var pr = GetComponent<ParticleSystemRenderer>();
            pr.renderMode = ParticleSystemRenderMode.Billboard;
            pr.alignment = ParticleSystemRenderSpace.View;
            pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pr.receiveShadows = false;
            pr.material = FlakeMaterial();

            ps.Play();
        }

        void LateUpdate()
        {
            if (_cam == null)
            {
                var c = Camera.main;
                if (c == null) return;
                _cam = c.transform;
            }
            // Centre the emission band on the camera; world-sim keeps the falling motion.
            transform.position = _cam.position;
        }

        void OnDestroy() { Active = false; }

        static Material _flakeMat;
        static Material FlakeMaterial()
        {
            if (_flakeMat != null) return _flakeMat;
            var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                     ?? Shader.Find("Universal Render Pipeline/Unlit");
            var m = new Material(sh) { name = "HookSnowFlake" };
            m.SetTexture("_BaseMap", FlakeSprite());
            m.SetColor("_BaseColor", Color.white);
            // Transparent alpha-blend (the URP incantation ApplyTexMode uses on the importer side).
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            _flakeMat = m;
            return m;
        }

        static Texture2D _flakeTex;
        static Texture2D FlakeSprite()
        {
            if (_flakeTex != null) return _flakeTex;
            const int N = 32;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { name = "HookSnowFlakeTex" };
            float c = (N - 1) / 2f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    float a = Mathf.Clamp01(1f - d);
                    a *= a;   // soft round falloff, no hard rim
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            t.Apply();
            t.wrapMode = TextureWrapMode.Clamp;
            _flakeTex = t;
            return t;
        }
    }

    // URP post-process bloom. The importer bakes an empty "Bloom_<intensity>_<threshold>"
    // marker; we create a runtime global Volume carrying a Bloom override and flip the
    // render camera's post-processing on in beginCameraRendering (the game camera is null
    // via Camera.main during load, same reason SkydomeFollow uses the pipeline-supplied
    // camera). If the game's URP renderer has post-processing disabled at the feature level
    // this is inert — verified by playtest, not assertable here.
    public class PostFX : MonoBehaviour
    {
        public static bool Active;
        float _intensity = 0.7f, _threshold = 0.9f;

        public static void Enable(float intensity, float threshold)
        {
            if (Active) return;
            var go = new GameObject("HookPostFX");
            var fx = go.AddComponent<PostFX>();
            fx._intensity = Mathf.Max(0f, intensity);
            fx._threshold = Mathf.Max(0f, threshold);
            fx.Build();
            Active = true;
        }

        void Build()
        {
            var vol = gameObject.AddComponent<UnityEngine.Rendering.Volume>();
            vol.isGlobal = true;
            vol.priority = 100f;   // win over any game volume
            var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
            vol.sharedProfile = profile;

            var bloom = profile.Add<UnityEngine.Rendering.Universal.Bloom>(true);
            bloom.intensity.Override(_intensity);
            bloom.threshold.Override(_threshold);
            bloom.scatter.Override(0.72f);
            bloom.tint.Override(new Color(0.9f, 0.95f, 1f));

            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }

        void OnBeginCamera(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == null || !cam.CompareTag("MainCamera")) return;
            var data = cam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            if (data != null && !data.renderPostProcessing)
                data.renderPostProcessing = true;
        }

        void OnDisable()
        {
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        }
        void OnDestroy() { Active = false; }
    }

    // A wind push volume. Attached by the Hook's scene scan to every "Wind_<strength>" GO
    // the importer emits. While a Snowboarder's origin is inside the (oriented) box, each
    // physics step adds WorldDir * Strength * dt to that rider's private Snowboarder.velocity
    // — the field Integrate() writes and UpdateGrounded() feeds to CharacterController.Move.
    //
    // The box is described by a BoxCollider on this GO, but we do NOT use it as a physics
    // trigger: the game's ground SphereCast hits triggers by default (Caster.Cast uses the
    // layerMask-only overload → global Physics.queriesHitTriggers == true), so a live wind
    // trigger on a cast-included layer would read as ground and float the rider. Instead we
    // read the collider's size once, DISABLE it (so it never participates in physics or
    // casts), and do an analytic oriented-box containment test each FixedUpdate. That makes
    // the wind volume fully independent of the layer collision matrix and cast masks.
    public class WindVolume : MonoBehaviour
    {
        public Vector3 WorldDir;   // world push direction (normalized in Awake)
        public float Strength;     // acceleration magnitude, m/s^2

        Vector3 _halfLocal = new Vector3(0.5f, 0.5f, 0.5f); // local half-extents from the BoxCollider
        Snowboarder[] _riders;
        readonly Dictionary<Snowboarder, Traverse> _vel = new Dictionary<Snowboarder, Traverse>();

        void Awake()
        {
            WorldDir = WorldDir.sqrMagnitude > 1e-8f ? WorldDir.normalized : transform.forward;
            var box = GetComponent<BoxCollider>();
            if (box != null)
            {
                _halfLocal = box.size * 0.5f;
                box.enabled = false; // geometry carrier only — never a live collider at runtime
            }
        }

        void FixedUpdate()
        {
            if (_riders == null || _riders.Length == 0)
            {
                _riders = UnityEngine.Object.FindObjectsOfType<Snowboarder>();
                if (_riders.Length == 0) return; // riders not spawned yet
            }
            float dt = Time.fixedDeltaTime;
            foreach (var sb in _riders)
            {
                if (sb == null) continue;
                // Oriented-box containment: rider origin into this GO's local space, compare
                // against the box half-extents. InverseTransformPoint folds in position,
                // rotation and (unit) scale, so this respects the box's orientation.
                Vector3 local = transform.InverseTransformPoint(sb.transform.position);
                if (Mathf.Abs(local.x) > _halfLocal.x ||
                    Mathf.Abs(local.y) > _halfLocal.y ||
                    Mathf.Abs(local.z) > _halfLocal.z) continue;

                if (!_vel.TryGetValue(sb, out var t))
                {
                    t = Traverse.Create(sb).Field("velocity");
                    _vel[sb] = t;
                }
                Vector3 v = t.GetValue<Vector3>();
                v += WorldDir * Strength * dt;
                t.SetValue(v);
            }
        }
    }

    // Scrolls the material UVs of everything under this GO — the runtime half of the
    // animated-texture pipeline (SSX's UVScrolling effect: LCD scanlines, jumbotrons,
    // boost-pad chevrons, river water). The importer bakes the decoded rates into the
    // GO name; the Hook parses them and attaches this. renderer.materials instantiates
    // per-renderer material COPIES once in Awake — required, or scrolling one LCD would
    // scroll every material sharing that texture (the importer's material cache dedups
    // aggressively). The offset is absolute (rate * time), not accumulated, so playback
    // speed changes cleanly and no float error builds up; wrapped to [0,1) so precision
    // holds over long sessions.
    public class UVScrollBehaviour : MonoBehaviour
    {
        public Vector2 RatePerFrame; // UV units per frame @60Hz, parsed from the GO name

        Renderer[] _rends;
        Material[][] _mats;
        Vector2[][] _base;

        // First-attached instance emits two one-line probes (~15s and ~30s into the
        // level) — shader name, written vs read-back offsets, and whether the renderer
        // still holds our instances. Cheap enough to leave on; it's the evidence for
        // the "attached but nothing moves" class of report (2026-07-16 playtest).
        static UVScrollBehaviour _diag;
        int _diagShots = 2;
        float _nextDiagAt = 15f;

        void Awake()
        {
            _rends = GetComponentsInChildren<Renderer>(true);
            _mats = new Material[_rends.Length][];
            _base = new Vector2[_rends.Length][];
            for (int i = 0; i < _rends.Length; i++)
                Bind(i);
            if (_diag == null) _diag = this;
        }

        void OnDestroy()
        {
            if (_diag == this) _diag = null;
        }

        // renderer.materials instantiates per-renderer COPIES and assigns them —
        // required, or scrolling one LCD would scroll every material sharing that
        // texture (the importer's material cache dedups aggressively).
        void Bind(int i)
        {
            _mats[i] = _rends[i].materials;
            _base[i] = new Vector2[_mats[i].Length];
            for (int j = 0; j < _mats[i].Length; j++)
                _base[i][j] = _mats[i][j] != null ? _mats[i][j].mainTextureOffset : Vector2.zero;
        }

        void Update()
        {
            float fps = Plugin.uvScrollFrameRate != null ? Plugin.uvScrollFrameRate.Value : 60f;
            Vector2 scroll = RatePerFrame * fps * Time.time;
            Vector2 o = Vector2.zero;
            for (int i = 0; i < _rends.Length; i++)
            {
                var r = _rends[i];
                if (r == null) continue;
                // Self-heal: if anything replaced the renderer's material array after
                // our Awake (the copies we scroll would then be orphaned — writes land
                // on materials nobody renders), re-instantiate from the current array.
                var shared = r.sharedMaterials;
                if (_mats[i].Length != shared.Length ||
                    (shared.Length > 0 && !ReferenceEquals(shared[0], _mats[i][0])))
                    Bind(i);
                for (int j = 0; j < _mats[i].Length; j++)
                {
                    var m = _mats[i][j];
                    if (m == null) continue;
                    o = _base[i][j] + scroll;
                    o.x -= Mathf.Floor(o.x);
                    o.y -= Mathf.Floor(o.y);
                    // URP/Lit tags _BaseMap [MainTexture], so mainTextureOffset targets
                    // it — but write the property explicitly too, so the scroll doesn't
                    // hang on the main-texture resolution of a remapped shader.
                    m.mainTextureOffset = o;
                    if (m.HasProperty("_BaseMap"))
                        m.SetTextureOffset("_BaseMap", o);
                }
            }

            if (_diag == this && _diagShots > 0 && Time.timeSinceLevelLoad >= _nextDiagAt)
            {
                _diagShots--;
                _nextDiagAt += 15f;
                Material m0 = null;
                if (_mats.Length > 0 && _mats[0].Length > 0) m0 = _mats[0][0];
                if (m0 != null && _rends.Length > 0 && _rends[0] != null)
                {
                    var shared = _rends[0].sharedMaterials;
                    bool bound = shared.Length > 0 && ReferenceEquals(shared[0], m0);
                    Vector2 rb = m0.HasProperty("_BaseMap") ? m0.GetTextureOffset("_BaseMap") : new Vector2(-9, -9);
                    Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "[UVScroll] diag t={0:F0}s '{1}': shader='{2}' wrote=({3:F3},{4:F3}) baseMapST=({5:F3},{6:F3}) mainOfs=({7:F3},{8:F3}) bound={9} rate=({10:R},{11:R})",
                        Time.timeSinceLevelLoad, name, m0.shader != null ? m0.shader.name : "null",
                        o.x, o.y, rb.x, rb.y, m0.mainTextureOffset.x, m0.mainTextureOffset.y,
                        bound, RatePerFrame.x, RatePerFrame.y));
                }
            }
        }
    }

    // A teleport volume. Attached by the Hook's scene scan to every "Teleport_*" GO the
    // importer emits (source box + a child "Target" transform for the destination). When a
    // Snowboarder ENTERS the (oriented) box it is repositioned to the Target — edge-triggered
    // (once per entry; it must leave and re-enter to fire again), so it never loops even if
    // the destination is adjacent. Containment is analytic (same collider-free OBB test as
    // WindVolume — the box never participates in physics/casts).
    //
    // The reposition mirrors the game's own Snowboarder.Respawn: DISABLE the rider's
    // CharacterController (so set_position isn't fought by CC depenetration), set rotation to
    // the Target's facing (always definite — authored targetFacing, else the box direction),
    // set position seated by up*cc.radius (the exact seat Respawn uses so the capsule clears
    // the surface), RE-ENABLE the CC, then zero velocity.
    public class TeleportVolume : MonoBehaviour
    {
        Vector3 _halfLocal = new Vector3(0.5f, 0.5f, 0.5f);
        Vector3 _targetPos;
        Quaternion _targetRot = Quaternion.identity;

        Snowboarder[] _riders;
        readonly Dictionary<Snowboarder, Traverse> _vel = new Dictionary<Snowboarder, Traverse>();
        readonly Dictionary<Snowboarder, Traverse> _cc  = new Dictionary<Snowboarder, Traverse>();
        readonly HashSet<Snowboarder> _inside = new HashSet<Snowboarder>();

        void Awake()
        {
            var box = GetComponent<BoxCollider>();
            if (box != null)
            {
                _halfLocal = box.size * 0.5f;
                box.enabled = false; // geometry carrier only
            }
            var target = transform.Find("Target");
            if (target != null)
            {
                _targetPos = target.position;
                // The importer always orients the Target to a definite landing facing
                // (authored targetFacing, else the box direction) — so we always seat the
                // rider at the Target's world rotation. No has-facing ambiguity.
                _targetRot = target.rotation;
            }
            else
            {
                enabled = false; // no destination — inert (the scan also guards this)
            }
        }

        void FixedUpdate()
        {
            if (_riders == null || _riders.Length == 0)
            {
                _riders = UnityEngine.Object.FindObjectsOfType<Snowboarder>();
                if (_riders.Length == 0) return; // riders not spawned yet
            }
            foreach (var sb in _riders)
            {
                if (sb == null) continue;
                Vector3 local = transform.InverseTransformPoint(sb.transform.position);
                bool inside = Mathf.Abs(local.x) <= _halfLocal.x
                           && Mathf.Abs(local.y) <= _halfLocal.y
                           && Mathf.Abs(local.z) <= _halfLocal.z;

                if (!inside) { _inside.Remove(sb); continue; }
                if (!_inside.Add(sb)) continue;   // already counted as inside — edge-trigger only
                Teleport(sb);
            }
        }

        void Teleport(Snowboarder sb)
        {
            if (!_cc.TryGetValue(sb, out var ccT))
            { ccT = Traverse.Create(sb).Field("characterController"); _cc[sb] = ccT; }
            var cc = ccT.GetValue<CharacterController>();

            if (cc != null) cc.enabled = false;                 // let set_position win over CC
            sb.transform.rotation = _targetRot;
            float radius = cc != null ? cc.radius : 0f;
            sb.transform.position = _targetPos + sb.transform.up * radius;  // Respawn's seat
            if (cc != null) cc.enabled = true;

            if (!_vel.TryGetValue(sb, out var velT))
            { velT = Traverse.Create(sb).Field("velocity"); _vel[sb] = velT; }
            velT.SetValue(Vector3.zero);
        }
    }

    // Relay for a trigger-driven animated door (feature: gameplay.animDoors). The
    // importer emits the door GO's Animation separately (N triggers can share one door,
    // which the game's own PlayAnimationOnTriggerEnter can't express — it force-binds to
    // its own GO). While a Snowboarder's origin enters this box we Play() the referenced
    // door once per entry (edge-triggered). Analytic OBB containment (box disabled) like
    // TeleportVolume/WindVolume — thin trigger volumes tunnel physics trigger callbacks.
    // ClampForever clips: the door opens and stays open; a replay on re-entry is harmless.
    public class AnimDoorTrigger : MonoBehaviour
    {
        public Animation target;
        public string clipName;

        Vector3 _halfLocal = new Vector3(0.5f, 0.5f, 0.5f);
        Vector3 _center = Vector3.zero;   // box.center: model-bounds offset from the instance origin
        Snowboarder[] _riders;
        readonly HashSet<Snowboarder> _inside = new HashSet<Snowboarder>();
        readonly Dictionary<Snowboarder, Vector3> _prev = new Dictionary<Snowboarder, Vector3>();
        bool _opened;   // door colliders disabled once (idempotent), and log emitted once

        void Awake()
        {
            var box = GetComponent<BoxCollider>();
            if (box != null)
            {
                _halfLocal = box.size * 0.5f;   // local half-extents; containment is in local space
                _center = box.center;
                box.enabled = false;            // geometry carrier only
            }
            // NOTE: do NOT self-disable on a null target here. AddComponent runs Awake
            // synchronously, BEFORE the wiring assigns `target` on the next line — so target is
            // ALWAYS null at this point and disabling would switch off every relay permanently
            // (the "attached N, fired 0" bug). FixedUpdate already early-returns on a null target.
        }

        void FixedUpdate()
        {
            if (target == null) return;
            if (_riders == null || _riders.Length == 0)
            {
                _riders = UnityEngine.Object.FindObjectsOfType<Snowboarder>();
                if (_riders.Length == 0) return; // riders not spawned yet
            }
            foreach (var sb in _riders)
            {
                if (sb == null) continue;
                // Swept test: the trigger boxes are only ~6 m across and the rider crosses at
                // 70-85 m/s (>1 box-width per fixed tick), so a point-in-box sample tunnels clean
                // through and never fires. Test the segment from the rider's PREVIOUS origin to its
                // current one against the box (local-space slab test) — this can't tunnel. Mirrors
                // the Smashable swept design for the same reason.
                Vector3 cur = transform.InverseTransformPoint(sb.transform.position);
                Vector3 prev = _prev.TryGetValue(sb, out var p) ? p : cur;
                _prev[sb] = cur;
                bool hit = SegmentHitsBox(prev, cur, _center, _halfLocal);
                if (!hit) { _inside.Remove(sb); continue; }
                if (!_inside.Add(sb)) continue;   // still crossing — edge-trigger only
                Fire();
            }
        }

        void Fire()
        {
            if (!string.IsNullOrEmpty(clipName)) target.Play(clipName);
            else target.Play();
            if (_opened) return;
            _opened = true;
            // Open the path independent of how far the baked animation actually travels: disable the
            // closed door's blocking colliders. The trigger sits ahead of the door, so this clears
            // before the rider arrives; the animation then plays purely for show. No deadlock.
            foreach (var col in target.GetComponentsInChildren<Collider>())
                col.enabled = false;
            Plugin.Log.LogInfo($"[AnimDoor] trigger '{name}' fired -> opened '{target.name}'");
        }

        // Segment p0->p1 vs axis-aligned box (center, half) in the box's own local space. Slab
        // method: clamp the [0,1] parametric interval against each axis' slab; empty => miss.
        static bool SegmentHitsBox(Vector3 p0, Vector3 p1, Vector3 center, Vector3 half)
        {
            Vector3 d = p1 - p0;
            float tmin = 0f, tmax = 1f;
            for (int a = 0; a < 3; a++)
            {
                float o = p0[a] - center[a];
                float di = d[a];
                float mn = -half[a], mx = half[a];
                if (Mathf.Abs(di) < 1e-8f)
                {
                    if (o < mn || o > mx) return false; // parallel to slab and outside it
                }
                else
                {
                    float t1 = (mn - o) / di, t2 = (mx - o) / di;
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    if (t1 > tmin) tmin = t1;
                    if (t2 < tmax) tmax = t2;
                    if (tmin > tmax) return false;
                }
            }
            return true;
        }
    }

    // Drives the game's own Destructible.Break on a smashable pane/prop when the rider
    // rides through it. WHY NOT the game's trigger path: Snowboarder.OnTriggerEnter DOES
    // call Destructible.Break(velocity) — but only when a live trigger fires an enter
    // event for the rider's CharacterController, and that path is unreliable for us: the
    // rider crosses at 150-190 mph (~70-85 m/s → ~1.5 m per fixed tick) so a thin pane
    // trigger is tunnelled between discrete overlap samples, and a bundle-added trigger on
    // the game's glass layer isn't guaranteed to fire against the rider's layer. So we
    // drive Break ourselves with a SWEPT test (segment from the rider's previous origin to
    // its current one vs the box), which can't tunnel. Same collider-free design as Wind/
    // Teleport: the BoxCollider is a geometry carrier, DISABLED at runtime (a live trigger
    // on a cast-included layer would perturb the rider) — but kept present, because
    // Destructible.Break calls GetComponent<Collider>().enabled=false and would NPE without
    // one. One-shot: Break has no re-entry guard (it re-launches every fragment), so we
    // Destroy this component after the first hit.
    public class Smashable : MonoBehaviour
    {
        const float RiderRadiusWorld = 0.6f; // ~capsule radius; catch margin, scale-folded

        Destructible _destr;
        Vector3 _center;                     // box centre, local (unscaled) space
        Vector3 _half = new Vector3(0.15f, 0.15f, 0.15f);
        Snowboarder[] _riders;
        readonly Dictionary<Snowboarder, Vector3> _prev = new Dictionary<Snowboarder, Vector3>();

        void Awake()
        {
            _destr = GetComponent<Destructible>();
            var box = GetComponent<BoxCollider>();
            if (box != null)
            {
                _center = box.center;
                _half = box.size * 0.5f;
                box.enabled = false; // geometry carrier only — never a live collider
            }
            if (_destr == null) enabled = false;
        }

        void FixedUpdate()
        {
            if (_destr == null) { Destroy(this); return; }
            if (_riders == null || _riders.Length == 0)
            {
                _riders = UnityEngine.Object.FindObjectsOfType<Snowboarder>();
                if (_riders.Length == 0) return; // riders not spawned yet
            }
            // Fold the rider's world catch-radius into local (unscaled) units so the margin
            // is a constant ~0.6 m in the world regardless of map WorldScale (the box is
            // authored at unit scale; the transform carries the scale-up).
            float ls = transform.lossyScale.x;
            float m = ls > 1e-4f ? RiderRadiusWorld / ls : RiderRadiusWorld;
            Vector3 half = _half + new Vector3(m, m, m);

            foreach (var sb in _riders)
            {
                if (sb == null) continue;
                Vector3 cur = transform.InverseTransformPoint(sb.transform.position);
                if (!_prev.TryGetValue(sb, out var prev)) prev = cur; // first tick: point test
                _prev[sb] = cur;
                if (!SegmentHitsBox(prev, cur, _center, half)) continue;

                Vector3 vel = Traverse.Create(sb).Field("velocity").GetValue<Vector3>();
                _destr.Break(vel);
                Destroy(this); // one-shot — Break re-launches every fragment if called twice
                return;
            }
        }

        // Slab test: does segment p0->p1 intersect the AABB centred at c with half-extents h?
        static bool SegmentHitsBox(Vector3 p0, Vector3 p1, Vector3 c, Vector3 h)
        {
            Vector3 d = p1 - p0;
            float tmin = 0f, tmax = 1f;
            for (int a = 0; a < 3; a++)
            {
                float o = p0[a] - c[a], di = d[a], ha = h[a];
                if (Mathf.Abs(di) < 1e-8f)
                {
                    if (o < -ha || o > ha) return false; // parallel to slab and outside it
                }
                else
                {
                    float t1 = (-ha - o) / di, t2 = (ha - o) / di;
                    if (t1 > t2) { var tmp = t1; t1 = t2; t2 = tmp; }
                    if (t1 > tmin) tmin = t1;
                    if (t2 < tmax) tmax = t2;
                    if (tmin > tmax) return false;
                }
            }
            return true;
        }
    }
}
