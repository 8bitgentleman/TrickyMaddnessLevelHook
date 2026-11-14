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
        static void EnablePostFX(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
            // Post-process bloom and colour grade, both on ONE global URP Volume (feature:
            // PostFX). Naming contracts (SSXImporter bakes both from Overrides.json lighting):
            //   "Bloom_<intensity>_<threshold>"           lighting.bloom
            //   "Grade_<sat>_<con>_<exp>_<RRGGBB>"        lighting.grade
            // Each is opt-in per map: no marker, no Volume, no camera post — a map without
            // either renders exactly as before. First marker of each kind wins.
            bool bloom = false, grade = false;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!bloom && t.name.StartsWith("Bloom_")) { bloom = true; ParseBloom(t.name); }
                    else if (!grade && t.name.StartsWith("Grade_")) { grade = true; ParseGrade(t.name); }
                    if (bloom && grade) break;
                }
                if (bloom && grade) break;
            }
            blocks.Add($"bloom={(PostFX.BloomOn ? 1 : 0)}");
            blocks.Add($"grade={(PostFX.GradeOn ? 1 : 0)}");
        }

        // ⚠️ Parse into a scratch float, NOT straight into the default. TryParse zeroes its
        // out param on failure, so passing bi directly turned a malformed "Bloom_x_y" into
        // intensity 0 — bloom silently OFF — while the log line reported the 0 as authored.
        static float SegFloat(string[] seg, int i, float dflt, string name, string what)
        {
            if (seg.Length <= i) return dflt;
            if (float.TryParse(seg[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return v;
            Log.LogDebug($"[PostFX] '{name}' has no parseable {what} -> default {dflt:F2}");
            return dflt;
        }

        static void ParseBloom(string name)
        {
            string[] seg = name.Split('_');
            float bi = SegFloat(seg, 1, 0.7f, name, "intensity");
            float bt = SegFloat(seg, 2, 0.9f, name, "threshold");
            PostFX.EnableBloom(bi, bt);
            Log.LogInfo($"[PostFX] bloom enabled (intensity {bi:F2}, threshold {bt:F2})");
            LoadClock.Mark("PostFX");
        }

        // "Grade_<saturation>_<contrast>_<postExposure>_<RRGGBB>" — URP ColorAdjustments units:
        // saturation/contrast -100..100 (0 = neutral), postExposure in EV stops (0 = neutral),
        // colorFilter an sRGB hex (FFFFFF = neutral). A missing/unparseable field is neutral,
        // never zero-as-authored (saturation 0 IS neutral, but exposure "x" must not become -inf).
        static void ParseGrade(string name)
        {
            string[] seg = name.Split('_');
            float sat = SegFloat(seg, 1, 0f, name, "saturation");
            float con = SegFloat(seg, 2, 0f, name, "contrast");
            float exp = SegFloat(seg, 3, 0f, name, "postExposure");
            Color filter = Color.white;
            if (seg.Length > 4 && !ColorUtility.TryParseHtmlString("#" + seg[4].TrimStart('#'), out filter))
            {
                Log.LogWarning($"[PostFX] '{name}' colour filter '{seg[4]}' is not RRGGBB -> white");
                filter = Color.white;
            }
            PostFX.EnableGrade(sat, con, exp, filter);
            Log.LogInfo($"[PostFX] grade enabled (saturation {sat:+0.#;-0.#;0}, contrast {con:+0.#;-0.#;0}, "
                + $"postExposure {exp:+0.###;-0.###;0}, filter #{ColorUtility.ToHtmlStringRGB(filter)})");
            LoadClock.Mark("PostFX grade");
        }
    }

    // URP post-process bloom + colour grade. The importer bakes empty "Bloom_..." /
    // "Grade_..." markers; we create ONE runtime global Volume carrying whichever overrides
    // the map asked for, and flip the render camera's post-processing on in
    // beginCameraRendering (the game camera is null via Camera.main during load, same reason
    // SkydomeFollow uses the pipeline-supplied camera). If the game's URP renderer has
    // post-processing disabled at the feature level this is inert — verified by playtest,
    // not assertable here. Built with the Hook's own code only (no shader ships, §4), so it
    // renders the same on a Mac and a Windows bundle.
    //
    // The grade is URP ColorAdjustments: saturation, contrast, postExposure, colorFilter.
    // Ssx3Painter's Volume (priority 101) also carries ColorAdjustments.colorFilter and wins
    // over this one (100) where both are set — no shipped map has both.
    public class PostFX : MonoBehaviour
    {
        public static bool Active => _inst != null;
        public static bool BloomOn => _inst != null && _inst._bloom != null;
        public static bool GradeOn => _inst != null && _inst._grade != null;
        static PostFX _inst;
        UnityEngine.Rendering.VolumeProfile _profile;
        UnityEngine.Rendering.Universal.Bloom _bloom;
        UnityEngine.Rendering.Universal.ColorAdjustments _grade;

        static PostFX Ensure()
        {
            if (_inst != null) return _inst;
            var go = new GameObject("HookPostFX");
            _inst = go.AddComponent<PostFX>();
            var vol = go.AddComponent<UnityEngine.Rendering.Volume>();
            vol.isGlobal = true;
            vol.priority = 100f;   // win over any game volume
            _inst._profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
            vol.sharedProfile = _inst._profile;
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += _inst.OnBeginCamera;
            return _inst;
        }

        public static void EnableBloom(float intensity, float threshold)
        {
            var fx = Ensure();
            if (fx._bloom != null) return;
            fx._bloom = fx._profile.Add<UnityEngine.Rendering.Universal.Bloom>(true);
            fx._bloom.intensity.Override(Mathf.Max(0f, intensity));
            fx._bloom.threshold.Override(Mathf.Max(0f, threshold));
            fx._bloom.scatter.Override(0.72f);
            fx._bloom.tint.Override(new Color(0.9f, 0.95f, 1f));
        }

        // URP's own ranges: saturation/contrast clamp to [-100, 100]; postExposure is
        // unbounded EV, clamped here to +-4 stops so a typo cannot black out a map.
        public static void EnableGrade(float saturation, float contrast, float postExposure, Color filter)
        {
            var fx = Ensure();
            if (fx._grade != null) return;
            fx._grade = fx._profile.Add<UnityEngine.Rendering.Universal.ColorAdjustments>(true);
            fx._grade.saturation.Override(Mathf.Clamp(saturation, -100f, 100f));
            fx._grade.contrast.Override(Mathf.Clamp(contrast, -100f, 100f));
            fx._grade.postExposure.Override(Mathf.Clamp(postExposure, -4f, 4f));
            fx._grade.colorFilter.Override(filter);
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
        void OnDestroy()
        {
            if (_inst == this) _inst = null;
            if (_profile != null) Destroy(_profile);
        }
    }
}
