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
        // Per-level sky-fill override, baked into the bundle by SSXImporter as an empty
        // marker GO named "SkyFill_<RRGGBB>" (from Overrides.json lighting.skyFillColor).
        // Parsed in LoadLevelManager before SkydomeFollow attaches; wins over the global
        // [Skydome] FillColor config wherever the Hook clears the camera. Null = no
        // override on the active map.
        internal static Color? SceneSkyFill;

        static void ReadSkyFill(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
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
                        LoadClock.Mark("Skydome");
                    }
                    else
                        Log.LogWarning($"[Skydome] sky-fill marker '{t.name}' not parseable; using [Skydome] FillColor");
                }
            }
            blocks.Add($"skyfill={(SceneSkyFill.HasValue ? 1 : 0)}");
        }

        static void AttachSkydomeFollow(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
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
            blocks.Add($"skydome={skydomes}");
            LoadClock.Mark("Skydome");
        }
    }

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

        // Re-point the clear color mid-map (LightingRamp, on chain maps whose sky goes from
        // day to night as you descend). It has to come through here rather than being written
        // straight onto the camera: OnBeginCamera re-asserts _fill every frame, so an external
        // per-frame write would just be fought and the two would flicker against each other.
        public void SetFill(Color c) { _fill = c; _fillValid = true; }

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

            if (_fillValid)
            {
                // SSX 3 per-zone fog (§37): the game fogs its sky at the zone's full cap, and
                // Unity fogs the dome mesh but never the clear colour above its open rim. The
                // PS2 blends in display space, so lerp the gamma-space colours.
                Color fill = Ssx3Painter.SkyFogAmount > 0f
                    ? Color.Lerp(_fill, Ssx3Painter.SkyFogColor, Ssx3Painter.SkyFogAmount) : _fill;
                if (cam.clearFlags != CameraClearFlags.SolidColor || cam.backgroundColor != fill)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = fill;
                }
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
}
