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
    // SSX 3's per-zone depth fog and ScreenTint: WorldPainter slots 5 and 7, emitted by
    // ssx/ssx3_painter.py as the plain-text sidecar "<map>.painter.txt" (HOOK_NOTES §37).
    // Two independent zone maps (fog zones and tint zones are separate quadtrees in the
    // data). Each frame the camera's raw (x, y) picks one zone per slot and the record eases
    // toward it by k0² per 60 Hz frame, first sample snapping: Ssx3SnowFall's query, same data
    // family. Off every cell the last value HOLDS (a guess: the weather query resets to its
    // ctor defaults there, but neither slot's defaults are decoded).
    //
    // FOG is an approximation, not the game's curve. SSX 3 fogs with a full-screen sprite pass
    // reading view depth through a CLUT; seven PS2 GS dumps measured it
    // (ssx/rendering_re/README.md §4.5.1, VERIFIED):
    //   fog(d) = 1 − exp(−D/10 · t),  t = clamp((1/d0 − 1/d) / (1/d0 − 1/Far), 0, 1)
    // with d0 ≈ 6,120 raw the pass's Z cutoff (far clip 30,000) — the zone's Near is inert —
    // so it is 0 inside d0, rises in 1/d, and CAPS at 1 − e^(−D/10) from Far on, sky
    // included. Unity fog always reaches 100% and has no 1/d mode; Exp/Exp2 fog from d = 0
    // and would fog the rider. So Linear from max(Near, 6000)·S, reaching the cap at the
    // PS2's far clip (30,000 raw; [Visual] Ssx3FogReach overrides that distance): the
    // least-squares optimum over the PS2's draw range among lines that never fog nearer than
    // d0 and never exceed the cap anywhere the PS2 drew (HOOK_NOTES §37). The price is
    // everything TM draws past the PS2's far clip, the skydome included, running on to 100%.
    //
    // TINT is the verified operator out = clamp(fb·Tint) + 0.5·Fill, in display (gamma) space,
    // done with URP's colour grading since the Hook can ship no shader (§4): ColorAdjustments
    // .colorFilter = Tint (URP linearises it, so the multiply is the gamma-space multiply to
    // sRGB-curve accuracy), and the additive Fill is FITTED with LiftGammaGain — per channel a
    // gain, lift and gamma chosen so black, mid-grey and (1 − fill) land exactly; the worst
    // residual is 0.012 at Fill 0.15 and 0.021 at Fill 0.3 (dark greys). Identity zones set
    // the volume's weight to 0, as the game skips the pass.
    public class Ssx3Painter : MonoBehaviour
    {
        public static bool Active, OwnsFog;
        // The sky's share of the fog, read by SkydomeFollow for the unfogged clear colour above
        // the dome's open rim (the game fogs its sky at the full cap; Unity fogs the dome mesh
        // itself but never the clear colour). 0 = no painter fog.
        public static float SkyFogAmount;
        public static Color SkyFogColor;
        static Ssx3Painter _inst;
        const float Bucket = 8192f;            // raw units per lookup-grid bucket
        const float FogZCutoffRaw = 6000f;     // d0 ≈ 6,120 raw (farclip 30k) / 5,550 (20k), rounded
        const float PsFarClipRaw = 30000f;     // 20,000 in two forest dumps, cause unknown (README §4.5.1)

        class Slot
        {
            public string kind;
            public float[][] zones;            // 7 floats each, k0..k6
            public float[] x0, y0, size;
            public int[] zone;
            public Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
            public int lastCell = -1, lastZone = -2;
            public readonly float[] acc = new float[7];
            public bool seeded;
        }

        Slot _fog, _tint;
        float _s, _reach;
        int _fogLogged = -2;
        Transform _camT;
        bool _saved, _origFog;
        FogMode _origMode;
        Color _origColor;
        float _origDen, _origStart, _origEnd;
        UnityEngine.Rendering.Volume _vol;
        UnityEngine.Rendering.Universal.ColorAdjustments _ca;
        UnityEngine.Rendering.Universal.LiftGammaGain _lgg;
        readonly float[] _fitFill = { -1f, -1f, -1f };

        static long Key(int bx, int by) => ((long)bx << 32) ^ (uint)by;

        // Reads the sidecar. Refuses the whole file on any malformed line: a partly-read zone
        // table would silently paint one zone's fog over another's ground.
        public static bool TrySpawn(string path, float worldScale, float reach, Scene scene, out string why)
        {
            if (_inst != null) DestroyImmediate(_inst.gameObject);
            var inv = CultureInfo.InvariantCulture;
            var zones = new Dictionary<string, List<float[]>> { { "fog", new List<float[]>() }, { "tint", new List<float[]>() } };
            var cells = new Dictionary<string, List<float[]>> { { "fog", new List<float[]>() }, { "tint", new List<float[]>() } };
            var off = new HashSet<string>();
            int version = -1, lineNo = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                lineNo++;
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var tok = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                switch (tok[0])
                {
                    case "painter":
                        if (tok.Length != 2 || !int.TryParse(tok[1], out version)) { why = $"line {lineNo}: bad 'painter' header"; return false; }
                        break;
                    case "off":
                        if (tok.Length != 2 || (tok[1] != "fog" && tok[1] != "tint")) { why = $"line {lineNo}: bad 'off' line"; return false; }
                        off.Add(tok[1]);
                        break;
                    case "region":
                        break;
                    case "fog":
                    case "tint":
                    {
                        // <kind> <i> <REGION> <zone> k0..k6
                        var k = new float[7];
                        if (tok.Length != 11 || !int.TryParse(tok[1], out int i) || i != zones[tok[0]].Count)
                        { why = $"line {lineNo}: bad or out-of-order '{tok[0]}' record"; return false; }
                        for (int j = 0; j < 7; j++)
                            if (!float.TryParse(tok[4 + j], NumberStyles.Float, inv, out k[j]))
                            { why = $"line {lineNo}: '{tok[0]}' field k{j} unparseable"; return false; }
                        zones[tok[0]].Add(k);
                        break;
                    }
                    case "fogcell":
                    case "tintcell":
                    {
                        var c = new float[4];
                        if (tok.Length != 5) { why = $"line {lineNo}: bad '{tok[0]}'"; return false; }
                        for (int j = 0; j < 4; j++)
                            if (!float.TryParse(tok[1 + j], NumberStyles.Float, inv, out c[j]))
                            { why = $"line {lineNo}: '{tok[0]}' field {j} unparseable"; return false; }
                        cells[tok[0] == "fogcell" ? "fog" : "tint"].Add(c);
                        break;
                    }
                    default:
                        why = $"line {lineNo}: unknown record '{tok[0]}'";
                        return false;
                }
            }
            if (version != 1) { why = $"painter version {version}, this Hook reads 1"; return false; }
            if (worldScale <= 0f) { why = $"world scale {worldScale}"; return false; }

            var slots = new Dictionary<string, Slot>();
            foreach (var kind in new[] { "fog", "tint" })
            {
                if (off.Contains(kind) || zones[kind].Count == 0 || cells[kind].Count == 0) continue;
                var sl = new Slot { kind = kind, zones = zones[kind].ToArray() };
                int n = cells[kind].Count;
                sl.x0 = new float[n]; sl.y0 = new float[n]; sl.size = new float[n]; sl.zone = new int[n];
                for (int i = 0; i < n; i++)
                {
                    var c = cells[kind][i];
                    int z = (int)c[0];
                    if (z < 0 || z >= sl.zones.Length || c[3] <= 0f) { why = $"{kind}cell {i} names zone {z} of {sl.zones.Length}"; return false; }
                    sl.zone[i] = z; sl.x0[i] = c[1]; sl.y0[i] = c[2]; sl.size[i] = c[3];
                    int bx0 = Mathf.FloorToInt(c[1] / Bucket), bx1 = Mathf.FloorToInt((c[1] + c[3]) / Bucket);
                    int by0 = Mathf.FloorToInt(c[2] / Bucket), by1 = Mathf.FloorToInt((c[2] + c[3]) / Bucket);
                    for (int bx = bx0; bx <= bx1; bx++)
                        for (int by = by0; by <= by1; by++)
                        {
                            long key = Key(bx, by);
                            if (!sl.grid.TryGetValue(key, out var list)) sl.grid[key] = list = new List<int>();
                            list.Add(i);
                        }
                }
                slots[kind] = sl;
            }
            string offNote = off.Count > 0 ? $"; off by course: {string.Join(",", off)}" : "";
            if (slots.Count == 0) { why = $"nothing to paint{offNote}"; return false; }

            var go = new GameObject("HookSsx3Painter");
            if (scene.IsValid() && scene.isLoaded) SceneManager.MoveGameObjectToScene(go, scene);
            var p = go.AddComponent<Ssx3Painter>();
            p._s = worldScale / 100f;          // SSXImporter.P: S = scale / 100
            p._reach = reach;
            slots.TryGetValue("fog", out p._fog);
            slots.TryGetValue("tint", out p._tint);
            if (p._tint != null) p.BuildVolume();
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += p.OnBeginCamera;
            _inst = p;
            Active = true;
            OwnsFog = p._fog != null;
            why = (p._fog != null ? $"fog {p._fog.zones.Length} zone(s)/{p._fog.zone.Length} cell(s)" : "fog off")
                + ", " + (p._tint != null ? $"tint {p._tint.zones.Length} zone(s)/{p._tint.zone.Length} cell(s)" : "tint off")
                + $", S {p._s}, fog reaches its cap at {(reach > 0f ? reach.ToString("F0", inv) + " u ([Visual] Ssx3FogReach)" : (PsFarClipRaw * p._s).ToString("F0", inv) + " u (PS2 far clip)")}{offNote}";
            return true;
        }

        void BuildVolume()
        {
            _vol = gameObject.AddComponent<UnityEngine.Rendering.Volume>();
            _vol.isGlobal = true;
            _vol.priority = 101f;              // above PostFX's bloom volume (100)
            _vol.weight = 0f;                  // nothing until the first sample
            var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
            _vol.sharedProfile = profile;
            _ca = profile.Add<UnityEngine.Rendering.Universal.ColorAdjustments>(true);
            _ca.colorFilter.Override(Color.white);
            _lgg = profile.Add<UnityEngine.Rendering.Universal.LiftGammaGain>(true);
            _lgg.lift.Override(new Vector4(1f, 1f, 1f, 0f));
            _lgg.gamma.Override(new Vector4(1f, 1f, 1f, 0f));
            _lgg.gain.Override(new Vector4(1f, 1f, 1f, 0f));
        }

        void OnBeginCamera(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == null || !cam.CompareTag("MainCamera")) return;
            _camT = cam.transform;
            if (_tint == null) return;
            var data = cam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            if (data != null && !data.renderPostProcessing) data.renderPostProcessing = true;
        }

        int ZoneAt(Slot sl, float rx, float ry)
        {
            int c = sl.lastCell;
            if (c >= 0 && rx >= sl.x0[c] && rx < sl.x0[c] + sl.size[c] && ry >= sl.y0[c] && ry < sl.y0[c] + sl.size[c])
                return sl.zone[c];
            if (!sl.grid.TryGetValue(Key(Mathf.FloorToInt(rx / Bucket), Mathf.FloorToInt(ry / Bucket)), out var list))
                return -1;
            foreach (int i in list)
                if (rx >= sl.x0[i] && rx < sl.x0[i] + sl.size[i] && ry >= sl.y0[i] && ry < sl.y0[i] + sl.size[i])
                {
                    sl.lastCell = i;
                    return sl.zone[i];
                }
            return -1;
        }

        // acc += w²·(target − acc) per 60 Hz frame, w = −k0 of the TARGET zone (weather's rule).
        static bool Blend(Slot sl, int zone, float dt)
        {
            if (zone != sl.lastZone)
            {
                if (zone >= 0)
                    Plugin.Log.LogInfo($"[Painter] {sl.kind} zone {sl.lastZone} -> {zone}: " +
                        string.Join(" ", Array.ConvertAll(sl.zones[zone], v => v.ToString("0.###", CultureInfo.InvariantCulture))));
                sl.lastZone = zone;
            }
            if (zone < 0) return sl.seeded;    // off the map: hold
            var t = sl.zones[zone];
            if (!sl.seeded) { Array.Copy(t, sl.acc, 7); sl.seeded = true; return true; }
            float w2 = t[0] * t[0];
            float a = 1f - Mathf.Pow(1f - Mathf.Clamp01(w2), dt * 60f);
            for (int k = 1; k < 7; k++) sl.acc[k] += a * (t[k] - sl.acc[k]);
            sl.acc[0] = t[0];
            return true;
        }

        void LateUpdate()
        {
            Transform cam = _camT;
            if (cam == null) { var c = Camera.main; if (c == null) return; cam = c.transform; }
            float rx = cam.position.x / _s, ry = cam.position.z / _s;   // P() = (x·S, z·S, y·S)
            float dt = Time.deltaTime;
            if (_fog != null && Blend(_fog, ZoneAt(_fog, rx, ry), dt))
            {
                if (_fog.lastZone >= 0 && _fog.lastZone != _fogLogged)
                {
                    // The zone's steady state; the eased record passes through blends of two.
                    _fogLogged = _fog.lastZone;
                    if (FogMap(_fog.zones[_fogLogged], out float st, out float en, out float cp))
                        Plugin.Log.LogInfo($"[Painter] fog zone {_fogLogged} -> Unity Linear start {st:F0} end {en:F0} " +
                                           $"(cap {cp:F3} at {st + cp * (en - st):F0} u)");
                    else
                        Plugin.Log.LogInfo($"[Painter] fog zone {_fogLogged} -> fog off (Density 0)");
                }
                ApplyFog(_fog.acc);
            }
            if (_tint != null && Blend(_tint, ZoneAt(_tint, rx, ry), dt)) ApplyTint(_tint.acc);
        }

        // The measured curve's knee is d0 (Near is inert: README §4.5.1), so the line starts at
        // max(Near, 6000)·S — max() keeps a Near past d0 (two shipped zones have 7000), which
        // the game would honour too since its index would then fall inside the table (judgment,
        // unmeasured). It reaches the cap at `reach` = the PS2 far clip unless the user trims it,
        // never nearer than the zone's own Far (the game plateaus there) nor inside the start.
        bool FogMap(float[] k, out float start, out float end, out float cap)
        {
            cap = 1f - Mathf.Exp(-k[1] / 10f);
            start = Mathf.Max(k[2], FogZCutoffRaw) * _s;
            float reach = _reach > 0f ? _reach : PsFarClipRaw * _s;
            reach = Mathf.Max(reach, k[3] * _s, start + 1f);
            end = start + (reach - start) / Mathf.Max(cap, 1e-4f);
            return cap >= 1e-4f;             // Density 0: the game skips the pass
        }

        void ApplyFog(float[] k)
        {
            if (!_saved)
            {
                _saved = true;
                _origFog = RenderSettings.fog; _origMode = RenderSettings.fogMode; _origColor = RenderSettings.fogColor;
                _origDen = RenderSettings.fogDensity; _origStart = RenderSettings.fogStartDistance; _origEnd = RenderSettings.fogEndDistance;
            }
            if (!FogMap(k, out float start, out float end, out float cap))
            {
                RenderSettings.fog = false;
                SkyFogAmount = 0f;
                return;
            }
            var col = new Color(k[4], k[5], k[6], 1f);   // CLUT = int(c·255), gamma space
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = start;
            RenderSettings.fogEndDistance = end;
            RenderSettings.fogColor = col;
            SkyFogAmount = cap;
            SkyFogColor = col;
        }

        void ApplyTint(float[] k)
        {
            bool ident = true;
            for (int c = 0; c < 3; c++)
                if (Mathf.Abs(k[1 + c] - 1f) > 1e-3f || k[4 + c] > 1e-3f) ident = false;
            _vol.weight = ident ? 0f : 1f;
            if (ident) return;
            _ca.colorFilter.Override(new Color(k[1], k[2], k[3], 1f));
            bool changed = false;
            for (int c = 0; c < 3; c++) if (Mathf.Abs(_fitFill[c] - k[4 + c]) > 1e-4f) changed = true;
            if (!changed) return;
            var G = new float[3]; var L = new float[3]; var P = new float[3];
            for (int c = 0; c < 3; c++)
            {
                _fitFill[c] = k[4 + c];
                FitFill(0.5f * k[4 + c], out G[c], out L[c], out P[c]);
            }
            _lgg.lift.Override(Pack(L, 0f, 0.15f));
            _lgg.gain.Override(Pack(G, 1f, 0.8f));
            var a = new float[3];
            for (int c = 0; c < 3; c++) a[c] = 1f / P[c];
            _lgg.gamma.Override(Pack(a, 1f, 0.8f));
        }

        // Linear-space c·G + L then ^p, per channel, standing in for gamma-space c + f.
        // Anchors: black -> f, 0.5 -> 0.5 + f, (1 − f) -> 1. Converges in < 12 iterations.
        static void FitFill(float f, out float G, out float L, out float p)
        {
            f = Mathf.Clamp(f, 0f, 0.45f);
            G = 1f; L = 0f; p = 1f;
            if (f < 1e-5f) return;
            float gf = Mathf.GammaToLinearSpace(f), g1 = Mathf.GammaToLinearSpace(1f - f);
            float gh = Mathf.GammaToLinearSpace(0.5f), ghf = Mathf.GammaToLinearSpace(0.5f + f);
            for (int it = 0; it < 12; it++)
            {
                L = Mathf.Pow(gf, 1f / p);
                G = (1f - L) / g1;
                p = Mathf.Log(ghf) / Mathf.Log(G * gh + L);
            }
        }

        // Inverts URP's ColorUtils.PrepareLiftGammaGain (IL-verified): it takes
        // xyz' = GammaToLinear(xyz)·scale, then xyz' − Luminance(xyz') + w (+1 for gain and
        // gamma, and gamma is then reciprocated — pass 1/p). We want `want − bias` exactly per
        // channel: put the per-channel spread in xyz and the common part in w.
        static Vector4 Pack(float[] want, float bias, float scale)
        {
            float a0 = want[0] - bias, a1 = want[1] - bias, a2 = want[2] - bias;
            float m = Mathf.Min(a0, Mathf.Min(a1, a2));
            float w = 0.2126729f * a0 + 0.7151522f * a1 + 0.072175f * a2;
            return new Vector4(Mathf.LinearToGammaSpace((a0 - m) / scale),
                               Mathf.LinearToGammaSpace((a1 - m) / scale),
                               Mathf.LinearToGammaSpace((a2 - m) / scale), w);
        }

        void OnDestroy()
        {
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            if (_saved)
            {
                RenderSettings.fog = _origFog; RenderSettings.fogMode = _origMode; RenderSettings.fogColor = _origColor;
                RenderSettings.fogDensity = _origDen; RenderSettings.fogStartDistance = _origStart; RenderSettings.fogEndDistance = _origEnd;
            }
            if (_inst == this) { _inst = null; Active = false; OwnsFog = false; SkyFogAmount = 0f; }
        }
    }
}
