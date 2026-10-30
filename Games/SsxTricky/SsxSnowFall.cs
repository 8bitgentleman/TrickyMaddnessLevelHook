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
        // "SnowSSX_<chance>_<intensity>_<unitsPerMetre>" + str2 on its disabled renderer
        // (SSXImporter.BakeSsxSnowMarker). All-or-nothing parse: a malformed marker draws no
        // snow rather than guessing a scale.
        static void SpawnSsxSnow(Transform t)
        {
            var tok = t.name.Split('_');
            float chance = 0f, intensity = 0f, upm = 0f;
            if (tok.Length != 4
                || !float.TryParse(tok[1], NumberStyles.Float, CultureInfo.InvariantCulture, out chance)
                || !float.TryParse(tok[2], NumberStyles.Float, CultureInfo.InvariantCulture, out intensity)
                || !float.TryParse(tok[3], NumberStyles.Float, CultureInfo.InvariantCulture, out upm)
                || upm <= 0f)
            {
                Log.LogWarning($"[SnowFall] '{t.name}' is not SnowSSX_<chance>_<intensity>_<unitsPerMetre>; no snow");
                return;
            }
            var mat = t.GetComponent<Renderer>()?.sharedMaterial;
            Texture tex = mat != null && mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : null;
            if (tex == null && mat != null) tex = mat.mainTexture;
            float gain = mat != null && mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor").r : 1f;
            if (tex == null)
                Log.LogWarning($"[SnowFall] '{t.name}' carries no str2 texture; drawing the fallback blob");
            if (SsxSnowFall.TrySpawn(chance, intensity, upm, tex, gain, ssxSnowFlakeSize.Value, out string why))
                Log.LogInfo($"[SnowFall] SSX Tricky snow spawned: {why}");
            else
                Log.LogInfo($"[SnowFall] SSX Tricky snow: {why}");
        }
    }

    // SSX Tricky's own weather snow: the ELF's SnowFallMan, decoded in ssx/PARTICLES_PLAN.md §6.
    // Marker "SnowSSX_<chance>_<intensity>_<unitsPerMetre>" (SSXImporter.BakeSsxSnowMarker)
    // carrying str2 + its raw-alpha gain on a disabled renderer. SSX draws a world-anchored
    // field of flakes in 3.5 m cells around the camera, all falling together; here that is a
    // fixed set of world-space particles whose camera-relative offset is wrapped into a cube of
    // half-side 7 cells every frame, so the field is endless and a flake never pops in view
    // (the cube's faces sit at the fade's zero). Constants are SnowFallMan's, verified in the
    // ELF; the flake size is the one guess (A/B, meaning unknown — [SnowFall] SsxFlakeSize).
    public class SsxSnowFall : MonoBehaviour
    {
        public static bool Active;
        const float CellM = 3.5f, FadeStartCells = 6f, ReachCells = 7f;   // fade 21 m -> 0 at 24.5 m
        const float FallMps = 3f, Alpha = 0.1875f;                         // (0,0,-300) raw/s; +0x4b0
        const float DensMin = 0.05f, DensSpan = 0.15f;                     // flakes/m³, rolled per race
        const int MaxFlakes = 30000;

        Transform _cam;
        ParticleSystem _ps;
        ParticleSystem.Particle[] _buf;
        int _n;
        float _upm, _reach, _fadeStart, _sizeA, _sizeB;
        bool _seeded;

        // Rolls the per-race chance; false = no snow this load (the roll is SSX's own).
        public static bool TrySpawn(float chance, float intensity, float upm, Texture tex, float gain,
                                    float sizeScale, out string why)
        {
            float roll = UnityEngine.Random.value;
            if (roll >= chance) { why = $"rolled {roll:F3} >= chance {chance:F3}, no snow this race"; return false; }
            var go = new GameObject("HookSsxSnowFall");
            why = go.AddComponent<SsxSnowFall>().Build(intensity, upm, tex, gain, sizeScale);
            Active = true;
            return true;
        }

        string Build(float intensity, float upm, Texture tex, float gain, float sizeScale)
        {
            _upm = upm;
            _reach = ReachCells * CellM * upm;
            _fadeStart = FadeStartCells * CellM * upm;
            float density = (DensMin + DensSpan * UnityEngine.Random.value) * intensity;
            float sideM = 2f * ReachCells * CellM;
            _n = Mathf.Clamp(Mathf.RoundToInt(density * sideM * sideM * sideM), 0, MaxFlakes);
            // SetRange(5 + r, 7 - r); per flake U[A, B). Read as world cm (guess).
            float r = UnityEngine.Random.value;
            _sizeA = (5f + r) * 0.01f * upm * sizeScale;
            _sizeB = (7f - r) * 0.01f * upm * sizeScale;

            _ps = gameObject.AddComponent<ParticleSystem>();
            _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = _ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f;
            main.startLifetime = 1e6f;
            main.gravityModifier = 0f;
            main.maxParticles = Mathf.Max(1, _n);
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
            pr.material = MakeMaterial(tex, gain);
            _ps.Play();
            _buf = new ParticleSystem.Particle[Mathf.Max(1, _n)];
            return $"density {density:F3}/m³ -> {_n} flakes, size {_sizeA / upm * 100f:F1}-{_sizeB / upm * 100f:F1} cm, " +
                   $"{upm:F2} u/m, texture {(tex != null ? tex.name : "none (fallback blob)")}";
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
            if (!_seeded)
            {
                // SnowFallMan's scroll velocity; Unity integrates it for world-sim particles.
                var ep = new ParticleSystem.EmitParams
                {
                    velocity = new Vector3(0f, -FallMps * _upm, 0f),
                    startLifetime = 1e6f,
                    applyShapeToPosition = false,
                };
                for (int i = 0; i < _n; i++)
                {
                    ep.position = cp + new Vector3(UnityEngine.Random.Range(-_reach, _reach),
                                                   UnityEngine.Random.Range(-_reach, _reach),
                                                   UnityEngine.Random.Range(-_reach, _reach));
                    ep.startSize = UnityEngine.Random.Range(_sizeA, _sizeB);
                    ep.startColor = new Color32(255, 255, 255, 0);
                    _ps.Emit(ep, 1);
                }
                _seeded = true;
            }

            int n = _ps.GetParticles(_buf);
            float side = 2f * _reach, inv = 1f / side, fadeSpan = _reach - _fadeStart;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = _buf[i].position - cp;
                d.x -= side * Mathf.Floor((d.x + _reach) * inv);
                d.y -= side * Mathf.Floor((d.y + _reach) * inv);
                d.z -= side * Mathf.Floor((d.z + _reach) * inv);
                _buf[i].position = cp + d;
                // Linear 1 -> 0 over the last cell. SSX fades per CELL off its min corner;
                // per flake is the smooth version of the same ramp.
                float dist = d.magnitude;
                float a = dist <= _fadeStart ? Alpha : dist >= _reach ? 0f : Alpha * (_reach - dist) / fadeSpan;
                _buf[i].startColor = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
            }
            _ps.SetParticles(_buf, n);
        }

        void OnDestroy() { Active = false; }

        // Additive (SnowFallMan's blend slot 0x1e0 arg 5 = MT2.0 U50 0), vertex colour
        // multiplies str2, gain carries the raw-alpha ×255/128 the importer computed.
        static Material MakeMaterial(Texture tex, float gain)
        {
            var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                     ?? Shader.Find("Universal Render Pipeline/Unlit");
            var m = new Material(sh) { name = "HookSsxSnowFlake" };
            m.SetTexture("_BaseMap", tex != null ? tex : SnowFall.FlakeSprite());
            m.SetColor("_BaseColor", new Color(gain, gain, gain, 1f));
            m.SetFloat("_ColorMode", 0f);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 2f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            m.SetFloat("_ZWrite", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return m;
        }
    }
}
