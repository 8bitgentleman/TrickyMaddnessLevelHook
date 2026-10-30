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
        static void EnableBloom(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
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
                    float bi = 0.7f, bt = 0.9f, parsed;
                    // ⚠️ Parse into a scratch float, NOT straight into bi/bt. TryParse
                    // zeroes its out param on failure, so passing bi directly turned a
                    // malformed "Bloom_x_y" into intensity 0 — bloom silently OFF — while
                    // the log line below cheerfully reported the 0 as if authored.
                    if (seg.Length >= 2)
                    {
                        if (float.TryParse(seg[1], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) bi = parsed;
                        else Log.LogDebug($"[PostFX] '{t.name}' has no parseable intensity -> default {bi:F2}");
                    }
                    if (seg.Length >= 3)
                    {
                        if (float.TryParse(seg[2], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) bt = parsed;
                        else Log.LogDebug($"[PostFX] '{t.name}' has no parseable threshold -> default {bt:F2}");
                    }
                    PostFX.Enable(bi, bt);
                    Log.LogInfo($"[PostFX] bloom enabled (intensity {bi:F2}, threshold {bt:F2})");
                    LoadClock.Mark("PostFX");
                }
                if (PostFX.Active) break;
            }
            blocks.Add($"bloom={(PostFX.Active ? 1 : 0)}");
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
}
