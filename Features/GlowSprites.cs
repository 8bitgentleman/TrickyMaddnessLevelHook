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
        static void AttachGlowSprites(BuildContext ctx, List<string> blocks)
        {
            var levelManagerMaker = ctx.Maker;
            // Engine-drawn lamp halos. Naming contract with SSXImporter.BuildGlowSprites:
            // a "GlowSprites*" MeshRenderer baked DISABLED whose vertex channels carry the
            // halos. GlowSprites rebuilds the camera-facing quads every camera and enables
            // the renderer only once the data reads back whole.
            int glows = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("GlowSprites") || t.GetComponent<GlowSprites>() != null) continue;
                    var gs = t.gameObject.AddComponent<GlowSprites>();
                    if (!gs.Init()) { UnityEngine.Object.Destroy(gs); continue; }
                    glows++;
                    var gm = t.GetComponent<MeshFilter>().sharedMesh;
                    Log.LogInfo($"[Glow] '{t.name}': {gm.vertexCount / 4} halo quads driven");
                    LoadClock.Mark("Glow");
                }
            }
            blocks.Add($"glow={glows}");
        }
    }

    // Engine-drawn light halos (X-Games Snowboarder's glow sprites; contract is source-
    // agnostic). The importer bakes Glows.json into ONE mesh named "GlowSprites*" whose
    // verts are all parked on their halo centre and whose renderer is baked DISABLED --
    // every behaviour is camera-dependent, so the bake is data and we build the quads.
    // Vertex layout is SSXImporter.BuildGlowSprites' comment; the two must match.
    //
    // Per camera, in beginCameraRendering (after all Updates, zero lag, and per camera so
    // split-screen views each get quads facing THEM -- URP renders each camera straight
    // after its callback, so rewriting one shared mesh per camera is consistent):
    //   fade   b = 1 to near, linear to 0 at far, hidden at/after far or below 1/51
    //   flash  on-phase: intensity 1 (or rises OnAdd per 1/60 s), size lerps start->end;
    //          off-phase: intensity *= OffMultiplier per 1/60 s, size holds at end.
    //          Takes precedence over slowfade, as the arcade's update does.
    //   slow   b eases 10% per 1/60 s toward that target (state per camera)
    //   dist   size *= 1 + 4(d - (far-near))/near once d > far-near
    //   bias   quad slides Bias toward the camera;  viewrot spins by -pi(1+dot(axis,toCam))
    // Hotspot quads (flag 16) share their main quad's brightness, flash and scale.
    public class GlowSprites : MonoBehaviour
    {
        const int FDist = 1, FSlow = 2, FRot = 4, FFlash = 8, FHot = 16;
        const float Tick = 1f / 60f, MinIntensity = 1f / 51f;

        Mesh _mesh;
        Vector3[] _c, _axis, _v;          // per quad centre/axis; per vert output
        Color[] _col, _outCol;             // per quad base colour; per vert output
        Vector4[] _a, _b, _fl, _fs;        // per quad: (size,bias) (near,far,flags) flash scale
        int[] _sprite;                     // quad -> sprite index (hotspots follow their main quad)
        float[] _phase, _period, _onFrac;  // per sprite, jittered once
        float[] _cx, _cy;                  // per vert corner (+-0.5)
        readonly Dictionary<int, float[]> _slow = new Dictionary<int, float[]>();
        readonly Dictionary<int, int> _slowFrame = new Dictionary<int, int>();
        bool _linear;
        int _nQuads, _nSprites;

        public bool Init()
        {
            var mf = GetComponent<MeshFilter>();
            var mr = GetComponent<MeshRenderer>();
            var src = mf != null ? mf.sharedMesh : null;
            if (src == null || mr == null) { Plugin.Log.LogWarning($"[Glow] '{name}' has no mesh/renderer; skipped"); return false; }
            if (!src.isReadable) { Plugin.Log.LogWarning($"[Glow] '{name}' mesh is not readable; glows stay off"); return false; }
            int nv = src.vertexCount;
            if (nv == 0 || nv % 4 != 0) { Plugin.Log.LogWarning($"[Glow] '{name}' has {nv} verts (need 4 per quad); skipped"); return false; }
            var pos = src.vertices; var nrm = src.normals; var col = src.colors;
            var l1 = new List<Vector4>(); var l2 = new List<Vector4>(); var l3 = new List<Vector4>(); var l4 = new List<Vector4>();
            src.GetUVs(1, l1); src.GetUVs(2, l2); src.GetUVs(3, l3); src.GetUVs(4, l4);
            if (nrm.Length != nv || col.Length != nv || l1.Count != nv || l2.Count != nv || l3.Count != nv || l4.Count != nv)
            {
                Plugin.Log.LogWarning($"[Glow] '{name}' is missing vertex channels (normals {nrm.Length} colors {col.Length} uv1-4 {l1.Count}/{l2.Count}/{l3.Count}/{l4.Count} of {nv}); glows stay off");
                return false;
            }
            _nQuads = nv / 4;
            _c = new Vector3[_nQuads]; _axis = new Vector3[_nQuads]; _col = new Color[_nQuads];
            _a = new Vector4[_nQuads]; _b = new Vector4[_nQuads]; _fl = new Vector4[_nQuads]; _fs = new Vector4[_nQuads];
            _sprite = new int[_nQuads];
            _cx = new float[nv]; _cy = new float[nv];
            int sprite = -1;
            for (int q = 0; q < _nQuads; q++)
            {
                int i = q * 4;
                _c[q] = pos[i]; _axis[q] = nrm[i]; _col[q] = col[i];
                _a[q] = new Vector4(l1[i].z, l1[i].w, 0f, 0f);
                _b[q] = l2[i]; _fl[q] = l3[i]; _fs[q] = l4[i];
                int flags = Mathf.RoundToInt(_b[q].z);
                if ((flags & FHot) == 0 || sprite < 0) sprite++;
                _sprite[q] = sprite;
                for (int k = 0; k < 4; k++) { _cx[i + k] = l1[i + k].x; _cy[i + k] = l1[i + k].y; }
            }
            _nSprites = sprite + 1;
            _phase = new float[_nSprites]; _period = new float[_nSprites]; _onFrac = new float[_nSprites];
            for (int q = 0; q < _nQuads; q++)
            {
                if ((Mathf.RoundToInt(_b[q].z) & FHot) != 0) continue;
                int s = _sprite[q];
                // Arcade per-instance init: +-10% on period and on-fraction plus a random
                // phase. Seeded by sprite index so a halo blinks the same way every load.
                var rng = new System.Random(s * 7919 + 17);
                float j = _fs[q].z;
                float p = Mathf.Max(Tick, _fl[q].x * (1f + j * (float)(rng.NextDouble() * 2.0 - 1.0)));
                _period[s] = p;
                _onFrac[s] = Mathf.Clamp01(_fl[q].y * (1f + j * (float)(rng.NextDouble() * 2.0 - 1.0)));
                _phase[s] = j > 0f ? (float)rng.NextDouble() * p : 0f;
            }

            _v = new Vector3[nv]; _outCol = new Color[nv];
            _mesh = new Mesh { name = "GlowSpritesRuntime" };
            if (nv > 65535) _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _mesh.MarkDynamic();
            _mesh.vertices = pos;
            _mesh.uv = src.uv;
            _mesh.colors = col;
            _mesh.triangles = src.triangles;
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);   // verts move every camera
            mf.sharedMesh = _mesh;
            mr.enabled = true;
            _linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            return true;
        }

        void OnEnable() { UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += OnBeginCamera; }
        void OnDisable() { UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OnBeginCamera; }
        void OnDestroy() { if (_mesh != null) Destroy(_mesh); }

        void OnBeginCamera(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (_mesh == null || cam == null || cam.cameraType != CameraType.Game) return;
            if ((cam.cullingMask & (1 << gameObject.layer)) == 0) return;
            try { Rebuild(cam); }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Glow] rebuild failed, glows disabled: {e}");
                enabled = false;
                GetComponent<MeshRenderer>().enabled = false;
            }
        }

        void Rebuild(Camera cam)
        {
            var ct = cam.transform;
            Vector3 eye = ct.position, right = ct.right, up = ct.up;
            // Local-space halo centres: the importer bakes world positions under an identity
            // root, but map-scale roots aren't guaranteed identity, so go through the matrix.
            var l2w = transform.localToWorldMatrix;
            var w2l = transform.worldToLocalMatrix;
            float tNow = Time.time;

            int id = cam.GetInstanceID();
            if (!_slow.TryGetValue(id, out var slow)) { slow = null; }
            bool stepSlow = !_slowFrame.TryGetValue(id, out int lastFrame) || lastFrame != Time.frameCount;
            float ease = 1f - Mathf.Pow(0.9f, Mathf.Max(0f, Time.deltaTime) / Tick);
            bool first = slow == null;
            if (first) { slow = new float[_nSprites]; _slow[id] = slow; }
            _slowFrame[id] = Time.frameCount;

            float spriteB = 0f, spriteScale = 1f;
            for (int q = 0; q < _nQuads; q++)
            {
                int i = q * 4;
                int flags = Mathf.RoundToInt(_b[q].z);
                bool hot = (flags & FHot) != 0;
                Vector3 c = l2w.MultiplyPoint3x4(_c[q]);
                Vector3 d = eye - c;
                float dist = d.magnitude;
                float near = _b[q].x, far = _b[q].y;

                if (!hot)   // hotspots reuse their main quad's brightness and scale
                {
                    int s = _sprite[q];
                    float b = 1f;
                    bool culled = false;
                    if (far > near)
                    {
                        if (dist >= far) { b = 0f; culled = true; }
                        else if (dist > near) b = 1f - (dist - near) / (far - near);
                    }
                    float scale = 1f;
                    if ((flags & FFlash) != 0)
                    {
                        float period = _period[s], on = _onFrac[s] * period;
                        float tt = Mathf.Repeat(tNow + _phase[s], period);
                        float inten;
                        if (tt < on)
                        {
                            float onAdd = _fl[q].w;
                            // OnAdd ramps from what the off-phase decayed to (closed form).
                            inten = onAdd > 0f
                                ? Mathf.Min(1f, Mathf.Pow(_fl[q].z, (period - on) / Tick) + onAdd * tt / Tick)
                                : 1f;
                            if (on > 0f) scale = Mathf.Lerp(_fs[q].x, _fs[q].y, tt / on);
                        }
                        else
                        {
                            inten = Mathf.Pow(_fl[q].z, (tt - on) / Tick);
                            scale = _fs[q].y;
                        }
                        b *= inten;
                    }
                    else if ((flags & FSlow) != 0)
                    {
                        // Initialised to the target on a camera's first frame (guess: the
                        // arcade's init value isn't known), then eased once per frame.
                        if (first) slow[s] = b;
                        else if (stepSlow) slow[s] += (b - slow[s]) * ease;
                        b = slow[s];   // at/after far it still eases toward 0 but is not drawn
                    }
                    if ((flags & FDist) != 0 && far > near)
                    {
                        float span = far - near;
                        if (dist > span && near > 0f) scale *= 1f + 4f * (dist - span) / near;
                    }
                    spriteB = culled || b < MinIntensity ? 0f : b;
                    spriteScale = scale;
                }

                float size = _a[q].x * spriteScale;
                if (spriteB <= 0f || size <= 0f)
                {
                    for (int k = 0; k < 4; k++) _v[i + k] = _c[q];   // degenerate: draws nothing
                    continue;
                }
                Vector3 dir = dist > 1e-5f ? d / dist : -ct.forward;
                Vector3 p = c + dir * _a[q].y;
                float cs = 1f, sn = 0f;
                if ((flags & FRot) != 0)
                {
                    float ang = -Mathf.PI * (1f + Vector3.Dot(l2w.MultiplyVector(_axis[q]).normalized, dir));
                    cs = Mathf.Cos(ang); sn = Mathf.Sin(ang);
                }
                Color bc = _col[q];
                float r = bc.r * spriteB, g = bc.g * spriteB, bl = bc.b * spriteB;
                if (_linear) { r = Mathf.GammaToLinearSpace(r); g = Mathf.GammaToLinearSpace(g); bl = Mathf.GammaToLinearSpace(bl); }
                var oc = new Color(r, g, bl, bc.a);
                for (int k = 0; k < 4; k++)
                {
                    float ox = (_cx[i + k] * cs - _cy[i + k] * sn) * size;
                    float oy = (_cx[i + k] * sn + _cy[i + k] * cs) * size;
                    _v[i + k] = w2l.MultiplyPoint3x4(p + right * ox + up * oy);
                    _outCol[i + k] = oc;
                }
            }
            _mesh.vertices = _v;
            _mesh.colors = _outCol;
        }
    }
}
