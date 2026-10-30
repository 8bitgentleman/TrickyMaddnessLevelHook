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
        static void AttachUVScrollers(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
            // Animated textures (feature 14). Naming contract with SSXImporter.BuildProps:
            // a GO named "UVScroll_<u>_<v>_<originalName>" (rates written with the
            // invariant-culture round-trip format) renders SSX geometry whose material
            // UVs scroll — LCD scanlines, jumbotrons, boost-pad chevrons, river water.
            // The original name may itself contain underscores, so only the first three
            // '_'-separated segments are parsed. UVScrollBehaviour instantiates the
            // renderers' materials once and scrolls their base-map offset per frame.
            int uvAttached = 0, uvSkipped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("UVScroll_")) continue;
                    if (t.GetComponent<UVScrollBehaviour>() != null) continue; // already attached
                    string[] seg = t.name.Split(new[] { '_' }, 4);
                    float ru, rv;
                    if (seg.Length < 4 ||
                        !float.TryParse(seg[1], NumberStyles.Float, CultureInfo.InvariantCulture, out ru) ||
                        !float.TryParse(seg[2], NumberStyles.Float, CultureInfo.InvariantCulture, out rv))
                    {
                        uvSkipped++;
                        Log.LogDebug($"[UVScroll] '{t.name}' skipped: unparseable rates");
                        continue;
                    }
                    var us = t.gameObject.AddComponent<UVScrollBehaviour>();
                    us.RatePerFrame = new Vector2(ru, rv);
                    uvAttached++;
                }
            }
            if (uvAttached > 0 || uvSkipped > 0)
                Log.LogInfo($"[UVScroll] attached {uvAttached} UV-scroller(s), skipped {uvSkipped}");
            blocks.Add($"uvscroll={uvAttached}/{uvSkipped}");
            LoadClock.Mark("UVScroll");
        }
    }

    // Scrolls the material UVs of everything under this GO — the runtime half of the
    // animated-texture pipeline (SSX's UVScrolling effect: LCD scanlines, jumbotrons,
    // boost-pad chevrons, river water). The importer bakes the decoded rates into the
    // GO name; the Hook parses them and attaches this. renderer.materials instantiates
    // per-renderer material COPIES once in Awake — required, or scrolling one LCD would
    // scroll every material sharing that texture (the importer's material cache dedups
    // aggressively). The offset is absolute (rate * time), not accumulated, so playback
    // speed changes cleanly and no float error builds up; wrapped to [0,1) so precision
    // holds over long sessions.
    public class UVScrollBehaviour : MonoBehaviour
    {
        public Vector2 RatePerFrame; // UV units per frame @60Hz, parsed from the GO name

        Renderer[] _rends;
        Material[][] _mats;
        Vector2[][] _base;

        // URP's Particles/* shaders copy the vertex UV straight through and NEVER apply
        // _BaseMap_ST (ParticlesUnlitForwardPass -> GetParticleTexcoords: `outputTexcoord =
        // inputTexcoords.xy`; plain Unlit does `TRANSFORM_TEX`). The X-Games soft-edged
        // planes use Particles/Unlit because it is the one game shader that multiplies
        // vertex colour, so a material-offset scroll is written, read back correctly, and
        // draws NOTHING. For those renderers the offset goes into a private clone of the
        // mesh's UVs instead — shader-independent, and the material path is left alone
        // for every other renderer so no other course changes behaviour.
        Mesh[] _uvMesh;
        Vector2[][] _uvBase;
        Vector2[][] _uvTmp;

        // First-attached instance emits two one-line probes (~15s and ~30s into the
        // level) — shader name, written vs read-back offsets, and whether the renderer
        // still holds our instances. Cheap enough to leave on; it's the evidence for
        // the "attached but nothing moves" class of report (2026-07-16 playtest).
        static UVScrollBehaviour _diag;
        int _diagShots = 2;
        float _nextDiagAt = 15f;

        void Awake()
        {
            _rends = GetComponentsInChildren<Renderer>(true);
            _mats = new Material[_rends.Length][];
            _base = new Vector2[_rends.Length][];
            _uvMesh = new Mesh[_rends.Length];
            _uvBase = new Vector2[_rends.Length][];
            _uvTmp = new Vector2[_rends.Length][];
            for (int i = 0; i < _rends.Length; i++)
                Bind(i);
            if (_diag == null) _diag = this;
        }

        void OnDestroy()
        {
            if (_diag == this) _diag = null;
        }

        // renderer.materials instantiates per-renderer COPIES and assigns them —
        // required, or scrolling one LCD would scroll every material sharing that
        // texture (the importer's material cache dedups aggressively).
        void Bind(int i)
        {
            _mats[i] = _rends[i].materials;
            _base[i] = new Vector2[_mats[i].Length];
            for (int j = 0; j < _mats[i].Length; j++)
                _base[i][j] = _mats[i][j] != null ? _mats[i][j].mainTextureOffset : Vector2.zero;
            BindMesh(i);
        }

        // Switches renderer i to UV-scrolling its own mesh copy when EVERY material on it
        // is a Particles/* shader (all-or-nothing: shifting the mesh UVs would double-scroll
        // any non-Particles material sharing the mesh, and the soft-edged planes are all
        // single-segment). Idempotent — Bind() re-runs on self-heal, and must not re-clone.
        void BindMesh(int i)
        {
            var mats = _mats[i];
            bool allParticles = mats.Length > 0;
            for (int j = 0; j < mats.Length && allParticles; j++)
                allParticles = mats[j] != null && mats[j].shader != null &&
                    mats[j].shader.name.IndexOf("Particles", StringComparison.OrdinalIgnoreCase) >= 0;
            var mf = _rends[i].GetComponent<MeshFilter>();
            if (!allParticles || mf == null || mf.sharedMesh == null)
            {
                _uvMesh[i] = null;
                return;
            }
            if (ReferenceEquals(mf.sharedMesh, _uvMesh[i])) return;
            var src = mf.sharedMesh;
            if (!src.isReadable)
            {
                Plugin.Log.LogWarning("[UVScroll] '" + name + "': mesh '" + src.name +
                    "' is not readable, cannot UV-scroll a Particles-shader material");
                _uvMesh[i] = null;
                return;
            }
            var clone = Instantiate(src);
            clone.name = src.name + "_uvscroll";
            mf.sharedMesh = clone;
            _uvMesh[i] = clone;
            _uvBase[i] = clone.uv;
            _uvTmp[i] = new Vector2[_uvBase[i].Length];
        }

        void Update()
        {
            float fps = Plugin.uvScrollFrameRate != null ? Plugin.uvScrollFrameRate.Value : 60f;
            Vector2 scroll = RatePerFrame * fps * Time.time;
            Vector2 o = Vector2.zero;
            for (int i = 0; i < _rends.Length; i++)
            {
                var r = _rends[i];
                if (r == null) continue;
                // Self-heal: if anything replaced the renderer's material array after
                // our Awake (the copies we scroll would then be orphaned — writes land
                // on materials nobody renders), re-instantiate from the current array.
                var shared = r.sharedMaterials;
                if (_mats[i].Length != shared.Length ||
                    (shared.Length > 0 && !ReferenceEquals(shared[0], _mats[i][0])))
                    Bind(i);
                if (_uvMesh[i] != null)
                {
                    o = _base[i][0] + scroll;
                    o.x -= Mathf.Floor(o.x);
                    o.y -= Mathf.Floor(o.y);
                    var ub = _uvBase[i];
                    var ut = _uvTmp[i];
                    for (int k = 0; k < ub.Length; k++)
                        ut[k] = ub[k] + o;
                    _uvMesh[i].uv = ut;
                    continue;
                }
                for (int j = 0; j < _mats[i].Length; j++)
                {
                    var m = _mats[i][j];
                    if (m == null) continue;
                    o = _base[i][j] + scroll;
                    o.x -= Mathf.Floor(o.x);
                    o.y -= Mathf.Floor(o.y);
                    // URP/Lit tags _BaseMap [MainTexture], so mainTextureOffset targets
                    // it — but write the property explicitly too, so the scroll doesn't
                    // hang on the main-texture resolution of a remapped shader.
                    m.mainTextureOffset = o;
                    if (m.HasProperty("_BaseMap"))
                        m.SetTextureOffset("_BaseMap", o);
                }
            }

            if (_diag == this && _diagShots > 0 && Time.timeSinceLevelLoad >= _nextDiagAt)
            {
                _diagShots--;
                _nextDiagAt += 15f;
                Material m0 = null;
                if (_mats.Length > 0 && _mats[0].Length > 0) m0 = _mats[0][0];
                if (m0 != null && _rends.Length > 0 && _rends[0] != null)
                {
                    var shared = _rends[0].sharedMaterials;
                    bool bound = shared.Length > 0 && ReferenceEquals(shared[0], m0);
                    Vector2 rb = m0.HasProperty("_BaseMap") ? m0.GetTextureOffset("_BaseMap") : new Vector2(-9, -9);
                    Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "[UVScroll] diag t={0:F0}s '{1}': shader='{2}' wrote=({3:F3},{4:F3}) baseMapST=({5:F3},{6:F3}) mainOfs=({7:F3},{8:F3}) bound={9} rate=({10:R},{11:R}) via={12}",
                        Time.timeSinceLevelLoad, name, m0.shader != null ? m0.shader.name : "null",
                        o.x, o.y, rb.x, rb.y, m0.mainTextureOffset.x, m0.mainTextureOffset.y,
                        bound, RatePerFrame.x, RatePerFrame.y,
                        _uvMesh[0] != null ? "mesh-uv" : "material-offset"));
                }
            }
        }
    }
}
