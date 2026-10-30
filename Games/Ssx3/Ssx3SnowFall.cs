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
    // SSX 3's own weather snow: WorldPainter slot 12 + the 6 SnowBox emitters, decoded in
    // ssx/PARTICLES_PLAN.md §7.1/§7.8. Marker "WeatherSSX3_<S>_<x0>_<y0>_<rawPerPixel>"
    // (SSXImporter.BakeSsx3WeatherMarker) carries the zone map as an R8 lookup texture on a
    // disabled renderer (pixel = zone + 1, 0 = no weather), plus one child per zone,
    // "WxZone_<i>_<k0>_..._<k19>", the 20 raw record floats. Each frame the camera's raw (x, y)
    // picks ONE zone; the 20 fields ease toward it by k0² per 60 Hz frame, snap on the first
    // sample, and reset to the ctor defaults (no snow) off the map — the game's own query.
    // Four flake boxes and two fluff boxes, each a camera-wrapped cube whose flakes share one
    // drifting offset, as SnowBox does. Verified constants are the ELF's; the guesses are
    // marked inline and listed in PARTICLES_PLAN §7.7 Phase 3.
    public class Ssx3SnowFall : MonoBehaviour
    {
        public static bool Active;

        // Box table (§7.1): edge in raw units, flake/fluff layer.
        static readonly float[] BoxEdge = { 1500f, 1500f, 2000f, 3000f, 2000f, 2000f };
        const int FlakeBoxes = 4, Boxes = 6;
        const float FlakeFall = 200f, FluffFall = 100f, FluffSize = 600f;   // raw/s, raw
        const float FlakeWind = 500f, FluffWind = 250f;
        const float FlakePerK1 = 500f, FluffPerK9 = 6f;
        const int MaxFlakesPerBox = 4000, MaxFluffPerBox = 16;              // k1 ≤ 8, k9 ≤ 2.6
        const float FadeFrac = 0.8f;   // guess: fade over the outer 20% of the half-edge

        // Ctor defaults (§7.1), k0 = the "never sampled" sentinel.
        static readonly float[] Defaults =
            { -99999f, 0f, 6f, 0f, 0f, 0f, 10f, 1f, 1f, 0f, 0f, 0f, 1f, 1f, 1f, 1f, 0.06f, 1f, 1f, 1f };

        float _s, _x0, _y0, _px, _sizeScale = 5f;
        int _w, _h;
        byte[] _map;
        float[][] _zones;
        readonly float[] _acc = (float[])Defaults.Clone();
        int _lastZone = -2;

        Transform _cam;
        ParticleSystem _ps;
        ParticleSystem.Particle[] _buf;
        Vector3[][] _base;                     // per box, world-anchored seed positions
        readonly Vector3[] _off = new Vector3[Boxes];
        Vector3 _gustFrom, _gustTo;
        float _gustT, _gustPeriod = 1f;
        bool _seeded;

        public static bool TrySpawn(Transform marker, float sizeScale, out string why)
        {
            var tok = marker.name.Split('_');
            var inv = CultureInfo.InvariantCulture;
            float s = 0f, x0 = 0f, y0 = 0f, px = 0f;
            if (tok.Length != 5
                || !float.TryParse(tok[1], NumberStyles.Float, inv, out s)
                || !float.TryParse(tok[2], NumberStyles.Float, inv, out x0)
                || !float.TryParse(tok[3], NumberStyles.Float, inv, out y0)
                || !float.TryParse(tok[4], NumberStyles.Float, inv, out px)
                || s <= 0f || px <= 0f)
            {
                why = $"'{marker.name}' is not WeatherSSX3_<S>_<x0>_<y0>_<rawPerPixel>; no snow";
                return false;
            }
            var mat = marker.GetComponent<Renderer>()?.sharedMaterial;
            var tex = (mat != null && mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : null) as Texture2D;
            if (tex == null) { why = $"'{marker.name}' carries no zone-map texture; no snow"; return false; }
            byte[] map;
            try { map = tex.GetPixelData<byte>(0).ToArray(); }
            catch (Exception e) { why = $"zone map '{tex.name}' is not CPU-readable ({e.Message}); no snow"; return false; }
            if (map.Length != tex.width * tex.height)
            {
                why = $"zone map '{tex.name}' is {tex.format}, {map.Length} B for {tex.width}x{tex.height} (want R8); no snow";
                return false;
            }

            var zones = new List<float[]>();
            foreach (Transform c in marker)
            {
                var zt = c.name.Split('_');
                if (zt.Length != 22 || zt[0] != "WxZone" || !int.TryParse(zt[1], out int zi) || zi < 0 || zi > 254)
                { why = $"zone child '{c.name}' is malformed; no snow"; return false; }
                var k = new float[20];
                for (int j = 0; j < 20; j++)
                    if (!float.TryParse(zt[j + 2], NumberStyles.Float, inv, out k[j]))
                    { why = $"zone child '{c.name}' field k{j} unparseable; no snow"; return false; }
                while (zones.Count <= zi) zones.Add(null);
                zones[zi] = k;
            }
            int maxIdx = 0;
            foreach (var b in map) if (b > maxIdx) maxIdx = b;
            if (maxIdx > zones.Count || (maxIdx > 0 && zones[maxIdx - 1] == null))
            { why = $"zone map references zone {maxIdx - 1} but the marker carries {zones.Count}; no snow"; return false; }

            var go = new GameObject("HookSsx3SnowFall");
            var sf = go.AddComponent<Ssx3SnowFall>();
            sf._s = s; sf._x0 = x0; sf._y0 = y0; sf._px = px;
            sf._sizeScale = sizeScale;
            sf._w = tex.width; sf._h = tex.height; sf._map = map; sf._zones = zones.ToArray();
            sf.Build();
            Active = true;
            int snowing = 0;
            foreach (var z in zones) if (z != null && (z[1] > 0f || z[9] > 0f)) snowing++;
            why = $"{zones.Count} zone(s), {snowing} snowing; {tex.width}x{tex.height} map at {px} raw/px, S {s}, flake size x{sizeScale:F2}";
            return true;
        }

        void Build()
        {
            _ps = gameObject.AddComponent<ParticleSystem>();
            _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            int max = FlakeBoxes * MaxFlakesPerBox + (Boxes - FlakeBoxes) * MaxFluffPerBox;
            var main = _ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f;
            main.startLifetime = 1e6f;
            main.gravityModifier = 0f;
            main.maxParticles = max;
            main.playOnAwake = false;
            var emission = _ps.emission;
            emission.enabled = false;
            var shape = _ps.shape;
            shape.enabled = false;

            var pr = GetComponent<ParticleSystemRenderer>();
            pr.renderMode = ParticleSystemRenderMode.Billboard;
            pr.alignment = ParticleSystemRenderSpace.View;
            pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pr.receiveShadows = false;
            // Non-additive: shipped blend bits 0x14, additive (0x1c) is a debug toggle (§7.1).
            // 0x14 = ordinary alpha-blend is §7.2's hypothesis. Texture unknown -> the soft blob.
            pr.material = SnowFall.FlakeMaterial();
            _ps.Play();
            _buf = new ParticleSystem.Particle[max];
            _base = new Vector3[Boxes][];
        }

        int ZoneAt(Vector3 unity)
        {
            // P() is (x·S, z·S, y·S), so raw x = unity.x / S, raw y = unity.z / S.
            float rx = unity.x / _s, ry = unity.z / _s;
            int i = Mathf.FloorToInt((rx - _x0) / _px), j = Mathf.FloorToInt((ry - _y0) / _px);
            if (i < 0 || j < 0 || i >= _w || j >= _h) return -1;
            return _map[j * _w + i] - 1;
        }

        void Blend(int zone, float dt)
        {
            if (zone < 0)
            {
                // Off the tree or no zone: the game resets to the ctor defaults at once (§7.8).
                Array.Copy(Defaults, _acc, 20);
                return;
            }
            var t = _zones[zone];
            if (_acc[0] == Defaults[0]) { Array.Copy(t, _acc, 20); return; }   // first sample snaps
            // acc += w²·(target − acc) per query, w = −k0. One query per 60 Hz frame is §7.1's
            // assumption; made frame-rate independent here.
            float w2 = t[0] * t[0];
            float a = 1f - Mathf.Pow(1f - Mathf.Clamp01(w2), dt * 60f);
            for (int k = 0; k < 20; k++) _acc[k] += a * (t[k] - _acc[k]);
            _acc[0] = t[0];
        }

        void LateUpdate()
        {
            if (_cam == null)
            {
                var c = Camera.main;
                if (c == null) return;
                _cam = c.transform;
            }
            Vector3 cp = _cam.position;
            float dt = Time.deltaTime;
            // Camera, not rider: the PS2 getters are per VIEWPORT (§7.1). Guess that the
            // viewport's query point is its camera.
            int zone = ZoneAt(cp);
            Blend(zone, dt);
            if (zone != _lastZone)
            {
                // Info on purpose: a zone change is rare (a course has <= 11 zones), and it is
                // the only way to tell "this spot has no weather" from "the snow isn't drawing".
                var z = zone >= 0 ? _zones[zone] : Defaults;
                Plugin.Log.LogInfo($"[SnowFall] SSX 3 weather zone {_lastZone} -> {zone} at raw " +
                                   $"({cp.x / _s:F0}, {cp.z / _s:F0}): {(int)(z[1] * FlakePerK1)} flakes/box " +
                                   $"size {z[2]:F1}, {(int)(z[9] * FluffPerK9)} fluff/box, wind {z[3]:F2}@{z[4]:F0}°");
                _lastZone = zone;
            }

            if (!_seeded)
            {
                for (int b = 0; b < Boxes; b++)
                {
                    float half = 0.5f * BoxEdge[b] * _s;
                    var arr = _base[b] = new Vector3[b < FlakeBoxes ? MaxFlakesPerBox : MaxFluffPerBox];
                    for (int i = 0; i < arr.Length; i++)
                        arr[i] = cp + new Vector3(UnityEngine.Random.Range(-half, half),
                                                  UnityEngine.Random.Range(-half, half),
                                                  UnityEngine.Random.Range(-half, half));
                }
                _seeded = true;
            }

            // Wind: steady along k4, mixed with a rerolled random gust by k7 (§7.1). Guesses:
            // heading 0° = raw +x turning toward raw +y; the gust is a random horizontal
            // direction at up to full wind speed, lerped from the previous one over its period.
            _gustT += dt;
            if (_gustT >= _gustPeriod)
            {
                _gustT = 0f;
                _gustPeriod = 1f + 4f * UnityEngine.Random.value;
                _gustFrom = _gustTo;
                Vector2 r = UnityEngine.Random.insideUnitCircle;
                _gustTo = new Vector3(r.x, 0f, r.y);
            }
            Vector3 gust = Vector3.Lerp(_gustFrom, _gustTo, _gustT / _gustPeriod);
            float h = _acc[4] * Mathf.Deg2Rad;
            Vector3 steady = new Vector3(Mathf.Cos(h), 0f, Mathf.Sin(h));
            float k7 = _acc[7];
            Vector3 windDir = (1f - k7) * steady + k7 * gust;
            // Gravity vector × fall speed × k8; read as "down" for k8 > 0 (sign of 0x4ff160 not traced).
            Vector3 flakeVel = (windDir * (_acc[3] * FlakeWind) + Vector3.down * (FlakeFall * _acc[8])) * _s;
            Vector3 fluffVel = (windDir * (_acc[10] * FluffWind) + Vector3.down * (FluffFall * _acc[8])) * _s;

            int nFlake = Mathf.Clamp((int)(_acc[1] * FlakePerK1), 0, MaxFlakesPerBox);
            int nFluff = Mathf.Clamp((int)(_acc[9] * FluffPerK9), 0, MaxFluffPerBox);
            float flakeSize = Mathf.Max(0f, _acc[2]) * _s * _sizeScale, fluffSize = FluffSize * _s;
            // PS2 packet colour k·128, where 128 = 1.0.
            Color flakeCol = new Color(_acc[13], _acc[14], _acc[15], Mathf.Clamp01(_acc[12]));
            Color fluffCol = new Color(_acc[17], _acc[18], _acc[19], Mathf.Clamp01(_acc[16]));

            int n = 0;
            for (int b = 0; b < Boxes; b++)
            {
                bool flake = b < FlakeBoxes;
                int count = flake ? nFlake : nFluff;
                _off[b] += (flake ? flakeVel : fluffVel) * dt;
                if (count == 0) continue;
                float half = 0.5f * BoxEdge[b] * _s, side = 2f * half, inv = 1f / side;
                float fadeStart = FadeFrac * half, fadeSpan = half - fadeStart;
                Color col = flake ? flakeCol : fluffCol;
                float size = flake ? flakeSize : fluffSize;
                var arr = _base[b];
                for (int i = 0; i < count; i++)
                {
                    Vector3 d = arr[i] + _off[b] - cp;
                    d.x -= side * Mathf.Floor((d.x + half) * inv);
                    d.y -= side * Mathf.Floor((d.y + half) * inv);
                    d.z -= side * Mathf.Floor((d.z + half) * inv);
                    float dist = d.magnitude;
                    float fade = dist <= fadeStart ? 1f : dist >= half ? 0f : (half - dist) / fadeSpan;
                    Color c = col; c.a *= fade;
                    _buf[n].position = cp + d;
                    _buf[n].velocity = Vector3.zero;
                    _buf[n].startLifetime = 1e6f;
                    _buf[n].remainingLifetime = 1e6f;
                    _buf[n].startSize = size;
                    _buf[n].startColor = c;
                    n++;
                }
            }
            _ps.SetParticles(_buf, n);
        }

        void OnDestroy() { Active = false; }
    }
}
