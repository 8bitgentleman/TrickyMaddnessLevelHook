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
    // A LAUNCH VOLUME — the rider's velocity is SET to WorldDir * Speed the moment they
    // cross into the box, replaced rather than added to. This is Tokyo Megaplex's fan
    // shaft, and it is a general primitive: SSX Tricky's own lap and boost verbs are all
    // one-shot launches off an invisible collision box, never sustained fields.
    //
    // WHY. megaple is not a single descent. In SSX Tricky you ride almost to the bottom, a
    // glass fan shaft blasts you back up to the top of the course, you do that twice, and
    // on the THIRD descent the lift is off and you ride through to the finish. All of that
    // is recovered, measured, from the course's own data (ssx/SSFLOGIC_FINDINGS.md):
    //   * Mdl_Endboost_Lap_1000 — an invisible PlayerCollision box at the FOOT of the glass
    //     shaft (raw elev -46,894..-43,814) carrying SSFLogic MainType 0 / SubType 15, the
    //     lap verb. The finishing race line runs straight through it (19 densified samples
    //     inside), which is exactly the gimmick: crossing it launches you on passes 1 and 2,
    //     and on pass 3 it is disarmed so the same line carries you to the finish.
    //   * Mdl_Endboost_Z_1000 — SubType 18, the mid-shaft second-stage launch (U6 400000).
    //   * Mdl_Endboost_End_1000 — SubType 24 at the TOP (+1,772), carrying three unit
    //     direction vectors and three speeds (27/35/35). All three aim at
    //     Mdl_EndholeCover_A_1000, the rideable deck at the course start. So "back to the
    //     top" is a ballistic throw across open air, not a teleport: megaple contains ZERO
    //     top-level MainType-24 teleport records.
    // The caps over the shaft (Mdl_Cap_A/B_1000) are PlayerCollision:false — render-only
    // lids you pass straight through — and all three Endboost boxes are invisible, so the
    // importer's invisible-barrier drop removes them and nothing is there in the converted
    // map. These volumes put the mechanism back.
    //
    // WHY SET, NOT PUSH. A native AirStream (§11) replaces gravity while you are inside it,
    // which sounds like the shaft but is not: over the shaft's ~1,146-unit height it spits
    // the rider out at ~200 u/s and oscillates, and the entry impulse leaves enough residual
    // sideways velocity to drift ~114 units over the climb — inside a tube of ~25-unit
    // radius. Setting the velocity zeroes that drift, so the rider goes straight up the
    // axis, and it matches what the source game's own verbs do.
    //
    // COUNTING. Per RIDER (Dictionary<Snowboarder,int>), so an AI cannot consume the
    // player's allowance and every rider gets the same run. Laps <= 0 means the volume
    // fires every time (an ordinary boost pad); Laps = N disarms after N launches for that
    // rider. Edge-triggered on the outside->inside transition, so sitting in the box does
    // not re-fire, and a rider who leaves and comes back gets the next launch.
    //
    // The box is a BoxCollider on this GO, DISABLED at runtime for the same reason
    // WindVolume disables its own: the game's ground SphereCast hits triggers by default
    // (Caster.Cast uses the layerMask-only overload), so a live trigger on a cast-included
    // layer reads as ground and floats the rider. Containment is the analytic OBB test.
    public class LaunchVolume : MonoBehaviour
    {
        public Vector3 WorldDir;   // world launch direction (normalized in Awake)
        public float Speed;        // magnitude the velocity is SET to, Unity units/s
        public int Laps;           // launches per rider before disarming; <= 0 = unlimited
        public bool Center;        // slide the rider onto the box axis before throwing

        Vector3 _halfLocal = new Vector3(0.5f, 0.5f, 0.5f);
        Vector3 _centerLocal = Vector3.zero;
        Snowboarder[] _riders;
        readonly Dictionary<Snowboarder, Traverse> _vel = new Dictionary<Snowboarder, Traverse>();
        readonly Dictionary<Snowboarder, int> _fired = new Dictionary<Snowboarder, int>();
        readonly HashSet<Snowboarder> _inside = new HashSet<Snowboarder>();

        void Awake()
        {
            WorldDir = WorldDir.sqrMagnitude > 1e-8f ? WorldDir.normalized : transform.forward;
            var box = GetComponent<BoxCollider>();
            if (box != null)
            {
                _halfLocal = box.size * 0.5f;
                _centerLocal = box.center;
                box.enabled = false; // geometry carrier only — never a live collider at runtime
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
                if (!_inside.Add(sb)) continue;      // already in the box — not a new entry

                int n;
                _fired.TryGetValue(sb, out n);
                if (Laps > 0 && n >= Laps) continue; // spent for this rider — ride on through
                _fired[sb] = n + 1;

                if (!_vel.TryGetValue(sb, out var t))
                {
                    t = Traverse.Create(sb).Field("velocity");
                    _vel[sb] = t;
                }
                // Setting velocity zeroes the rider's lateral SPEED but not their lateral
                // OFFSET, and the offset is what decides where the throw lands. On megaple
                // the entry offset was measured at 21.89 units against a box half-extent of
                // 21.94 — the rider entered hugging the wall, rode straight up it, and the
                // solved exit arc (aimed from the box axis) would have missed its deck by
                // ~23 units. Centring drops the component of the offset PERPENDICULAR to the
                // throw, so the arc always starts on the axis the direction was solved from,
                // and leaves the along-axis component alone so the volume still fires and
                // lands where the author aimed it.
                float centred = 0f;
                if (Center)
                {
                    Vector3 c = transform.TransformPoint(_centerLocal);
                    Vector3 off = sb.transform.position - c;
                    Vector3 target = c + Vector3.Project(off, WorldDir);
                    centred = Vector3.Distance(sb.transform.position, target);
                    // A CharacterController owns its transform; writing position with it
                    // enabled is fought or ignored (same rule as the respawn teleport, §23).
                    var cc = sb.GetComponent<CharacterController>();
                    bool wasEnabled = cc != null && cc.enabled;
                    if (wasEnabled) cc.enabled = false;
                    sb.transform.position = target;
                    if (wasEnabled) cc.enabled = true;
                }

                t.SetValue(WorldDir * Speed);

                // Leave the ground the way the game's own LaunchPad tag does. Telemetry shows
                // the rider goes airborne unaided on the next step, but Integrate()'s grounded
                // branch can project velocity onto the ground plane before that, and we have
                // just moved the rider — so make the transition explicit rather than racing it.
                // All three members are private on Snowboarder; a miss must not kill the volume.
                if (Traverse.Create(sb).Method("Grounded").GetValue<bool>())
                {
                    try
                    {
                        Traverse.Create(sb).Field("tookOffFromGround").SetValue(true);
                        Traverse.Create(sb).Method("ResetCaster").GetValue();
                        Traverse.Create(sb).Method("Takeoff").GetValue();
                    }
                    catch (Exception ex) { Plugin.Log.LogWarning($"[Launch] '{name}': takeoff relay failed: {ex.Message}"); }
                }

                Plugin.Log.LogInfo((Laps > 0
                    ? $"[Launch] '{name}': fired {n + 1} of {Laps} for '{sb.name}' at {Speed:0.#} u/s"
                    : $"[Launch] '{name}': fired for '{sb.name}' at {Speed:0.#} u/s")
                    + (Center ? $" (centred {centred:0.##} u onto the box axis)" : ""));
            }
        }
    }
}
