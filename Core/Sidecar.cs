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
        // World scale of the active custom map, from the optional "scale <f>"
        // header line in its .aipaths.txt sidecar (gen_aipaths.py --scale).
        // 1.0 for built-in levels, sidecar-less maps, and maps with no scale line
        // — i.e. everything that exists today.
        internal static float WorldScale = 1f;

        // Per-map gravity multiplier from the optional "gravity <f>" header line in
        // the active custom map's .aipaths.txt sidecar. INDEPENDENT of WorldScale:
        // applies at 1x too, so a course can ask for heavier (>1) or lighter (<1)
        // gravity regardless of its size. 1.0 for built-in levels, sidecar-less
        // maps, and maps with no gravity line — i.e. everything that exists today.
        // Reset to 1 on every built-in selection and menu return (same lifetime
        // rule as WorldScale) so built-in gravity stays provably stock.
        internal static float MapGravityMultiplier = 1f;

        // Physics tick rate the active custom map asks for, from the optional
        // "physicshz <f>" header in its .aipaths.txt sidecar. 0 = no declaration
        // (built-in levels and every map that doesn't ask), which leaves the
        // game's own Time.fixedDeltaTime alone.
        //
        // WHY a map gets to ask: Unity runs FixedUpdate repeatedly to drag the
        // physics clock up to the frame clock, capped by Time.maximumDeltaTime.
        // The game ships fixedDeltaTime=0.01 (100 Hz — double Unity's own 50 Hz
        // default) with maximumDeltaTime=0.05, so a frame slower than 50 ms pins
        // at FIVE full FixedUpdate passes per rendered frame, and each slow frame
        // buys more of them. On the SSX3 whole-mountain chain that is exactly what
        // happens: measured 5.0 fixed steps per frame on every row of a 21-row
        // ablation sweep. Halving the rate was worth +54.5% (65 ms -> 29.7 ms,
        // 15.5 -> 33.7 fps) and dropped it to 1.5 steps/frame — i.e. the map
        // stopped pinning at the cap and started tracking real time again.
        //
        // ⚠️ The cost is NOT the PhysX solve. Stopping simulation outright
        // (simulationMode = Script, which leaves FixedUpdate still being CALLED)
        // measured inside the noise floor on the same sweep. What's expensive is
        // whatever runs per FixedUpdate iteration, paid five times a frame — so
        // the lever is the iteration COUNT, not the physics workload.
        //
        // ⚠️ Global setting, so it MUST be restored on every exit path, or a huge
        // map's rate silently follows the player into the next level and changes
        // the feel of a course that never asked for it. Same lifetime rule as
        // WorldScale: reset on built-in selection and on menu return.
        internal static float MapPhysicsHz = 0f;

        // Lateral catch half-width (METRES, absolute) the active custom map asks
        // for, from the optional "railcatch <f>" header in its .aipaths.txt
        // sidecar. 0 = no declaration, which keeps BuildRailCollider's original
        // `0.25f * WorldScale` — i.e. every map that shipped before this header
        // existed is bit-identical.
        //
        // WHY a map gets to ask: the WorldScale formula assumes a map's finished
        // SIZE is proportional to its scale header, which holds across the SSX
        // family only because the importer's S = scale/100 converts SSX
        // CENTIMETRES to metres. WildTangent's raw units are already metres, so
        // its scale header is 100 and S = 1.0 — the geometry passes through 1:1
        // and the course comes out the same size as every 2.5x port, while the
        // formula computes a 25 m catch radius for a 0.24 m rail. Absolute metres
        // is the only unit that is right for both families, and the header lets a
        // map say so without changing what the 153 already-deployed maps do.
        //
        // Same lifetime rule as WorldScale: reset on built-in selection and on
        // menu return, so a built-in level can never inherit a custom map's value.
        internal static float MapRailCatch = 0f;

        // Per-map gold PACE in world units/second, from the sidecar's `ridepace`
        // header. 0 = the map does not declare one, and then the medal rescue below
        // falls back to TB_RIDE_SPEED (45) — the number every already-deployed map
        // was built with. That sentinel is load-bearing for the same reason
        // MapRailCatch's is: it keeps the rescue a no-op on maps that never asked.
        //
        // The number itself is measured per course from real telemetry, by
        // ssx/make_pace_table.py; gen_aipaths.py copies it into the sidecar. Same
        // lifetime rule as WorldScale — reset on built-in selection and menu return.
        internal static float MapRidePace = 0f;

        // Per-map ROUTE scale, from the sidecar's `routescale` header: the
        // SSX-equivalent world scale the route-building, respawn-marker, medal and
        // rail-chaining tolerances are multiplied by. 0 = not declared, and then
        // RouteScale falls back to WorldScale — bit-identical for every map shipped
        // before this header existed.
        //
        // WHY: those tolerances are SSX-sized lengths (15 u respawn cut, 80 u
        // spine half-width, 100 u minimum split piece) scaled by WorldScale, which
        // assumes scale/100 is the SSX port factor. The Raw Thrills family and
        // Mad Trix declare `scale 139` as a raw->Unity ratio instead (1 raw = 1.39 u),
        // so the same tolerances come out ~55x too big: Ski Camp's respawn line was
        // cut 2088 u short of the finish and COM routes never split. Same idea as
        // MapRailCatch — the map states its real size. Same lifetime rule as
        // WorldScale: reset on built-in selection and on menu return.
        internal static float MapRouteScale = 0f;

        internal static float RouteScale =>
            MapRouteScale > 0f ? MapRouteScale : (WorldScale > 0f ? WorldScale : 1f);

        // The game's own fixedDeltaTime, captured once at Awake before anything
        // can have modified it. Restoring from a snapshot taken at apply time
        // would re-capture an already-overridden value if two custom maps loaded
        // back to back, permanently baking one map's rate in as "stock".
        internal static float StockFixedDeltaTime = 0.02f;

        // Platform the active custom map's bundle was authored native to, from an
        // optional "platform <windows|mac>" line in its .aipaths.txt sidecar. null
        // (every existing map, no header) means "unknown — assume the bytecode is
        // foreign to whatever GPU we're on", the historical always-remap behavior.
        // Declaring it lets ShaderLooksBroken trust the bundle's own shaders when
        // it runs on that platform: the original Windows-built Garibaldi ships
        // "platform windows" so its native D3D shaders are left alone on a Windows
        // box (restoring the pre-D3D-distrust look) yet still remapped on Metal,
        // where a Windows bundle genuinely is foreign. Reset to null on non-custom loads.
        internal static string BundleNativePlatform;

        // The default this Hook uses whenever a level declares no provenance —
        // OG Tricky Madness. Built-in courses and any sidecar-less drop-in ARE
        // the game's own content by construction, so absence == this value.
        internal const string DefaultSource = "tricky_madness";

        // Provenance of the active custom map, from the optional "source <slug>"
        // header in its .aipaths.txt sidecar (gen_aipaths.py --source). Purely
        // informational — logged so a bug report / log tells you which game a
        // loaded map came from. Slugs in use: "ssx_tricky" (SSX Tricky ports +
        // community customs) and "ssx3" (SSX 3 ports). Defaults to DefaultSource
        // ("tricky_madness") on every non-custom load and whenever the header is
        // absent. Nothing keys behavior off it — it exists only to answer
        // "where is this level from?" from inside the running game.
        internal static string BundleSource = DefaultSource;

        // Release version of the active custom map, from the optional
        // "mapversion <v>" header in its .aipaths.txt sidecar (gen_aipaths.py
        // --mapversion). Same deal as BundleSource: logged, never keyed off.
        // It answers "which build of this course is the player running?" from a
        // pasted log. Empty == unversioned, which every map shipped before this
        // header existed is, and which built-in courses always are.
        internal static string BundleMapVersion = "";

        // How LoadLevelManager seats the start gate + respawn markers onto the
        // terrain, from the optional "spawnsnap <both|raise|off>" header in the
        // active map's .aipaths.txt sidecar (gen_aipaths.py --spawnsnap):
        //   Both  (default) — seat exactly on the surface + Clearance regardless
        //          of the authored Y. Kills BOTH failure modes at once: an
        //          authored-too-high marker (immediate drop) is lowered, an
        //          embedded one (rider sinks into space) is raised.
        //   Raise — only ever lift a marker out of the ground, never lower it;
        //          a clean authored Y is untouched (the original, conservative
        //          behavior — zero regression risk to a hand-tuned start).
        //   Off   — leave every marker exactly as authored (gari's hand-placed
        //          start is sensitive; opt it out via its sidecar).
        // Custom-maps-only by construction: the markers only exist in rebuilt
        // bundles, and the header is re-read on every custom load.
        internal enum SpawnSnap { Both, Raise, Off }

        internal static SpawnSnap SpawnSnapMode = SpawnSnap.Both;

        // Shape check shared by the six one-key-per-line sidecar header readers below.
        // True only for an exact "<key> <value>" line.
        //
        // The reason this is a function and not an inline comparison is the ELSE branch.
        // A line whose first token IS the key but whose shape is wrong — a trailing
        // comment ("scale 2.5 # doubled"), wrong case ("Scale 2.5"), a stray third token
        // — used to be skipped in silence, completely indistinguishable from the header
        // being absent. A map with NO sidecar and a map whose sidecar says `Scale 2.5`
        // produced byte-identical logs and both silently rode at scale 1.0, with
        // unscaled jump arcs and mis-sized rail colliders. An absent header is a normal,
        // documented state and stays quiet; a header we ALMOST understood is a typo
        // someone needs told about.
        // Every header gen_aipaths.py can emit. Anything else in a sidecar is an
        // AI-path point line. Matched on the first token, so a point line can
        // never be mistaken for a header (or the reverse) on a prefix.
        private static readonly string[] SidecarHeaders =
        { "scale", "gravity", "physicshz", "railcatch", "ridepace", "routescale", "platform", "source", "spawnsnap",
          "mapversion", "group", "order", "displayname" };

        private static bool IsHeaderLine(string line)
        {
            int sp = line.IndexOfAny(new[] { ' ', '\t' });
            string key = sp < 0 ? line : line.Substring(0, sp);
            for (int i = 0; i < SidecarHeaders.Length; i++)
                if (string.Equals(key, SidecarHeaders[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool HeaderMatches(string[] t, string key, string sidecarName)
        {
            if (t.Length == 2 && t[0] == key) return true;
            if (t.Length > 0 && string.Equals(t[0], key, StringComparison.OrdinalIgnoreCase))
                Log.LogWarning($"[Sidecar] {sidecarName}: '{string.Join(" ", t)}' looks like the "
                    + $"'{key}' header but is not exactly '{key} <value>' — IGNORED, using the default.");
            return false;
        }

        // Reads the optional "scale <f>" header line from a map's .aipaths.txt
        // sidecar (written by gen_aipaths.py --scale). Returns 1.0 for a missing
        // sidecar, no scale line, or a non-positive value — every path through
        // here that isn't an explicitly scaled map yields exactly 1.0.
        internal static float ReadWorldScale(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return 1f;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!HeaderMatches(t, "scale", Path.GetFileName(sidecarPath))) continue;
                    float s = float.Parse(t[1], CultureInfo.InvariantCulture);
                    if (s > 0f) return s;
                    Log.LogWarning($"[Gravity] sidecar {Path.GetFileName(sidecarPath)} declares scale {t[1]} (must be > 0); ignoring");
                    return 1f;
                }
                return 1f;
            }
            catch (Exception e)
            {
                Log.LogError($"[Gravity] failed reading world scale sidecar for {bundlePath}: {e}");
                return 1f;
            }
        }

        // Reads the optional "gravity <f>" header from a map's .aipaths.txt
        // sidecar. Returns 1.0 (no change) for a missing sidecar, no gravity
        // line, or a non-positive value. Independent of the "scale" header —
        // applies at any world scale (see ScaledGravity).
        internal static float ReadGravityMultiplier(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return 1f;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!HeaderMatches(t, "gravity", Path.GetFileName(sidecarPath))) continue;
                    float g = float.Parse(t[1], CultureInfo.InvariantCulture);
                    if (g > 0f) return g;
                    Log.LogWarning($"[Gravity] sidecar {Path.GetFileName(sidecarPath)} declares gravity {t[1]} (must be > 0); ignoring");
                    return 1f;
                }
                return 1f;
            }
            catch (Exception e)
            {
                Log.LogError($"[Gravity] failed reading gravity multiplier sidecar for {bundlePath}: {e}");
                return 1f;
            }
        }

        // Reads the optional "physicshz <f>" header from a map's .aipaths.txt
        // sidecar. Returns 0 (no declaration — leave the game's rate alone) for a
        // missing sidecar, no physicshz line, or a value outside 10-1000 Hz.
        //
        // The bounds are a typo guard, not a tuning range: below ~30 Hz a rider at
        // speed starts tunnelling through thin geometry (rails, fences, the
        // destructible panes), and a value in the hundreds would make the catch-up
        // spiral this header exists to escape strictly worse.
        internal static float ReadPhysicsHz(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return 0f;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!HeaderMatches(t, "physicshz", Path.GetFileName(sidecarPath))) continue;
                    float hz = float.Parse(t[1], CultureInfo.InvariantCulture);
                    if (hz >= 10f && hz <= 1000f) return hz;
                    Log.LogWarning($"[Physics] sidecar {Path.GetFileName(sidecarPath)} declares physicshz {t[1]} "
                        + "(must be 10-1000); ignoring, using the game's rate");
                    return 0f;
                }
                return 0f;
            }
            catch (Exception e)
            {
                Log.LogError($"[Physics] failed reading physics rate sidecar for {bundlePath}: {e}");
                return 0f;
            }
        }

        // Reads the optional "railcatch <f>" header from a map's .aipaths.txt
        // sidecar. Returns 0 (no declaration — keep the WorldScale formula) for a
        // missing sidecar, no railcatch line, or a value outside 0.02-100 m.
        //
        // The bounds are a typo guard, not a tuning range. Below a couple of
        // centimetres the strip is thinner than the rail art and no approach ever
        // lands on it; above ~100 m the collider is a field the rider grinds by
        // walking near it, which is exactly the bug this header exists to fix.
        internal static float ReadRailCatch(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return 0f;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!HeaderMatches(t, "railcatch", Path.GetFileName(sidecarPath))) continue;
                    float w = float.Parse(t[1], CultureInfo.InvariantCulture);
                    if (w >= 0.02f && w <= 100f) return w;
                    Log.LogWarning($"[Rails] sidecar {Path.GetFileName(sidecarPath)} declares railcatch {t[1]} "
                        + "(must be 0.02-100 m); ignoring, using 0.25 * worldScale");
                    return 0f;
                }
                return 0f;
            }
            catch (Exception e)
            {
                Log.LogError($"[Rails] failed reading rail catch sidecar for {bundlePath}: {e}");
                return 0f;
            }
        }

        // Per-map route scale (see Plugin.MapRouteScale). Bounds are a typo guard:
        // the SSX family spans 1-5 (Tricky 2.5, SSX 3 chains ~1.4).
        internal static float ReadRouteScale(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return 0f;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!HeaderMatches(t, "routescale", Path.GetFileName(sidecarPath))) continue;
                    float w = float.Parse(t[1], CultureInfo.InvariantCulture);
                    if (w >= 0.1f && w <= 20f) return w;
                    Log.LogWarning($"[AIPaths] sidecar {Path.GetFileName(sidecarPath)} declares routescale {t[1]} "
                        + "(must be 0.1-20); ignoring, using worldScale");
                    return 0f;
                }
                return 0f;
            }
            catch (Exception e)
            {
                Log.LogError($"[AIPaths] failed reading route scale sidecar for {bundlePath}: {e}");
                return 0f;
            }
        }

        // Per-map gold PACE in world units/second (see Plugin.MapRidePace).
        //
        // The bounds are a typo guard, not a tuning range: the measured spread across
        // this library's seven source games is ~45 (SSX Tricky) to ~70 (X-Games) u/s,
        // so anything outside 5-500 is a unit mistake rather than a fast course. A
        // pace an order of magnitude high hands out a gold nobody can reach, which is
        // exactly the failure this header exists to fix — so refuse it loudly and keep
        // the 45 fallback rather than trusting the file.
        internal static float ReadRidePace(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return 0f;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!HeaderMatches(t, "ridepace", Path.GetFileName(sidecarPath))) continue;
                    float v = float.Parse(t[1], CultureInfo.InvariantCulture);
                    if (v >= 5f && v <= 500f) return v;
                    Log.LogWarning($"[TimeBudget] sidecar {Path.GetFileName(sidecarPath)} declares ridepace {t[1]} "
                        + "(must be 5-500 u/s); ignoring, using the 45 u/s fallback");
                    return 0f;
                }
                return 0f;
            }
            catch (Exception e)
            {
                Log.LogError($"[TimeBudget] failed reading ride pace sidecar for {bundlePath}: {e}");
                return 0f;
            }
        }

        // Applies MapPhysicsHz (or restores the game's rate when it is 0 or the
        // feature is configured off). Idempotent, so calling it on every scene
        // load is safe.
        internal static void ApplyPhysicsRate(string why)
        {
            float want = (MapPhysicsHz > 0f && mapPhysicsRate.Value)
                ? 1f / MapPhysicsHz
                : StockFixedDeltaTime;
            if (Mathf.Approximately(Time.fixedDeltaTime, want)) return;
            Time.fixedDeltaTime = want;
            Log.LogInfo($"[Physics] fixedDeltaTime -> {want.ToString("F4", CultureInfo.InvariantCulture)} "
                + $"({(1f / want).ToString("F0", CultureInfo.InvariantCulture)} Hz), up to "
                + $"{Mathf.Floor(Time.maximumDeltaTime / want):F0} step(s) per frame — {why}");
        }

        // Reads the optional "platform <windows|mac>" header from a map's
        // .aipaths.txt sidecar. Returns null (no declaration) for a missing
        // sidecar, no platform line, or an unrecognized value — the safe default
        // that keeps the always-remap behavior. Only "windows" and "mac" are
        // honored; they let the shader remap trust the bundle's own shaders when
        // it runs on that platform (see BundleIsNativeToCurrentGpu).
        internal static string ReadBundleNativePlatform(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return null;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!HeaderMatches(t, "platform", Path.GetFileName(sidecarPath))) continue;
                    string p = t[1].ToLowerInvariant();
                    if (p == "windows" || p == "mac") return p;
                    Log.LogWarning($"[ShaderRemap] sidecar {Path.GetFileName(sidecarPath)} declares platform '{t[1]}' (expected 'windows' or 'mac'); ignoring");
                    return null;
                }
                return null;
            }
            catch (Exception e)
            {
                Log.LogError($"[ShaderRemap] failed reading platform sidecar for {bundlePath}: {e}");
                return null;
            }
        }

        // Reads the optional "group <label>" header from a map's .aipaths.txt
        // sidecar — the level-select section this map belongs in. Returns
        // DefaultGroup for a missing sidecar or no group line.
        //
        // ⚠️ Deliberately does NOT go through HeaderMatches: that requires exactly
        // two tokens, and a section heading is prose ("SSX (2012)"). Everything
        // after the key is the label, whitespace-collapsed.
        internal static string ReadBundleGroup(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return DefaultGroup;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length == 0 || !string.Equals(t[0], "group", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (t.Length < 2)
                    {
                        Log.LogWarning($"[Sidecar] {Path.GetFileName(sidecarPath)}: 'group' header has no "
                            + $"label — IGNORED, this map lands in {DefaultGroup}.");
                        return DefaultGroup;
                    }
                    return string.Join(" ", t, 1, t.Length - 1);
                }
                return DefaultGroup;
            }
            catch (Exception e)
            {
                Log.LogError($"[Sidecar] failed reading group sidecar for {bundlePath}: {e}");
                return DefaultGroup;
            }
        }

        // Reads the optional "displayname <text>" header — the level-select and
        // results-screen title, when it should differ from the .asset filename.
        // Returns null (use the filename) for a missing sidecar or no such line.
        // Free text like `group`, so it bypasses HeaderMatches the same way. Every
        // file lookup (bundle, sidecar, thumbnail) keys off the path, never this.
        internal static string ReadBundleDisplayName(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return null;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length == 0 || !string.Equals(t[0], "displayname", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (t.Length < 2)
                    {
                        Log.LogWarning($"[Sidecar] {Path.GetFileName(sidecarPath)}: 'displayname' header has "
                            + "no text — IGNORED, the menu shows the filename.");
                        return null;
                    }
                    return string.Join(" ", t, 1, t.Length - 1);
                }
                return null;
            }
            catch (Exception e)
            {
                Log.LogError($"[Sidecar] failed reading displayname for {bundlePath}: {e}");
                return null;
            }
        }

        // Reads the optional "order <n>" header from a map's .aipaths.txt sidecar —
        // this map's position in the level select, globally. Returns NoOrder for a
        // missing sidecar, no order line, or an unparseable value, which sorts the
        // map after everything that did declare one.
        internal static int ReadBundleOrder(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return NoOrder;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!HeaderMatches(t, "order", Path.GetFileName(sidecarPath))) continue;
                    int v;
                    if (int.TryParse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                        return v;
                    Log.LogWarning($"[Sidecar] {Path.GetFileName(sidecarPath)}: order '{t[1]}' is not a "
                        + "whole number — IGNORED, this map sorts last.");
                    return NoOrder;
                }
                return NoOrder;
            }
            catch (Exception e)
            {
                Log.LogError($"[Sidecar] failed reading order sidecar for {bundlePath}: {e}");
                return NoOrder;
            }
        }

        // Reads the optional "source <slug>" header from a map's .aipaths.txt
        // sidecar. Returns DefaultSource ("tricky_madness") for a missing sidecar
        // or no source line — absence means OG content (built-in levels and any
        // sidecar-less drop-in). The slug is lowercased; it's informational only,
        // so an unrecognized value is kept as-is rather than rejected.
        internal static string ReadBundleSource(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return DefaultSource;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!HeaderMatches(t, "source", Path.GetFileName(sidecarPath))) continue;
                    return t[1].ToLowerInvariant();
                }
                return DefaultSource;
            }
            catch (Exception e)
            {
                Log.LogError($"[Source] failed reading source sidecar for {bundlePath}: {e}");
                return DefaultSource;
            }
        }

        // Reads the optional "mapversion <v>" header from a map's .aipaths.txt
        // sidecar. Returns "" for a missing sidecar or no mapversion line —
        // absence just means the map build predates the header (or nobody
        // stamped it). Informational only, so the value is kept verbatim.
        internal static string ReadBundleMapVersion(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return "";
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!HeaderMatches(t, "mapversion", Path.GetFileName(sidecarPath))) continue;
                    return t[1];
                }
                return "";
            }
            catch (Exception e)
            {
                Log.LogError($"[Source] failed reading mapversion sidecar for {bundlePath}: {e}");
                return "";
            }
        }

        // Reads the optional "spawnsnap <both|raise|off>" header from a map's
        // .aipaths.txt sidecar. Returns Both (seat markers on the surface, both
        // ways) for a missing sidecar or no spawnsnap line — the desired default
        // for every rebuilt map. Only the three known modes are honored; an
        // unrecognized value falls back to Both with a warning.
        internal static SpawnSnap ReadSpawnSnap(string bundlePath)
        {
            try
            {
                string sidecarPath = Path.ChangeExtension(bundlePath, ".aipaths.txt");
                if (!File.Exists(sidecarPath)) return SpawnSnap.Both;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!HeaderMatches(t, "spawnsnap", Path.GetFileName(sidecarPath))) continue;
                    switch (t[1].ToLowerInvariant())
                    {
                        case "both": return SpawnSnap.Both;
                        case "raise": return SpawnSnap.Raise;
                        case "off": return SpawnSnap.Off;
                        default:
                            Log.LogWarning($"[Spawn] sidecar {Path.GetFileName(sidecarPath)} declares spawnsnap '{t[1]}' (expected both/raise/off); using both");
                            return SpawnSnap.Both;
                    }
                }
                return SpawnSnap.Both;
            }
            catch (Exception e)
            {
                Log.LogError($"[Spawn] failed reading spawnsnap sidecar for {bundlePath}: {e}");
                return SpawnSnap.Both;
            }
        }
    }
}
