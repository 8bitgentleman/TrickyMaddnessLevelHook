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
    // A teleport volume. Attached by the Hook's scene scan to every "Teleport_*" GO the
    // importer emits (source box + a child "Target" transform for the destination). When a
    // Snowboarder ENTERS the (oriented) box it is repositioned to the Target — edge-triggered
    // (once per entry; it must leave and re-enter to fire again), so it never loops even if
    // the destination is adjacent. Containment is analytic (same collider-free OBB test as
    // WindVolume — the box never participates in physics/casts).
    //
    // The reposition mirrors the game's own Snowboarder.Respawn: DISABLE the rider's
    // CharacterController (so set_position isn't fought by CC depenetration), set rotation to
    // the Target's facing (always definite — authored targetFacing, else the box direction),
    // set position seated by up*cc.radius (the exact seat Respawn uses so the capsule clears
    // the surface), RE-ENABLE the CC, then zero velocity.
    public class TeleportVolume : MonoBehaviour
    {
        Vector3 _halfLocal = new Vector3(0.5f, 0.5f, 0.5f);
        Vector3 _targetPos;
        Quaternion _targetRot = Quaternion.identity;

        Snowboarder[] _riders;
        readonly Dictionary<Snowboarder, Traverse> _vel = new Dictionary<Snowboarder, Traverse>();
        readonly Dictionary<Snowboarder, Traverse> _cc  = new Dictionary<Snowboarder, Traverse>();
        readonly HashSet<Snowboarder> _inside = new HashSet<Snowboarder>();

        void Awake()
        {
            var box = GetComponent<BoxCollider>();
            if (box != null)
            {
                _halfLocal = box.size * 0.5f;
                box.enabled = false; // geometry carrier only
            }
            var target = transform.Find("Target");
            if (target != null)
            {
                _targetPos = target.position;
                // The importer always orients the Target to a definite landing facing
                // (authored targetFacing, else the box direction) — so we always seat the
                // rider at the Target's world rotation. No has-facing ambiguity.
                _targetRot = target.rotation;
            }
            else
            {
                enabled = false; // no destination — inert (the scan also guards this)
            }
        }

        void FixedUpdate()
        {
            if (_riders == null || _riders.Length == 0)
            {
                _riders = UnityEngine.Object.FindObjectsOfType<Snowboarder>();
                if (_riders.Length == 0) return; // riders not spawned yet
            }
            foreach (var sb in _riders)
            {
                if (sb == null) continue;
                Vector3 local = transform.InverseTransformPoint(sb.transform.position);
                bool inside = Mathf.Abs(local.x) <= _halfLocal.x
                           && Mathf.Abs(local.y) <= _halfLocal.y
                           && Mathf.Abs(local.z) <= _halfLocal.z;

                if (!inside) { _inside.Remove(sb); continue; }
                if (!_inside.Add(sb)) continue;   // already counted as inside — edge-trigger only
                Teleport(sb);
            }
        }

        void Teleport(Snowboarder sb)
        {
            if (!_cc.TryGetValue(sb, out var ccT))
            { ccT = Traverse.Create(sb).Field("characterController"); _cc[sb] = ccT; }
            var cc = ccT.GetValue<CharacterController>();

            if (cc != null) cc.enabled = false;                 // let set_position win over CC
            sb.transform.rotation = _targetRot;
            float radius = cc != null ? cc.radius : 0f;
            sb.transform.position = _targetPos + sb.transform.up * radius;  // Respawn's seat
            if (cc != null) cc.enabled = true;

            if (!_vel.TryGetValue(sb, out var velT))
            { velT = Traverse.Create(sb).Field("velocity"); _vel[sb] = velT; }
            velT.SetValue(Vector3.zero);
        }
    }
}
