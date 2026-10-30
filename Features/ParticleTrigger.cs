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
    // Trigger-fired SSX particle effects (feature: lighting.particleTriggers — fireworks, pyro).
    // Naming contract with SSXImporter.BuildParticleEmitters:
    //   PfxShot_<host>_<hdr>     — group of stopped ParticleSystems at the SSX host instance.
    //   ParticleTrig_<trig>      — trigger box (disabled BoxCollider = geometry only, layer 2).
    //     PfxTarget_<host>_<hdr> — one child per group it fires; the join is by name, N:M.
    // Detection is AnimDoorTrigger's swept segment for the same tunnelling reason. SSX consumes
    // the volume on first contact (DeadNodeMode 0.5), so this fires ONCE per scene load, for
    // whichever rider (player or COM) crosses first — and, like the doors, nothing re-arms it
    // on a restart, which reloads the scene anyway.
    //     PfxImpact_<rearm|once>  — contact-on-self host (lighting.particleImpacts: hydrants,
    //                               bumpers, balloons). Fires on a fresh ENTRY only (a rider
    //                               pressed against the host does not re-fire it), re-arms
    //                               after <rearm> s (SSX's Debounce; 0 = next entry) or never
    //                               ("once" = DeadNodeMode), and posts no sound — none of those
    //                               headers carries an MT8, so borrowing Play_Firework would lie.
    //     PfxHide_<instance>      — impact only: a scene GO (resolved by original SSX instance
    //                               name) to SetActive(false) when it fires — SSX's MT7 into a
    //                               DeadNodeMode-only header, i.e. a model swap (merquer's
    //                               Trigger_Highway consuming Highway_Good).
    //     PfxHideSelf             — impact "once" riding a mover (snow's balloons): disable the
    //                               enclosing MoveSurf_'s mesh renderers when it fires. Renderers,
    //                               not SetActive — the PfxShot_ burst is parented under it.
    public class ParticleTrigger : MonoBehaviour
    {
        public List<ParticleSystem> targets = new List<ParticleSystem>();
        public List<GameObject> hides = new List<GameObject>();
        public List<Renderer> hideRenderers = new List<Renderer>();   // PfxHideSelf
        public bool impact;
        public float rearm = -1f;   // impact only; < 0 = once
        // Floor on rearm 0 so a rider grazing the box edge can't restart the burst every tick.
        // Guessed, not measured.
        const float MinRearm = 0.25f;
        float _armAt;

        Vector3 _halfLocal = new Vector3(0.5f, 0.5f, 0.5f);
        Vector3 _center = Vector3.zero;
        Snowboarder[] _riders;
        readonly Dictionary<Snowboarder, Vector3> _prev = new Dictionary<Snowboarder, Vector3>();
        bool _fired;

        void Awake()
        {
            var box = GetComponent<BoxCollider>();
            if (box != null)
            {
                _halfLocal = box.size * 0.5f;
                _center = box.center;
                box.enabled = false;            // geometry carrier only
            }
        }

        void FixedUpdate()
        {
            if (_fired || targets.Count == 0) return;
            if (_riders == null || _riders.Length == 0)
            {
                _riders = UnityEngine.Object.FindObjectsOfType<Snowboarder>();
                if (_riders.Length == 0) return; // riders not spawned yet
            }
            foreach (var sb in _riders)
            {
                if (sb == null) continue;
                Vector3 cur = transform.InverseTransformPoint(sb.transform.position);
                Vector3 prev = _prev.TryGetValue(sb, out var p) ? p : cur;
                _prev[sb] = cur;
                if (!AnimDoorTrigger.SegmentHitsBox(prev, cur, _center, _halfLocal)) continue;
                if (impact && (Inside(prev) || Time.time < _armAt)) continue;
                Fire();
                return;
            }
        }

        void Hide()
        {
            foreach (var g in hides)
                if (g != null) g.SetActive(false);
            foreach (var r in hideRenderers)
                if (r != null) r.enabled = false;
        }

        bool Inside(Vector3 q)
        {
            var d = q - _center;
            return Mathf.Abs(d.x) <= _halfLocal.x && Mathf.Abs(d.y) <= _halfLocal.y && Mathf.Abs(d.z) <= _halfLocal.z;
        }

        void Fire()
        {
            if (impact && rearm >= 0f)
            {
                _armAt = Time.time + Mathf.Max(rearm, MinRearm);
                foreach (var ps in targets)
                    if (ps != null) { ps.Clear(false); ps.Play(false); }
                Hide();
                Plugin.Log.LogDebug($"[ParticleTrig] impact '{name}' fired -> {targets.Count} emitter(s), re-arms in {Mathf.Max(rearm, MinRearm):F2} s");
                return;
            }
            _fired = true;
            if (impact)
            {
                foreach (var ps in targets)
                    if (ps != null) { ps.Clear(false); ps.Play(false); }
                Hide();
                Plugin.Log.LogInfo($"[ParticleTrig] impact '{name}' fired once -> {targets.Count} emitter(s), hid {hides.Count}");
                enabled = false;
                return;
            }
            // SSX gives each fired group one SoundPlay (MT8) record, but its sound ids mean nothing
            // to TM's Wwise banks. TM's own Fireworks posts Play_Firework (Snowboard_Bank, always
            // loaded) on its GO, so each group borrows that, posted on the group for 3D position.
            var groups = new HashSet<GameObject>();
            foreach (var ps in targets)
            {
                if (ps == null) continue;
                ps.Clear(false);
                ps.Play(false);
                var g = ps.transform.parent != null ? ps.transform.parent.gameObject : ps.gameObject;
                groups.Add(g);
            }
            int nSound = 0;
            foreach (var g in groups)
                if (AkSoundEngine.PostEvent("Play_Firework", g) != 0) nSound++;   // 0 = AK_INVALID_PLAYING_ID
            Plugin.Log.LogInfo($"[ParticleTrig] trigger '{name}' fired -> {targets.Count} emitter(s), Play_Firework {nSound}/{groups.Count} group(s)");
            enabled = false;
        }
    }
}
