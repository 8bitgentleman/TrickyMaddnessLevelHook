using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TrickyMaddnessLevelHook
{
    // SSX (2012)'s per-volume snow (ssx/SSX2012_WEATHER_PLAN.md Phase 3, D1 = the Hook's own
    // field with fx_falling_snow's numbers). Naming contract with SSXImporter.BuildZoneWeather:
    //   root  "ZoneWeather_<format=1>_<unitsPerMetre>"
    //   child "WxDefault_<precip>_<windSpeed>_<dx>_<dy>_<dz>"   weather outside every volume
    //   child "WxSnow_<key>_<v...>"                               fx_falling_snow state 0002
    //   child "WxBox_<kind>_<index>_<tail>"                       disabled BoxCollider = the volume
    // Each frame the camera picks the precip box it is inside; flakes drawn = precip × cap, as
    // a STEP (2012 re-applies the value every tick, no blend: W6.21). Wind boxes drift the
    // flakes along the box's +Z at <value> × upm × the effect's wind response (Phase 4 — the
    // unit is a GUESS, m/s; RPCS3 Gate M measures it). Lighting boxes are ZoneWeatherLight's.
    // Game-agnostic on purpose (Features/), so a future source game can bake the same markers.
    public class ZoneWeatherSnow : MonoBehaviour
    {
        public static bool Active;
        const int FORMAT = 1;
        const float FadeFrac = 0.8f;   // guess, borrowed from Ssx3SnowFall: fade the outer 20%
        // fx_falling_snow's ParticleActionWeather fWeatherScalar (W6.21: VERIFIED value,
        // INFERRED to be the wind-response multiplier).
        const float WindResponse = 1f;
        // Roof cut-off, ported from TM's FallingSnow.Update (IL, build 26514993): one ray up
        // from the camera per frame, 150 u, Snowboarder.CastLayerMask; a hit stops emission.
        // TM's live flakes then expire over their lifetime — ours have none (they wrap), so the
        // count ramps over fx_falling_snow's 2 s life instead (judgment call).
        const float RoofRay = 150f, RoofRamp = 2f;

        struct Box { public Transform t; public Vector3 c, half; public float v; public int idx; }

        float _defaultPrecip, _defaultWind, _windUnit, _wrap, _sizeMean, _sizeVar, _fallMean, _fallVar, _alpha, _sizeScale;
        int _cap;
        bool _exitHolds;
        Box[] _boxes, _wind;
        Vector3 _defaultDir, _windVel, _windOff;
        int _lastWind = -2;

        Transform _cam;
        ParticleSystem _ps;
        ParticleSystem.Particle[] _buf;
        Vector3[] _base;
        float[] _fall, _size;
        float _t, _precip, _roof = 1f;
        bool _underRoof;
        int _roofMask;
        int _lastBox = -2;
        bool _seeded;
        Texture _flakeTex;

        static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        static float[] Get(Dictionary<string, float[]> snow, List<string> missing, string k, params float[] dflt)
        {
            float[] v;
            if (snow.TryGetValue(k, out v) && v.Length >= dflt.Length) return v;
            missing.Add(k); return dflt;
        }

        public static bool TrySpawn(Transform root, bool exitHolds, float sizeScale, float windUnit, out string why)
        {
            float upm;
            var tok = root.name.Split('_');
            if (tok.Length != 3 || tok[1] != FORMAT.ToString() || !float.TryParse(tok[2], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out upm) || upm <= 0f)
            {
                why = $"'{root.name}' is not ZoneWeather_{FORMAT}_<unitsPerMetre>; no snow";
                return false;
            }
            var snow = new Dictionary<string, float[]>();
            float defPrecip = float.NaN, defWind = 0f;
            Vector3 defDir = Vector3.forward;
            var boxes = new List<Box>();
            var wind = new List<Box>();
            Texture flakeTex = null;
            try
            {
                foreach (Transform c in root)
                {
                    var p = c.name.Split('_');
                    if (p[0] == "WxSnow" && p.Length >= 3)
                    {
                        var v = new List<float>();
                        float f;
                        for (int i = 2; i < p.Length; i++)
                            if (float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f)) v.Add(f);
                        snow[p[1]] = v.ToArray();
                    }
                    else if (c.name == "WxFlake")
                    {
                        var mr = c.GetComponent<MeshRenderer>();
                        if (mr != null && mr.sharedMaterial != null) flakeTex = mr.sharedMaterial.GetTexture("_BaseMap");
                    }
                    else if (p[0] == "WxDefault" && p.Length == 6)
                    {
                        defPrecip = F(p[1]); defWind = F(p[2]);
                        // The default's direction is in the importer's raw frame order (x, y, z)
                        // = the source's own axes, which the adapter round-trips to Unity's.
                        defDir = new Vector3(F(p[3]), F(p[4]), F(p[5]));
                    }
                    else if (p[0] == "WxBox" && p.Length == 4 && (p[1] == "precip" || p[1] == "wind"))
                    {
                        var bc = c.GetComponent<BoxCollider>();
                        if (bc == null) { why = $"'{c.name}' has no BoxCollider; no snow"; return false; }
                        bc.enabled = false;   // geometry only; containment is analytic below
                        (p[1] == "precip" ? boxes : wind).Add(new Box { t = c, c = bc.center, half = 0.5f * bc.size, v = F(p[3]), idx = int.Parse(p[2]) });
                    }
                }
            }
            catch (Exception e) { why = $"malformed weather marker under '{root.name}' ({e.Message}); no snow"; return false; }
            if (float.IsNaN(defPrecip)) { why = $"'{root.name}' carries no WxDefault_; no snow"; return false; }

            // fx_falling_snow numbers. Each falls back to W6.21's value so a bundle missing one key
            // still snows; the miss is logged because it means the importer and the Hook drifted.
            var missing = new List<string>();
            var size = Get(snow, missing, "size", 0.09f, 0.03f);
            var vel = Get(snow, missing, "velocity", 0f, -4f, 0f, 0f, 2f, 0f);
            var ramp = Get(snow, missing, "alphaRamp", 0f, 0f, 0.2f, 0.75f, 0.8f, 0.66f, 1f, 0f);
            float alpha = 0f;
            for (int i = 1; i < ramp.Length; i += 2) alpha = Mathf.Max(alpha, ramp[i]);

            boxes.Sort((a, b) => a.idx.CompareTo(b.idx));
            wind.Sort((a, b) => a.idx.CompareTo(b.idx));
            var go = new GameObject("HookZoneWeatherSnow");
            var z = go.AddComponent<ZoneWeatherSnow>();
            z._defaultPrecip = defPrecip;
            z._wrap = Get(snow, missing, "wrapBox", 20f)[0] * upm;
            z._cap = Mathf.Max(0, (int)Get(snow, missing, "maxParticles", 3000f)[0]);
            z._sizeMean = size[0] * upm; z._sizeVar = size[1] * upm;
            z._fallMean = -vel[1] * upm; z._fallVar = vel[4] * upm;
            // The ramp's plateau, not its envelope: a wrapped field has no particle lifetime to
            // ramp over (the 2 s life is replaced by wrapping) — judgment call.
            z._alpha = alpha > 0f ? alpha : 0.75f;
            z._sizeScale = sizeScale;
            z._exitHolds = exitHolds;
            z._boxes = boxes.ToArray();
            z._wind = wind.ToArray();
            z._defaultWind = defWind;
            z._defaultDir = defDir.sqrMagnitude > 1e-6f ? defDir.normalized : Vector3.forward;
            z._windUnit = windUnit * upm;
            z._precip = defPrecip;
            z._flakeTex = flakeTex;
            z.Build();
            Active = true;
            why = $"{boxes.Count} precip + {wind.Count} wind volume(s), default precip {defPrecip:0.##} wind {defWind:0.##}, cap {z._cap}, " +
                  $"wrap {z._wrap:0.#} u, {upm:0.###} u/m, flake {(flakeTex != null ? flakeTex.name : "stand-in blob (no WxFlake)")}, exit {(exitHolds ? "HOLDS last value" : "returns to default")} (guess)" +
                  (missing.Count > 0 ? $"; WxSnow_ keys missing, W6.21 values used: {string.Join(",", missing)}" : "");
            return true;
        }

        void Build()
        {
            _ps = gameObject.AddComponent<ParticleSystem>();
            _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = _ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f;
            main.startLifetime = 1e6f;
            main.gravityModifier = 0f;
            main.maxParticles = Mathf.Max(1, _cap);
            main.playOnAwake = false;
            var emission = _ps.emission; emission.enabled = false;
            var shape = _ps.shape; shape.enabled = false;
            var pr = GetComponent<ParticleSystemRenderer>();
            pr.renderMode = ParticleSystemRenderMode.Billboard;
            pr.alignment = ParticleSystemRenderSpace.View;
            pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pr.receiveShadows = false;
            // 2012 draws fx_snowballblur, alpha-blended (W6.21). The importer bakes it onto a
            // disabled "WxFlake" renderer; without one the soft procedural blob stands in.
            var m = new Material(SnowFall.FlakeMaterial()) { name = "HookZoneWeatherFlake" };
            if (_flakeTex != null) m.SetTexture("_BaseMap", _flakeTex);
            pr.material = m;
            _ps.Play();
            _buf = new ParticleSystem.Particle[Mathf.Max(1, _cap)];
        }

        // Last containing box by index wins. 2012's overlap order is unknown (W6.21: last
        // writer per tick); on newzeal0901 every precip box is 0.5, so it can't matter there.
        static int BoxAt(Box[] boxes, Vector3 p)
        {
            int hit = -1;
            for (int i = 0; i < boxes.Length; i++)
            {
                var b = boxes[i];
                if (b.t == null) continue;
                Vector3 l = b.t.InverseTransformPoint(p) - b.c;
                if (Mathf.Abs(l.x) <= b.half.x && Mathf.Abs(l.y) <= b.half.y && Mathf.Abs(l.z) <= b.half.z) hit = i;
            }
            return hit;
        }

        void LateUpdate()
        {
            if (_cam == null)
            {
                var c = Camera.main;
                if (c == null) return;
                _cam = c.transform;
            }
            Vector3 cp = _cam.position;   // query point = camera (plan §3; 2012's is the view)
            float dt = Time.deltaTime;
            _t += dt;

            int box = BoxAt(_boxes, cp);
            if (box >= 0) _precip = _boxes[box].v;
            else if (!_exitHolds) _precip = _defaultPrecip;
            if (box != _lastBox)
            {
                // Info on purpose: the only way to tell "no volume here" from "snow isn't drawing".
                Plugin.Log.LogInfo($"[SnowFall] 2012 precip volume {(_lastBox >= 0 ? _boxes[_lastBox].idx : -1)} -> " +
                                   $"{(box >= 0 ? _boxes[box].idx : -1)} at ({cp.x:F0}, {cp.y:F0}, {cp.z:F0}): " +
                                   $"precip {_precip:0.##} = {(int)(Mathf.Clamp01(_precip) * _cap)} flakes");
                _lastBox = box;
            }

            // Wind: same step + exit rule as precip (2012's WindSpeedTrack is re-applied per tick
            // like precip — INFERRED from the shared script shape, W6.21).
            int wb = BoxAt(_wind, cp);
            if (wb >= 0) _windVel = _wind[wb].t.forward * (_wind[wb].v * _windUnit * WindResponse);
            else if (!_exitHolds) _windVel = _defaultDir * (_defaultWind * _windUnit * WindResponse);
            if (wb != _lastWind)
            {
                Plugin.Log.LogInfo($"[SnowFall] 2012 wind volume {(_lastWind >= 0 ? _wind[_lastWind].idx : -1)} -> " +
                                   $"{(wb >= 0 ? _wind[wb].idx : -1)}: drift ({_windVel.x:F1}, {_windVel.y:F1}, {_windVel.z:F1}) u/s");
                _lastWind = wb;
            }
            _windOff += _windVel * dt;

            float half = 0.5f * _wrap, side = _wrap, inv = 1f / side;
            if (!_seeded)
            {
                _base = new Vector3[_cap]; _fall = new float[_cap]; _size = new float[_cap];
                for (int i = 0; i < _cap; i++)
                {
                    _base[i] = cp + new Vector3(UnityEngine.Random.Range(-half, half),
                                                UnityEngine.Random.Range(-half, half),
                                                UnityEngine.Random.Range(-half, half));
                    // "mean ± var" read as a uniform spread — guess, the lnx distribution isn't decoded.
                    _fall[i] = Mathf.Max(0.1f * _fallMean, _fallMean + UnityEngine.Random.Range(-_fallVar, _fallVar));
                    _size[i] = Mathf.Max(0f, _sizeMean + UnityEngine.Random.Range(-_sizeVar, _sizeVar)) * _sizeScale;
                }
                _seeded = true;
            }

            if (_roofMask == 0) _roofMask = RoofMask();
            // QueryTriggerInteraction.Ignore, unlike TM's UseGlobal: an overhead trigger (finish
            // box, launch volume) is not a roof — deliberate deviation.
            bool roof = Physics.Raycast(cp, Vector3.up, RoofRay, _roofMask, QueryTriggerInteraction.Ignore);
            if (roof != _underRoof)
            {
                Plugin.Log.LogInfo($"[SnowFall] 2012 snow {(roof ? "under a roof — stopping" : "out from under a roof — resuming")} at ({cp.x:F0}, {cp.y:F0}, {cp.z:F0})");
                _underRoof = roof;
            }
            _roof = Mathf.MoveTowards(_roof, roof ? 0f : 1f, dt / RoofRamp);

            int n = Mathf.Clamp((int)(Mathf.Clamp01(_precip) * _roof * _cap), 0, _cap);
            float fadeStart = FadeFrac * half, fadeSpan = half - fadeStart;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = _base[i] + _windOff - cp;
                d.y -= Mathf.Repeat(_fall[i] * _t, side);
                d.x -= side * Mathf.Floor((d.x + half) * inv);
                d.y -= side * Mathf.Floor((d.y + half) * inv);
                d.z -= side * Mathf.Floor((d.z + half) * inv);
                float dist = d.magnitude;
                float fade = dist <= fadeStart ? 1f : dist >= half ? 0f : (half - dist) / fadeSpan;
                _buf[i].position = cp + d;
                _buf[i].velocity = Vector3.zero;
                _buf[i].startLifetime = 1e6f;
                _buf[i].remainingLifetime = 1e6f;
                _buf[i].startSize = _size[i];
                _buf[i].startColor = new Color(1f, 1f, 1f, _alpha * fade);
            }
            _ps.SetParticles(_buf, n);
        }

        // Snowboarder.CastLayerMask is 0 until a Snowboarder builds it; same exclusions rebuilt
        // from layer names otherwise (IL-verified list).
        static int RoofMask()
        {
            if (Snowboarder.CastLayerMask != 0) return Snowboarder.CastLayerMask;
            int ex = 0;
            foreach (var n in new[] { "Rail", "Ignore Raycast", "OOB", "PipeWall", "Snowboarder", "Ragdoll", "PhysicsProp", "Caster" })
            {
                int l = LayerMask.NameToLayer(n);
                if (l >= 0) ex |= 1 << l;
            }
            return ~ex;
        }

        void OnDestroy() { Active = false; }
    }
}
