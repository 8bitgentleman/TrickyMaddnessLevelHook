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
        // Rideable-ground layers a spawn marker may sit over: Default(0)/Ice(6)/
        // Rock(7)/Metal(14) — the surfaces Snowboarder treats as ground (Wall(8)
        // is bonk-not-ground, Water(4) is unreferenced; neither is ever a spawn
        // floor). Broader than the original 0|6 so a start over rock/metal snaps.
        const int SpawnGroundMask = (1 << 0) | (1 << 6) | (1 << 7) | (1 << 14);

        // Rider capsule geometry, measured ONCE from the prefab, used to seat a
        // marker so the capsule rests TANGENT to the surface without penetrating
        // it — the key to a clean start on a banked gate. A flat "feet on the
        // vertical surface point" seat sinks the capsule's uphill side into a
        // sloped surface; PhysX then ejects the rider along the surface normal
        // (mostly sideways on a steep bank) = the "shooting off to the side" bug.
        // radius: capsule radius. skin: CC skinWidth. bottomSphereLocalY: the
        // vertical offset, in the rider transform's local space, of the bottom
        // hemisphere's CENTER above the transform origin (the game seats the rider
        // transform on the marker). ok=false => probe failed, safe fallback used.
        struct RiderCapsule { public float radius, height, centerY, skin, bottomSphereLocalY; public bool ok; }

        static RiderCapsule _capsule;

        static bool _capsuleProbed;

        static RiderCapsule Capsule()
        {
            if (_capsuleProbed) return _capsule;
            _capsuleProbed = true;
            try
            {
                var rider = Resources.Load<GameObject>("Controllers/snowboarder");
                var cc = rider != null ? rider.GetComponentInChildren<CharacterController>(true) : null;
                if (cc != null)
                {
                    float r = Mathf.Max(0.01f, cc.radius);
                    float h = Mathf.Max(2f * r, cc.height); // Unity clamps CC height to >= 2*radius
                    _capsule = new RiderCapsule
                    {
                        radius = r,
                        height = h,
                        centerY = cc.center.y,
                        skin = cc.skinWidth,
                        bottomSphereLocalY = cc.center.y - (h * 0.5f - r),
                        ok = true,
                    };
                    Log.LogInfo($"[Spawn] rider capsule radius={r:F2} height={h:F2} center.y={cc.center.y:F2} skin={cc.skinWidth:F3}");
                    return _capsule;
                }
                Log.LogWarning("[Spawn] rider prefab 'Controllers/snowboarder' CharacterController not found; using fallback capsule");
            }
            catch (Exception e) { Log.LogWarning($"[Spawn] capsule probe failed ({e.Message}); using fallback capsule"); }
            _capsule = new RiderCapsule { radius = 0.3f, height = 0.9f, centerY = 0f, skin = 0.01f, bottomSphereLocalY = 0f, ok = false };
            return _capsule;
        }

        // Seats one spawn/respawn marker on the terrain below it. Casts down from
        // just above the authored point (bounded, so it finds the nearby surface,
        // not a distant ceiling under an overhang). Seats the capsule tangent to
        // the hit surface using its NORMAL: the bottom sphere sits radius+skin
        // clear along the normal, which needs (radius+skin)/cos(slope) of vertical
        // lift — more on a bank, so the uphill side never penetrates. In Both mode
        // the marker moves there whether that raises OR lowers it; Raise only ever
        // moves up. Caller gates out Off mode and syncs transforms once per batch.
        // The cast + capsule-seating half of SnapMarkerToGround, split out so the
        // race-line respawn markers below can reuse it. False = nothing on the
        // ground layers within reach, which the callers treat differently: a
        // spawn marker is left as authored, a race-line respawn is dropped.
        internal static bool GroundSeat(Vector3 p, out float y, out float terrainY, out float cosSlope)
        {
            y = p.y; terrainY = p.y; cosSlope = 1f;
            if (!Physics.Raycast(p + Vector3.up * 20f, Vector3.down, out var hit, 120f, SpawnGroundMask, QueryTriggerInteraction.Ignore))
                return false;
            var cap = Capsule();
            // cos of the slope under the marker; clamp so a near-vertical face (a
            // bad ray hit) can't demand an absurd lift.
            cosSlope = Mathf.Clamp(hit.normal.y, 0.2f, 1f);
            float clearance = (cap.radius + cap.skin + 0.02f) / cosSlope - cap.bottomSphereLocalY;
            if (clearance < 0.02f) clearance = 0.02f;
            terrainY = hit.point.y;
            y = hit.point.y + clearance;
            return true;
        }

        static void SnapMarkerToGround(Transform t, string label)
        {
            Vector3 p = t.position;
            if (!GroundSeat(p, out float target, out float terrainY, out float cosSlope))
            {
                Log.LogWarning($"[Spawn] no ground under {label} at {p} on layers 0|6|7|14 — left as authored");
                return;
            }
            float newY = Plugin.SpawnSnapMode == Plugin.SpawnSnap.Raise ? Mathf.Max(p.y, target) : target;
            if (Mathf.Abs(newY - p.y) > 0.01f)
            {
                t.position = new Vector3(p.x, newY, p.z);
                Log.LogInfo($"[Spawn] {label} snapped {p.y:F2} -> {newY:F2} (terrain y={terrainY:F2}, {(newY > p.y ? "+" : "")}{newY - p.y:F2}, slope {Mathf.Acos(cosSlope) * Mathf.Rad2Deg:F0}deg, clearance {target - terrainY:F2})");
            }
            else Log.LogInfo($"[Spawn] {label} already seated (y={p.y:F2}, terrain y={terrainY:F2}, slope {Mathf.Acos(cosSlope) * Mathf.Rad2Deg:F0}deg)");
        }
    }

    // Seats each start lane on the snow. LevelManager.Start instantiates every rider at
    // startingGate.position + gate.rotation * (laneX, 0.45, 0) — the player at
    // -5.25 + 1.5*slot, the COMs at -6.75 + 1.5k stepping over the player's slot
    // (read out of the IL) — so the lanes span ~9 m across at the marker's height and
    // ONLY the marker itself was ever ground-seated. A lane over a dip, or off the
    // edge of a narrow start deck, stands in the air or inside the slope, drops at
    // countdown release and — with nothing under it — falls to the ResetVolumes floor
    // and respawns at the nearest marker, which on SSX3 The Throne was ~92% down the
    // course: the 45 s "wins". Only ever RAISES a lane (a floating one just settles
    // at release), and a lane with no ground under it at all is walked in toward the
    // marker until it has some. Honours SpawnSnap Off. Custom maps only.
    [HarmonyPatch(typeof(LevelManager), "Start")]
    public class LevelManager_SeatStartLanes
    {
        const float GameLift = 0.45f;   // the game's own lane lift above the gate (IL)
        const float WalkStep = 0.25f;

        [HarmonyPostfix]
        public static void Postfix(LevelManager __instance)
        {
            if (string.IsNullOrEmpty(Plugin.LoadedAssetBundle)) return;
            if (Plugin.SpawnSnapMode == Plugin.SpawnSnap.Off) return;
            var gate = Traverse.Create(__instance).Field("startingGate").GetValue<Transform>();
            var riders = Traverse.Create(__instance).Field("snowboarders").GetValue<List<Snowboarder>>();
            if (gate == null || riders == null) return;
            foreach (var sb in riders)
            {
                if (sb == null) continue;
                try { Seat(sb, gate); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Spawn] lane seat failed for {sb.name}: {e.Message}"); }
            }
        }

        static void Seat(Snowboarder sb, Transform gate)
        {
            Vector3 p = sb.transform.position;
            float laneX = gate.InverseTransformPoint(p).x;
            Vector3 q = p; float target, terrainY, cos;
            bool found = Plugin.GroundSeat(q, out target, out terrainY, out cos);
            // No ground under the lane: walk it in along the gate's right axis, toward
            // the (seated, so known-good) marker, until the cast finds some.
            float walked = 0f;
            while (!found && Mathf.Abs(laneX) - walked > WalkStep)
            {
                walked += WalkStep;
                q = p - gate.right * (Mathf.Sign(laneX) * walked);
                found = Plugin.GroundSeat(q, out target, out terrainY, out cos);
            }
            if (!found)
            {
                Plugin.Log.LogWarning($"[Spawn] lane x={laneX:F2} {sb.name}: no ground under it or anywhere in to the marker — left as spawned at {p}");
                return;
            }
            float y = Mathf.Max(q.y, target + GameLift);
            if (walked == 0f && y - p.y < 0.01f)
            {
                Plugin.Log.LogInfo($"[Spawn] lane x={laneX:F2} {sb.name} on snow (y={p.y:F2}, terrain {terrainY:F2}, {p.y - terrainY:+0.00;-0.00} above)");
                return;
            }
            var to = new Vector3(q.x, y, q.z);
            var cc = Traverse.Create(sb).Field("characterController").GetValue<CharacterController>();
            if (cc != null) cc.enabled = false;
            sb.transform.position = to;
            if (cc != null) cc.enabled = true;
            Plugin.Log.LogInfo($"[Spawn] lane x={laneX:F2} {sb.name} seated {p} -> {to} (terrain {terrainY:F2}, raised {y - p.y:+0.00;-0.00}"
                + (walked > 0f ? $", walked {walked:F2} in toward the marker — no ground at its own lane)" : ")"));
        }
    }

    // Custom-map start-launch fix. The game launches every rider with a purely
    // VERTICAL kick — Snowboarder.UpdateStarting sets velocity = Vector3.up * -45
    // at the countdown's end — and the ground step then projects that onto the
    // terrain. On a stock course the start pad's fall-line points down-course, so
    // the projection sends the rider down-course. Custom-map starts often sit on
    // terrain that slopes SIDEWAYS relative to the intended line, so projecting a
    // vertical vector onto a sideways slope yields a sideways fall-line and the
    // rider shoots off left/right (and which side is random — it depends where in
    // the start line-up the rider lands).
    //
    // Two corrections, applied per rider from the frame it's released:
    //   * HUMAN: for ~0.3 s, rotate the horizontal velocity to the spawn facing
    //     (the authored down-course direction). Once the velocity carries a strong
    //     horizontal down-course term the ground projection preserves it, so the
    //     sideways fall-line never takes over. AI keep their own direction — they
    //     steer their assigned path and would fight an imposed heading.
    //   * ALL riders: a ONE-TIME horizontal speed trim by StartLaunchSpeedScale on
    //     the launch frame. The sideways projection preserves nearly the full kick,
    //     so the line-up comes out hot; trimming everyone equally keeps the human
    //     and the COM on even footing (trimming only the human would hand the COM a
    //     head start). Speed is scaled ONCE — re-scaling every frame would compound
    //     into a decay to zero; the game's drag/accel governs speed thereafter.
    public class StartLaunchRedirect : MonoBehaviour
    {
        public Vector3 CourseForward = Vector3.forward; // horizontal, normalized, down-course

        class Rider { public Traverse vel; public Vector3 startPos; public bool isHuman, released; public int active; }
        readonly Dictionary<Snowboarder, Rider> _riders = new Dictionary<Snowboarder, Rider>();
        bool _found;
        int _life;
        const int MaxLife = 1800;    // hard lifetime cap (~30 s) — never leak
        const int HumanFrames = 20;  // ~0.33 s of down-course steering off the line

        void FixedUpdate()
        {
            if (++_life >= MaxLife) { Destroy(gameObject); return; }
            if (!_found)
            {
                var all = UnityEngine.Object.FindObjectsOfType<Snowboarder>();
                if (all.Length == 0) return; // riders not spawned yet
                foreach (var sb in all)
                {
                    // Human vs AI: both are Snowboarder MonoBehaviours; the AI's
                    // private 'snowboardController' is an AISnowboarder, human's isn't.
                    var ctrl = Traverse.Create(sb).Field("snowboardController").GetValue();
                    _riders[sb] = new Rider {
                        vel = Traverse.Create(sb).Field("velocity"),
                        startPos = sb.transform.position,
                        isHuman = !(ctrl is AISnowboarder),
                    };
                }
                _found = true;
            }

            bool pending = false;
            foreach (var kv in _riders)
            {
                var sb = kv.Key; var r = kv.Value;
                if (sb == null) continue;
                Vector3 v = r.vel.GetValue<Vector3>();
                if (!r.released)
                {
                    // Release = the frame the rider first moves or gains velocity.
                    if (v.magnitude > 0.05f || (sb.transform.position - r.startPos).magnitude > 0.05f)
                        r.released = true;
                    else { pending = true; continue; }
                }
                float hspeed = new Vector2(v.x, v.z).magnitude;
                if (r.isHuman)
                {
                    if (r.active < HumanFrames)
                    {
                        if (hspeed > 0.01f && CourseForward.sqrMagnitude > 1e-4f)
                        {
                            if (r.active == 0)
                            {
                                hspeed *= Plugin.startLaunchSpeedScale.Value;
                                Plugin.Log.LogInfo($"[StartLaunch] human launch down-course {CourseForward} x{Plugin.startLaunchSpeedScale.Value} (was {new Vector3(v.x,0,v.z)})");
                            }
                            Vector3 nv = CourseForward * hspeed; nv.y = v.y;
                            r.vel.SetValue(nv);
                        }
                        r.active++;
                        pending = true;
                    }
                }
                else if (r.active == 0)
                {
                    // AI: one-time speed trim, direction preserved.
                    if (hspeed > 0.01f)
                    {
                        float k = Plugin.startLaunchSpeedScale.Value;
                        r.vel.SetValue(new Vector3(v.x * k, v.y, v.z * k));
                    }
                    r.active++;
                }
            }
            if (_found && !pending) { Plugin.Log.LogInfo("[StartLaunch] done"); Destroy(gameObject); }
        }
    }
}
