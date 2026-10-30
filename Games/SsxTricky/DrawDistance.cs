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
        static partial void AttachDrawDistance(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
            // Per-level camera draw distance. Naming contract with SSXImporter.BuildLighting:
            // an empty marker GO named "DrawDist_<unity units>" (from Overrides.json
            // lighting.drawDistanceRaw — SSX Tricky's per-course WorldConf terrain cull
            // distance, ssx/NOTES.md). The component lives on the marker, so it dies with
            // the level scene and restores the camera's far clip on the way out. MUST attach
            // before SkydomeFollow below: both hook beginCameraRendering, and subscription
            // order is call order, so the dome fits to the clipped far plane from frame one.
            int drawDists = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("DrawDist_")) continue;
                    if (t.GetComponent<DrawDistance>() != null) continue;
                    if (float.TryParse(t.name.Substring("DrawDist_".Length), NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out float far) && far > 1f)
                    {
                        t.gameObject.AddComponent<DrawDistance>().far = far;
                        drawDists++;
                        Log.LogInfo($"[DrawDist] per-level camera far clip {far:F1}");
                        LoadClock.Mark("DrawDist");
                    }
                    else
                        Log.LogWarning($"[DrawDist] marker '{t.name}' not parseable; far clip left alone");
                }
            }
            blocks.Add($"drawdist={drawDists}");
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
    // Clamps the main camera's far clip to a per-level draw distance (DrawDist_ marker).
    // SSX Tricky drops terrain past a hardcoded per-course distance instead of fogging it
    // (ssx/NOTES.md § Draw distance), which is what keeps Mesablanca's upper sections out of
    // sight from the rails below them; TM's stock 2500 shows them. Unity clips per pixel
    // where the PS2 drops whole .ltg cells, so the edge differs slightly — accepted.
    // Re-asserted every frame (the game may reconfigure its camera), original restored
    // on disable.
    public class DrawDistance : MonoBehaviour
    {
        public float far;
        Camera _cam;
        float _orig;

        void OnEnable()  { UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += OnBeginCamera; }
        void OnDisable()
        {
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            if (_cam != null) _cam.farClipPlane = _orig;
            _cam = null;
        }

        void OnBeginCamera(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == null || !cam.CompareTag("MainCamera")) return;
            if (cam != _cam)
            {
                if (_cam != null) _cam.farClipPlane = _orig;
                _cam = cam;
                _orig = cam.farClipPlane;
                Plugin.Log.LogInfo($"[DrawDist] camera '{cam.name}' far clip {_orig:F0} -> {far:F1}");
            }
            if (cam.farClipPlane != far) cam.farClipPlane = far;
        }
    }
}
