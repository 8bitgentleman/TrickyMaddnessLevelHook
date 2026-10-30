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
        static void SpawnLightingRamp(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
            // Time-of-day ramp. Naming contract with SSXImporter.BuildLightStops: empty GOs
            // "LightStop_<region>_<y>_<sunI>_<sunHex>_<ambR>_<ambG>_<ambB>_<fillHex>_
            // <fogHex>_<fogDen>_<terrHex>", one per source course of a whole-mountain chain.
            // A Unity scene bakes ONE lighting grade; SSX 3's final descent is daylight at
            // the summit and night by Metro-City, so LightingRamp blends between the stops
            // by rider elevation. Needs at least two — one stop is just the baked grade.
            // Attached after the skydome and terrain exist, because it drives their
            // materials.
            int rampStops = -1;   // -1 = [Visual] LightingRamp off
            if (lightingRamp == null || lightingRamp.Value)
            {
                var stops = new List<LightingRamp.Stop>();
                int badStops = 0;
                foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
                    foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                    {
                        if (!t.name.StartsWith("LightStop_")) continue;
                        LightingRamp.Stop s;
                        if (LightingRamp.TryParse(t.name, out s)) stops.Add(s);
                        else
                        {
                            badStops++;
                            Log.LogWarning($"[LightingRamp] marker '{t.name}' is malformed; ignored");
                        }
                    }
                // A partly-parsed ramp is worse than none: it would blend across the stops
                // that dropped out, so a whole course's stretch of mountain silently takes
                // its neighbours' grade. Refuse the whole thing and say so.
                if (badStops > 0 && stops.Count > 0)
                {
                    Log.LogWarning($"[LightingRamp] {badStops} of {stops.Count + badStops} stop marker(s) "
                        + "unreadable — ramp DISABLED; the map keeps its single baked grade. Rebuild it.");
                }
                else if (stops.Count >= 2)
                {
                    LightingRamp.Spawn(stops);
                }
                else if (stops.Count == 1)
                {
                    Log.LogWarning("[LightingRamp] only 1 stop marker found; a ramp needs 2 to "
                        + "interpolate, so the map keeps its single baked grade.");
                }
                rampStops = stops.Count;
            }
            blocks.Add($"ramp={rampStops}");
        }
    }

    // Time-of-day ramp for whole-mountain chain maps.
    //
    // A Unity scene bakes exactly ONE lighting grade. That is right for a single course and
    // wrong for a chain: SSX 3's Peak-to-Valley descent starts in daylight at the summit and
    // is night by the time you drop into Metro-City, and the merge that assembles a chain can
    // only carry one of its five source courses' grades. So the importer bakes every source
    // course's grade as a "LightStop_*" marker (with the anchor elevation measured off that
    // course's own terrain), and we interpolate between them here by the rider's height.
    //
    // Elevation, not distance-along-the-race-line: the chain is a 26,000-unit vertical drop
    // and height is monotonic over it, so it needs no race-line lookup, behaves sanely when
    // the rider is off-piste or respawning, and cannot desync from the geometry.
    //
    // Five channels move, all of them absolute writes of the sampled grade (never accumulating
    // deltas, so a dropped frame or a respawn can't drift the look):
    //   sun light, RenderSettings ambient trilight, RenderSettings fog, the skydome, and the
    //   terrain material tint.
    // ⚠️ The terrain sweep matches "terr_"/"terrlit_" ONLY. The lightmap-overlay materials
    //   (LightmapOverlay_<ID>) also carry a _BaseColor, but theirs is a load-bearing (2,2,2,1)
    //   multiplier — writing a tint there would blow out or kill every baked shadow.
    public class LightingRamp : MonoBehaviour
    {
        public struct Stop
        {
            public string region;
            public float y;          // anchor elevation, Unity units (measured at import)
            public float sunI;
            public Color sunC;
            public Color amb;        // resolved trilight colour, may exceed 1 per channel
            public Color fill;       // camera clear / sky fill
            public Color fog;
            public float fogDen;
            public Color terr;       // terrain material tint
            // Linear-fog support (Avalanche is GX_FOG_PERSP_LIN on every course; exp2 has no
            // faithful equivalent — see avalanche/FORMAT_RE.md §20). "exp2" (default) keeps
            // today's ExponentialSquared/fogDen behaviour; "linear" switches to
            // fogStart/fogEnd and ignores fogDen. Legacy (12-token) markers parse to "exp2"
            // with fogStart/fogEnd at 0, so an old marker is byte-identical to before.
            public string fogMode;   // "exp2" | "linear"
            public float fogStart;   // world units, linear mode only
            public float fogEnd;     // world units, linear mode only
        }

        List<Stop> _stops;
        Light _sun;
        SkydomeFollow _dome;
        Material[] _domeMats = new Material[0];
        Material[] _terrMats = new Material[0];
        Color[] _domeBase = new Color[0];   // each dome material's own baked _BaseColor
        Color _fillRef = Color.white;       // stop 0's fill — the dome tint's unity point
        bool _anyFog;

        Transform _anchor;
        int _rescan;
        float _y;
        int _logCountdown;

        public static void Spawn(List<Stop> stops)
        {
            stops.Sort((a, b) => b.y.CompareTo(a.y));   // top of the mountain first
            var go = new GameObject("HookLightingRamp");
            go.AddComponent<LightingRamp>().Init(stops);
            var names = new string[stops.Count];
            for (int i = 0; i < stops.Count; i++) names[i] = $"{stops[i].region}@{stops[i].y:F0}";
            Plugin.Log.LogInfo($"[LightingRamp] {stops.Count} stops, {string.Join(" -> ", names)}");
        }

        void Init(List<Stop> stops)
        {
            _stops = stops;
            _y = stops[0].y;            // hold the summit grade until the rider exists
            _fillRef = stops[0].fill;

            _sun = RenderSettings.sun;
            if (_sun == null)
            {
                // The importer names it "Sun" and assigns RenderSettings.sun, but the sun
                // reference is a scene setting and a bundle load has been known to drop
                // scene settings; fall back to the only directional light on the map.
                foreach (var l in FindObjectsOfType<Light>())
                    if (l.type == LightType.Directional) { _sun = l; break; }
                if (_sun == null)
                    Plugin.Log.LogWarning("[LightingRamp] no directional light found — the sun will not ramp "
                        + "(ambient, fog, sky and terrain still will).");
            }

            var domeMats = new List<Material>();
            var terrMats = new List<Material>();
            foreach (var r in FindObjectsOfType<MeshRenderer>())
            {
                bool underDome = r.GetComponentInParent<SkydomeFollow>() != null;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    if (underDome) { if (!domeMats.Contains(m)) domeMats.Add(m); }
                    // Prefix match, not equality: the importer names these per texture
                    // ("terrlit_<texstem>"), lit and unlit variants alike. It deliberately
                    // does NOT match LightmapOverlay_* — see the class comment.
                    else if (m.name.StartsWith("terr_") || m.name.StartsWith("terrlit_"))
                    { if (!terrMats.Contains(m)) terrMats.Add(m); }
                }
            }
            _domeMats = domeMats.ToArray();
            _terrMats = terrMats.ToArray();
            _domeBase = new Color[_domeMats.Length];
            for (int i = 0; i < _domeMats.Length; i++)
                _domeBase[i] = _domeMats[i].HasProperty("_BaseColor")
                    ? _domeMats[i].GetColor("_BaseColor") : Color.white;

            var domes = FindObjectsOfType<SkydomeFollow>();
            if (domes.Length > 0) _dome = domes[0];

            foreach (var s in _stops)
                if (s.fogDen > 0f || (s.fogMode == "linear" && s.fogEnd > 0f)) { _anyFog = true; break; }

            Plugin.Log.LogInfo($"[LightingRamp] driving sun={(_sun != null ? _sun.name : "none")}, "
                + $"{_domeMats.Length} skydome material(s), {_terrMats.Length} terrain material(s)");
        }

        // The rider is the anchor, not the camera: the camera on these maps swings wide on
        // big airs and Camera.main is frequently null here anyway. Re-found lazily (once
        // every 30 frames at most) because the Snowboarder does not exist on the first frames
        // of the scene and is replaced on some respawn paths.
        float AnchorY()
        {
            if (_anchor == null)
            {
                if (--_rescan > 0) return _y;
                _rescan = 30;
                var sbs = FindObjectsOfType<Snowboarder>();
                if (sbs.Length > 0) _anchor = sbs[0].transform;
                else { var c = Camera.main; if (c != null) _anchor = c.transform; }
                if (_anchor == null) return _y;
            }
            return _anchor.position.y;
        }

        // Linear blend between the two stops bracketing y. Above the top stop and below the
        // bottom one it clamps, so leaving the ramp's vertical range holds that end's grade
        // rather than extrapolating into colours nobody authored.
        Stop Sample(float y)
        {
            if (y >= _stops[0].y) return _stops[0];
            int last = _stops.Count - 1;
            if (y <= _stops[last].y) return _stops[last];
            for (int i = 0; i < last; i++)
            {
                Stop a = _stops[i], b = _stops[i + 1];
                if (y > b.y)
                {
                    float span = a.y - b.y;
                    // Two stops at the same elevation would divide by zero; treat as a step.
                    float t = span > 0.001f ? Mathf.Clamp01((a.y - y) / span) : 1f;
                    return new Stop
                    {
                        region = t < 0.5f ? a.region : b.region,
                        y = y,
                        sunI = Mathf.Lerp(a.sunI, b.sunI, t),
                        sunC = Color.Lerp(a.sunC, b.sunC, t),
                        amb = Color.Lerp(a.amb, b.amb, t),
                        fill = Color.Lerp(a.fill, b.fill, t),
                        fog = Color.Lerp(a.fog, b.fog, t),
                        fogDen = Mathf.Lerp(a.fogDen, b.fogDen, t),
                        terr = Color.Lerp(a.terr, b.terr, t),
                        // Mode is discrete, like region — picked from the nearer stop rather
                        // than blended; start/end lerp like every other float channel.
                        fogMode = t < 0.5f ? a.fogMode : b.fogMode,
                        fogStart = Mathf.Lerp(a.fogStart, b.fogStart, t),
                        fogEnd = Mathf.Lerp(a.fogEnd, b.fogEnd, t),
                    };
                }
            }
            return _stops[last];
        }

        // LateUpdate, not OnBeginCamera: everything written here is read during URP's per-
        // camera setup, and LateUpdate is guaranteed to have finished by then. (SkydomeFollow
        // uses OnBeginCamera because it needs the render camera's own transform, which we
        // don't.)
        void LateUpdate()
        {
            if (_stops == null || _stops.Count < 2) return;
            _y = AnchorY();
            var g = Sample(_y);

            if (_sun != null) { _sun.intensity = g.sunI; _sun.color = g.sunC; }

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = g.amb;
            RenderSettings.ambientEquatorColor = g.amb;
            RenderSettings.ambientGroundColor = g.amb;

            // Ssx3Painter owns fog on an SSX 3 map whose sidecar carries per-zone fog (the
            // chain has both); the ramp keeps sun, ambient, dome and terrain.
            if (_anyFog && !GameFog.Owned)
            {
                bool linear = g.fogMode == "linear";
                RenderSettings.fog = linear ? g.fogEnd > 0f : g.fogDen > 0f;
                RenderSettings.fogMode = linear ? FogMode.Linear : FogMode.ExponentialSquared;
                if (linear)
                {
                    RenderSettings.fogStartDistance = g.fogStart;
                    RenderSettings.fogEndDistance = g.fogEnd;
                }
                else
                {
                    RenderSettings.fogDensity = g.fogDen;
                }
                RenderSettings.fogColor = g.fog;
            }

            if (_dome != null) _dome.SetFill(g.fill);

            // The dome is a photographic daylight panorama, so its tint is RELATIVE to the
            // top stop's sky fill, not the fill itself: at the summit the ratio is 1 and the
            // dome renders exactly as the bundle bakes it, and it darkens/cools from there
            // (Metro-City's fill is ~40% of the summit's, which is the dusk the map wants).
            // An absolute write would repaint the daylight sky the instant the map loaded.
            if (_domeMats.Length > 0)
            {
                Color k = new Color(
                    _fillRef.r > 0.001f ? Mathf.Clamp(g.fill.r / _fillRef.r, 0f, 2f) : 1f,
                    _fillRef.g > 0.001f ? Mathf.Clamp(g.fill.g / _fillRef.g, 0f, 2f) : 1f,
                    _fillRef.b > 0.001f ? Mathf.Clamp(g.fill.b / _fillRef.b, 0f, 2f) : 1f,
                    1f);
                for (int i = 0; i < _domeMats.Length; i++)
                {
                    var b = _domeBase[i];
                    _domeMats[i].SetColor("_BaseColor",
                        new Color(b.r * k.r, b.g * k.g, b.b * k.b, b.a));
                }
            }

            // Terrain tint IS absolute — the importer already writes each stop's terrainTint
            // into _BaseColor, so the top stop reproduces the baked value exactly.
            for (int i = 0; i < _terrMats.Length; i++)
                _terrMats[i].SetColor("_BaseColor", g.terr);

            if (--_logCountdown <= 0)
            {
                _logCountdown = 600;   // ~10 s at 60 fps; enough to read the ramp in a log
                Plugin.Log.LogDebug($"[LightingRamp] y={_y:F0} near={g.region} sun={g.sunI:F2} "
                    + $"fog=#{ColorUtility.ToHtmlStringRGB(g.fog)}[{g.fogMode}]@{(g.fogMode == "linear" ? $"{g.fogStart:F0}-{g.fogEnd:F0}" : $"{g.fogDen:F4}")} "
                    + $"fill=#{ColorUtility.ToHtmlStringRGB(g.fill)}");
            }
        }

        // "LightStop_<region>_<y>_<sunI>_<sunHex>_<ambR>_<ambG>_<ambB>_<fillHex>_<fogHex>_
        //  <fogDen>_<terrHex>[_<fogMode>_<fogStart>_<fogEnd>]" — 12 tokens for the original
        // exp2-only marker, or 15 with the linear-fog suffix appended. Every channel is fully
        // resolved at import so there is nothing optional to branch on within a given length.
        // Anything short, long, or unparseable is refused rather than defaulted: a stop that
        // silently reads as black would be far worse than no ramp, and the caller disables the
        // whole ramp when any stop fails. A 12-token marker (every marker emitted before this
        // feature) parses to fogMode "exp2" with fogStart/fogEnd 0 — today's behaviour exactly.
        public static bool TryParse(string name, out Stop s)
        {
            s = new Stop();
            var t = name.Split('_');
            if (t.Length != 12 && t.Length != 15) return false;
            var ic = CultureInfo.InvariantCulture;
            float y, sunI, ar, ag, ab, fogDen;
            Color sunC, fill, fog, terr;
            if (!float.TryParse(t[2], NumberStyles.Float, ic, out y)) return false;
            if (!float.TryParse(t[3], NumberStyles.Float, ic, out sunI)) return false;
            if (!ColorUtility.TryParseHtmlString("#" + t[4], out sunC)) return false;
            if (!float.TryParse(t[5], NumberStyles.Float, ic, out ar)) return false;
            if (!float.TryParse(t[6], NumberStyles.Float, ic, out ag)) return false;
            if (!float.TryParse(t[7], NumberStyles.Float, ic, out ab)) return false;
            if (!ColorUtility.TryParseHtmlString("#" + t[8], out fill)) return false;
            if (!ColorUtility.TryParseHtmlString("#" + t[9], out fog)) return false;
            if (!float.TryParse(t[10], NumberStyles.Float, ic, out fogDen)) return false;
            if (!ColorUtility.TryParseHtmlString("#" + t[11], out terr)) return false;
            string fogMode = "exp2";
            float fogStart = 0f, fogEnd = 0f;
            if (t.Length == 15)
            {
                fogMode = t[12];
                if (fogMode != "exp2" && fogMode != "linear") return false;
                if (!float.TryParse(t[13], NumberStyles.Float, ic, out fogStart)) return false;
                if (!float.TryParse(t[14], NumberStyles.Float, ic, out fogEnd)) return false;
            }
            s.region = t[1]; s.y = y; s.sunI = sunI; s.sunC = sunC;
            s.amb = new Color(ar, ag, ab, 1f);
            s.fill = fill; s.fog = fog; s.fogDen = fogDen; s.terr = terr;
            s.fogMode = fogMode; s.fogStart = fogStart; s.fogEnd = fogEnd;
            return true;
        }
    }
}
