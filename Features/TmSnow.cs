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

        static void ConvertSnowflakes(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
            // Trick-multiplier pickups: hand the flakes to the GAME's own snowflake
            // behaviour instead of the Maker's generic one.
            //
            // The map pipeline can only emit Maker components, so an imported flake
            // ships with MakerSnowflake (tags the GO — that's all it does) plus a
            // MakerObjectAnimation spinning it 60 deg/s about its LOCAL Y. The game's
            // built-in courses instead carry `Snowflake`, which sets an ABSOLUTE
            // world rotation of 3 rad/s (~172 deg/s) about Y — verified in level1,
            // where every stock flake is: layer 2, tag Snowflake<n>, `Snowflake`, and
            // a SPHERE trigger of radius 10 (its mesh is a 14.6 x 16.8 x 1.1 panel,
            // so the sphere covers the flake's face AND stands ~10 units proud of it
            // on either side).
            //
            // Two things follow, and both are why ported pickups feel off:
            //   * spin is at the mercy of a Maker component we don't control (Molokai
            //     ships a bundle structurally identical to maps that do spin, yet
            //     doesn't) — the game's own component removes that variable, and
            //     matches the built-in courses' speed while it's there.
            //   * our trigger is the flake's own convex hull, i.e. ~2 units thick
            //     along the panel normal. The rider crosses that in a single frame at
            //     speed, so pickups get missed. Stock's grab depth is the radius, not
            //     the panel: a sphere of max(10, largest extent) is never smaller than
            //     stock's absolute grab and always encloses the (bigger, 2.5x-scaled)
            //     ported flake, so its corners stay collectable.
            int flakes = 0, flakeSpheres = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var mf in sceneRoot.GetComponentsInChildren<MakerSnowflake>(true))
                {
                    var flakeGO = mf.gameObject;
                    if (flakeGO.GetComponent<Snowflake>() != null) continue; // already converted
                    var makerAnim = flakeGO.GetComponent<MakerObjectAnimation>();
                    if (makerAnim != null) UnityEngine.Object.Destroy(makerAnim);
                    flakeGO.AddComponent<Snowflake>();
                    flakes++;

                    // Swap the mesh-tight trigger for the stock sphere. Keep the mesh
                    // collider only if there's no renderer to size a sphere from.
                    var meshCol = flakeGO.GetComponent<MeshCollider>();
                    var filter = flakeGO.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    var b = filter.sharedMesh.bounds;
                    var sphere = flakeGO.AddComponent<SphereCollider>();
                    sphere.isTrigger = true;
                    sphere.center = b.center;
                    // Stock parity is a flat 10 (TM's own forest flakes all carry
                    // m_Radius 10), and §19 justified it by noting the sphere still
                    // ENCLOSES the flake. That held for every gem in the corpus until
                    // WildTangent's coin, whose largest extent is 1.72 -- there the
                    // floor is 5.8x the coin's own diameter, so a rider on the snow
                    // sits entirely inside a trigger centred well overhead and grabs
                    // coins by riding under them. Keep the floor, but don't let it
                    // dwarf the flake: 3x the flake's own extent, capped at the stock
                    // 10 and never smaller than the flake itself.
                    // Measured, so this is a no-op everywhere it already worked:
                    // TM forest flake extent 8.39 -> 10 (unchanged), merquer/megaple
                    // gem 13.06 -> 13.06 (unchanged), dusk coin 1.72 -> 5.15.
                    // ⚠️ The 3x is a JUDGMENT CALL -- it is the reach-vs-precision
                    // dial, and nothing in the game's data pins it.
                    // ⚠️ Do NOT scale this by Plugin.WorldScale: rider physics is
                    // absolute (§19), and WildTangent rides at WorldScale 100, which
                    // would make this a 1000-unit grab sphere.
                    float ext = Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z));
                    sphere.radius = Mathf.Max(ext, Mathf.Min(10f, ext * 3f));
                    if (meshCol != null) UnityEngine.Object.Destroy(meshCol);
                    flakeSpheres++;
                }
            }
            if (flakes > 0)
                Log.LogInfo($"[Snowflakes] converted {flakes} pickup(s) to the game's Snowflake behaviour, {flakeSpheres} sphere trigger(s)");
            blocks.Add($"flake={flakes}/{flakeSpheres}");
            LoadClock.Mark("Snowflakes");
        }

        static void SpawnFallingSnow(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
            var levelManager = ctx.LevelManager;
            // Atmospheric falling snow. Naming contract with SSXImporter.BuildLighting:
            // an empty GO "SnowFX_<intensity>". That gets TM's own FallingSnow when the
            // build has it (TrySpawnTmSnow), else a camera-follow ParticleSystem built at
            // runtime (SnowFall) — a bundle-baked system would be engine-module-stripped
            // and, world-simulated, couldn't stay wrapped around the chase cam. One system
            // total (there's only ever one marker); intensity scales emission + cap.
            // "SnowSSX_<chance>_<intensity>_<unitsPerMetre>" is SSX Tricky's decoded
            // SnowFallMan instead (SsxSnowFall, BakeSsxSnowMarker); a failed per-race roll
            // still consumes the marker. "WeatherSSX3_<S>_<x0>_<y0>_<rawPerPixel>" is SSX 3's
            // zone-mapped weather (Ssx3SnowFall, BakeSsx3WeatherMarker).
            bool snowDone = SnowFall.Active || SsxSnowFall.Active || Ssx3SnowFall.Active;
            string snowKind = snowDone ? "active" : "none";
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                if (snowDone) break;
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name.StartsWith("SnowSSX_"))
                    {
                        SpawnSsxSnow(t);
                        snowKind = "ssx";
                        snowDone = true;
                        break;
                    }
                    if (t.name.StartsWith("WeatherSSX3_"))
                    {
                        if (Ssx3SnowFall.TrySpawn(t, ssx3SnowFlakeSize.Value, out string why))
                            Log.LogInfo($"[SnowFall] SSX 3 weather snow spawned: {why}");
                        else
                            Log.LogWarning($"[SnowFall] SSX 3 weather: {why}");
                        snowKind = "ssx3";
                        snowDone = true;
                        break;
                    }
                    if (!t.name.StartsWith("SnowFX_")) continue;
                    float si;
                    if (!float.TryParse(t.name.Substring("SnowFX_".Length), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out si))
                    {
                        si = 1f;
                        Log.LogDebug($"[SnowFall] '{t.name}' has no parseable intensity -> default {si:F2}");
                    }
                    snowKind = "tm";
                    if (TrySpawnTmSnow(levelManager, si, out string tmWhy))
                        Log.LogInfo($"[SnowFall] TM's own falling snow wired ({tmWhy})");
                    else
                    {
                        snowKind = "cam";
                        SnowFall.Spawn(si);
                        Log.LogInfo($"[SnowFall] camera-follow snow spawned (intensity {si:F2}); TM snow unavailable: {tmWhy}");
                        LoadClock.Mark("SnowFall");
                    }
                    snowDone = true;
                    break;
                }
            }
            blocks.Add($"snow={snowKind}");
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
