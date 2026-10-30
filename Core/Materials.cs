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

        // Scales the importer's generated detail (Overrides.json textures[].normal,
        // whose _BumpMap it names "tmdetail_*") by the TerrainDetail slider. The
        // bundle's values are cached per material so a reloaded map scales from the
        // shipped value instead of compounding. A source game's own normal map has no
        // such name and is left alone.
        static readonly Dictionary<int, Vector3> _detailBase = new Dictionary<int, Vector3>();

        private static bool ApplyTerrainDetail(Material mat)
        {
            if (!mat.HasProperty("_BumpMap")) return false;
            var bump = mat.GetTexture("_BumpMap");
            if (bump == null || !bump.name.StartsWith("tmdetail_", StringComparison.Ordinal)) return false;
            int id = mat.GetInstanceID();
            if (!_detailBase.TryGetValue(id, out var b))
            {
                // z = keyword state AFTER FixMaterial: off the native-GPU path
                // PruneKeywords has dropped _NORMALMAP (§4) and it must stay dropped.
                b = new Vector3(mat.GetFloat("_BumpScale"),
                                mat.HasProperty("_Smoothness") ? mat.GetFloat("_Smoothness") : 0f,
                                mat.IsKeywordEnabled("_NORMALMAP") ? 1f : 0f);
                _detailBase[id] = b;
            }
            float k = Mathf.Max(0f, terrainDetail != null ? terrainDetail.Value : 1f);
            mat.SetFloat("_BumpScale", b.x * k);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", Mathf.Clamp01(b.y * k));
            if (k > 0f && b.z > 0f) mat.EnableKeyword("_NORMALMAP"); else mat.DisableKeyword("_NORMALMAP");
            return true;
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
        // URP depth priming draws a depth-only prepass of everything in the opaque queue
        // range, then shades with ZTest Equal. Our maps put alpha-blended materials in that
        // range on purpose — the sky layers sit in the Background queue (1000+), and crowd
        // cards and other alpha props are opaque-queue — and the prepass records their
        // fully transparent texels as solid. Whatever lies behind them then fails the
        // Equal test and the camera clear colour shows through: Matterhorn's sky cut off
        // above the cloud layer, the clear-coloured fringe round the crowds. Disabling the
        // material's DepthOnly/DepthNormals passes did NOT fix it (playtested); turning
        // priming off did. The game's renderer ships with Auto, which is on here because
        // the SSAO feature already forces a depth-normals prepass.
        static readonly Dictionary<object, object> s_primingOriginal = new Dictionary<object, object>();

        static void SetDepthPriming(bool restore)
        {
            const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            try
            {
                var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                var list = rp?.GetType().GetField("m_RendererDataList", BF)?.GetValue(rp) as System.Array;
                if (list == null) { if (!restore) Log.LogWarning("[DepthPriming] no URP renderer list; priming left as is"); return; }
                foreach (var data in list)
                {
                    var prop = data?.GetType().GetProperty("depthPrimingMode", BF);
                    if (prop == null) continue;
                    object cur = prop.GetValue(data);
                    if (!s_primingOriginal.ContainsKey(data)) s_primingOriginal[data] = cur;
                    object want = restore ? s_primingOriginal[data] : System.Enum.ToObject(prop.PropertyType, 0); // 0 = Disabled
                    if (Equals(cur, want)) continue;
                    prop.SetValue(data, want);
                    Log.LogInfo($"[DepthPriming] {cur} -> {want} ({(restore ? "left custom map" : "custom map")})");
                }
            }
            catch (System.Exception e) { Log.LogWarning("[DepthPriming] could not change depth priming: " + e.Message); }
        }

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

            // 2) The game's skybox shader; copy matching properties. Skybox/Cubemap is
            // the only Skybox/* shader TM ships (TM 25638975: sharedassets1 only; no
            // 6 Sided/Panoramic/Procedural anywhere, unity_builtin_extra included).
            if (GameShaders != null && GameShaders.TryGetValue("Skybox/Cubemap", out replacement))
            {
                var newMat = new Material(replacement);
                CopySharedProperties(sky, newMat);
                RenderSettings.skybox = newMat;
                Log.LogInfo($"[ShaderRemap] skybox '{sh.name}' not in game build; rebuilt on game '{replacement.name}'");
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
    }
}
