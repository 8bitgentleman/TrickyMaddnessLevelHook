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
        // TM's own weather snow for "SnowFX_" maps. The levelmanager prefab carries a
        // fallingSnowPrefab (ParticleSystem + FallingSnow + the game's snowflake material)
        // that the game never instantiates — the official courses put a copy in the scene
        // and point LevelManager.fallingSnow at it, and LevelManager.Start binds it to the
        // camera and player. We do the same before Start runs, so a custom map gets TM's
        // look plus its speed/boost smear and roof cut-off. The component is looked up by
        // name so this method still JITs on a TM build without the type; false = fall back.
        static bool TrySpawnTmSnow(LevelManager lm, float intensity, out string why)
        {
            if (fFallingSnow == null || fFallingSnowPrefab == null)
            { why = "this TM build has no LevelManager.fallingSnow"; return false; }
            var prefab = fFallingSnowPrefab.GetValue(lm) as GameObject;
            if (prefab == null) { why = "LevelManager.fallingSnowPrefab is null"; return false; }
            var go = Instantiate(prefab);
            var fs = go.GetComponent("FallingSnow");
            if (fs == null) { Destroy(go); why = "fallingSnowPrefab has no FallingSnow"; return false; }
            // Same clamp and meaning as SnowFall: snowIntensity scales emission and cap.
            intensity = Mathf.Clamp(intensity, 0.05f, 8f);
            var ps = go.GetComponent<ParticleSystem>();
            var main = ps.main;
            main.maxParticles = Mathf.CeilToInt(main.maxParticles * intensity);
            var em = ps.emission;
            em.rateOverTimeMultiplier *= intensity;
            fFallingSnow.SetValue(lm, fs);
            why = $"intensity {intensity:F2}, rate {em.rateOverTimeMultiplier:F0}/s, cap {main.maxParticles}";
            return true;
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
        internal static Material FlakeMaterial()
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
        internal static Texture2D FlakeSprite()
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
}
