using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TrickyMaddnessLevelHook
{
    public partial class Plugin
    {
        static void SpawnSnowGlitter(BuildContext ctx, List<string> blocks)
        {
            // Sun glints on snow and ice. Naming contract with SSXImporter (lighting.glitter):
            // an empty "Glitter_<intensity>_<density>_<range>" marker. Opt-in per map — no
            // marker, no component, nothing changes. First marker wins.
            foreach (var sceneRoot in ctx.Maker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Glitter_")) continue;
                    string[] seg = t.name.Split('_');
                    float inten = SegFloat(seg, 1, 2f, t.name, "intensity");
                    float dens = SegFloat(seg, 2, 48f, t.name, "density");
                    float range = SegFloat(seg, 3, 120f, t.name, "range");
                    if (SnowGlitter.Spawn(inten, dens, range, out string why))
                        Log.LogInfo($"[Glitter] '{t.name}': {why}");
                    else
                        Log.LogWarning($"[Glitter] '{t.name}' not started: {why}");
                    LoadClock.Mark("Glitter");
                    blocks.Add($"glitter={(SnowGlitter.Active ? 1 : 0)}");
                    return;
                }
            }
            blocks.Add("glitter=0");
        }
    }

    // Snow/frost glitter WITHOUT a custom shader. Shaun White HD gets it from ekgeneric's
    // APPLY_SNOW path (a frost normal map adds glitter·sunSpec to the specular —
    // shaunwhite_hd/findings/normal_spec_reflect.md §3). A bundle can't carry a working shader
    // to both platforms (the Hook's foreign-shader remap is lossy, root CLAUDE.md), so this is
    // done the way TM's own snow is: a Hook-made ParticleSystem on the GAME's URP
    // Particles/Unlit shader (same lookup as SnowFall.FlakeMaterial), which renders the same
    // on a Mac and a Windows bundle because neither bundle supplies it.
    //
    // Every frame `density` rays go out from the main camera through random viewport points,
    // up to `range` units. A hit on snow (layer Default = 0) or Ice (6) whose normal faces up
    // is a candidate microfacet: its normal is jittered (the frost normal map's job) and it
    // glints with probability pow(N'·H, Power) for the sun's half vector, unless something
    // shadows it from the sun. Accepted glints are tiny additive stars with a short life, so
    // they flicker as the camera moves, which is what glitter does. Size is set per
    // distance, so a glint covers the same few pixels near or far.
    public class SnowGlitter : MonoBehaviour
    {
        public static bool Active => _inst != null;
        static SnowGlitter _inst;

        const int SurfaceMask = (1 << 0) | (1 << 6);   // Default (snow) + Ice — Rock/Metal don't glint
        const float MinUp = 0.5f;          // GUESS: cos 60° — snow only sits on faces looking up
        const float Jitter = 0.45f;        // GUESS: microfacet normal spread
        const float Power = 48f;           // GUESS: glint lobe sharpness
        const float AngularSize = 0.006f;  // GUESS: world size per unit distance (~0.35°, a few px at 1080p)
        const float ShadowReach = 400f;    // GUESS: how far toward the sun a blocker is looked for

        float _intensity, _range;
        int _rays;
        ParticleSystem _ps;
        Light _sun;
        Camera _cam;
        Material _mat;

        public static bool Spawn(float intensity, float density, float range, out string why)
        {
            if (_inst != null) { why = "already running"; return false; }
            var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                     ?? Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) { why = "no URP particle/unlit shader in this TM build"; return false; }

            var go = new GameObject("HookSnowGlitter");
            var g = go.AddComponent<SnowGlitter>();
            g._intensity = Mathf.Clamp(intensity, 0f, 16f);
            g._rays = Mathf.Clamp(Mathf.RoundToInt(density), 1, 512);
            g._range = Mathf.Max(1f, range);
            g._mat = GlintMaterial(sh, g._intensity);
            g.BuildParticles();
            g._sun = FindSun();
            _inst = g;
            why = $"intensity {g._intensity:F2}, {g._rays} rays/frame, range {g._range:F0} u, sun "
                + (g._sun != null ? $"'{g._sun.name}'" : "MISSING (glitter stays dark)");
            return true;
        }

        static Light FindSun()
        {
            // Same lookup as LightingRamp: RenderSettings.sun, else the only directional light.
            var sun = RenderSettings.sun;
            if (sun != null) return sun;
            foreach (var l in FindObjectsOfType<Light>())
                if (l.type == LightType.Directional) return l;
            return null;
        }

        void BuildParticles()
        {
            _ps = gameObject.AddComponent<ParticleSystem>();
            _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = _ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f;
            main.gravityModifier = 0f;
            main.maxParticles = _rays * 24;
            var em = _ps.emission; em.enabled = false;     // emitted by hand
            var shape = _ps.shape; shape.enabled = false;
            var sol = _ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0f), new Keyframe(0.25f, 1f), new Keyframe(1f, 0f)));
            var pr = GetComponent<ParticleSystemRenderer>();
            pr.renderMode = ParticleSystemRenderMode.Billboard;
            pr.alignment = ParticleSystemRenderSpace.View;
            pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pr.receiveShadows = false;
            pr.material = _mat;
            _ps.Play();
        }

        void LateUpdate()
        {
            if (Time.timeScale <= 0f || _sun == null || !_sun.isActiveAndEnabled) return;
            if (_cam == null) { _cam = Camera.main; if (_cam == null) return; }
            try { Emit(); }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Glitter] emit failed, glitter disabled: {e}");
                enabled = false;
            }
        }

        void Emit()
        {
            Vector3 L = -_sun.transform.forward;
            if (L.y <= 0f) return;                       // sun below the horizon
            Color sunCol = _sun.color * Mathf.Min(1f, _sun.intensity);
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (int i = 0; i < _rays; i++)
            {
                var ray = _cam.ViewportPointToRay(new Vector3(UnityEngine.Random.value, UnityEngine.Random.value, 0f));
                if (!Physics.Raycast(ray, out RaycastHit hit, _range, SurfaceMask, QueryTriggerInteraction.Ignore))
                    continue;
                if (hit.collider is CharacterController || hit.rigidbody != null) continue;   // the rider, props
                Vector3 n = hit.normal;
                if (n.y < MinUp) continue;
                Vector3 v = -ray.direction;
                Vector3 h = (L + v).normalized;
                Vector3 nj = (n + UnityEngine.Random.insideUnitSphere * Jitter).normalized;
                float s = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(nj, h)), Power);
                if (UnityEngine.Random.value >= s) continue;
                if (Physics.Raycast(hit.point + n * 0.1f, L, ShadowReach, Physics.DefaultRaycastLayers,
                                    QueryTriggerInteraction.Ignore))
                    continue;                            // in shadow: no sun to glint
                float size = hit.distance * AngularSize * Mathf.Lerp(0.6f, 1.4f, UnityEngine.Random.value);
                ep.position = hit.point + v * size;      // lift toward the eye so the quad isn't half buried
                ep.startSize = size;
                ep.startLifetime = Mathf.Lerp(0.08f, 0.22f, UnityEngine.Random.value);
                ep.startColor = sunCol * Mathf.Clamp01(0.5f + s);
                _ps.Emit(ep, 1);
            }
        }

        // Additive (One One) on the game's particle shader. Blend factors are material
        // properties, not keywords, so this is the SAME shader variant SnowFall's alpha-blended
        // flake already uses (_SURFACE_TYPE_TRANSPARENT). _BaseColor is HDR and carries the
        // intensity (particle vertex colour is 8-bit and would clamp it), so a glint above 1
        // can feed PostFX's bloom where a map has it.
        static Material GlintMaterial(Shader sh, float intensity)
        {
            var m = new Material(sh) { name = "HookSnowGlitter" };
            m.SetTexture("_BaseMap", StarSprite());
            m.SetColor("_BaseColor", new Color(intensity, intensity, intensity, 1f));
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 2f);   // URP particles: Additive
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return m;
        }

        // Four-point star: a bright core plus thin horizontal/vertical spikes. RGB carries the
        // falloff (One One ignores alpha); alpha matches it in case a shader variant uses it.
        static Texture2D StarSprite()
        {
            const int N = 32;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { name = "HookSnowGlitterTex" };
            float c = (N - 1) / 2f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x - c) / c, dy = (y - c) / c;
                    float core = Mathf.Exp(-(dx * dx + dy * dy) * 30f);
                    float spikes = 0.7f * (Mathf.Exp(-Mathf.Abs(dy) * 22f) * Mathf.Max(0f, 1f - Mathf.Abs(dx))
                                        + Mathf.Exp(-Mathf.Abs(dx) * 22f) * Mathf.Max(0f, 1f - Mathf.Abs(dy)));
                    float a = Mathf.Clamp01(core + spikes);
                    t.SetPixel(x, y, new Color(a, a, a, a));
                }
            t.Apply();
            t.wrapMode = TextureWrapMode.Clamp;
            return t;
        }

        void OnDestroy()
        {
            if (_inst == this) _inst = null;
            if (_mat != null)
            {
                var tex = _mat.GetTexture("_BaseMap");
                Destroy(_mat);
                if (tex != null) Destroy(tex);
            }
        }
    }
}
