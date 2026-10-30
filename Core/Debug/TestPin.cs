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
        // Playtest start pin — the editor's SSX -> Playtest From Pin, and NOTHING else.
        // Moves a custom map's START to an arbitrary point on the course, so a spot
        // ninety seconds into a run can be tested without riding down to it on every
        // launch. Applied in LoadLevelManager BEFORE the ground-snap, so exactly like
        // an authored spawn marker only XZ and facing have to be right and Y is
        // computed (a spawnsnap-off map keeps the pin's own Y).
        //
        // ⚠️ COMMAND LINE ONLY, deliberately — there is no config key for this and there
        // must not be one. A pin is a debugging state that is WRONG for every ordinary
        // launch, and a config key is exactly the kind of state that gets set once and
        // silently outlives the session that wanted it. A launch flag cannot: Steam and
        // a double-click both start the game without one, so normal play is untouched
        // by construction rather than by remembering to clear something.
        internal static Vector3? TestPin;

        internal static float TestPinYaw;

        internal static bool TestPinHasYaw;

        // The track --tm-map named. The pin applies to THAT map only: a playtest run
        // routinely ends with quitting to the menu and picking something else, and a
        // pin from another course drops you in the sky above this one. --tm-pin without
        // --tm-map is honoured on every custom map, but the editor always sends both.
        internal static string TestPinMap;

        // "x,y,z" or "x,y,z,yaw", invariant culture — this arrives from a shell or a
        // config file, never from the user's locale. A malformed spec logs and leaves
        // the pin unset: starting at the gate is the right failure, since the
        // alternative is teleporting the rider to a half-parsed coordinate.
        private static void SetTestPin(string spec, string origin)
        {
            TestPin = null; TestPinHasYaw = false;
            if (string.IsNullOrEmpty(spec)) return;
            string[] f = spec.Split(',');
            if (f.Length < 3 || f.Length > 4)
            {
                Log.LogError($"[TestPin] {origin} \"{spec}\" is not \"x,y,z\" or \"x,y,z,yaw\" — ignored");
                return;
            }
            var v = new float[4];
            for (int i = 0; i < f.Length; i++)
                if (!float.TryParse(f[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                {
                    Log.LogError($"[TestPin] {origin} \"{spec}\": field {i + 1} is not a number — ignored");
                    return;
                }
            TestPin = new Vector3(v[0], v[1], v[2]);
            TestPinHasYaw = f.Length == 4;
            TestPinYaw = v[3];
            // Warning, not Info: a pin left set is indistinguishable in-game from a map
            // with a broken start marker, and this line is the whole difference.
            // Scope is decided by the caller (Awake sets TestPinMap from --tm-map), so this
            // only reports the parse. Warning, not Info: a run that starts somewhere other
            // than the gate should be findable in the log without knowing to look for it.
            Log.LogWarning($"[TestPin] {origin} parsed: {TestPin.Value.ToString("0.##", CultureInfo.InvariantCulture)}"
                + (TestPinHasYaw ? $" facing {TestPinYaw.ToString("0.#", CultureInfo.InvariantCulture)} deg" : " (facing unchanged)")
                + " — y is re-seated onto the terrain (RescuePinY), so only xz and facing matter.");
        }

        // Pin-only Y rescue. A pin's Y comes from a raycast in the EDITOR scene, so it is
        // ON the surface the moment it is placed — but it is stored per course and outlives
        // a rebuild, and a hand-typed --tm-pin has no raycast behind it at all. The ordinary
        // ground-snap probes a deliberately narrow band (marker y+20, 120 m down) and outside
        // it leaves the marker exactly as authored: for a respawn, silently relocating one by
        // 500 m would be a worse bug than the bad data that caused it. For a PIN the trade
        // inverts — "left as authored" means the run starts in empty space with the course off
        // somewhere else, which is the one thing a shorten-the-loop tool must not do. So when
        // the narrow probe misses, cast the whole column and re-seat, loudly.
        const float PinRescueUp   = 2000f;

        const float PinRescueDown = 8000f;

        static void RescuePinY(Transform t)
        {
            Vector3 p = t.position;
            if (GroundSeat(p, out _, out _, out _)) return;      // in band; the snap below has it

            if (!Physics.Raycast(new Vector3(p.x, p.y + PinRescueUp, p.z), Vector3.down,
                                 out var hit, PinRescueUp + PinRescueDown, SpawnGroundMask,
                                 QueryTriggerInteraction.Ignore))
            {
                Log.LogWarning($"[TestPin] nothing under the pin at x={p.x:F2} z={p.z:F2} in a "
                    + $"{PinRescueUp + PinRescueDown:F0} m column — the pin is off the course "
                    + "entirely, and the run WILL start in empty space");
                return;
            }
            // +2 rather than exactly on it: the ground-snap below seats it properly, and this
            // only has to land inside its band. A spawnsnap-off map keeps the 2 m as a drop.
            t.position = new Vector3(p.x, hit.point.y + 2f, p.z);
            Log.LogWarning($"[TestPin] pin y={p.y:F2} was off the surface (terrain here is "
                + $"{hit.point.y:F2}) — re-seated. An editor-placed pin lands on the surface, so "
                + "this usually means the pin predates a rebuild that moved this course's terrain.");
        }
    }
}
