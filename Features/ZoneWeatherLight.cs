using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TrickyMaddnessLevelHook
{
    // SSX (2012)'s lighting volumes (ssx/SSX2012_WEATHER_PLAN.md Phase 5): inside a
    // trigger_lgt_whiteout box the scene blends to that box's preset over blendIn seconds and
    // back over blendOut. Naming contract with SSXImporter.BuildZoneWeather /
    // BakeZoneWeatherLight, under the same "ZoneWeather_<format>_<upm>" root as ZoneWeatherSnow:
    //   "WxBox_lighting_<idx>_<blendIn>_<blendOut>_<preset>"   preset LAST (names carry '_')
    //   "WxLight_<preset>"  children sun_<I>_<RRGGBB>, amb_<9 floats>, fog_<den>_<RRGGBB>,
    //                       sky_<tint>_<ground>_<exposure>, fill_<RRGGBB>  (values RESOLVED)
    // The base is whatever the scene shows on the first frame (BuildLighting's bake), so the
    // Hook carries no lighting defaults of its own. Blend shape is LINEAR in time (GUESS — 2012
    // states a duration only, W6.21); overlapping presets add, normalised past a sum of 1.
    public class ZoneWeatherLight : MonoBehaviour
    {
        class Preset
        {
            public string name;
            public bool hasSun, hasAmb, hasFog, hasSky, hasFill;
            public float sunI, fogDen, skyExp;
            public Color sunC, ambS, ambE, ambG, fogC, skyTint, skyGround, fill;
            public float w;          // current blend weight 0..1
            public bool inside;
            public float blendIn = 10f, blendOut = 10f;
        }
        struct Box { public Transform t; public Vector3 c, half; public Preset p; }

        Preset[] _presets;
        Box[] _boxes;
        Transform _cam;
        bool _based, _ownFog, _canFog;

        Light _sun;
        float _bSunI, _bFogDen, _bSkyExp;
        Color _bSunC, _bAmbS, _bAmbE, _bAmbG, _bFogC, _bSkyTint, _bSkyGround, _bFill;
        bool _bFog;
        FogMode _bFogMode;
        Material _sky;
        SkydomeFollow _dome;

        static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        static Color Hex(string s)
        {
            Color c;
            if (!ColorUtility.TryParseHtmlString("#" + s, out c)) throw new FormatException($"bad colour '{s}'");
            return c;
        }

        public static bool TrySpawn(Transform root, out string why)
        {
            var presets = new Dictionary<string, Preset>();
            var boxes = new List<Box>();
            var pending = new List<KeyValuePair<Transform, string[]>>();
            try
            {
                foreach (Transform c in root)
                {
                    if (c.name.StartsWith("WxLight_"))
                    {
                        var p = new Preset { name = c.name.Substring("WxLight_".Length) };
                        foreach (Transform k in c)
                        {
                            var a = k.name.Split('_');
                            switch (a[0])
                            {
                                case "sun":  p.hasSun = true; p.sunI = F(a[1]); p.sunC = Hex(a[2]); break;
                                case "amb":
                                    p.hasAmb = true;
                                    p.ambS = new Color(F(a[1]), F(a[2]), F(a[3]));
                                    p.ambE = new Color(F(a[4]), F(a[5]), F(a[6]));
                                    p.ambG = new Color(F(a[7]), F(a[8]), F(a[9]));
                                    break;
                                case "fog":  p.hasFog = true; p.fogDen = F(a[1]); p.fogC = Hex(a[2]); break;
                                case "sky":  p.hasSky = true; p.skyTint = Hex(a[1]); p.skyGround = Hex(a[2]); p.skyExp = F(a[3]); break;
                                case "fill": p.hasFill = true; p.fill = Hex(a[1]); break;
                            }
                        }
                        presets[p.name] = p;
                    }
                    else if (c.name.StartsWith("WxBox_lighting_"))
                    {
                        var a = c.name.Split('_');
                        if (a.Length < 6) throw new FormatException($"'{c.name}' is not WxBox_lighting_<i>_<in>_<out>_<preset>");
                        pending.Add(new KeyValuePair<Transform, string[]>(c, a));
                    }
                }
                foreach (var kv in pending)
                {
                    var a = kv.Value;
                    string name = string.Join("_", a, 5, a.Length - 5);
                    Preset p;
                    if (!presets.TryGetValue(name, out p))
                    {
                        why = $"'{kv.Key.name}' names preset '{name}' but no WxLight_{name} was baked; no lighting blend";
                        return false;
                    }
                    var bc = kv.Key.GetComponent<BoxCollider>();
                    if (bc == null) { why = $"'{kv.Key.name}' has no BoxCollider; no lighting blend"; return false; }
                    bc.enabled = false;
                    // One preset, one blend time in 2012's data (every whiteout volume is 10/10);
                    // if two boxes disagree the last one wins — judgment call.
                    p.blendIn = Mathf.Max(1e-3f, F(a[3]));
                    p.blendOut = Mathf.Max(1e-3f, F(a[4]));
                    boxes.Add(new Box { t = kv.Key, c = bc.center, half = 0.5f * bc.size, p = p });
                }
            }
            catch (Exception e) { why = $"malformed lighting marker under '{root.name}' ({e.Message}); no lighting blend"; return false; }
            if (boxes.Count == 0) { why = null; return false; }

            var go = new GameObject("HookZoneWeatherLight");
            var z = go.AddComponent<ZoneWeatherLight>();
            z._presets = new List<Preset>(presets.Values).ToArray();
            z._boxes = boxes.ToArray();
            why = $"{boxes.Count} lighting volume(s) over {presets.Count} preset(s) [{string.Join(",", presets.Keys)}], " +
                  "linear blend (shape is a guess)";
            return true;
        }

        void SnapshotBase()
        {
            _sun = RenderSettings.sun;
            if (_sun == null)
                foreach (var l in FindObjectsOfType<Light>())
                    if (l.type == LightType.Directional) { _sun = l; break; }
            if (_sun != null) { _bSunI = _sun.intensity; _bSunC = _sun.color; }
            _bAmbS = RenderSettings.ambientSkyColor;
            _bAmbE = RenderSettings.ambientEquatorColor;
            _bAmbG = RenderSettings.ambientGroundColor;
            _bFog = RenderSettings.fog;
            _bFogMode = RenderSettings.fogMode;
            _bFogDen = _bFog ? RenderSettings.fogDensity : 0f;
            _bFogC = RenderSettings.fogColor;
            // Only exp/exp2 fog blends by density. A linear base fog is left alone (logged).
            _canFog = !_bFog || _bFogMode != FogMode.Linear;
            _sky = RenderSettings.skybox;
            if (_sky != null && _sky.HasProperty("_SkyTint") && _sky.HasProperty("_GroundColor") && _sky.HasProperty("_Exposure"))
            {
                _bSkyTint = _sky.GetColor("_SkyTint");
                _bSkyGround = _sky.GetColor("_GroundColor");
                _bSkyExp = _sky.GetFloat("_Exposure");
            }
            else _sky = null;
            var domes = FindObjectsOfType<SkydomeFollow>();
            if (domes.Length > 0) { _dome = domes[0]; _bFill = _dome.Fill; }
            _based = true;
            Plugin.Log.LogInfo($"[ZoneWeather] lighting base captured: sun {(_sun != null ? _sun.name : "NONE")}, " +
                               $"fog {(_bFog ? _bFogMode.ToString() : "off")}{(_canFog ? "" : " (linear: fog not blended)")}, " +
                               $"sky {(_sky != null ? _sky.shader.name : "not procedural: not blended")}, dome {(_dome != null ? "yes" : "none")}");
        }

        void LateUpdate()
        {
            if (_cam == null)
            {
                var c = Camera.main;
                if (c == null) return;
                _cam = c.transform;
            }
            if (!_based) SnapshotBase();

            Vector3 p = _cam.position;
            foreach (var pr in _presets) pr.inside = false;
            foreach (var b in _boxes)
            {
                if (b.t == null) continue;
                Vector3 l = b.t.InverseTransformPoint(p) - b.c;
                if (Mathf.Abs(l.x) <= b.half.x && Mathf.Abs(l.y) <= b.half.y && Mathf.Abs(l.z) <= b.half.z) b.p.inside = true;
            }
            float dt = Time.deltaTime, sum = 0f;
            bool changed = false;
            foreach (var pr in _presets)
            {
                float w = Mathf.MoveTowards(pr.w, pr.inside ? 1f : 0f, dt / (pr.inside ? pr.blendIn : pr.blendOut));
                if (w != pr.w) changed = true;
                pr.w = w; sum += w;
            }
            if (!changed) return;
            Apply(sum > 1f ? 1f / sum : 1f, sum);
        }

        void Apply(float norm, float sum)
        {
            float sunI = _bSunI, fogDen = _bFogDen, skyExp = _bSkyExp;
            Color sunC = _bSunC, ambS = _bAmbS, ambE = _bAmbE, ambG = _bAmbG, fogC = _bFogC,
                  tint = _bSkyTint, ground = _bSkyGround, fill = _bFill;
            bool fogFromOff = false;
            foreach (var pr in _presets)
            {
                float w = pr.w * norm;
                if (w <= 0f) continue;
                if (pr.hasSun) { sunI += w * (pr.sunI - _bSunI); sunC += w * (pr.sunC - _bSunC); }
                if (pr.hasAmb) { ambS += w * (pr.ambS - _bAmbS); ambE += w * (pr.ambE - _bAmbE); ambG += w * (pr.ambG - _bAmbG); }
                if (pr.hasFog)
                {
                    fogDen += w * (pr.fogDen - _bFogDen);
                    // From no fog, the colour is the preset's outright (density carries the fade).
                    fogC += w * (pr.fogC - (_bFog ? _bFogC : pr.fogC));
                    if (!_bFog) { fogFromOff = true; fogC = pr.fogC; }
                }
                if (pr.hasSky) { tint += w * (pr.skyTint - _bSkyTint); ground += w * (pr.skyGround - _bSkyGround); skyExp += w * (pr.skyExp - _bSkyExp); }
                if (pr.hasFill) fill += w * (pr.fill - _bFill);
            }
            if (_sun != null) { _sun.intensity = sunI; _sun.color = sunC; }
            RenderSettings.ambientSkyColor = ambS;
            RenderSettings.ambientEquatorColor = ambE;
            RenderSettings.ambientGroundColor = ambG;
            if (_canFog)
            {
                bool active = sum > 0f;
                if (active && !_ownFog && !GameFog.Owned) { GameFog.Owned = true; _ownFog = true; }
                if (_ownFog)
                {
                    RenderSettings.fog = _bFog || (fogFromOff && fogDen > 0f);
                    if (!_bFog) RenderSettings.fogMode = FogMode.ExponentialSquared;
                    RenderSettings.fogDensity = Mathf.Max(0f, fogDen);
                    RenderSettings.fogColor = fogC;
                    if (!active) RestoreFog();
                }
            }
            if (_sky != null)
            {
                _sky.SetColor("_SkyTint", tint);
                _sky.SetColor("_GroundColor", ground);
                _sky.SetFloat("_Exposure", skyExp);
            }
            if (_dome != null) _dome.SetFill(fill);
        }

        void RestoreFog()
        {
            if (!_ownFog) return;
            RenderSettings.fog = _bFog;
            RenderSettings.fogMode = _bFogMode;
            if (_bFog) RenderSettings.fogDensity = _bFogDen;
            RenderSettings.fogColor = _bFogC;
            GameFog.Owned = false;
            _ownFog = false;
        }

        void OnDestroy()
        {
            if (!_based) return;
            // The skybox material is a bundle asset: put it back so a replay of the course
            // without a bundle reload doesn't start inside the whiteout.
            if (_sky != null)
            {
                _sky.SetColor("_SkyTint", _bSkyTint);
                _sky.SetColor("_GroundColor", _bSkyGround);
                _sky.SetFloat("_Exposure", _bSkyExp);
            }
            RestoreFog();
        }
    }
}
