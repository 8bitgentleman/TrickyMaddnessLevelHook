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
    // A wind push volume. Attached by the Hook's scene scan to every "Wind_<accel>" GO the
    // importer emits. The number in the name is an ACCELERATION in Unity units/s^2: while a
    // Snowboarder's origin is inside the (oriented) box, each physics step adds Accel*dt along
    // WorldDir to that rider's velocity. Snowboarder.velocity is the field Integrate() writes
    // and UpdateGrounded() feeds to CharacterController.Move.
    //
    // The number is SSX's own BoostAmount passed through 1:1, with NO curve and NO cap. That
    // means TIME IN THE BOX is what a volume's effect scales with, which is the property the
    // courses are built around — see below.
    //
    // TWO EARLIER MODELS, BOTH WRONG, both worth not re-deriving:
    //
    //   * Until 2026-09-12 this was an acceleration too, but the importer multiplied the rating
    //     by an invented BoostAmountMultiplier curve that was quadratic across 50..100. megaple
    //     is the only course in that band, so its rating-60 conveyors came out at 270 u/s^2 —
    //     13x gravity in a course whose measured cruise is 41 u/s. That is the "Wind_270 is WAY
    //     too strong" report. The curve was the bug; the units were never the bug.
    //
    //   * For one build on 2026-09-12 the rating was read as a TARGET SPEED instead, capped and
    //     non-stacking. That reads plausibly off the numbers (45..200 against a measured 41 u/s
    //     cruise) but it is refuted by the geometry, in two places at once. megaple's exhaust
    //     vents are 8.2 units tall and sit 3.4 units under the XCrossRails they are meant to lift
    //     you onto — a cap has to be low to do that, but the same cap then makes alaska's END
    //     SHAFT (7 rating-45 boxes overlapping into a continuous 396-unit column) a nine-second
    //     elevator ride at a fixed 45 u/s. Reported as "slowly pushed the rest of the way".
    //     Acceleration resolves both: the vent's 0.18 s pass adds ~18 u/s, while the shaft's
    //     396 units accelerate the whole way.
    //
    // 1:1 is a JUDGMENT call, not a measurement — SSX's units are not TM's and nothing says the
    // scale factor is one. It is the simplest reading that fits every cluster in the corpus, and
    // it is 1/4.5 of the strength that drew the complaint. Overlapping boxes DO sum (alaska's
    // shaft overlaps in four consecutive pairs) and there is no ceiling, so a runaway is possible
    // in principle; the knob to reach for is the rating on the volume, not a constant in here.
    //
    // The box is described by a BoxCollider on this GO, but we do NOT use it as a physics
    // trigger: the game's ground SphereCast hits triggers by default (Caster.Cast uses the
    // layerMask-only overload → global Physics.queriesHitTriggers == true), so a live wind
    // trigger on a cast-included layer would read as ground and float the rider. Instead we
    // read the collider's size once, DISABLE it (so it never participates in physics or
    // casts), and do an analytic oriented-box containment test each FixedUpdate. That makes
    // the wind volume fully independent of the layer collision matrix and cast masks.
    public class WindVolume : MonoBehaviour
    {
        public Vector3 WorldDir;   // world push direction (normalized in Awake)
        public float Accel;        // acceleration along WorldDir, Unity units/s^2 (SSX BoostAmount, 1:1)

        Vector3 _halfLocal = new Vector3(0.5f, 0.5f, 0.5f); // local half-extents from the BoxCollider
        Snowboarder[] _riders;
        readonly Dictionary<Snowboarder, Traverse> _vel = new Dictionary<Snowboarder, Traverse>();

        void Awake()
        {
            WorldDir = WorldDir.sqrMagnitude > 1e-8f ? WorldDir.normalized : transform.forward;
            var box = GetComponent<BoxCollider>();
            if (box != null)
            {
                _halfLocal = box.size * 0.5f;
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
            float dt = Time.fixedDeltaTime;
            foreach (var sb in _riders)
            {
                if (sb == null) continue;
                // Oriented-box containment: rider origin into this GO's local space, compare
                // against the box half-extents. InverseTransformPoint folds in position,
                // rotation and (unit) scale, so this respects the box's orientation.
                Vector3 local = transform.InverseTransformPoint(sb.transform.position);
                if (Mathf.Abs(local.x) > _halfLocal.x ||
                    Mathf.Abs(local.y) > _halfLocal.y ||
                    Mathf.Abs(local.z) > _halfLocal.z) continue;

                if (!_vel.TryGetValue(sb, out var t))
                {
                    t = Traverse.Create(sb).Field("velocity");
                    _vel[sb] = t;
                }
                t.SetValue(t.GetValue<Vector3>() + WorldDir * (Accel * dt));
            }
        }
    }
}
