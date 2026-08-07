using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection.Emit;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace TrickyMaddnessLevelHook
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static Harmony harmony;
        public static MenuManager menuManager;

        public static AssetBundle myLoadedAssetBundle;
        public static string LoadedAssetBundle = "";
        public static string LoadedTrack;
        public static string LoadedTrackName;

        // Custom-scene load in flight. pendingSceneName is the bundle-internal
        // scene name we told MenuManager to load; null once it has loaded (or the
        // load was aborted). pendingSince timestamps the swap so a load that never
        // arrives can be detected and cleared instead of wedging future selections.
        internal static string pendingSceneName;
        internal static float pendingSince;

        // When false, the level-select card strip is left exactly as vanilla (no
        // scrolling behaviour attached). Default on.
        internal static ConfigEntry<bool> levelSelectScroll;

        // Vertical grid with a heading per source game, instead of the vanilla
        // one-row strip. Default on; false falls back to the strip scroller.
        internal static ConfigEntry<bool> levelSelectGrid;
        internal static ConfigEntry<float> levelSelectGridHeight;

        // Opt-in feel-tuning knob (default OFF): multiply the game's rider/caster
        // gravity constants by the active map's declared world scale. Scaled-up
        // maps deliberately keep stock gravity — the rider's jumps are tuned for
        // big courses and over-clear a 1x map, so a bigger world with the SAME
        // jump is the point; heavier gravity would shrink the arcs right back.
        // Inert at scale 1 and on built-in levels regardless of the flag.
        internal static ConfigEntry<bool> scaleGravityWithMap;
        internal static ConfigEntry<float> startLaunchSpeedScale;
        internal static ConfigEntry<bool> fixRailProjection;
        internal static ConfigEntry<bool> mapPhysicsRate;
        internal static ConfigEntry<bool> respawnLoopGuard;
        internal static ConfigEntry<bool> respawnLoopGuardPlayer;
        internal static ConfigEntry<int> respawnLoopAdvance;

        // The race-line respawn markers built in BuildScene, in course order, so the
        // loop guard can escalate a wedged rider FORWARD by index. The game itself
        // only ever picks the nearest marker, which is exactly why it can't break a
        // loop: the nearest marker to a rider stuck in an OOB volume is the one that
        // keeps putting them back in it.
        internal static readonly List<Transform> RaceLineRespawns = new List<Transform>();
        // Fine-tune multiplier on the skydome's auto-fit (feature 13). SkydomeFollow
        // sizes the dome to ~95% of the camera far-clip plane each frame so it envelops
        // the whole visible course; this scales that target (1 = the fit). Tunable, no
        // rebuild.
        internal static ConfigEntry<float> skydomeScale;
        // SSX domes only reach ~+20 deg elevation — an open-topped bowl. Above the rim
        // the camera shows its clear color, which is black by default (feature-5's blue
        // clear only fires for skybox-remapped maps, and a domed map's RenderSettings
        // .skybox is null → that fallback stays dormant). So for a domed map SkydomeFollow
        // clears the camera to this sky color so looking up past the rim reads as sky, not
        // a black void. Hex; tunable without a rebuild.
        internal static ConfigEntry<string> skydomeFillColor;
        // Per-level sky-fill override, baked into the bundle by SSXImporter as an empty
        // marker GO named "SkyFill_<RRGGBB>" (from Overrides.json lighting.skyFillColor).
        // Parsed in LoadLevelManager before SkydomeFollow attaches; wins over the global
        // [Skydome] FillColor config wherever the Hook clears the camera. Null = no
        // override on the active map.
        internal static Color? SceneSkyFill;
        // Animated-texture playback speed (feature 14). The importer decodes SSX's
        // UVScrolling effect (SSFLogic MainType 0 / SubType 10) and bakes the two
        // scroll rates into the GO name ("UVScroll_<u>_<v>_<orig>"); those rates are
        // UV-units PER FRAME at the PS2's 60 Hz, so UVScrollBehaviour multiplies by
        // this to get UV-units/second. 60 = authentic speed. Tunable, no rebuild.
        internal static ConfigEntry<float> uvScrollFrameRate;
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

        public static List<string> TrackNames = new List<string>();
        public static List<string> Paths = new List<string>();

        // Resolved absolute path to the Maps directory (cross-platform).
        public static string MapsDir;
        // Static log sink so the Harmony-patched static methods can log too.
        internal static ManualLogSource Log;
        // When true, every registered map is opened at Awake and its scene paths
        // logged, to verify a Windows-built bundle even loads on this platform
        // without needing to navigate menus. Off in shipped builds.
        private static ConfigEntry<bool> debugBundleLoad;
        // When true, [ShaderRemap] logs one line per scanned material (shader name,
        // origin, isSupported, keywords, action taken). Off in shipped builds.
        private static ConfigEntry<bool> shaderDiagnostics;
        // Name of a registered track to auto-select shortly after boot, for
        // deterministic headless testing (attract mode picks maps at random).
        // Empty in shipped builds.
        private static ConfigEntry<string> debugAutoLoadMap;
        // When true, after building rail colliders a SphereCast probe verifies the
        // game-side grind-detection contract (layer-9 hit + GetComponent<Rail>).
        // Off in shipped builds.
        private static ConfigEntry<bool> railColliderProbe;
        // When true, custom maps spawn PerfHarness: an on-screen frame-rate readout
        // plus hotkeys that switch each suspected frame-rate cost on and off mid-ride,
        // so which one dominates is measured rather than guessed. Off in shipped builds.
        private static ConfigEntry<bool> perfHarness;

        // Snapshot of the GAME's shaders, taken in the LoadScene prefix right
        // before the map bundle is loaded (so nothing bundle-origin can be in it).
        // Used to (a) resolve duplicate shader names to the game's platform-correct
        // copy — Shader.Find is unspecified with duplicates — and (b) tell whether
        // a material's shader shipped with the game or with the bundle.
        internal static Dictionary<string, Shader> GameShaders;
        internal static HashSet<int> GameShaderIds;
        // Per game-shader union of keywords observed on game materials. Bundle
        // material keywords outside this set select shader variants the game build
        // likely stripped (magenta at draw time), so we prune to this set.
        internal static Dictionary<int, HashSet<string>> GameShaderKeywords;

        internal static Plugin Instance;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            // Plugin startup logic
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");

            // Snapshot the game's physics rate before anything (us, another
            // plugin, a map's physicshz header) can change it. This is what every
            // restore path returns to, so it has to be read exactly once, here.
            StockFixedDeltaTime = Time.fixedDeltaTime;

            debugBundleLoad = Config.Bind("Debug", "DebugBundleLoad", false,
                "Load every registered map bundle at startup and log its scene paths. " +
                "Diagnostic only; leave false for normal play.");
            shaderDiagnostics = Config.Bind("Debug", "ShaderDiagnostics", false,
                "Log one line per material scanned by the shader-remap pass. " +
                "Diagnostic only; leave false for normal play.");
            debugAutoLoadMap = Config.Bind("Debug", "AutoLoadMap", "",
                "Name of a registered track to auto-select shortly after boot, for " +
                "deterministic headless testing. Diagnostic only; leave empty for " +
                "normal play.");
            railColliderProbe = Config.Bind("Debug", "RailColliderProbe", false,
                "After building rail colliders on a custom map, SphereCast-probe the " +
                "first rail to verify grind detection would hit it. Diagnostic only; " +
                "leave false for normal play.");
            perfHarness = Config.Bind("Debug", "PerfHarness", false,
                "On custom maps, record every respawn the game performs — split by " +
                "route (crashed vs out-of-bounds contact), clustered by location, " +
                "with the offending OOB colliders named — plus which riders stop " +
                "racing and whether they are wedged or looping. Written to the log " +
                "on level exit, or on demand with Ctrl+U. Diagnostic only; leave " +
                "false for normal play.");
            levelSelectScroll = Config.Bind("UI", "LevelSelectScroll", true,
                "Scroll the level-select card strip when more cards exist than fit " +
                "on screen, auto-scrolling to the selected card. When all cards fit, " +
                "the menu is left pixel-identical to vanilla. Set false to disable.");
            levelSelectGrid = Config.Bind("UI", "LevelSelectGrid", true,
                "Lay the level select out as a vertical scrolling grid with a heading " +
                "per source game (Tricky Madness / SSX Tricky / SSX 3 / SSX 2012), " +
                "instead of one long horizontal strip. Set false to fall back to the " +
                "vanilla strip with LevelSelectScroll's scrolling. Ignored entirely " +
                "when LevelSelectScroll is false.");
            levelSelectGridHeight = Config.Bind("UI", "LevelSelectGridHeight", 1800f,
                "Height of the level-select grid, in the menu canvas's 3840x2160 " +
                "reference pixels. The bottom of the grid is fixed just above the " +
                "nav bar (y=216); this is how far up the screen it reaches, so 1944 " +
                "is full height. Lower it if the grid crowds the top of your screen.");
            scaleGravityWithMap = Config.Bind("Gameplay", "ScaleGravityWithMap", false,
                "Opt-in feel tuning: on custom maps that declare a world scale (a " +
                "'scale' line in their .aipaths.txt sidecar), multiply the " +
                "rider/caster gravity by that scale, shrinking jump arcs back " +
                "toward original proportions. Off by default — scaled-up maps are " +
                "meant to keep stock gravity so jumps cover proportionally less " +
                "ground. No effect on built-in levels or unscaled maps.");
            startLaunchSpeedScale = Config.Bind("Gameplay", "StartLaunchSpeedScale", 0.7f,
                "Custom-map start-launch fix (see StartLaunchRedirect): the game " +
                "kicks every rider straight DOWN at the countdown's end and lets the " +
                "ground redirect it. On custom maps whose start sits on a sideways " +
                "slope that flings the rider off-line, the Hook re-steers the launch " +
                "down-course; this scales the launch speed so it doesn't come out " +
                "hot (the sideways projection preserves nearly the full kick, which a " +
                "gentle down-course start would not). 1.0 = keep the full speed. " +
                "Human rider only; no effect on built-in levels.");
            fixRailProjection = Config.Bind("Gameplay", "FixRailProjection", true,
                "Bug fix in the GAME's own Rail.GetClosestPosition: it builds the " +
                "closest point on a rail segment as 'start + segmentVector * t' " +
                "where t is a distance in metres, so the projection is only correct " +
                "on segments exactly 1 m long — and no rail in the game is, stock " +
                "levels included. The rider can be snapped metres off the rail and, " +
                "at the tail, the method reports 'still on the rail' when they have " +
                "run off it, so the game re-attaches them behind where they were " +
                "(the rail-end snap-back loop; CPU riders never escape it). " +
                "On by default. Set false to restore the stock behaviour — this " +
                "is the one Hook feature that changes built-in levels too.");
            respawnLoopGuard = Config.Bind("Gameplay", "RespawnLoopGuard", true,
                "Break the out-of-bounds respawn loop. The GAME's own " +
                "Snowboarder.OnControllerColliderHit calls Respawn() the instant it " +
                "touches anything on the OOB layer, with no cooldown and no " +
                "CanRespawn() check — and Respawn puts the rider on the NEAREST " +
                "respawn marker, which for a rider stuck inside an OOB volume is the " +
                "marker that drops them straight back into it. Measured on the SSX3 " +
                "whole-mountain chain: one rider respawning 2817 times in 114 seconds, " +
                "once per rendered frame, which is what the map's frame rate was " +
                "actually being spent on. This detects a rider respawning repeatedly " +
                "without getting anywhere and moves them FORWARD along the race line " +
                "until they are clear. Only acts on custom maps that have race-line " +
                "respawn markers; built-in levels are untouched.");
            respawnLoopGuardPlayer = Config.Bind("Gameplay", "RespawnLoopGuardPlayer", false,
                "Apply RespawnLoopGuard to the human rider too. Off by default: " +
                "skipping a stuck CPU rider forward costs nothing, but doing it to the " +
                "player moves them down the course through terrain they did not ride, " +
                "which is a gift in a race. Turn it on if you would rather be nudged " +
                "past a bad spot than sit in a respawn loop.");
            respawnLoopAdvance = Config.Bind("Gameplay", "RespawnLoopAdvance", 3,
                "How many race-line respawn markers forward the loop guard moves a " +
                "wedged rider each time it fires. Markers sit roughly 25 m apart, so " +
                "3 is about 75 m down-course — far enough to clear a reset volume, " +
                "short enough that a rider who was only unlucky loses little. The " +
                "guard re-fires if that landing is also bad, so this compounds rather " +
                "than having to be right first time.");
            mapPhysicsRate = Config.Bind("Gameplay", "MapPhysicsRate", true,
                "Honour a custom map's 'physicshz <n>' sidecar header, which lets a " +
                "very large map ask to run physics at a lower tick rate than the " +
                "game's 100 Hz. Unity catches the physics clock up to the frame " +
                "clock by running FixedUpdate repeatedly, so on a map heavy enough " +
                "to drop below 20 fps it runs the maximum FIVE full physics passes " +
                "per rendered frame — and each slow frame buys more of them. The " +
                "SSX3 whole-mountain chain measured exactly that; asking for 50 Hz " +
                "(which is Unity's own default rate, half what this game ships) " +
                "more than doubled its frame rate. The trade is coarser collision: " +
                "at a lower rate a rider at speed is likelier to clip through thin " +
                "geometry. Only maps that declare the header are affected; built-in " +
                "levels and every other custom map keep the game's rate untouched. " +
                "Set false to ignore the header everywhere.");
            skydomeScale = Config.Bind("Skydome", "Scale", 1f,
                "Fine-tune multiplier on the skydome's automatic fit. The dome is " +
                "auto-sized each frame so its radius is ~90% of the camera's far-clip " +
                "plane — big enough to envelop the whole visible course so it reads as a " +
                "distant sky, not a nearby wall. 1 = the auto fit; raise slightly to push " +
                "it out further (values >1.1 risk the far plane clipping the dome), lower " +
                "to pull it in. No rebuild needed, just relaunch.");
            skydomeFillColor = Config.Bind("Skydome", "FillColor", "#3373C3",
                "Camera clear color (hex) for maps with a skydome. SSX domes are open " +
                "at the top (~+20 deg), so above the rim the camera would otherwise show " +
                "black; this fills it with sky. Default matches the dome's blue so the " +
                "rim seam blends. No rebuild needed, just relaunch.");
            uvScrollFrameRate = Config.Bind("Visual", "UVScrollFrameRate", 60f,
                "Playback speed for animated (UV-scrolling) textures on custom maps — " +
                "LCD scanlines, jumbotrons, boost-pad chevrons, river water. The " +
                "authored rates are per-frame at the PS2's 60 Hz, so 60 = authentic " +
                "speed, 0 = frozen. No rebuild needed, just relaunch.");

            MapsDir = ResolveMapsDir();
            Logger.LogInfo($"Maps directory resolved to: {MapsDir}");
            if (!Directory.Exists(MapsDir))
            {
                Directory.CreateDirectory(MapsDir);
            }
            AddTracks();

            // Cross-platform shader fix: Windows-built bundles ship DX-compiled
            // shaders that won't run on Metal. Re-bind each material to the game's
            // own (platform-correct) shader of the same name once the scene loads.
            SceneManager.sceneLoaded += OnSceneLoaded;

            if (debugBundleLoad.Value)
            {
                RunDebugBundleLoad();
            }
            if (!string.IsNullOrEmpty(debugAutoLoadMap.Value))
            {
                StartCoroutine(AutoLoadMap(debugAutoLoadMap.Value));
            }

            DoPatching();
        }

        // GameRootPath points at the executable's folder. On macOS that is
        // TrickyMadness.app/Contents/MacOS; walk up to the folder that holds the
        // .app (next to run_bepinex.sh) so Maps sits where it does on Windows.
        private static string ResolveMapsDir()
        {
            string root = BepInEx.Paths.GameRootPath;
            int idx = root.IndexOf(".app" + Path.DirectorySeparatorChar + "Contents",
                StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                idx = root.IndexOf(".app/Contents", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int slash = root.LastIndexOfAny(new[] { '/', '\\' }, idx);
                if (slash > 0) root = root.Substring(0, slash);
            }
            return Path.Combine(root, "Maps");
        }

        private static void RunDebugBundleLoad()
        {
            Log.LogInfo($"[DebugBundleLoad] Probing {Paths.Count} registered map(s)...");
            for (int i = 0; i < Paths.Count; i++)
            {
                string p = Paths[i];
                try
                {
                    var bundle = AssetBundle.LoadFromFile(p);
                    if (bundle == null)
                    {
                        Log.LogError($"[DebugBundleLoad] LoadFromFile returned NULL for {p}");
                        continue;
                    }
                    string[] scenes = bundle.GetAllScenePaths();
                    if (scenes == null || scenes.Length == 0)
                    {
                        Log.LogError($"[DebugBundleLoad] {Path.GetFileName(p)} loaded but has NO scene paths");
                    }
                    else
                    {
                        Log.LogInfo($"[DebugBundleLoad] {Path.GetFileName(p)} OK, {scenes.Length} scene(s): {string.Join(", ", scenes)}");
                    }
                    bundle.Unload(true);
                }
                catch (Exception e)
                {
                    Log.LogError($"[DebugBundleLoad] Exception loading {p}: {e}");
                }
            }
        }

        // Headless test hook: selects a named track through the game's own
        // SelectLevel(index) — the exact path a real menu click takes (racer
        // setup, LoadScene coroutine, our prefix), unlike attract mode which
        // picks maps at random.
        private static System.Collections.IEnumerator AutoLoadMap(string trackName)
        {
            float deadline = Time.realtimeSinceStartup + 60f;
            while (MenuManager.Instance == null && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (MenuManager.Instance == null)
            {
                Log.LogError("[Debug] AutoLoadMap: MenuManager never appeared within 60s");
                yield break;
            }
            yield return new WaitForSecondsRealtime(5f); // let the menu finish booting

            int index = -1;
            for (int i = 0; i < LevelEntries.GetLevelCount(); i++)
            {
                if (LevelEntries.GetEntry(i).name == trackName) { index = i; break; }
            }
            if (index < 0)
            {
                Log.LogError($"[Debug] AutoLoadMap: no registered level named '{trackName}'");
                yield break;
            }
            Log.LogInfo($"[Debug] AutoLoadMap: selecting '{trackName}' (index {index})");
            MenuManager.Instance.SelectLevel(index);
        }

        // Scene name of the active custom map, derived from LoadedTrack the same
        // way the LoadScene prefix does (never Path.* — on macOS '\' is an
        // ordinary filename character). Null when no custom bundle is active.
        internal static string CustomSceneName()
        {
            if (string.IsNullOrEmpty(LoadedAssetBundle) || string.IsNullOrEmpty(LoadedTrack))
                return null;
            var parts = LoadedTrack.Split('\\', '/');
            return parts[parts.Length - 1].Split('.')[0];
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Material fixing must only run for the custom bundle's own scene.
            // Gating on the flag alone is not enough — returning to the menu doesn't
            // go through MenuManager.LoadScene(LevelEntry), so the flag stays set and
            // we'd rebind MENU materials while the bundle's DX-compiled
            // duplicate-name shaders are still loaded (Shader.Find with duplicate
            // names is unspecified → potential pink menu until restart).
            string customScene = CustomSceneName();
            bool isCustomScene = customScene != null && scene.name == customScene;

            // The custom scene has finished loading: retire the in-flight guard so
            // future selections aren't rejected as "already in progress".
            if (isCustomScene && scene.name == pendingSceneName)
                pendingSceneName = null;

            // Leaving a scaled custom map for anywhere else (menu return doesn't
            // go through the LoadScene prefix) restores stock gravity. The pending
            // guard keeps a possible intermediate loading scene during a custom
            // load from resetting the scale the prefix just read.
            // MapPhysicsHz belongs to the same reset — and must be part of the
            // GUARD too, not just the body: a map that declares only physicshz
            // (no scale, no gravity) would otherwise fail the condition and leave
            // its tick rate applied to every level loaded afterwards.
            if (!isCustomScene && pendingSceneName == null
                && (WorldScale != 1f || MapGravityMultiplier != 1f || MapPhysicsHz != 0f))
            {
                WorldScale = 1f;
                MapGravityMultiplier = 1f;
                MapPhysicsHz = 0f;
                ApplyPhysicsRate("left custom map");
                Log.LogInfo("[Gravity] left custom map; world scale + gravity multiplier reset to 1");
            }

            // Per-level sky fill must never outlive its map (same lifetime rule as
            // WorldScale); LoadLevelManager re-reads it on the next custom load.
            if (!isCustomScene && pendingSceneName == null)
                SceneSkyFill = null;

            // The skybox needs watching on EVERY scene: on built-in levels/menus we
            // only cache a known-good game skybox material (never modify anything);
            // on the custom scene we wait for the bundle's skybox to be bound —
            // empirically RenderSettings.skybox is still null when sceneLoaded fires
            // and only appears during activation — and then fix it.
            if (Instance != null) Instance.StartCoroutine(SkyboxWatch(isCustomScene));

            if (!isCustomScene) return;

            // (The open-top sky-fill for domed maps is applied by SkydomeFollow directly
            // on the render camera in OnBeginCamera — it can't rely on Camera.main here,
            // which is null on these maps, so it doesn't route through SolidColorSky.)

            try
            {
                int scanned = 0, remapped = 0, litFallbacks = 0, pruned = 0;
                var seen = new HashSet<Material>();
                var missing = new HashSet<string>();

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        foreach (var mat in r.sharedMaterials)
                        {
                            if (mat == null || !seen.Add(mat)) continue;
                            scanned++;
                            FixMaterial(mat, missing, ref remapped, ref litFallbacks, ref pruned);
                        }
                    }
                }

                // Clip-only materials: the V2-ported animation flipbooks (crowd cd
                // frames, sign frames) live ONLY inside AnimationClips until their
                // first frame swaps them onto a renderer — the renderer walk above
                // never sees them, so on the foreign-GPU path they'd flip to
                // magenta mid-animation. Sweep every loaded material with the
                // importer's "tex_" name prefix instead; FixMaterial is a no-op on
                // game-origin shaders, so catching a stray game "tex_*" is harmless.
                foreach (var mat in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (mat == null || !mat.name.StartsWith("tex_", StringComparison.Ordinal) || !seen.Add(mat)) continue;
                    scanned++;
                    FixMaterial(mat, missing, ref remapped, ref litFallbacks, ref pruned);
                }

                string miss = missing.Count == 0 ? "none" : string.Join(", ", missing);
                Log.LogInfo($"[ShaderRemap] scene '{scene.name}': scanned {scanned} material(s), remapped {remapped}, lit-fallbacks {litFallbacks}, keyword-pruned {pruned}, unfixable: {miss}");
            }
            catch (Exception e)
            {
                Log.LogError($"[ShaderRemap] failed on scene '{scene.name}': {e}");
            }
        }

        // A shader is fine if it shipped with the game (it runs there by
        // definition). A bundle-origin shader is broken if Unity says so — but
        // isSupported can report true for foreign-platform bytecode, so we also
        // distrust any bundle-origin shader outright when the running GPU backend
        // differs from the one the bundle was built for. We now build every bundle
        // on macOS (Metal bytecode) and ship it to BOTH platforms, so a bundle run
        // on Metal is native (its own shaders would work) yet a bundle run on D3D
        // (Windows) or Vulkan carries foreign Metal bytecode that reports
        // isSupported=true but renders magenta — those MUST be remapped to the
        // game's platform-correct shaders. Distrusting bundle shaders on every
        // backend except… nothing: the game has same-named copies of everything we
        // use, so remapping is always safe. (Historically this was Metal/Vulkan-only
        // because community bundles were Windows-built; that assumption no longer
        // holds now that we author the bundles ourselves on Mac — see CLAUDE.md
        // Hook feature 4. Verified on real D3D via CrossOver 2026-07-13.)
        // The one exception to the always-distrust default above: a bundle may
        // declare (via a "platform" header in its .aipaths.txt) the platform it
        // was authored native to. When it runs on THAT GPU family its own shaders
        // are the platform-correct bytecode — remapping them is not just needless
        // but LOSSY for hand-authored materials (the original Windows-built
        // Garibaldi's cutout trees flattened and its skybox vanished on a
        // Windows/D3D box the moment the D3D distrust was added, because the
        // keyword-prune dropped variants the game's own build never compiled).
        // Undeclared maps (every Mac-built bundle we author) keep the default.
        private static bool ShaderLooksBroken(Shader s, bool gameOrigin)
        {
            if (gameOrigin) return false;
            if (!s.isSupported) return true;
            var gpu = SystemInfo.graphicsDeviceType;
            if (BundleIsNativeToCurrentGpu()) return false;
            return gpu == UnityEngine.Rendering.GraphicsDeviceType.Metal
                || gpu == UnityEngine.Rendering.GraphicsDeviceType.Vulkan
                || gpu == UnityEngine.Rendering.GraphicsDeviceType.Direct3D11
                || gpu == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12;
        }

        // True when the active custom bundle declared (via its .aipaths.txt
        // "platform" header) that it was built native to the running GPU family:
        // "windows" → Direct3D11/12, "mac" → Metal. Undeclared (BundleNativePlatform
        // null — every existing map) returns false, so nothing is trusted by
        // default and the remap fires exactly as it did before this opt-out existed.
        private static bool BundleIsNativeToCurrentGpu()
        {
            var gpu = SystemInfo.graphicsDeviceType;
            if (BundleNativePlatform == "windows")
                return gpu == UnityEngine.Rendering.GraphicsDeviceType.Direct3D11
                    || gpu == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12;
            if (BundleNativePlatform == "mac")
                return gpu == UnityEngine.Rendering.GraphicsDeviceType.Metal;
            return false;
        }

        private static void Diag(Material mat, string action)
        {
            if (shaderDiagnostics == null || !shaderDiagnostics.Value) return;
            var sh = mat.shader;
            bool gameOrigin = GameShaderIds != null && sh != null && GameShaderIds.Contains(sh.GetInstanceID());
            string kw = sh == null ? "" : string.Join(" ", mat.shaderKeywords);
            Log.LogInfo($"[ShaderDiag] mat '{mat.name}' shader '{(sh == null ? "<null>" : sh.name)}' gameOrigin={gameOrigin} isSupported={(sh != null && sh.isSupported)} keywords=[{kw}] -> {action}");
        }

        private static void FixMaterial(Material mat, HashSet<string> missing,
            ref int remapped, ref int litFallbacks, ref int pruned)
        {
            var sh = mat.shader;
            if (sh == null) { Diag(mat, "null shader, skipped"); return; }
            bool gameOrigin = GameShaderIds != null && GameShaderIds.Contains(sh.GetInstanceID());

            // Error shaders baked into the bundle at build time render magenta by
            // design on every platform — route them to the Lit fallback too.
            bool bakedError = sh.name.Contains("FallbackError");

            if (!bakedError && !ShaderLooksBroken(sh, gameOrigin))
            {
                Diag(mat, "ok, untouched");
                return;
            }

            // Assigning Material.shader RESETS renderQueue to the new shader's
            // default (documented Unity behavior). Our importer's transparent
            // materials rely on m_CustomRenderQueue (2450 cutout / 3000 blend);
            // losing it drops a ZWrite-off blend material into the opaque pass,
            // where later same-queue draws (terrain) overwrite it — fences/foliage
            // rendered invisible except against sky/buildings (2026-07-16 report).
            int queue = mat.renderQueue;

            Shader replacement;
            if (!bakedError && GameShaders != null && GameShaders.TryGetValue(sh.name, out replacement))
            {
                mat.shader = replacement;
                mat.renderQueue = queue;
                remapped++;
                List<string> removed = PruneKeywords(mat, replacement);
                if (removed != null) pruned++;
                Diag(mat, removed == null
                    ? $"remapped to game '{replacement.name}'"
                    : $"remapped to game '{replacement.name}', pruned stripped-variant keywords [{string.Join(" ", removed)}]");
                return;
            }

            // No same-name shader in the game build: substitute the game's URP Lit.
            // Property values (incl. _BaseMap/_BaseColor/_BumpMap and tiling) live on
            // the material and survive the shader swap; keywords are reset so we get
            // the base variant, which every URP build has. Plain but not pink.
            if (GameShaders != null && GameShaders.TryGetValue("Universal Render Pipeline/Lit", out replacement))
            {
                mat.shader = replacement;
                mat.renderQueue = queue;
                // Keep the safe-listed feature keywords (alpha clip / transparent
                // surface) — clearing them un-cuts cutout foliage; the base Lit
                // variant plus these is still a variant the game build compiles.
                var keep = new List<string>();
                foreach (var k in mat.shaderKeywords)
                    if (SafeKeywords.Contains(k)) keep.Add(k);
                mat.shaderKeywords = keep.ToArray();
                litFallbacks++;
                Diag(mat, "lit-fallback (keywords cleared)");
            }
            else
            {
                missing.Add(sh.name);
                Diag(mat, "UNFIXABLE: no game shader with this name and no game URP Lit");
            }
        }

        // Feature keywords the game build is KNOWN to compile even though the
        // menu-time snapshot can't see them: the materials that use them live in
        // built-in level scenes (ForestBush01/MapleTree01 carry _ALPHATEST_ON,
        // glass carries _SURFACE_TYPE_TRANSPARENT — verified in sharedassets1),
        // which aren't loaded when the union is taken. Without this safe-list the
        // prune strips alpha clip from every cutout material and foliage/fences
        // render as opaque blocks (2026-07-16 gari_25x report: exactly the 13
        // cutout + 4 blend mats were the 17 pruned).
        private static readonly HashSet<string> SafeKeywords = new HashSet<string>
        {
            "_ALPHATEST_ON", "_SURFACE_TYPE_TRANSPARENT",
        };

        // Drop material keywords never observed on any game material using the same
        // shader: the game build only compiles the shader variants its own content
        // uses, and a bundle keyword combination outside that set can select a
        // stripped variant (magenta at draw time). Losing a keyword costs a feature
        // (e.g. emission); keeping a stripped combination costs the whole material.
        // Returns the list of removed keywords, or null if nothing was pruned.
        private static List<string> PruneKeywords(Material mat, Shader gameShader)
        {
            HashSet<string> union = null;
            if (GameShaderKeywords != null)
                GameShaderKeywords.TryGetValue(gameShader.GetInstanceID(), out union);

            var kws = mat.shaderKeywords;
            if (kws == null || kws.Length == 0) return null;
            var kept = new List<string>();
            var removed = new List<string>();
            foreach (var k in kws)
            {
                if (SafeKeywords.Contains(k) || (union != null && union.Contains(k))) kept.Add(k);
                else removed.Add(k);
            }
            if (removed.Count == 0) return null;
            mat.shaderKeywords = kept.ToArray();
            return removed;
        }

        // The game's own skybox shaders/materials (Skybox/Cubemap + SkyboxDay1 etc.)
        // live inside built-in level scenes, NOT in AlwaysIncludedShaders or
        // Resources — so from a cold boot straight into a custom map there is no
        // way to Shader.Find them. Whenever any scene shows a working game skybox,
        // remember it so a later custom map can borrow it.
        private static Material CachedGameSkybox;

        // Waits for RenderSettings.skybox to be bound (it is still null at
        // sceneLoaded time; the scene's lighting settings bind during activation).
        // On non-custom scenes this only caches a known-good game skybox material;
        // on the custom scene it fixes a broken one.
        private static System.Collections.IEnumerator SkyboxWatch(bool isCustomScene)
        {
            float deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (isCustomScene && string.IsNullOrEmpty(LoadedAssetBundle)) yield break; // bundle gone
                var sky = RenderSettings.skybox;
                if (sky != null && sky.shader != null)
                {
                    bool gameOrigin = GameShaderIds == null // pre-snapshot: nothing bundle-origin can be loaded
                        || GameShaderIds.Contains(sky.shader.GetInstanceID());
                    if (!ShaderLooksBroken(sky.shader, gameOrigin) && !sky.shader.name.Contains("FallbackError"))
                    {
                        if (gameOrigin && sky.shader.isSupported && !ReferenceEquals(sky, CachedGameSkybox))
                        {
                            CachedGameSkybox = sky;
                            Diag(sky, "working game skybox cached for custom maps");
                        }
                        if (isCustomScene) Log.LogInfo($"[ShaderRemap] skybox '{sky.shader.name}' is usable, untouched");
                        yield break;
                    }
                    if (!isCustomScene) yield break; // broken sky outside our scene is not ours to touch
                    FixSkybox(sky);
                    yield break;
                }
                yield return null;
            }
            if (isCustomScene) Log.LogInfo("[ShaderRemap] skybox: RenderSettings.skybox never appeared within 15s");
        }

        private static void FixSkybox(Material sky)
        {
            var sh = sky.shader;

            // 1) Same-name shader from the game build.
            Shader replacement;
            if (GameShaders != null && GameShaders.TryGetValue(sh.name, out replacement))
            {
                sky.shader = replacement;
                Log.LogInfo($"[ShaderRemap] skybox remapped to game '{replacement.name}'");
                return;
            }

            // 2) Another skybox shader the game has loaded; copy matching properties.
            string[] candidates = { "Skybox/6 Sided", "Skybox/Cubemap", "Skybox/Panoramic", "Skybox/Procedural" };
            foreach (var candName in candidates)
            {
                if (GameShaders == null || !GameShaders.TryGetValue(candName, out replacement)) continue;
                var newMat = new Material(replacement);
                CopySharedProperties(sky, newMat);
                RenderSettings.skybox = newMat;
                Log.LogInfo($"[ShaderRemap] skybox '{sh.name}' not in game build; rebuilt on game '{candName}'");
                return;
            }

            // 3) Any working game skybox material currently loaded, or one cached
            // from an earlier scene this session. A wrong-but-real sky beats pink.
            Material donor = null;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null) continue;
                if (!m.shader.name.StartsWith("Skybox/")) continue;
                if (GameShaderIds == null || !GameShaderIds.Contains(m.shader.GetInstanceID())) continue;
                if (!m.shader.isSupported) continue;
                donor = m;
                break;
            }
            if (donor == null) donor = CachedGameSkybox;
            if (donor != null)
            {
                var newMat = new Material(donor); // copy — never mutate the game's own material
                CopySharedProperties(sky, newMat);
                RenderSettings.skybox = newMat;
                Log.LogInfo($"[ShaderRemap] skybox '{sh.name}' unfixable by name; borrowed game skybox material '{donor.name}' ({donor.shader.name})");
                return;
            }

            // 4) Nothing skybox-shaped exists in memory (cold boot straight into the
            // custom map). Hide the broken skybox behind a solid-color camera clear
            // — a plain sky beats a pink one. RenderSettings.skybox is deliberately
            // left alone: the scene's baked ambient probe is unaffected by it at
            // runtime, and clearing it could black out ambient lighting.
            Color skyColor = SceneSkyFill
                ?? (sky.HasProperty("_Tint") ? sky.GetColor("_Tint") : new Color(0.53f, 0.70f, 0.86f));
            Log.LogInfo($"[ShaderRemap] skybox '{sh.name}' unfixable (no game skybox available); falling back to solid sky color {skyColor}");
            if (Instance != null) Instance.StartCoroutine(SolidColorSky(skyColor));
        }

        // Applies a solid-color clear to whatever the main camera currently is, and
        // keeps re-applying it while this custom map stays loaded — the game
        // re-instantiates 'Main Camera(Clone)' on run start/restart, and each new
        // camera comes back with skybox clear flags.
        private static System.Collections.IEnumerator SolidColorSky(Color c)
        {
            bool announced = false;
            var waitHalfSecond = new WaitForSecondsRealtime(0.5f);
            // Stop when the player leaves this scene — LoadedAssetBundle alone is
            // not enough (returning to the menu doesn't clear it), and the menu
            // camera must never be recolored.
            string sceneName = SceneManager.GetActiveScene().name;
            while (!string.IsNullOrEmpty(LoadedAssetBundle)
                && SceneManager.GetActiveScene().name == sceneName)
            {
                var cam = Camera.main;
                if (cam != null && (cam.clearFlags != CameraClearFlags.SolidColor || cam.backgroundColor != c))
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = c;
                    if (!announced)
                    {
                        Log.LogInfo($"[ShaderRemap] solid sky applied to camera '{cam.name}'");
                        announced = true;
                    }
                }
                yield return waitHalfSecond;
            }
        }

        // Copy texture (plus tiling/offset) and a few common scalar/color properties
        // from the bundle's material onto the replacement, where names match.
        private static void CopySharedProperties(Material from, Material to)
        {
            foreach (var prop in from.GetTexturePropertyNames())
            {
                if (!to.HasProperty(prop)) continue;
                to.SetTexture(prop, from.GetTexture(prop));
                to.SetTextureScale(prop, from.GetTextureScale(prop));
                to.SetTextureOffset(prop, from.GetTextureOffset(prop));
            }
            string[] colorProps = { "_Tint", "_Color", "_SkyTint", "_GroundColor" };
            foreach (var prop in colorProps)
                if (from.HasProperty(prop) && to.HasProperty(prop)) to.SetColor(prop, from.GetColor(prop));
            string[] floatProps = { "_Exposure", "_Rotation" };
            foreach (var prop in floatProps)
                if (from.HasProperty(prop) && to.HasProperty(prop)) to.SetFloat(prop, from.GetFloat(prop));
        }

        // Called from the LoadScene prefix immediately BEFORE the bundle is loaded,
        // so everything captured here is guaranteed game-origin.
        public static void SnapshotGameShaders()
        {
            GameShaders = new Dictionary<string, Shader>();
            GameShaderIds = new HashSet<int>();
            foreach (var sh in Resources.FindObjectsOfTypeAll<Shader>())
            {
                if (sh == null) continue;
                GameShaderIds.Add(sh.GetInstanceID());
                if (!GameShaders.ContainsKey(sh.name)) GameShaders[sh.name] = sh;
            }
            GameShaderKeywords = new Dictionary<int, HashSet<string>>();
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null) continue;
                int id = m.shader.GetInstanceID();
                if (!GameShaderIds.Contains(id)) continue;
                HashSet<string> set;
                if (!GameShaderKeywords.TryGetValue(id, out set))
                {
                    set = new HashSet<string>();
                    GameShaderKeywords[id] = set;
                }
                foreach (var k in m.shaderKeywords) set.Add(k);
            }
            Log.LogInfo($"[ShaderRemap] snapshotted {GameShaderIds.Count} game shader(s), keyword unions for {GameShaderKeywords.Count}");
        }

        // After a custom-map swap, verify the scene actually loads. OnSceneLoaded
        // clears pendingSceneName on success; if a newer selection replaced it,
        // pendingSceneName no longer equals ours and we bow out. Only a genuine
        // never-loaded scene (bundle wrong/broken) trips the timeout.
        internal static System.Collections.IEnumerator LoadWatchdog(string sceneName)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (pendingSceneName != sceneName) yield break; // loaded, or superseded
                yield return null;
            }
            if (pendingSceneName == sceneName)
            {
                Log.LogError($"LoadScene prefix: custom scene '{sceneName}' never loaded within 30s — check the bundle");
                pendingSceneName = null;
            }
        }

        // ── Level-selector grouping ───────────────────────────────────────────
        // The level select is grouped into labelled sections, and each map decides
        // for itself which section it lands in and where inside it, via two
        // .aipaths.txt sidecar headers:
        //
        //     group SSX TRICKY     <- the section heading, verbatim (may contain spaces)
        //     order 1003           <- global sort key; sections fall out as runs
        //
        // There is deliberately NO table of course names in here. Sorting is
        // by `order` alone, so a section is simply a maximal run of adjacent maps
        // sharing a `group` — which means a map's position is a property OF THE MAP,
        // set by whatever built it, and the Hook works the same for somebody else's
        // levels as for ours. (It used to be three hardcoded arrays of SSX course
        // names, which no one else's install has any use for, and which drifted
        // silently: a renamed course just started sorting last.)
        //
        // Neither header is required. A map with no `order` sorts after every map
        // that has one, by name; a map with no `group` lands in DefaultGroup. Two
        // maps sharing a `group` but not adjacent in `order` produce two sections
        // with the same heading — honest, and the fix is to fix the numbers.
        internal const string DefaultGroup = "CUSTOM";

        // The game's own levels: registered by MenuManager before our postfix runs,
        // never on disk, so they can't carry a sidecar and always sort first.
        internal const string BuiltInGroup = "TRICKY MADNESS";

        internal const int NoOrder = int.MaxValue;

        // Asset path -> group/order, filled by AddTracks. The grid reads these to
        // find its section boundaries; keyed by path rather than by card index
        // because the menu's card list has the game's own levels in front of ours.
        internal static readonly Dictionary<string, string> TrackGroup =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal static readonly Dictionary<string, int> TrackOrder =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public void AddTracks()
        {
            var List = Directory.GetFiles(MapsDir, "*.asset");
            // Read each map's headers once, up front: the comparison below runs
            // O(n log n) times and each lookup would otherwise re-read a sidecar.
            TrackGroup.Clear();
            TrackOrder.Clear();
            foreach (var p in List)
            {
                TrackGroup[p] = ReadBundleGroup(p);
                TrackOrder[p] = ReadBundleOrder(p);
            }

            // Directory.GetFiles order is filesystem-defined (roughly alphabetical).
            // Sort by declared order, then by name so scale variants of one course
            // sit adjacent and in a stable order (base before "2x").
            Array.Sort(List, (a, b) =>
            {
                int cmp = TrackOrder[a].CompareTo(TrackOrder[b]);
                if (cmp != 0) return cmp;
                return string.Compare(Path.GetFileNameWithoutExtension(a),
                                      Path.GetFileNameWithoutExtension(b),
                                      StringComparison.OrdinalIgnoreCase);
            });
            for (int i = 0; i < List.Length; i++)
            {
                Paths.Add(List[i]);
                TrackNames.Add(Path.GetFileNameWithoutExtension(List[i]));
            }

            // Per-group counts in registration order, so a map that lost its headers
            // (and therefore dropped into DefaultGroup, or to the end) is visible in
            // the log rather than only in the menu.
            var counts = new List<string>();
            string run = null; int n = 0;
            foreach (var p in List)
            {
                if (TrackGroup[p] != run)
                {
                    if (run != null) counts.Add($"{run}={n}");
                    run = TrackGroup[p]; n = 0;
                }
                n++;
            }
            if (run != null) counts.Add($"{run}={n}");
            Logger.LogInfo($"AddTracks: {Paths.Count} track(s) registered from {MapsDir} "
                + $"[{string.Join(", ", counts)}]");
        }

        public static void DoPatching()
        {
            harmony = new Harmony("com.glitcherog.patch");
            //Level appears
            var mOriginal = AccessTools.Method(typeof(MenuManager), "RegisterLevels"); // if possible use nameof() here
            var mPrefix = SymbolExtensions.GetMethodInfo(() => RegisterLevels());

            harmony.Patch(mOriginal, null, new HarmonyMethod(mPrefix));

            mOriginal = AccessTools.Method(typeof(MakerLevelData), "Awake"); // if possible use nameof() here
            mPrefix = SymbolExtensions.GetMethodInfo(() => LoadLevelManager());
            harmony.Patch(mOriginal, null, new HarmonyMethod(mPrefix));

            harmony.PatchAll();
        }

        public static void RegisterLevels()
        {
            menuManager = MenuManager.Instance;

            for (int i = 0; i < Paths.Count; i++)
            {
                LevelEntries.RegisterLevel(new LevelEntry
                {
                    name = TrackNames[i],
                    sceneName = "$" + Paths[i],
                    scoreThresholds = new float[]
                    {
                    100000f,
                    150000f,
                    200000f
                    },
                    timeThresholds = new float[]
                    {
                    110f,
                    100f,
                    95f
                    },
                    freestyleTimeLimit = 150f
                });
            }
        }

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
        static bool GroundSeat(Vector3 p, out float y, out float terrainY, out float cosSlope)
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

        public static void LoadLevelManager()
        {
            //Find Main Starting Objects
            var levelManagerMaker = FindAnyObjectByType<MakerLevelData>();

            var MakerSpawnPoint = FindAnyObjectByType<MakerSpawnPoint>();
            var MakerRespawnPoint = FindAnyObjectByType<MakerRespawnPoints>();

            // --- Spawn / respawn ground-snap -------------------------------------
            // The game seats the rider's CharacterController CENTER on the
            // startingGate transform with NO ground-check for the START (respawns
            // get only a 0.5 m down-probe). Two authoring failure modes result: an
            // authored Y BELOW the terrain (the capsule straddles the floor, PhysX
            // depenetrates DOWNWARD, rider drops through — e.g. SSX3 peak runs B/D
            // stitched from peak-cap + race-body regions), and an authored Y ABOVE
            // it (an immediate drop the instant the run starts). SnapMarkerToGround
            // casts onto the terrain and, per the map's spawnsnap mode, seats each
            // marker exactly on the surface (Both) or only lifts it clear (Raise);
            // Off leaves everything authored (gari's hand-tuned start). This makes
            // XZ the only coordinate a map author has to get right — Y is computed.
            if (Plugin.SpawnSnapMode != Plugin.SpawnSnap.Off)
            {
                Physics.SyncTransforms();
                if (MakerSpawnPoint != null)
                    SnapMarkerToGround(MakerSpawnPoint.transform, "start");
                if (MakerRespawnPoint != null)
                    foreach (Transform rp in MakerRespawnPoint.transform)
                        SnapMarkerToGround(rp, rp.name);
            }
            //Find AI Paths Object

            //Update Level Entry
            var LevelEntry = menuManager.lastLevelEntry;

            LevelEntry.timeThresholds[2] = levelManagerMaker.GoldTime;
            LevelEntry.timeThresholds[1] = levelManagerMaker.SilverTime;
            LevelEntry.timeThresholds[0] = levelManagerMaker.BronzeTime;

            LevelEntry.scoreThresholds[2] = levelManagerMaker.GoldPoints;
            LevelEntry.scoreThresholds[1] = levelManagerMaker.SilverPoints;
            LevelEntry.scoreThresholds[0] = levelManagerMaker.BronzePoints;

            LevelEntry.freestyleTimeLimit = levelManagerMaker.TimeLeft;

            Traverse.Create(menuManager).Field("lastLevelEntry").SetValue(LevelEntry);

            //Insert Starting Gate
            var StartingGate = Resources.Load<GameObject>("game/startinggate");
            GameObject StartingGateObject = Instantiate(StartingGate);
            StartingGateObject.transform.position = MakerSpawnPoint.transform.TransformPoint(new Vector3(-0.5f,0,0.5f));
            StartingGateObject.transform.rotation = MakerSpawnPoint.transform.rotation;

            // Custom-map start-launch fix: steer the human's launch down-course for
            // the first fraction of a second so a sideways start slope can't fling it
            // off the line, and trim the whole field's launch speed evenly (the
            // game's countdown kick is purely vertical — see StartLaunchRedirect).
            // Spawn facing is horizontal by construction (LookRotation, y-zeroed).
            if (MakerSpawnPoint != null)
            {
                Vector3 courseFwd = MakerSpawnPoint.transform.forward; courseFwd.y = 0f;
                if (courseFwd.sqrMagnitude > 1e-4f)
                {
                    var rd = new GameObject("StartLaunchRedirect").AddComponent<StartLaunchRedirect>();
                    rd.CourseForward = courseFwd.normalized;
                }
            }

            //Insert Level Manager
            var LevelManagerObject = Resources.Load<GameObject>("game/levelmanager");
            var gameObject = Instantiate(LevelManagerObject, levelManagerMaker.transform);

            var levelManager = gameObject.GetComponent<LevelManager>();

            //Set Level Manager Data Points
            Traverse.Create(levelManager).Field("startingGate").SetValue(MakerSpawnPoint.transform);
            Traverse.Create(levelManager).Field("respawnPointsParent").SetValue(MakerRespawnPoint.transform);
            Traverse.Create(levelManager).Field("snowflakes").SetValue(new GameObject("Temp")); //FIX to get all snowflakes and apply

            //Create UI Hooks
            var TempObject = Traverse.Create(levelManager).Field("speedLabel").GetValue() as TextMeshProUGUI;
            var CanvasObjects = FindObjectsByType(typeof(MakerCanvas), FindObjectsSortMode.None) as MakerCanvas[];
            for (int i = 0; i < CanvasObjects.Length; i++)
            {
                GameObject gameObject1 = Instantiate(TempObject.transform.gameObject, levelManager.transform.GetChild(0));
                gameObject1.SetActive(true);
                gameObject1.GetComponent<TextMeshProUGUI>().text = "NewText";
                CanvasObjects[i].Generate(gameObject1.GetComponent<TextMeshProUGUI>());
            }

            //Convert From Make Rails to Game Rails
            var RailObjects = FindObjectsByType(typeof(MakerRails), FindObjectsSortMode.None) as MakerRails[];
            int railColliders = 0, railSegments = 0;
            GameObject probeRailGO = null;
            List<Rail.Segment> probeSegments = null;
            for (int i = 0; i < RailObjects.Length; i++)
            {
                var NewRail = RailObjects[i].gameObject.AddComponent<RailInvis>();

                List<Rail.Segment> NewSegments = BuildRailSegments(RailObjects[i]);

                // The bool is checked, not discarded: on a miss TryParse yields
                // default(RailMaterial) silently, which is indistinguishable from an
                // authored value and just changes the grind sound/FX for no visible reason.
                Rail.RailMaterial material1;
                if (!Enum.TryParse(RailObjects[i].Materials.ToString(), out material1))
                    Log.LogWarning($"[Rails] '{RailObjects[i].gameObject.name}' declares material "
                        + $"'{RailObjects[i].Materials}', which has no match in Rail.RailMaterial — "
                        + $"using {material1} instead (grind sound/FX will be wrong on this rail).");

                NewRail.material = material1;

                Traverse.Create(NewRail).Field("segments").SetValue(NewSegments);

                // Grind detection BoxCast/SphereCasts layer 9 ("Rail") and then does
                // GetComponent<Rail>() on the collider's OWN GameObject — so a rail
                // is only grindable with a collider on this exact GO, on layer 9.
                // Maker rails ship with neither. (MakerRails.Start() sets the layer
                // too, but a frame after scene build — set it now.)
                RailObjects[i].gameObject.layer = 9;
                railSegments += NewSegments.Count;
                if (NewSegments.Count < 2)
                {
                    Log.LogDebug($"[Rails] '{RailObjects[i].name}': {NewSegments.Count} segment(s), no collider built");
                    continue;
                }
                if (RailObjects[i].GetComponent<Collider>() != null)
                {
                    Log.LogDebug($"[Rails] '{RailObjects[i].name}': already has a collider, left as-is");
                    continue;
                }
                if (BuildRailCollider(RailObjects[i].gameObject, NewSegments))
                {
                    railColliders++;
                    if (probeRailGO == null)
                    {
                        probeRailGO = RailObjects[i].gameObject;
                        probeSegments = NewSegments;
                    }
                }
            }
            if (RailObjects.Length > 0)
                Log.LogInfo($"[Rails] converted {RailObjects.Length} rail(s), built {railColliders} collider(s), {railSegments} segment(s) total");
            if (railColliderProbe.Value && probeRailGO != null && Instance != null)
                Instance.StartCoroutine(RailProbe(probeRailGO, probeSegments));

            GameObject AIParent = new GameObject("AI Paths Parent");
            AIParent.transform.parent = levelManager.transform;

            // MakerAIPath.m_points is a List of a user-defined [Serializable]
            // nested class, and Unity fails to deserialize those for
            // runtime-loaded (BepInEx) assemblies — on custom maps every list
            // comes back empty (engine limitation; lists of engine value types
            // like the race line's List<Vector3> are fine). The maker copy below
            // still wins whenever data DID arrive (future fixed bundles);
            // otherwise the paths are rebuilt from a plain-text sidecar next to
            // the .asset, or, failing that, from the race line. AI enumerates
            // DIRECT children of pathsParent, so every AIPath must sit
            // immediately under AIParent.
            var MakerAIPaths = FindObjectsByType(typeof(MakerAIPath), FindObjectsSortMode.None) as MakerAIPath[];
            int copied = 0;
            for (int i = 0; i < MakerAIPaths.Length; i++)
            {
                // An empty maker path would become an empty AIPath, which is what
                // sends AI into the 1.5s respawn loop — skip it entirely.
                if (MakerAIPaths[i].m_points == null || MakerAIPaths[i].m_points.Count == 0)
                    continue;

                var NewPath = MakerAIPaths[i].gameObject.AddComponent<AIPath>();

                MakerAIPaths[i].transform.parent = AIParent.transform;

                NewPath.m_points = new List<AIPath.Point>();

                for (int j = 0; j < MakerAIPaths[i].m_points.Count; j++)
                {
                    var TempPoint = new AIPath.Point();
                    TempPoint.position = MakerAIPaths[i].m_points[j].position;
                    TempPoint.windup = MakerAIPaths[i].m_points[j].windup;
                    NewPath.m_points.Add(TempPoint);
                }

                NewPath.CalculateSegments();
                copied++;
            }

            if (copied > 0)
            {
                Log.LogInfo($"[AIPaths] copied {copied} maker path(s) from the bundle (sidecar ignored)");
            }
            else
            {
                // AI needs ONE complete start->finish route. The sidecar holds SSX
                // branch FRAGMENTS — no single fragment spans the course, so the AI
                // runs off a fragment end and (especially on a scaled map, where the
                // gaps exceed FindNewPath's absolute 50-unit reacquire threshold)
                // loses its path, hits the 1.5s no-path timeout, and respawn-loops
                // without ever finishing (MECHANICS_DIAGNOSIS Bug 3). The race line
                // IS a complete ordered route, so prefer it as the AI path; fall
                // back to the sidecar fragments only if there's no usable race line.
                int built = BuildAIPathFromRaceLine(AIParent.transform);
                if (built == 0) built = BuildAIPathsFromSidecar(AIParent.transform);
                if (built == 0)
                    Log.LogWarning("[AIPaths] no AI path data (maker points empty, no usable race line, no sidecar); AI will hold the start gate but cannot race");
            }
            Traverse.Create(levelManager).Field("pathsParent").SetValue(AIParent.transform);


            GameObject AIPDividerParent = new GameObject("AI Paths Divider Parent");
            AIPDividerParent.transform.parent = levelManager.transform;
            var MakerAIPathsDivider = FindObjectsByType(typeof(MakerAIPathDivider), FindObjectsSortMode.None) as MakerAIPathDivider[];
            for (int i = 0; i < MakerAIPathsDivider.Length; i++)
            {
                var NewPath = MakerAIPathsDivider[i].gameObject.AddComponent<AIPathDivider>();

                // The game enumerates DIRECT children of dividersParent — parent
                // dividers under the transform actually registered as
                // dividersParent (they used to go under AIParent and were never
                // found).
                MakerAIPathsDivider[i].transform.parent = AIPDividerParent.transform;

                NewPath.m_points = MakerAIPathsDivider[i].m_points;
            }
            Traverse.Create(levelManager).Field("dividersParent").SetValue(AIPDividerParent.transform);

            var TempMakerRaceLine = FindAnyObjectByType<MakerRaceLine>();
            var TempRaceLine = TempMakerRaceLine.gameObject.AddComponent<RaceLine>();

            TempRaceLine.m_points = new List<RaceLine.Point>();

            for (int i = 0;i < TempMakerRaceLine.m_points.Count;i++)
            {
                var RaceLinePoint = new RaceLine.Point();

                RaceLinePoint.position = TempMakerRaceLine.m_points[i];

                TempRaceLine.m_points.Add(RaceLinePoint);
            }

            TempRaceLine.CalculateSegments();

            Traverse.Create(levelManager).Field("raceLine").SetValue(TempRaceLine);

            // Respawn markers ALONG the race line (MECHANICS_DIAGNOSIS Bug 2).
            // The maker respawn points are SSX per-racer branch-line HEADS,
            // scattered off the ridden route; the game's Respawn picks the nearest
            // marker, so a fallen rider gets teleported to an off-route pocket they
            // "have never seen" — worse at 2.5x marker spacing. Replace the parent
            // with markers sampled on the reconstructed race line, each facing
            // down-line (Respawn's down-course 2x penalty reads child.forward), so
            // every respawn drops the rider back ONTO the route facing downhill.
            // The game re-enumerates children live per respawn, so runtime-built
            // markers just work. Falls back to the maker parent if the race line is
            // too short to sample.
            // ⚠️ Seat these on the snow, and DROP the ones with nothing under them.
            // SnapMarkerToGround above only ever sees the MAKER markers, which this
            // block then throws away — so before this, every race-line respawn was
            // used exactly as authored. That is fine for an SSX/SSX3 race line
            // (ground-following AI data) and wrong for one derived from a recorded
            // human run: alps0101's ghost line has 53% of its points more than 5 m
            // up and 13% more than 40 m, so half its respawns were into freefall.
            // Dropping the unreachable ones beats snapping them, because what is
            // under a big jump is not the course (one alps0101 point flies a ravine
            // whose floor is 122 m down); the game picks the NEAREST marker and
            // they sit ~25 m apart, so the neighbours cover the gap. Gated on
            // spawnsnap so a map that opts out (gari) keeps its authored markers.
            if (TempRaceLine.m_points != null && TempRaceLine.m_points.Count >= 2)
            {
                var respawnParent = new GameObject("Respawn Points (race line)");
                respawnParent.transform.parent = levelManager.transform;
                // Static, so it outlives the scene it belongs to: a second map would
                // otherwise escalate riders onto the previous map's markers.
                Plugin.RaceLineRespawns.Clear();
                Snowboarder_Respawn.ResetLoopState();
                var rpts = TempRaceLine.m_points;
                bool seat = Plugin.SpawnSnapMode != Plugin.SpawnSnap.Off;
                int made = 0, moved = 0, dropped = 0;
                for (int i = 0; i < rpts.Count; i++)
                {
                    Vector3 p = rpts[i].position;
                    if (seat)
                    {
                        if (!GroundSeat(p, out float sy, out _, out _)) { dropped++; continue; }
                        if (Plugin.SpawnSnapMode == Plugin.SpawnSnap.Raise) sy = Mathf.Max(p.y, sy);
                        if (Mathf.Abs(sy - p.y) > 0.01f) moved++;
                        p.y = sy;
                    }
                    var go = new GameObject("Respawn " + i.ToString(CultureInfo.InvariantCulture));
                    go.transform.parent = respawnParent.transform;
                    go.transform.position = p;
                    Vector3 fwd = (i + 1 < rpts.Count)
                        ? rpts[i + 1].position - rpts[i].position
                        : rpts[i].position - rpts[i - 1].position;
                    fwd.y = 0f;
                    if (fwd.sqrMagnitude > 1e-6f)
                        go.transform.rotation = Quaternion.LookRotation(fwd.normalized, Vector3.up);
                    Plugin.RaceLineRespawns.Add(go.transform);
                    made++;
                }
                // Every point airborne means the cast never found the terrain at all
                // (bad ground layers, say) — keep the maker markers rather than hand
                // the game an empty parent, which would strand a fallen rider.
                if (made > 0)
                {
                    Traverse.Create(levelManager).Field("respawnPointsParent").SetValue(respawnParent.transform);
                    Log.LogInfo($"[Respawn] built {made} race-line respawn marker(s) ({moved} seated on ground, {dropped} dropped as airborne), replacing maker branch-head markers");
                }
                else
                {
                    UnityEngine.Object.Destroy(respawnParent);
                    // The markers we just recorded are about to be destroyed; leaving
                    // them listed would have the loop guard escalate onto nulls.
                    Plugin.RaceLineRespawns.Clear();
                    Log.LogWarning($"[Respawn] all {rpts.Count} race-line points are airborne — keeping maker branch-head markers");
                }
            }

            // Kill the "bouncy glass" (MECHANICS_DIAGNOSIS Bug 4). SSX glass panes
            // import as SOLID thin double-sided MeshColliders on layer 8; a
            // CharacterController hitting the thin quad depenetrates along the
            // contact normal while KEEPING its speed = a bounce (no PhysicMaterial
            // is involved). Strip the collider from any near-flat layer-8
            // MeshCollider whose thin axis is roughly HORIZONTAL (a vertical
            // wall/pane the rider slams into sideways) so the rider rides straight
            // through it — the user is fine with glass not breaking as long as it
            // isn't bouncy. Panes whose thin axis is VERTICAL (floors, and the
            // glass RAMPS you ride ON, e.g. merquer) are left solid so they stay
            // rideable. Convex colliders (PhysProps) are excluded.
            int glassStripped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var mc in sceneRoot.GetComponentsInChildren<MeshCollider>(true))
                {
                    var go = mc.gameObject;
                    if (go.layer != 8 || mc.convex || mc.sharedMesh == null) continue;
                    Vector3 ext = mc.sharedMesh.bounds.size;
                    int thin = 0;
                    if (ext.y < ext[thin]) thin = 1;
                    if (ext.z < ext[thin]) thin = 2;
                    float med = Mathf.Max(ext[(thin + 1) % 3], ext[(thin + 2) % 3]);
                    if (med < 1e-3f || ext[thin] > 0.3f * med) continue; // not a thin sheet
                    Vector3 thinLocal = thin == 0 ? Vector3.right : thin == 1 ? Vector3.up : Vector3.forward;
                    Vector3 thinWorld = go.transform.TransformDirection(thinLocal).normalized;
                    if (Mathf.Abs(thinWorld.y) > 0.7f) continue; // thin axis vertical -> floor/ramp, keep solid
                    UnityEngine.Object.Destroy(mc);
                    glassStripped++;
                }
            }
            if (glassStripped > 0)
                Log.LogInfo($"[Glass] stripped {glassStripped} thin vertical layer-8 collider(s) (bounce fix)");

            //Convert pipeline-marked props into crashable PhysicsProps
            // Naming contract with the map pipeline: any GameObject named
            // "PhysProp_*" becomes a game PhysicsProp (the crash-through-it prop:
            // rider contact calls Launch()). Order matters — PhysicsProp.Awake
            // immediately does GetComponent<Rigidbody>().Sleep(), so the Rigidbody
            // must exist BEFORE PhysicsProp is added. Layer 15 ("PhysicsProp") is
            // excluded from both CastLayerMask and GroundLayers, so these can
            // never read as ground. soundName is a private serialized string that
            // arrives null on runtime-added components; null passes the game's
            // != "" check and would PostEvent(null) — set it to "" explicitly.
            // A dynamic Rigidbody needs a convex collider: log-and-skip anything
            // with a non-convex MeshCollider (or none at all).
            int propsConverted = 0, propsSkipped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("PhysProp_")) continue;
                    var propGO = t.gameObject;
                    if (propGO.GetComponent<PhysicsProp>() != null) continue; // already a prop
                    var col = propGO.GetComponent<Collider>();
                    var meshCol = col as MeshCollider;
                    if (col == null || (meshCol != null && !meshCol.convex))
                    {
                        propsSkipped++;
                        Log.LogDebug($"[PhysProps] '{propGO.name}' skipped: {(col == null ? "no collider" : "non-convex MeshCollider")}");
                        continue;
                    }
                    propGO.AddComponent<Rigidbody>();
                    var prop = propGO.AddComponent<PhysicsProp>();
                    propGO.layer = 15;
                    Traverse.Create(prop).Field("soundName").SetValue("");
                    propsConverted++;
                }
            }
            if (propsConverted > 0 || propsSkipped > 0)
                Log.LogInfo($"[PhysProps] converted {propsConverted} crashable prop(s), skipped {propsSkipped}");

            // Trick-multiplier pickups: hand the flakes to the GAME's own snowflake
            // behaviour instead of the Maker's generic one.
            //
            // The map pipeline can only emit Maker components, so an imported flake
            // ships with MakerSnowflake (tags the GO — that's all it does) plus a
            // MakerObjectAnimation spinning it 60 deg/s about its LOCAL Y. The game's
            // built-in courses instead carry `Snowflake`, which sets an ABSOLUTE
            // world rotation of 3 rad/s (~172 deg/s) about Y — verified in level1,
            // where every stock flake is: layer 2, tag Snowflake<n>, `Snowflake`, and
            // a SPHERE trigger of radius 10 (its mesh is a 14.6 x 16.8 x 1.1 panel,
            // so the sphere covers the flake's face AND stands ~10 units proud of it
            // on either side).
            //
            // Two things follow, and both are why ported pickups feel off:
            //   * spin is at the mercy of a Maker component we don't control (Molokai
            //     ships a bundle structurally identical to maps that do spin, yet
            //     doesn't) — the game's own component removes that variable, and
            //     matches the built-in courses' speed while it's there.
            //   * our trigger is the flake's own convex hull, i.e. ~2 units thick
            //     along the panel normal. The rider crosses that in a single frame at
            //     speed, so pickups get missed. Stock's grab depth is the radius, not
            //     the panel: a sphere of max(10, largest extent) is never smaller than
            //     stock's absolute grab and always encloses the (bigger, 2.5x-scaled)
            //     ported flake, so its corners stay collectable.
            int flakes = 0, flakeSpheres = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var mf in sceneRoot.GetComponentsInChildren<MakerSnowflake>(true))
                {
                    var flakeGO = mf.gameObject;
                    if (flakeGO.GetComponent<Snowflake>() != null) continue; // already converted
                    var makerAnim = flakeGO.GetComponent<MakerObjectAnimation>();
                    if (makerAnim != null) UnityEngine.Object.Destroy(makerAnim);
                    flakeGO.AddComponent<Snowflake>();
                    flakes++;

                    // Swap the mesh-tight trigger for the stock sphere. Keep the mesh
                    // collider only if there's no renderer to size a sphere from.
                    var meshCol = flakeGO.GetComponent<MeshCollider>();
                    var filter = flakeGO.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    var b = filter.sharedMesh.bounds;
                    var sphere = flakeGO.AddComponent<SphereCollider>();
                    sphere.isTrigger = true;
                    sphere.center = b.center;
                    sphere.radius = Mathf.Max(10f, Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z)));
                    if (meshCol != null) UnityEngine.Object.Destroy(meshCol);
                    flakeSpheres++;
                }
            }
            if (flakes > 0)
                Log.LogInfo($"[Snowflakes] converted {flakes} pickup(s) to the game's Snowflake behaviour, {flakeSpheres} sphere trigger(s)");

            // Phase 4 custom items — Wind volumes. Naming contract with SSXImporter.BuildItems:
            // a GO named "Wind_<strength>" carries a BoxCollider (geometry only) and is oriented
            // so its forward (+Z) is the world push direction. We attach a WindVolume behavior
            // that, while a Snowboarder is inside the box, adds forward*strength*dt to that
            // rider's private Snowboarder.velocity each physics step (the exact field
            // UpdateGrounded feeds to CharacterController.Move). Containment is done analytically
            // (OBB test in WindVolume) and the collider is DISABLED at runtime — so a wind box
            // never blocks the rider and is never mistaken for ground by the game's ground
            // SphereCast (which hits triggers by default). Strength (m/s^2) is parsed from the
            // name, invariant culture, matching the importer.
            int windAttached = 0, windSkipped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Wind_")) continue;
                    var windGO = t.gameObject;
                    if (windGO.GetComponent<WindVolume>() != null) continue; // already attached
                    if (windGO.GetComponent<BoxCollider>() == null)
                    { windSkipped++; Log.LogDebug($"[Wind] '{windGO.name}' skipped: no BoxCollider"); continue; }
                    float strength;
                    string tail = t.name.Substring("Wind_".Length);
                    if (!float.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out strength))
                    {
                        strength = 10f; // default when the name has no parseable strength
                        Log.LogDebug($"[Wind] '{windGO.name}' name has no parseable strength -> default {strength}");
                    }
                    var wv = windGO.AddComponent<WindVolume>();
                    // Push direction: an optional "Dir" child (importer emits it for SSX boost
                    // fields, whose push vector is independent of the box's own orientation — the
                    // box is oriented to the source volume so its OBB containment matches). No
                    // Dir child (editor-drawn wind volumes) => the box's own forward, as before.
                    var windDir = t.Find("Dir");
                    wv.WorldDir = windDir != null ? windDir.forward : t.forward;
                    wv.Strength = strength;
                    windAttached++;
                }
            }
            if (windAttached > 0 || windSkipped > 0)
                Log.LogInfo($"[Wind] attached {windAttached} wind volume(s), skipped {windSkipped}");

            // Phase 4 custom items — Teleport volumes. Naming contract with
            // SSXImporter.BuildItems: a GO named "Teleport_<i>" carries a BoxCollider (the
            // source region) and a child "Target" transform (the destination pos + facing).
            // TeleportVolume repositions any rider that ENTERS the box to the Target, using
            // the game's own CharacterController-teleport idiom (disable CC -> set transform
            // -> re-enable CC -> zero velocity, RE'd from Snowboarder.Respawn). Edge-triggered
            // (once per entry). Same collider-free OBB containment as WindVolume.
            int tpAttached = 0, tpSkipped = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Teleport")) continue;
                    var tpGO = t.gameObject;
                    if (tpGO.GetComponent<TeleportVolume>() != null) continue; // already attached
                    if (tpGO.GetComponent<BoxCollider>() == null)
                    { tpSkipped++; Log.LogDebug($"[Teleport] '{tpGO.name}' skipped: no BoxCollider"); continue; }
                    var target = t.Find("Target");
                    if (target == null)
                    { tpSkipped++; Log.LogDebug($"[Teleport] '{tpGO.name}' skipped: no Target child"); continue; }
                    tpGO.AddComponent<TeleportVolume>();   // reads box + Target child in Awake
                    tpAttached++;
                }
            }
            if (tpAttached > 0 || tpSkipped > 0)
                Log.LogInfo($"[Teleport] attached {tpAttached} teleport volume(s), skipped {tpSkipped}");

            // Phase 4 custom items — TriggerAnim ("door") volumes. Naming contract with
            // SSXImporter.BuildItems: a GO named "TriggerAnim_*" ships fully authored —
            // LIVE trigger BoxCollider (layer 2), legacy Animation with the baked
            // closed→open clip (ClampForever), and the door prop reparented underneath.
            // We only attach the GAME'S OWN PlayAnimationOnTriggerEnter (HOOK_NOTES §11,
            // the SSX-Tokyo tree-trigger mechanism): any Snowboarder entering plays the
            // Animation. Its Start() self-heals m_animation and the audio locations, but
            // OnTriggerEnter posts startEvent with NO null check — a component born via
            // AddComponent (not deserialization) has null Wwise event fields, so blank
            // AK.Wwise.Event instances are Traverse-set (Event.Post is IsValid()-gated:
            // a blank event no-ops silently instead of NRE'ing). Its private `triggered`
            // one-shot flag is checked but never set by the game, so the anim replays on
            // every entry — harmless: ClampForever means an open door just stays open.
            int taAttached = 0, taSkipped = 0;
            var patType = AccessTools.TypeByName("PlayAnimationOnTriggerEnter");
            var wwiseEventType = AccessTools.TypeByName("AK.Wwise.Event");
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("TriggerAnim_")) continue;
                    var taGO = t.gameObject;
                    if (patType == null || wwiseEventType == null)
                    { taSkipped++; Log.LogWarning("[TriggerAnim] game types missing — skipped"); continue; }
                    if (taGO.GetComponent(patType) != null) continue;   // already attached
                    if (taGO.GetComponent<Animation>() == null)
                    { taSkipped++; Log.LogDebug($"[TriggerAnim] '{taGO.name}' skipped: no Animation"); continue; }
                    var pat = taGO.AddComponent(patType);
                    var tr = Traverse.Create(pat);
                    tr.Field("startEvent").SetValue(Activator.CreateInstance(wwiseEventType));
                    tr.Field("endEvent").SetValue(Activator.CreateInstance(wwiseEventType));
                    // Start() would set these anyway; set them here too in case a rider is
                    // already inside the box when physics wakes up before Start runs.
                    tr.Field("m_animation").SetValue(taGO.GetComponent<Animation>());
                    tr.Field("audioLocation1").SetValue(taGO);
                    tr.Field("audioLocation2").SetValue(taGO);
                    taAttached++;
                }
            }
            if (taAttached > 0 || taSkipped > 0)
                Log.LogInfo($"[TriggerAnim] attached {taAttached} door trigger(s), skipped {taSkipped}");

            // AnimDoor (feature: gameplay.animDoors). Naming contract with
            // SSXImporter.BuildAnimDoors:
            //   AnimDoor_<doorIdx>            — door GO with a legacy Animation (baked open clip).
            //   AnimDoorTrig_<doorIdx>_<trig> — trigger box relaying into that door.
            // Unlike TriggerAnim above (1 trigger reparents + plays 1 prop on its OWN GO),
            // SSX doors are N triggers -> M doors (merquer: one trigger opens two gates), and
            // the game's PlayAnimationOnTriggerEnter force-binds m_animation to its own GO in
            // Start() — so a shared door can't ride under a trigger. The door instead carries
            // its own Animation; each trigger references it by parsing <doorIdx> from its name.
            // AnimDoorTrigger Play()s the resolved door on rider entry (analytic OBB
            // containment like Teleport/Wind — no trigger-callback reliance for thin volumes).
            int adDoors = 0, adTrig = 0, adSkip = 0;
            var animDoorByIdx = new Dictionary<string, Animation>();
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("AnimDoor_")) continue;
                    var anim = t.GetComponent<Animation>();
                    if (anim == null) continue;   // static door (no animated part) — nothing to drive
                    animDoorByIdx[t.name.Substring("AnimDoor_".Length)] = anim;
                    adDoors++;
                }
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("AnimDoorTrig_")) continue;
                    var trigGO = t.gameObject;
                    if (trigGO.GetComponent<AnimDoorTrigger>() != null) continue;   // already attached
                    // name = AnimDoorTrig_<doorIdx>_<trigIdx>; the door idx is segment [1].
                    var segs = t.name.Split('_');
                    if (segs.Length < 3 || !animDoorByIdx.TryGetValue(segs[1], out var doorAnim))
                    { adSkip++; Log.LogDebug($"[AnimDoor] trigger '{t.name}' has no matching AnimDoor_ — skipped"); continue; }
                    var rel = trigGO.AddComponent<AnimDoorTrigger>();
                    rel.target = doorAnim;
                    rel.clipName = "AnimDoor_" + segs[1];
                    adTrig++;
                }
            if (adDoors > 0 || adTrig > 0 || adSkip > 0)
                Log.LogInfo($"[AnimDoor] attached {adTrig} trigger(s) driving {adDoors} door(s), skipped {adSkip}");

            // MovingSurface (feature: gameplay.movingColliders). Same relay as AnimDoor with one
            // difference that is the whole point of the feature: keepColliders. A door disables its
            // colliders on fire so a non-convex mesh never moves while colliding; a rideable kicker
            // must keep them and let the animation drag the collider. Naming contract with
            // SSXImporter.BuildMovingSurfaces:
            //   MoveSurf_<idx>              — mover GO (legacy Animation, layer 0, live colliders).
            //   MoveSurfTrig_<idx>_<trig>   — trigger box relaying into it.
            int msSurf = 0, msTrig = 0, msSkip = 0;
            var moveSurfByIdx = new Dictionary<string, Animation>();
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("MoveSurf_")) continue;
                    var anim = t.GetComponent<Animation>();
                    if (anim == null) continue;   // static mover (no animated part) — nothing to drive
                    moveSurfByIdx[t.name.Substring("MoveSurf_".Length)] = anim;
                    msSurf++;
                }
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("MoveSurfTrig_")) continue;
                    var trigGO = t.gameObject;
                    if (trigGO.GetComponent<AnimDoorTrigger>() != null) continue;   // already attached
                    // name = MoveSurfTrig_<surfIdx>_<trigIdx>; the mover idx is segment [1].
                    var segs = t.name.Split('_');
                    if (segs.Length < 3 || !moveSurfByIdx.TryGetValue(segs[1], out var surfAnim))
                    { msSkip++; Log.LogDebug($"[MoveSurf] trigger '{t.name}' has no matching MoveSurf_ — skipped"); continue; }
                    var rel = trigGO.AddComponent<AnimDoorTrigger>();
                    rel.target = surfAnim;
                    rel.clipName = "MoveSurf_" + segs[1];
                    rel.keepColliders = true;   // the collider MOVES — that is the feature
                    msTrig++;
                }
            if (msSurf > 0 || msTrig > 0 || msSkip > 0)
                Log.LogInfo($"[MoveSurf] attached {msTrig} trigger(s) driving {msSurf} mover(s), skipped {msSkip}");

            // Per-level sky-fill override. Naming contract with SSXImporter.BuildLighting:
            // an empty marker GO named "SkyFill_<RRGGBB>" (from Overrides.json
            // lighting.skyFillColor). MUST be read before the SkydomeFollow attach below —
            // its OnEnable samples the fill once. Always assigned (found or null) so a
            // previous map's override can't leak onto the next one.
            SceneSkyFill = null;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("SkyFill_")) continue;
                    string hex = "#" + t.name.Substring("SkyFill_".Length);
                    if (ColorUtility.TryParseHtmlString(hex, out var fill))
                    {
                        SceneSkyFill = fill;
                        Log.LogInfo($"[Skydome] per-level sky fill {hex} (overrides [Skydome] FillColor)");
                    }
                    else
                        Log.LogWarning($"[Skydome] sky-fill marker '{t.name}' not parseable; using [Skydome] FillColor");
                }
            }

            // Skydome. Naming contract with SSXImporter.BuildSkybox: a GO named
            // "Skydome" carries the level's unlit, Background-queue, ZWrite-off dome
            // mesh (no collider). We attach SkydomeFollow so it stays centered on the
            // camera — the dome is baked at map scale (only tens of units across), so
            // without following, the rider would ride out of it and lose the sky. Only
            // position follows; rotation stays world-fixed so the sky is compass-stable.
            int skydomes = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name != "Skydome") continue;
                    if (t.GetComponent<SkydomeFollow>() != null) continue; // already attached
                    t.gameObject.AddComponent<SkydomeFollow>();
                    skydomes++;
                }
            }
            if (skydomes > 0)
                Log.LogInfo($"[Skydome] attached camera-follow to {skydomes} skydome(s)");

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

            // Smashable glass / breakables. Naming contract with SSXImporter.BuildProps:
            // a GO named "Destructible_<orig>" carries the intact model (render mesh, NO
            // collider) plus inactive "Frag_<i>" children (per-shard meshes) and optional
            // "DisableRef_<name>" markers. We wire the game's own Destructible on it, plus a
            // Hook-side Smashable that DRIVES the break. WHY not rely on the game's trigger:
            // Snowboarder.OnTriggerEnter does call Destructible.Break, but only if the pane's
            // trigger fires an enter event for the rider's CharacterController — unreliable
            // for us (the rider crosses at 150-190 mph → ~1.5 m/tick, tunnelling a thin pane;
            // and a bundle-added trigger on the glass layer isn't guaranteed to fire against
            // the rider's layer). Smashable does a SWEPT test instead (can't tunnel) and calls
            // the public Break directly. The BoxCollider (render-mesh bounds, min 0.3 m thick)
            // is a geometry carrier for Smashable and for Break's GetComponent<Collider>() —
            // Smashable DISABLES it at runtime; layer 2 (the game's glass layer, excluded from
            // the ground SphereCast) keeps an intact pane from ever reading as ground. Private
            // serialized List<T>/string fields arrive null on runtime-added components
            // (feature 7): Traverse-set all four — lists may be empty but NEVER null
            // (Break foreach-es fragmentList; soundName null would PostEvent(null)).
            int smashWired = 0, smashFrags = 0;
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Destructible_")) continue;
                    var dGO = t.gameObject;
                    if (dGO.GetComponent<Destructible>() != null) continue; // already wired
                    var box = dGO.AddComponent<BoxCollider>();
                    var dmf = dGO.GetComponent<MeshFilter>();
                    if (dmf != null && dmf.sharedMesh != null)
                    {
                        var b = dmf.sharedMesh.bounds;
                        box.center = b.center;
                        box.size = Vector3.Max(b.size, new Vector3(0.3f, 0.3f, 0.3f));
                    }
                    box.isTrigger = true;
                    dGO.layer = 2;
                    var frags = new List<DestructibleFragment>();
                    var disables = new List<GameObject>();
                    foreach (Transform child in t)
                    {
                        if (child.name.StartsWith("Frag_"))
                        {
                            child.gameObject.SetActive(false);
                            child.gameObject.layer = 0;
                            frags.Add(child.gameObject.AddComponent<DestructibleFragment>());
                        }
                        else if (child.name.StartsWith("DisableRef_"))
                        {
                            // Resolve by original instance name; the scene GO may carry a
                            // pipeline prefix (UVScroll_/PhysProp_). Unresolved refs are
                            // fine — megaple's invisible Glass_Surface proxies are dropped
                            // at import, and the pane needs no wall (it's a pure trigger).
                            string target = child.name.Substring("DisableRef_".Length);
                            var found = FindSceneGOByInstanceName(levelManagerMaker.gameObject.scene, target);
                            if (found != null) disables.Add(found);
                        }
                    }
                    var destr = dGO.AddComponent<Destructible>();
                    var trav = Traverse.Create(destr);
                    trav.Field("fragmentList").SetValue(frags);
                    trav.Field("disableList").SetValue(disables);
                    trav.Field("hideSelf").SetValue(true);
                    trav.Field("soundName").SetValue("Play_GlassSmash_Large");
                    dGO.AddComponent<Smashable>(); // drives Break via swept detection; disables the box
                    smashWired++; smashFrags += frags.Count;
                }
            }
            if (smashWired > 0)
                Log.LogInfo($"[Destructible] wired {smashWired} smashable(s) with {smashFrags} shard fragment(s)");

            // Atmospheric falling snow. Naming contract with SSXImporter.BuildLighting:
            // an empty GO "SnowFX_<intensity>". We build a camera-follow ParticleSystem
            // at runtime (SnowFall) — a bundle-baked system would be engine-module-stripped
            // and, world-simulated, couldn't stay wrapped around the chase cam. One system
            // total (there's only ever one marker); intensity scales emission + cap.
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("SnowFX_")) continue;
                    if (SnowFall.Active) break;
                    float si;
                    if (!float.TryParse(t.name.Substring("SnowFX_".Length), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out si))
                    {
                        si = 1f;
                        Log.LogDebug($"[SnowFall] '{t.name}' has no parseable intensity -> default {si:F2}");
                    }
                    SnowFall.Spawn(si);
                    Log.LogInfo($"[SnowFall] camera-follow snow spawned (intensity {si:F2})");
                }
                if (SnowFall.Active) break;
            }

            // Post-process bloom. Naming contract: "Bloom_<intensity>_<threshold>". PostFX
            // creates a global URP Volume with a Bloom override and flips the render
            // camera's post-processing on (feature: PostFX). One volume total.
            foreach (var sceneRoot in levelManagerMaker.gameObject.scene.GetRootGameObjects())
            {
                foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith("Bloom_")) continue;
                    if (PostFX.Active) break;
                    string[] seg = t.name.Split('_');
                    float bi = 0.7f, bt = 0.9f, parsed;
                    // ⚠️ Parse into a scratch float, NOT straight into bi/bt. TryParse
                    // zeroes its out param on failure, so passing bi directly turned a
                    // malformed "Bloom_x_y" into intensity 0 — bloom silently OFF — while
                    // the log line below cheerfully reported the 0 as if authored.
                    if (seg.Length >= 2)
                    {
                        if (float.TryParse(seg[1], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) bi = parsed;
                        else Log.LogDebug($"[PostFX] '{t.name}' has no parseable intensity -> default {bi:F2}");
                    }
                    if (seg.Length >= 3)
                    {
                        if (float.TryParse(seg[2], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) bt = parsed;
                        else Log.LogDebug($"[PostFX] '{t.name}' has no parseable threshold -> default {bt:F2}");
                    }
                    PostFX.Enable(bi, bt);
                    Log.LogInfo($"[PostFX] bloom enabled (intensity {bi:F2}, threshold {bt:F2})");
                }
                if (PostFX.Active) break;
            }

            // Respawn / OOB instrumentation. Last, so it belongs to the scene the
            // player will actually ride and dies with it (which is what guarantees
            // its report gets written).
            if (perfHarness != null && perfHarness.Value)
                PerfHarness.Spawn();

            // NB: the open-top sky-fill is started from OnSceneLoaded (not here) —
            // SolidColorSky captures the ACTIVE scene name, which isn't the custom scene
            // yet at LoadLevelManager time, so starting it here exits immediately.
        }

        // Resolves a Destructible DisableRef_ marker back to the live scene GO. The
        // importer records the SSX instance's ORIGINAL name, but the pipeline may have
        // prefixed the scene GO (UVScroll_<u>_<v>_<name>, PhysProp_<name>) — so accept
        // an exact match or any name ending in "_<name>". Dropped instances (megaple's
        // invisible Glass_Surface proxies) simply resolve to null and are skipped.
        private static GameObject FindSceneGOByInstanceName(UnityEngine.SceneManagement.Scene scene, string instName)
        {
            string suffix = "_" + instName;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                    if (tr.name == instName || tr.name.EndsWith(suffix, StringComparison.Ordinal))
                        return tr.gameObject;
            return null;
        }

        // Builds the game-side segment polyline for a Maker rail.
        //
        // ⚠️ Do NOT go back to MakerRails.GetSegments(). Its GenerateSegments() loops
        // from index 1 and stores TempList[i] as the segment position, so a rail's
        // FIRST point never becomes a segment: the polyline — and the collider we
        // build from it — starts at P1, leaving the whole P0→P1 span dead. That span
        // is the ENTRY end of the rail, so you ride through the visible rail model
        // and nothing grabs. Measured across the shipped ports: a median 3–13 m of
        // ungrindable lead-in on EVERY rail (worst single case 187 m on Pipedream),
        // 6–19% of total rail length. It's the reason a small jump onto the middle
        // of a rail works when riding on from the end doesn't.
        //
        // So: read the PointList directly and emit one segment per point, P0 included.
        // Point positions are read as localPosition exactly as MakerRails does, so
        // every segment from P1 on is bit-identical to the old path; the only change
        // is the prepended P0 (and `distance` becoming true cumulative arc length,
        // which the old path lagged by one span — Snowboarder.railDistance only ever
        // compares it frame-to-frame, so the absolute origin is free).
        private static List<Rail.Segment> BuildRailSegments(MakerRails rail)
        {
            var segments = new List<Rail.Segment>();
            var list = rail.PointList == null ? null : rail.PointList.transform;
            if (list == null || list.childCount < 2)
            {
                // No usable PointList — fall back to whatever the Maker component has.
                // ⚠️ This is the BAD path the comment above exists to prevent: it drops
                // P0 and leaves an ungrindable lead-in. It is kept only so a rail with a
                // malformed PointList still grinds *somewhere* rather than not at all,
                // but it must never be silent — a rider reporting "this rail doesn't
                // grab from the top" needs to find this line in the log rather than
                // re-derive the whole P0 investigation. See CLAUDE.md §6.
                Log.LogWarning($"[Rails] '{rail.gameObject.name}' has no usable PointList "
                    + $"({(list == null ? "null" : list.childCount + " point(s), need 2")}) — "
                    + "falling back to MakerRails.GetSegments(), which DROPS THE FIRST POINT "
                    + "and leaves a dead ungrindable lead-in at the entry end of this rail.");
                foreach (var s in rail.GetSegments())
                {
                    var fb = new Rail.Segment(Vector3.zero, Vector3.up);
                    fb.position = s.Point;
                    fb.rotation = s.Rotation;
                    fb.distance = s.Distance;
                    segments.Add(fb);
                }
                return segments;
            }

            var points = new List<Vector3>(list.childCount);
            for (int i = 0; i < list.childCount; i++)
                points.Add(list.GetChild(i).localPosition);

            float distance = 0f;
            Quaternion rotation = Quaternion.identity;
            for (int i = 0; i < points.Count; i++)
            {
                // Segment i faces along the span that ARRIVES at it (P0 borrows P1's,
                // being the one span it starts rather than ends).
                Vector3 dir = i == 0 ? points[1] - points[0] : points[i] - points[i - 1];
                if (dir.sqrMagnitude > 1e-8f)
                    rotation = Quaternion.LookRotation(dir, Vector3.up); // else keep the last good one
                if (i > 0)
                    distance += (points[i] - points[i - 1]).magnitude;

                var seg = new Rail.Segment(Vector3.zero, Vector3.up);
                seg.position = points[i];
                seg.rotation = rotation;
                seg.distance = distance;
                segments.Add(seg);
            }
            return segments;
        }

        // Builds a solid (non-convex) MeshCollider along the rail's segment line —
        // the built-in-authentic setup: built-in rails are plain solid MeshColliders
        // on the same GameObject as the Rail component. Rail segment positions are
        // rail-GO-LOCAL (Rail.GetClosestPosition works in local space), so the mesh
        // is built in local space too. Each consecutive segment pair becomes a thin
        // box: top face ON the a-b line (the game snaps the rider to
        // GetClosestPosition + up*radius, so the surface must sit exactly there),
        // extruded 0.2 m straight down. Solid is safe: GroundLayers/CastLayerMask
        // exclude layer 9, so ground/side casts can never see these.
        private static bool BuildRailCollider(GameObject railGO, List<Rail.Segment> segments)
        {
            // HalfWidth is the LATERAL catch radius of the grind collider: the
            // game box-casts along rider velocity for a layer-9 collider, so a
            // wider strip is an easier target to land/jump onto. Widened from
            // 0.125 (0.25 m) to 0.25 (0.5 m) at user request. The top face stays
            // pinned to the a-b rail line (do NOT raise it — the rider is snapped
            // to GetClosestPosition + up*radius, so the surface must sit there);
            // only the width grows. Tunable if it feels too grabby / too narrow.
            //
            // SCALE with WorldScale (MECHANICS_DIAGNOSIS Bug 1): the rail MESH is
            // vertex-baked at the map's world scale, but the game's grind box
            // (BoardHalfExtents) is a fixed absolute size — so on a 2.5x map a
            // 0.5 m strip is a 2.5x-too-thin sliver on a rail that looks 2.5x
            // fatter, and a 0.2 m depth can't bridge 2.5x curve-chord sag → most
            // grind approaches miss. Multiplying by WorldScale restores the 1x
            // "target size relative to the visible rail". Inert at scale 1.
            float HalfWidth = 0.25f * Plugin.WorldScale;
            float Depth = 0.2f * Plugin.WorldScale;

            var verts = new List<Vector3>();
            var tris = new List<int>();
            // Outward-wound faces of one thin box (verts 0-3 top a->b, 4-7 below).
            int[] boxTris = {
                0, 2, 1,  0, 3, 2,   // top (+up)
                4, 5, 6,  4, 6, 7,   // bottom (-up)
                0, 1, 5,  0, 5, 4,   // end cap at a
                3, 6, 2,  3, 7, 6,   // end cap at b
                1, 2, 6,  1, 6, 5,   // +right side
                0, 4, 7,  0, 7, 3    // -right side
            };
            for (int i = 0; i + 1 < segments.Count; i++)
            {
                Vector3 a = segments[i].position;
                Vector3 b = segments[i + 1].position;
                Vector3 d = b - a;
                if (d.magnitude < 0.001f) continue; // degenerate pair (~1 mm)
                Vector3 dir = d.normalized;
                Vector3 right = Mathf.Abs(dir.y) > 0.99f
                    ? Vector3.right // near-vertical: any horizontal width works
                    : Vector3.Cross(Vector3.up, dir).normalized;
                Vector3 r = right * HalfWidth;
                Vector3 down = Vector3.down * Depth;

                int v0 = verts.Count;
                verts.Add(a - r);
                verts.Add(a + r);
                verts.Add(b + r);
                verts.Add(b - r);
                verts.Add(a - r + down);
                verts.Add(a + r + down);
                verts.Add(b + r + down);
                verts.Add(b - r + down);
                for (int t = 0; t < boxTris.Length; t++) tris.Add(v0 + boxTris[t]);
            }
            if (tris.Count == 0) return false;

            var mesh = new Mesh();
            mesh.name = "RailCollider (" + railGO.name + ")";
            if (verts.Count > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            railGO.AddComponent<MeshCollider>().sharedMesh = mesh; // convex stays false
            return true;
        }

        // [Debug] RailColliderProbe: replays the game's grind-detection contract
        // against the first built collider — SphereCast down on layer 9, then
        // GetComponent<Rail>() on the collider's own GameObject.
        private static System.Collections.IEnumerator RailProbe(GameObject railGO, List<Rail.Segment> segments)
        {
            yield return new WaitForFixedUpdate(); // let physics register the collider
            Vector3 localMid = (segments[0].position + segments[1].position) * 0.5f;
            Vector3 world = railGO.transform.TransformPoint(localMid);
            RaycastHit hit;
            if (!Physics.SphereCast(world + Vector3.up * 1f, 0.2f, Vector3.down, out hit, 5f, 1 << 9))
            {
                Log.LogWarning($"[Rails] probe MISS: no layer-9 hit above '{railGO.name}' at {world}");
                yield break;
            }
            var rail = hit.collider.gameObject.GetComponent<Rail>();
            var segs = rail == null ? null
                : Traverse.Create(rail).Field("segments").GetValue() as List<Rail.Segment>;
            Log.LogInfo($"[Rails] probe hit '{hit.collider.gameObject.name}' (layer {hit.collider.gameObject.layer}): Rail={(rail != null)}, segments={(segs == null ? 0 : segs.Count)}");
        }

        // Rebuilds AI paths from "<map>.aipaths.txt" next to the .asset
        // (generated by gen_aipaths.py from the bundle's own data). Plain text,
        // NOT JsonUtility — that is the same serializer that loses the nested
        // class in the first place. Format: '#' comments; a line "path" starts a
        // new path; point lines are "x z windup" (two invariant-culture floats,
        // world XZ, then 0/1). Returns the number of paths built.
        private static int BuildAIPathsFromSidecar(Transform parent)
        {
            if (string.IsNullOrEmpty(LoadedAssetBundle)) return 0;
            string sidecarPath = Path.ChangeExtension(LoadedAssetBundle, ".aipaths.txt");
            if (!File.Exists(sidecarPath)) return 0;
            try
            {
                var paths = new List<List<AIPath.Point>>();
                List<AIPath.Point> cur = null;
                int malformed = 0;
                foreach (var raw in File.ReadAllLines(sidecarPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    // Header lines. Each is read on its own at load time; here they
                    // are simply not points. ⚠️ This list must cover EVERY header
                    // gen_aipaths.py can emit. A 2-token header that slips through
                    // (gravity, spawnsnap) only inflates the malformed counter, but
                    // "group SSX (2012)" is 3 tokens and would reach float.Parse.
                    if (IsHeaderLine(line)) continue;
                    if (line == "path")
                    {
                        cur = new List<AIPath.Point>();
                        paths.Add(cur);
                        continue;
                    }
                    var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length < 3) { malformed++; continue; }
                    if (cur == null)
                    {
                        cur = new List<AIPath.Point>();
                        paths.Add(cur);
                    }
                    var p = new AIPath.Point();
                    p.position = new Vector2(
                        float.Parse(t[0], CultureInfo.InvariantCulture),
                        float.Parse(t[1], CultureInfo.InvariantCulture));
                    p.windup = t[2] == "1";
                    cur.Add(p);
                }

                int built = 0, points = 0, tooShort = 0;
                for (int i = 0; i < paths.Count; i++)
                {
                    if (paths[i].Count < 2) { tooShort++; continue; } // a path needs a direction
                    var go = new GameObject("AIPath (sidecar " + built.ToString(CultureInfo.InvariantCulture) + ")");
                    go.transform.parent = parent;
                    var path = go.AddComponent<AIPath>();
                    path.m_points = paths[i];
                    path.CalculateSegments();
                    built++;
                    points += paths[i].Count;
                }
                Log.LogInfo($"[AIPaths] built {built} path(s) ({points} points) from sidecar {Path.GetFileName(sidecarPath)}");
                // Both counters used to be invisible: the summary above reported only the
                // survivors, so a truncated sidecar looked exactly like a short one. A
                // path dropped here is a path the AI cannot run.
                if (malformed > 0)
                    Log.LogWarning($"[AIPaths] sidecar {Path.GetFileName(sidecarPath)}: {malformed} point line(s) "
                        + "had fewer than 3 tokens and were dropped (expected 'x z windup').");
                if (tooShort > 0)
                    Log.LogWarning($"[AIPaths] sidecar {Path.GetFileName(sidecarPath)}: {tooShort} path(s) "
                        + "had fewer than 2 points and were discarded entirely.");
                return built;
            }
            catch (Exception e)
            {
                Log.LogError($"[AIPaths] sidecar {sidecarPath} failed to load: {e}");
                return 0;
            }
        }

        // Last-ditch path source: the race line's List<Vector3> DOES deserialize,
        // so one AIPath along it (world XZ, no windups) keeps AI racing instead of
        // respawn-looping.
        private static int BuildAIPathFromRaceLine(Transform parent)
        {
            var raceLine = FindAnyObjectByType<MakerRaceLine>();
            if (raceLine == null || raceLine.m_points == null || raceLine.m_points.Count < 2)
                return 0;
            var go = new GameObject("AIPath (race-line fallback)");
            go.transform.parent = parent;
            var path = go.AddComponent<AIPath>();
            path.m_points = new List<AIPath.Point>();
            for (int i = 0; i < raceLine.m_points.Count; i++)
            {
                var p = new AIPath.Point();
                Vector3 v = raceLine.m_points[i];
                p.position = new Vector2(v.x, v.z);
                p.windup = false;
                path.m_points.Add(p);
            }
            path.CalculateSegments();
            Log.LogInfo($"[AIPaths] built 1 complete path from the race line ({path.m_points.Count} points)");
            return 1;
        }

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
        { "scale", "gravity", "platform", "source", "spawnsnap", "mapversion", "group", "order" };

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

        // Gravity for a world WorldScale times bigger. Called (via the transpilers
        // below) everywhere Snowboarder/Caster.Cast used their inlined 20f/17f
        // gravity constants. Returns the input untouched when the map is unscaled
        // or the feature is off — with WorldScale hard-reset to 1 on every
        // built-in selection and menu return, built-in gravity is provably stock.
        public static float ScaledGravity(float baseGravity)
        {
            float g = baseGravity;
            // Legacy opt-in: heavier gravity proportional to a scaled-UP world,
            // to shrink jump arcs back toward original proportions. Off by
            // default; inert at scale 1.
            if (WorldScale != 1f && scaleGravityWithMap != null && scaleGravityWithMap.Value)
                g *= WorldScale;
            // Per-map gravity multiplier from the sidecar (feature 8). Independent
            // of world scale and always applied — so a 1x port can still ask for
            // heavier gravity. 1.0 (the default, and reset on every built-in
            // selection / menu return) leaves gravity untouched, keeping built-in
            // levels provably stock.
            g *= MapGravityMultiplier;
            return g;
        }

        // Routes matching ldc.r4 literals through ScaledGravity. The gravity
        // constants are C# consts (Snowboarder.Gravity=20 / SoftGravity=17), so
        // they're inlined at every call site and can only be caught as literals.
        // Each caller passes ONLY the values that are gravity in that method —
        // e.g. UpdateGrinding also has a 20f windup multiplier, so it patches
        // just its 17f. Known non-bug at large scales: AI cornering gets sloppier
        // (absolute-meter look-ahead caps in AISnowboarder) — expected, not a
        // gravity problem.
        internal static IEnumerable<CodeInstruction> ScaleGravityLiterals(
            IEnumerable<CodeInstruction> instructions, string label, float[] values)
        {
            var scaled = AccessTools.Method(typeof(Plugin), nameof(ScaledGravity));
            int patched = 0;
            foreach (var ins in instructions)
            {
                yield return ins;
                if (ins.opcode == OpCodes.Ldc_R4 && ins.operand is float f
                    && Array.IndexOf(values, f) >= 0)
                {
                    yield return new CodeInstruction(OpCodes.Call, scaled);
                    patched++;
                }
            }
            if (patched == 0)
                Log.LogWarning($"[Gravity] {label}: no gravity literal found to patch — game update changed the method?");
            else
                Log.LogDebug($"[Gravity] {label}: routed {patched} gravity literal(s) through ScaledGravity");
        }

        // The corrected multiply for Rail.GetClosestPosition's closest-point
        // formula: the game scales the raw segment vector by a distance in
        // metres, where only the unit direction may be scaled. See
        // Rail_GetClosestPosition_Projection for the full story.
        internal static Vector3 RailProject(Vector3 segmentDelta, float distanceAlong)
        {
            return segmentDelta.normalized * distanceAlong;
        }
    }


    [HarmonyPatch(typeof(MenuManager), "LoadScene", new System.Type[] { typeof(LevelEntry) })]
    public class MenuManager_SelectLevel
    {
        // Returning false skips MenuManager.LoadScene entirely — the clean no-op
        // abort for any failure: the menu simply stays up instead of loading a
        // black screen. Every abort path logs first. State (myLoadedAssetBundle,
        // LoadedAssetBundle, LoadedTrack) is only committed AFTER a bundle has been
        // confirmed loaded with at least one scene, so a failed load can never
        // leave a null handle for a later selection to NRE on.
        [HarmonyPrefix]
        public static bool Prefix(ref LevelEntry levelEntry)
        {
            if (levelEntry.sceneName == null || !levelEntry.sceneName.Contains("$"))
            {
                // A custom load may still be in flight; unloading its bundle below
                // would rug-pull the pending LoadSceneAsync exactly like switching
                // custom maps would. Same guard semantics as the custom branch.
                if (Plugin.pendingSceneName != null
                    && Time.realtimeSinceStartup - Plugin.pendingSince < 30f)
                {
                    Plugin.Log.LogWarning($"LoadScene prefix: load of '{Plugin.pendingSceneName}' in progress; ignoring builtin selection");
                    return false;
                }

                // Built-in level: no custom bundle should stay loaded. Guard the
                // unload (there may be none) and reset ALL custom state, including
                // any in-flight guard, so the shader-remap/skybox gating (which
                // treats LoadedAssetBundle == "" as "no custom bundle") is correct.
                Plugin.LoadedAssetBundle = "";
                Plugin.LoadedTrack = null;
                if (Plugin.myLoadedAssetBundle != null)
                {
                    Plugin.myLoadedAssetBundle.Unload(true);
                    Plugin.myLoadedAssetBundle = null;
                }
                Plugin.pendingSceneName = null;
                Plugin.WorldScale = 1f;
                Plugin.MapGravityMultiplier = 1f;
                Plugin.MapPhysicsHz = 0f;
                Plugin.ApplyPhysicsRate("built-in level selected");
                Plugin.BundleNativePlatform = null;
                Plugin.BundleSource = Plugin.DefaultSource;
                Plugin.BundleMapVersion = "";
                return true;
            }

            try
            {
                string path = levelEntry.sceneName.TrimStart('$');
                Plugin.Log.LogInfo($"LoadScene prefix: loading custom map bundle '{levelEntry.name}' from {path}");

                // 1. A previous custom load may still be in flight. Unloading its
                // bundle now (to load another) would pull the rug from a pending
                // LoadSceneAsync. Reject the new selection until it settles.
                if (Plugin.pendingSceneName != null)
                {
                    if (Time.realtimeSinceStartup - Plugin.pendingSince < 30f)
                    {
                        Plugin.Log.LogWarning($"LoadScene prefix: load of '{Plugin.pendingSceneName}' already in progress; ignoring selection");
                        return false;
                    }
                    Plugin.Log.LogError($"LoadScene prefix: previous load of '{Plugin.pendingSceneName}' never completed within 30s; clearing and continuing");
                    Plugin.pendingSceneName = null;
                }

                AssetBundle bundle;
                if (Plugin.LoadedAssetBundle == path && Plugin.myLoadedAssetBundle != null)
                {
                    // 2. Same map re-selected and its bundle is still loaded: reuse it.
                    bundle = Plugin.myLoadedAssetBundle;
                    Plugin.Log.LogInfo($"LoadScene prefix: reusing already-loaded bundle {path}");
                }
                else
                {
                    // 3. Switching maps. The previously loaded bundle is idle here
                    // (no load pending — step 1 guarantees it — and level scenes are
                    // unloaded while in the menu), so unloading it is safe.
                    if (Plugin.myLoadedAssetBundle != null)
                    {
                        Plugin.myLoadedAssetBundle.Unload(true);
                        Plugin.myLoadedAssetBundle = null;
                        Plugin.LoadedAssetBundle = "";
                        Plugin.LoadedTrack = null;
                        Plugin.Log.LogInfo("LoadScene prefix: unloaded previous custom bundle");
                    }

                    // 4. Snapshot the game's shaders exactly once per session,
                    // BEFORE any bundle is ever loaded so nothing bundle-origin can
                    // pollute it. (Once captured it stays valid — the game's shaders
                    // don't change — so no re-snapshot on later switches.)
                    if (Plugin.GameShaders == null)
                        Plugin.SnapshotGameShaders();

                    // 5. Load the bundle. Null means the file is missing/corrupt or
                    // already loaded elsewhere — abort with state still cleared.
                    bundle = AssetBundle.LoadFromFile(path);
                    if (bundle == null)
                    {
                        Plugin.Log.LogError($"LoadScene prefix: bundle load failed (file missing/corrupt or already loaded): {path}");
                        return false;
                    }

                    // 6. A bundle with no scene is unusable — unload and abort.
                    string[] scenePaths = bundle.GetAllScenePaths();
                    if (scenePaths == null || scenePaths.Length == 0)
                    {
                        Plugin.Log.LogError($"LoadScene prefix: bundle has no scene paths, unusable: {path}");
                        bundle.Unload(true);
                        return false;
                    }

                    // 7. Commit state only now that the load is known good.
                    Plugin.myLoadedAssetBundle = bundle;
                    Plugin.LoadedAssetBundle = path;
                    Plugin.LoadedTrack = scenePaths[0];
                }

                // Per-map world scale from the sidecar header (1.0 when absent).
                // Read on reuse too — the sidecar may have changed on disk.
                Plugin.WorldScale = Plugin.ReadWorldScale(path);
                if (Plugin.WorldScale != 1f)
                    Plugin.Log.LogInfo($"[Gravity] map declares world scale {Plugin.WorldScale.ToString(CultureInfo.InvariantCulture)}; gravity scaling {(Plugin.scaleGravityWithMap.Value ? "enabled" : "disabled by config")}");

                // Per-map gravity multiplier from the same sidecar (1.0 when absent).
                // Read on reuse too — the sidecar may have changed on disk.
                Plugin.MapGravityMultiplier = Plugin.ReadGravityMultiplier(path);
                if (Plugin.MapGravityMultiplier != 1f)
                    Plugin.Log.LogInfo($"[Gravity] map declares gravity multiplier {Plugin.MapGravityMultiplier.ToString(CultureInfo.InvariantCulture)}x (applied on top of any world-scale gravity)");

                // Per-map physics tick rate from the same sidecar (0 = leave the
                // game's rate alone). Applied here rather than at sceneLoaded so
                // the rate is already right for the level's first FixedUpdate.
                Plugin.MapPhysicsHz = Plugin.ReadPhysicsHz(path);
                if (Plugin.MapPhysicsHz > 0f && !Plugin.mapPhysicsRate.Value)
                    Plugin.Log.LogInfo($"[Physics] map declares physicshz {Plugin.MapPhysicsHz.ToString(CultureInfo.InvariantCulture)} but MapPhysicsRate is off in config; keeping the game's rate");
                Plugin.ApplyPhysicsRate($"map '{levelEntry.name}'");

                // Native-platform declaration from the same sidecar (null when absent).
                // Read on reuse too — the sidecar may have changed on disk.
                Plugin.BundleNativePlatform = Plugin.ReadBundleNativePlatform(path);
                if (Plugin.BundleNativePlatform != null)
                    Plugin.Log.LogInfo($"[ShaderRemap] map declares native platform '{Plugin.BundleNativePlatform}'; its own shaders are trusted (not remapped) when running on that backend");

                // Provenance declaration from the same sidecar (defaults to
                // "tricky_madness" when absent). Read on reuse too — the sidecar
                // may have changed on disk. Informational: nothing keys off it.
                Plugin.BundleSource = Plugin.ReadBundleSource(path);
                Plugin.Log.LogInfo($"[Source] map '{levelEntry.name}' provenance: {Plugin.BundleSource}");
                Plugin.BundleMapVersion = Plugin.ReadBundleMapVersion(path);
                Plugin.Log.LogInfo($"[Source] map '{levelEntry.name}' version: "
                    + (Plugin.BundleMapVersion.Length > 0 ? Plugin.BundleMapVersion : "(unversioned)"));

                // Spawn/respawn ground-snap mode from the same sidecar (Both when
                // absent). Read on reuse too — the sidecar may have changed on disk.
                Plugin.SpawnSnapMode = Plugin.ReadSpawnSnap(path);
                if (Plugin.SpawnSnapMode != Plugin.SpawnSnap.Both)
                    Plugin.Log.LogInfo($"[Spawn] map declares spawnsnap '{Plugin.SpawnSnapMode.ToString().ToLowerInvariant()}'");

                // Derive the bundle-internal scene name from LoadedTrack. NOT Path.*:
                // on macOS '\' is an ordinary filename character, not a separator.
                var parts = Plugin.LoadedTrack.Split('\\', '/');
                string sceneName = parts[parts.Length - 1].Split('.')[0];
                levelEntry.sceneName = sceneName;
                Plugin.pendingSceneName = sceneName;
                Plugin.pendingSince = Time.realtimeSinceStartup;

                if (Plugin.Instance != null)
                    Plugin.Instance.StartCoroutine(Plugin.LoadWatchdog(sceneName));

                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"LoadScene prefix: exception loading custom map: {e}");
                return false;
            }
        }
    }

    // The game's OOB contact handler, and the reason this map's frame rate collapses
    // at one spot. Snowboarder.OnControllerColliderHit reads (IL-verified):
    //
    //     if (state == Dead) return;
    //     if (hit.collider.gameObject.layer == LayerMask.NameToLayer("OOB")) {
    //         Respawn(); OnOOB?.Invoke(); return;
    //     }
    //
    // Respawn() sets state = Riding, so that early-out can never engage on the rider
    // it just moved — and Respawn picks the NEAREST respawn marker, which for a rider
    // standing inside an OOB volume is the marker that puts them back inside it. There
    // is no cooldown, so the whole cycle re-runs on the next contact, i.e. every
    // rendered frame. Measured: 2817 respawns in 114 s for a single rider.
    //
    // SSX3 reset zones are what they hit: SSXImporter.EmitResetZone emits every
    // Mdl_Resetzone* instance as a SOLID (isTrigger = false) layer-10 BoxCollider,
    // 1234 of them on the chain.
    //
    // This prefix records only — it names the collider so the misplaced volumes can be
    // fixed in the map data, and flags the respawn about to happen as OOB-triggered so
    // the loop guard below can tell it apart from a crash.
    [HarmonyPatch(typeof(Snowboarder), "OnControllerColliderHit")]
    public class Snowboarder_OnControllerColliderHit
    {
        internal const int OobLayer = 10;

        // Set immediately before the game's own Respawn() call on the OOB branch and
        // consumed by Snowboarder_Respawn's prefix on the same call stack, so no
        // frame-boundary staleness is possible.
        internal static string PendingOobCollider;

        [HarmonyPrefix]
        public static void Prefix(ControllerColliderHit hit)
        {
            if (hit == null) return;
            var col = hit.collider;
            if (col == null || col.gameObject.layer != OobLayer) return;
            PendingOobCollider = col.gameObject.name;
            PerfHarness.NoteOobHit(PendingOobCollider, col.transform.position);
        }
    }

    // On custom maps AISnowboarder.GetSteeringInput calls Snowboarder.Respawn()
    // directly after 1.5s of pathlessness, bypassing CanRespawn() — during the
    // countdown that launches AI off the start line. Block Respawn while the
    // custom scene is in its Starting state; built-in levels
    // (LoadedAssetBundle == "") are untouched, and once the countdown releases
    // (state != Starting) everything behaves as stock. Deliberately mode-blind.
    [HarmonyPatch(typeof(Snowboarder), "Respawn")]
    public class Snowboarder_Respawn
    {
        private static string loggedScene;

        [HarmonyPrefix]
        public static bool Prefix(Snowboarder __instance)
        {
            // Snapshot BEFORE the game moves them — Respawn resets state to Riding and
            // deathTimer to 0, so a postfix can no longer tell why it was called.
            if (__instance != null)
            {
                object st = Traverse.Create(__instance).Field("state").GetValue();
                PerfHarness.NotePreRespawn(__instance.transform.position,
                    st != null && Convert.ToInt32(st) == 4);   // 4 = State.Dead
            }
            if (string.IsNullOrEmpty(Plugin.LoadedAssetBundle)) return true;
            string custom = Plugin.CustomSceneName();
            if (custom == null || SceneManager.GetActiveScene().name != custom) return true;
            object state = Traverse.Create(__instance).Field("state").GetValue();
            if (state == null || Convert.ToInt32(state) != 0) return true; // 0 = State.Starting
            if (loggedScene != custom)
            {
                loggedScene = custom;
                Plugin.Log.LogDebug($"[AIPaths] blocked pre-start Respawn on custom scene '{custom}' (state=Starting)");
            }
            return false;
        }

        // Ground truth for the perf harness's respawn report: the game telling us it
        // respawned somebody, rather than the harness inferring it from a position
        // jump big enough to clear a guessed threshold. Diagnostic only — it reads
        // position and records it, and changes nothing.
        [HarmonyPostfix]
        public static void Postfix(Snowboarder __instance, bool __runOriginal)
        {
            // Harmony runs postfixes even when a prefix skipped the original, so a
            // respawn WE blocked above must not be recorded as one that happened.
            if (!__runOriginal || __instance == null) return;
            bool wasOob = Snowboarder_OnControllerColliderHit.PendingOobCollider != null;
            Snowboarder_OnControllerColliderHit.PendingOobCollider = null;
            PerfHarness.NoteRespawn(__instance.transform, __instance.transform.position);
            if (wasOob) LoopGuard(__instance);
        }

        // ---- OOB respawn loop guard ------------------------------------------
        // Escalate a rider FORWARD along the race line when respawning has stopped
        // making progress. The game can't break the loop itself because it always
        // picks the nearest marker, and the nearest marker is what put them there.
        class Loop { public float lastT; public int hits; }
        static readonly Dictionary<int, Loop> _loops = new Dictionary<int, Loop>();
        static string _guardLoggedScene;

        // Consecutive OOB respawns within this window count as one loop. The real
        // loop fires every frame, so anything above a couple of frames is generous;
        // a genuine crash-respawn can't repeat this fast because CanRespawn() needs
        // deathTimer > 1.5.
        const float LoopWindow = 1.0f;
        const int LoopTrigger = 3;

        public static void ResetLoopState() { _loops.Clear(); _guardLoggedScene = null; }

        static void LoopGuard(Snowboarder sb)
        {
            if (!Plugin.respawnLoopGuard.Value) return;
            // Custom maps only, and only when BuildScene actually produced race-line
            // markers — with no ordered list there is no "forward" to escalate to, so
            // the guard fails to stock rather than guessing at a destination.
            if (string.IsNullOrEmpty(Plugin.LoadedAssetBundle)) return;
            var marks = Plugin.RaceLineRespawns;
            if (marks.Count < 2) return;

            bool isAI = Traverse.Create(sb).Field("snowboardController").GetValue() is AISnowboarder;
            if (!isAI && !Plugin.respawnLoopGuardPlayer.Value) return;

            int id = sb.GetInstanceID();
            Loop st;
            if (!_loops.TryGetValue(id, out st)) { st = new Loop(); _loops[id] = st; }

            float now = Time.unscaledTime;
            st.hits = (now - st.lastT <= LoopWindow) ? st.hits + 1 : 1;
            st.lastT = now;
            if (st.hits < LoopTrigger) return;
            st.hits = 0;

            // Nearest marker to where the game just dropped them, then step forward.
            // Escalating from the CURRENT nearest (rather than a remembered index)
            // is what makes repeated firings compound: each one starts from wherever
            // the last one landed.
            Vector3 p = sb.transform.position;
            int near = -1;
            float best = float.MaxValue;
            for (int i = 0; i < marks.Count; i++)
            {
                if (marks[i] == null) continue;
                float d = (marks[i].position - p).sqrMagnitude;
                if (d < best) { best = d; near = i; }
            }
            if (near < 0) return;

            int adv = Mathf.Max(1, Plugin.respawnLoopAdvance.Value);
            int dest = Mathf.Min(near + adv, marks.Count - 1);
            // Already at the last marker: the finish is right there and shunting them
            // further is meaningless. Leave them to the game.
            if (dest == near) return;
            var target = marks[dest];
            if (target == null) return;

            // A CharacterController caches its own position and will drag the rider
            // back to it, so a bare transform write does not stick — toggle it around
            // the move.
            var cc = sb.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            sb.transform.position = target.position;
            sb.transform.rotation = target.rotation;
            if (cc != null) cc.enabled = true;

            string scene = Plugin.CustomSceneName();
            if (_guardLoggedScene != scene)
            {
                _guardLoggedScene = scene;
                Plugin.Log.LogInfo($"[Respawn] loop guard active on '{scene}' — "
                    + $"escalating wedged riders {adv} marker(s) down-course "
                    + $"(first: {sb.name} at {p} -> marker {dest} of {marks.Count})");
            }
            Plugin.Log.LogDebug($"[Respawn] loop guard moved {sb.name} "
                + $"marker {near} -> {dest} (OOB respawn x{LoopTrigger})");
        }
    }

    // Gravity scaling for scaled custom maps. The gravity consts are inlined at
    // their use sites (see Plugin.ScaleGravityLiterals); the verified gravity
    // sites are exactly these three methods — Snowboarder.Integrate (g=20, soft
    // g=17 while rising), Snowboarder.UpdateGrinding (17 pulling the rider onto
    // the rail; its 20f is a windup multiplier and is NOT patched), and
    // Caster.Cast (the 20/17 trajectory prediction, which must agree with
    // Integrate or predicted landings drift). LaunchPad code also inlines 20f
    // but is deliberately untouched (out of scope, native tag mechanic).
    [HarmonyPatch(typeof(Snowboarder), "Integrate")]
    public class Snowboarder_Integrate_Gravity
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return Plugin.ScaleGravityLiterals(instructions, "Snowboarder.Integrate", new[] { 20f, 17f });
        }
    }

    [HarmonyPatch(typeof(Snowboarder), "UpdateGrinding")]
    public class Snowboarder_UpdateGrinding_Gravity
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return Plugin.ScaleGravityLiterals(instructions, "Snowboarder.UpdateGrinding", new[] { 17f });
        }
    }

    [HarmonyPatch(typeof(Caster), "Cast")]
    public class Caster_Cast_Gravity
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return Plugin.ScaleGravityLiterals(instructions, "Caster.Cast", new[] { 20f, 17f });
        }
    }

    // Rail.GetClosestPosition — fix the closest-point projection.
    //
    // IL-verified against Assembly-CSharp. Per segment i the game computes
    //     d   = seg[i+1].position - seg[i].position      (NOT normalized)
    //     dir = d.normalized
    //     len = d.magnitude
    //     t   = clamp(Dot(rider - seg[i].position, dir), 0, len)   // metres
    // and then takes the closest point on that segment as
    //     seg[i].position + d * t                        // <-- wrong term
    // where it must be dir * t. The two agree only when |d| == 1 m. Nothing
    // resamples rails to unit length — not the game's own authoring tool
    // (MakerRails.GenerateSegments emits one Segment per PointList child, no
    // resampling) and not our importer — and measured spacing is 1.8–7.0 m
    // median across every course we ship, so this is wrong on every rail in
    // the game, built-in levels included. The rotation and distance lerps
    // further down the same method both use the correct normalized parameter
    // t/len; the position line is the lone outlier, which is what makes this
    // read as a typo rather than as intent.
    //
    // Consequence: each segment's candidate point overshoots by a factor of
    // |d|, so a segment the rider is nowhere near can win the nearest-segment
    // contest. Measured on spline_CRA3_railslides_secret_1002 (13 points,
    // 4.9–5.9 m spans, 65.1 m long), walking the rider exactly along the true
    // polyline: the shipped formula snaps up to 3.55 m off the rail line, and
    // 5% past the tail it returns end = 0 where the corrected one returns
    // end = 1. `end` and `distance` are only committed for the winning
    // segment, so picking the wrong segment corrupts both. Snowboarder gates
    // re-attachment on `if (end == railDirection) return;` — with end stuck at
    // 0 the game never learns the rider ran off the end and re-attaches them
    // behind where they were. That is the reported rail-end snap-back loop;
    // jumping escapes it because jumping is a separate exit path, which is why
    // CPU riders (who don't jump out) stay stuck.
    //
    // Surgical fix: the method contains exactly ONE Vector3 * float multiply
    // and it is that term, so reroute that one call through Plugin.RailProject
    // (which normalizes first). No local-variable rewriting, nothing else
    // touched. Bails out unchanged and loudly if the match count isn't exactly
    // 1, so a future game update degrades to stock behaviour rather than to
    // corrupted rails.
    [HarmonyPatch(typeof(Rail), "GetClosestPosition")]
    public class Rail_GetClosestPosition_Projection
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            if (!Plugin.fixRailProjection.Value)
            {
                Plugin.Log.LogInfo("[Rail] FixRailProjection off — leaving Rail.GetClosestPosition stock");
                return code;
            }

            var mul = AccessTools.Method(typeof(Vector3), "op_Multiply",
                new[] { typeof(Vector3), typeof(float) });
            var fix = AccessTools.Method(typeof(Plugin), nameof(Plugin.RailProject));

            var hits = new List<int>();
            for (int i = 0; i < code.Count; i++)
                if (code[i].opcode == OpCodes.Call && Equals(code[i].operand, mul))
                    hits.Add(i);

            if (hits.Count != 1)
            {
                Plugin.Log.LogWarning($"[Rail] GetClosestPosition: expected exactly 1 Vector3*float multiply, found {hits.Count} — game updated? Leaving the method stock.");
                return code;
            }

            code[hits[0]].operand = fix;
            Plugin.Log.LogInfo("[Rail] GetClosestPosition: closest-point projection corrected (scale the unit direction, not the raw segment vector)");
            return code;
        }
    }

    // SnowTrail (the board's ground-snapped ribbon decal) ground-snaps via a
    // raycast that only hits Unity layer "Default" (IL-verified: SnowTrail.Start
    // sets its static layerMask to just 1 << LayerMask.NameToLayer("Default")).
    // Our rideable ground can legitimately be Default(0)/Ice(6)/Rock(7)/Metal(14)
    // (see Snowboarder's own layer semantics), so on any SSX/SSX3/2012 terrain
    // typed onto Ice/Rock/Metal the raycast misses every frame and the trail
    // never renders. Widen the mask once, right after Start() sets it.
    [HarmonyPatch(typeof(SnowTrail), "Start")]
    public class SnowTrail_Start_LayerMask
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            int rideableMask = (1 << 0) | (1 << 6) | (1 << 7) | (1 << 14);
            Traverse.Create(typeof(SnowTrail)).Field("layerMask").SetValue(rideableMask);
        }
    }

    // Custom thumbnails. The game sets each level's menu thumbnail via
    // Resources.Load<Sprite>(thumbnailPath), which can't read a PNG off disk and
    // runs before the map bundle is loaded. So instead, after the menu is built,
    // we override the thumbnail Image for any custom ("$"-marked) level with a PNG
    // dropped next to its .asset (e.g. Maps/Garibaldi Beta V2.png).
    [HarmonyPatch(typeof(LevelSelectMenu), "Populate")]
    public class LevelSelectMenu_Populate
    {
        // Cache so we don't re-read the file / leak textures every time the menu opens.
        private static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();

        [HarmonyPostfix]
        public static void Postfix(LevelSelectMenu __instance)
        {
            var entries = Traverse.Create(__instance).Field("levelSelectEntries")
                .GetValue() as List<LevelSelectEntry>;
            if (entries == null) return;

            int count = LevelEntries.GetLevelCount();
            for (int i = 0; i < entries.Count && i < count; i++)
            {
                var entry = LevelEntries.GetEntry(i);
                if (string.IsNullOrEmpty(entry.sceneName) || !entry.sceneName.StartsWith("$"))
                    continue; // built-in level, leave its Resources thumbnail alone

                string assetPath = entry.sceneName.TrimStart('$');
                string pngPath = Path.ChangeExtension(assetPath, ".png");

                var sprite = LoadSprite(pngPath);
                if (sprite == null) continue;

                var img = entries[i].thumbnail;
                if (img == null) continue;
                img.sprite = sprite;
                img.preserveAspect = true; // never distort whatever aspect the user provides
            }

            // Attach the scroller once (it no-ops itself when all cards fit). Put it
            // on the Scroll View GameObject — never on Content (whose children the
            // menu indexes by position) or the Scroll View's RectTransform (it has
            // an Animation component we must not disturb).
            if (Plugin.levelSelectScroll != null && !Plugin.levelSelectScroll.Value) return;
            var scrollRect = __instance.GetComponentInChildren<ScrollRect>(true);
            if (scrollRect == null)
            {
                Plugin.Log.LogWarning("[LevelSelect] no ScrollRect found under LevelSelectMenu; scrolling disabled");
                return;
            }
            bool grid = Plugin.levelSelectGrid == null || Plugin.levelSelectGrid.Value;
            if (grid)
            {
                if (scrollRect.gameObject.GetComponent<LevelSelectGrid>() == null)
                {
                    var g = scrollRect.gameObject.AddComponent<LevelSelectGrid>();
                    g.Init(scrollRect);
                    Plugin.Log.LogInfo("[LevelSelect] attached vertical grid");
                }
            }
            else if (scrollRect.gameObject.GetComponent<LevelSelectScroller>() == null)
            {
                var scroller = scrollRect.gameObject.AddComponent<LevelSelectScroller>();
                scroller.Init(scrollRect);
                Plugin.Log.LogInfo("[LevelSelect] attached card-strip scroller");
            }
        }

        // Which section heading a card belongs under. Card i is
        // LevelEntries.GetEntry(i) — the game's own levels first (they carry no "$"
        // scene marker, so they are BuiltInGroup by construction), then our custom
        // maps, whose group AddTracks already read from their sidecar. Keyed off
        // the entry rather than the sibling index so a future change to how many
        // built-ins ship can't skew it.
        internal static string GroupOfCard(int cardIndex)
        {
            if (cardIndex < 0 || cardIndex >= LevelEntries.GetLevelCount()) return Plugin.DefaultGroup;
            var entry = LevelEntries.GetEntry(cardIndex);
            if (string.IsNullOrEmpty(entry.sceneName) || !entry.sceneName.StartsWith("$"))
                return Plugin.BuiltInGroup;
            string group;
            return Plugin.TrackGroup.TryGetValue(entry.sceneName.TrimStart('$'), out group)
                ? group : Plugin.DefaultGroup;
        }

        private static Sprite LoadSprite(string pngPath)
        {
            if (cache.TryGetValue(pngPath, out var cached)) return cached;
            cache[pngPath] = null; // remember misses too, so we don't retry disk every menu open
            // Both misses below are logged. A map with no thumbnail is a legitimate,
            // shippable state, so this is a warning and not an error — but it used to be
            // wholly silent, and the only way to discover it was to look at the menu and
            // notice a blank card. The cache above means each path logs at most once.
            // ⚠️ Plugin.Log, NOT Debug.LogWarning: UnityEngine.Debug writes to Unity's
            // Player.log, which is not the log anyone here reads. A first cut used Debug
            // and the warnings fired correctly into a file nobody opens.
            if (!File.Exists(pngPath))
            {
                Plugin.Log.LogWarning("[Thumbnails] no menu thumbnail at " + pngPath
                    + " — this map's card will be blank. Expected a 1024x576 PNG named after the .asset.");
                return null;
            }
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(File.ReadAllBytes(pngPath)))
                {
                    Plugin.Log.LogWarning("[Thumbnails] menu thumbnail at " + pngPath
                        + " exists but could not be decoded as an image; this map's card will be blank.");
                    return null;
                }
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                cache[pngPath] = sprite;
                return sprite;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Thumbnails] thumbnail load failed for " + pngPath + ": " + e.Message);
                return null;
            }
        }
    }

    // Makes the level-select card strip scroll when more cards exist than fit,
    // auto-scrolling to the selected card. Lives on the "Scroll View" GameObject.
    //
    // Vanilla layout keeps Content stretch-filled to the viewport with the
    // ContentSizeFitter horizontally Unconstrained, so Content never overflows and
    // the ScrollRect has nothing to scroll — the cards are simply centred and any
    // beyond the edges are clipped. When the cards don't fit we switch Content to a
    // preferred-width, left-anchored layout so it overflows and the ScrollRect can
    // move it; when they DO fit we touch nothing, leaving the menu pixel-identical
    // to vanilla.
    public class LevelSelectScroller : MonoBehaviour
    {
        private ScrollRect scrollRect;
        private RectTransform content;
        private RectTransform viewport;
        // Set in OnEnable when all cards fit: LateUpdate then does nothing and no
        // layout is changed, so the vanilla look is preserved exactly.
        private bool disabled;

        // Card sizeDelta springs between these (LevelSelectEntry.FixedUpdate).
        private const float IdleCardWidth = 640f;
        private const float SelectedCardWidth = 768f;
        // Keep-in-view inset from each viewport edge (px).
        private const float EdgeMargin = 32f;

        // Vanilla Content layout, captured the first time overflow layout is
        // applied so a later all-fits OnEnable can restore it instead of leaving
        // the mutated layout stuck under a disabled scroller.
        private bool overflowApplied;
        private ContentSizeFitter.FitMode origFit;
        private bool origHadFitter;
        private Vector2 origAnchorMin;
        private Vector2 origAnchorMax;
        private float origPivotX;
        private float origAnchoredX;

        public void Init(ScrollRect sr)
        {
            scrollRect = sr;
        }

        private void OnEnable()
        {
            // AddComponent fires OnEnable before Init runs, but we're on the
            // ScrollRect's own GameObject, so GetComponent always resolves it.
            if (scrollRect == null) scrollRect = GetComponent<ScrollRect>();
            if (scrollRect == null) { disabled = true; return; }

            content = scrollRect.content;
            viewport = scrollRect.viewport != null
                ? scrollRect.viewport
                : scrollRect.transform.GetChild(0) as RectTransform;
            if (content == null || viewport == null) { disabled = true; return; }

            // An unbuilt/zero rect would make the fit test meaningless (everything
            // "overflows" a 0-wide viewport) — fail safe into vanilla for this
            // enable; the next menu open re-evaluates against a real rect.
            if (viewport.rect.width < 1f) { disabled = true; return; }

            // Child 0 is the inactive card template; count only active cards.
            int n = 0;
            for (int i = 0; i < content.childCount; i++)
                if (content.GetChild(i).gameObject.activeSelf) n++;

            // Use the MAX (selected) width for the one card so the fit decision
            // never flickers as widths spring frame to frame.
            float needed = (n - 1) * IdleCardWidth + SelectedCardWidth;
            if (needed <= viewport.rect.width)
            {
                // Everything fits — vanilla layout. If a previous enable applied
                // overflow layout, put the original back rather than leaving the
                // mutated anchors/fitter stuck under a disabled scroller.
                if (overflowApplied) RestoreVanillaLayout();
                disabled = true;
                return;
            }
            disabled = false;

            // Overflow layout: preferred-width content, left-anchored, pinned to the
            // viewport's left edge. Idempotent — safe to re-apply every menu open.
            var fitter = content.GetComponent<ContentSizeFitter>();
            if (!overflowApplied)
            {
                origHadFitter = fitter != null;
                origFit = origHadFitter ? fitter.horizontalFit : ContentSizeFitter.FitMode.Unconstrained;
                origAnchorMin = content.anchorMin;
                origAnchorMax = content.anchorMax;
                origPivotX = content.pivot.x;
                origAnchoredX = content.anchoredPosition.x;
                overflowApplied = true;
            }
            if (fitter != null)
                fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            content.anchorMin = new Vector2(0f, 0f);
            content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, content.pivot.y);
            content.anchoredPosition = new Vector2(0f, content.anchoredPosition.y);
        }

        private void RestoreVanillaLayout()
        {
            var fitter = content.GetComponent<ContentSizeFitter>();
            if (origHadFitter && fitter != null) fitter.horizontalFit = origFit;
            content.anchorMin = origAnchorMin;
            content.anchorMax = origAnchorMax;
            content.pivot = new Vector2(origPivotX, content.pivot.y);
            content.anchoredPosition = new Vector2(origAnchoredX, content.anchoredPosition.y);
            overflowApplied = false;
        }

        private void LateUpdate()
        {
            if (disabled || content == null || viewport == null) return;

            var es = EventSystem.current;
            GameObject sel = es == null ? null : es.currentSelectedGameObject;
            if (sel == null || !sel.transform.IsChildOf(content)) return;

            // Walk up to the card root (the child whose parent is Content).
            Transform card = sel.transform;
            while (card != null && card.parent != content) card = card.parent;
            var cardRt = card as RectTransform;
            if (cardRt == null) return;

            // Card horizontal extent in viewport-local space. Content is a child of
            // the viewport, so shifting content.anchoredPosition.x by d shifts these
            // by d too — the delta to bring an edge in-bounds is a direct target.
            Vector3[] corners = new Vector3[4];
            cardRt.GetWorldCorners(corners);
            float left = float.MaxValue, right = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 lp = viewport.InverseTransformPoint(corners[i]);
                if (lp.x < left) left = lp.x;
                if (lp.x > right) right = lp.x;
            }

            Rect vr = viewport.rect;
            float vLeft = vr.xMin + EdgeMargin;
            float vRight = vr.xMax - EdgeMargin;

            float targetX = content.anchoredPosition.x;
            bool needMove = false;
            if (left < vLeft) { targetX += vLeft - left; needMove = true; }       // card past left edge → move right
            else if (right > vRight) { targetX += vRight - right; needMove = true; } // past right edge → move left
            if (!needMove) return; // card fully visible — don't fight a mouse drag

            // Never scroll past the ends. content.rect.width tracks the springing
            // card widths, so recompute every frame.
            float minX = Mathf.Min(0f, viewport.rect.width - content.rect.width);
            targetX = Mathf.Clamp(targetX, minX, 0f);

            float cur = content.anchoredPosition.x;
            float nx = Mathf.Lerp(cur, targetX, 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime));
            content.anchoredPosition = new Vector2(nx, content.anchoredPosition.y);
        }
    }

    // Level select as a vertical scrolling GRID with a heading per source game,
    // replacing the vanilla one-row horizontal strip. Lives on the "Scroll View"
    // GameObject, same as LevelSelectScroller (which it replaces when enabled).
    //
    // Why: the strip was fine at 4 built-in levels and stayed usable at a handful
    // of mods. At 79 custom maps a 640-wide card in a 3840-wide canvas means 6 on
    // screen and ~14 screens of horizontal scrolling, with nothing telling you
    // which game a course came from. A grid at the same card size shows ~20 at a
    // time in ~4 screens, and the headings make the eras findable.
    //
    // ⚠️ THE constraint (IL-verified in LevelSelectMenu::OnEnable): the menu selects
    // its start card with content.GetChild(Mathf.Clamp(lastSelectedLevel + 1, 1,
    // entryCount)).GetComponentInChildren<Button>().gameObject — no null check. So
    // Content's children 1..N must stay exactly the N cards, in entry order. Era
    // dividers are therefore APPENDED after the last card (index > N), never
    // interleaved; landing the clamp on a divider is a NullReferenceException out
    // of OnEnable that leaves the menu with nothing selected. They are positioned
    // manually, so sibling order and visual order are unrelated.
    //
    // ⚠️ Card sizeDelta belongs to LevelSelectEntry::FixedUpdate, which springs it
    // 640x360 -> 768x432 (and nameLabel.fontSize 40 -> 48) on the selected card
    // every fixed step. We own anchoredPosition ONLY. That is why the cell pitch is
    // the SELECTED size: a cell exactly contains a card at full growth, so the
    // selected card never overlaps its neighbours.
    public class LevelSelectGrid : MonoBehaviour
    {
        private const int Columns = 5;
        // == LevelSelectEntry's selected-card size; see the note above.
        private const float CellW = 768f;
        private const float CellH = 432f;
        private const float GridW = Columns * CellW; // 3840 == the canvas reference width
        // The nav bar (BottomBar / NavOptions) occupies y 0..216 of the 2160-tall
        // reference canvas and the Scroll View is anchored at 0.1 with a bottom
        // pivot, so the grid grows upward from y=216 and 1944 is full height.
        private const float MaxGridH = 1944f;
        private const float HeaderH = 140f;
        private const float HeaderInset = 48f;
        private const float TopPad = 30f;
        private const float SectionGap = 40f;
        private const float BottomPad = 60f;
        private const float EdgeMargin = 24f;

        private ScrollRect scrollRect;
        private RectTransform content, viewport, scrollView;
        private bool built;
        private float contentHeight;

        // Parallel to `cards`: each card's cell top/bottom as a distance DOWN from
        // the content's top edge, which is the same space content.anchoredPosition.y
        // scrolls in. cardTop for a card on a section's first row includes the
        // heading above it, so selecting into a new section scrolls the heading
        // into view with it.
        private readonly List<RectTransform> cards = new List<RectTransform>();
        private readonly List<float> cardTop = new List<float>();
        private readonly List<float> cardBottom = new List<float>();
        private readonly List<GameObject> dividers = new List<GameObject>();

        private TextMeshProUGUI labelTemplate;

        public void Init(ScrollRect sr) { scrollRect = sr; }

        private void OnEnable()
        {
            // AddComponent fires OnEnable before Init runs, but we're on the
            // ScrollRect's own GameObject, so GetComponent always resolves it.
            if (scrollRect == null) scrollRect = GetComponent<ScrollRect>();
            if (!built) Build();
        }

        private void Build()
        {
            if (scrollRect == null) return;
            content = scrollRect.content;
            viewport = scrollRect.viewport != null
                ? scrollRect.viewport
                : scrollRect.transform.GetChild(0) as RectTransform;
            scrollView = transform as RectTransform;
            if (content == null || viewport == null || scrollView == null)
            {
                Plugin.Log.LogWarning("[LevelSelect] grid: ScrollRect has no content/viewport; leaving the strip alone");
                return;
            }

            // Drop any dividers from a previous build before recounting cards.
            for (int i = 0; i < dividers.Count; i++)
                if (dividers[i] != null) Destroy(dividers[i]);
            dividers.Clear();

            // Content child 0 is the inactive card template; everything active with
            // a Selectable on it is a card. (The Selectable test also means a
            // divider surviving from an earlier build can never be counted as one.)
            cards.Clear(); cardTop.Clear(); cardBottom.Clear();
            for (int i = 0; i < content.childCount; i++)
            {
                var ch = content.GetChild(i) as RectTransform;
                if (ch == null || !ch.gameObject.activeSelf) continue;
                if (ch.GetComponent<Selectable>() == null) continue;
                cards.Add(ch);
            }
            if (cards.Count == 0)
            {
                Plugin.Log.LogWarning("[LevelSelect] grid: no cards found under Content; leaving the strip alone");
                return;
            }

            // Split into contiguous era runs BEFORE touching anything, so the
            // nothing-to-group case below can still bail with the menu untouched.
            var sectionGroup = new List<string>();
            var sectionStart = new List<int>();
            for (int i = 0; i < cards.Count; )
            {
                string group = LevelSelectMenu_Populate.GroupOfCard(i);
                sectionGroup.Add(group);
                sectionStart.Add(i);
                while (i < cards.Count && LevelSelectMenu_Populate.GroupOfCard(i) == group) i++;
            }

            // Nothing to group — one era, one row's worth of cards. That is exactly
            // the empty-Maps-folder case: the game's own four levels and nothing
            // else. A grid there is strictly worse than vanilla (four cards pinned
            // to the top-left of a screen-tall empty panel, under a heading naming
            // the only section there is), and headings exist to separate things
            // there is nothing to separate. Leave the menu pixel-identical to
            // vanilla, same as LevelSelectScroller does when everything fits.
            if (sectionGroup.Count <= 1 && cards.Count <= Columns)
            {
                Plugin.Log.LogInfo($"[LevelSelect] grid: only {cards.Count} level(s) in one section — "
                    + "leaving the vanilla strip alone (nothing to group).");
                return;
            }

            labelTemplate = cards[0].GetComponentInChildren<TextMeshProUGUI>(true);
            if (labelTemplate == null)
                Plugin.Log.LogWarning("[LevelSelect] grid: no TMP label on the card template — "
                    + "section headings will draw as rules with no text.");

            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            // The scene's layout components fight manual placement. Neither is read
            // by any game code (verified over the whole Assembly-CSharp dump), and
            // the ContentSizeFitter is Unconstrained on both axes in the scene
            // anyway — it only ever did anything because LevelSelectScroller turned
            // it on. Disable rather than destroy so the strip fallback still finds them.
            var hlg = content.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null) hlg.enabled = false;
            var csf = content.GetComponent<ContentSizeFitter>();
            if (csf != null) csf.enabled = false;

            // Un-stretch Content and pin its top-left to the viewport's, so every
            // card/divider position below is an absolute offset from that corner.
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, 1f);
            content.anchoredPosition = Vector2.zero;

            var rows = new List<List<Selectable>>();
            float y = TopPad;
            for (int s = 0; s < sectionGroup.Count; s++)
            {
                int idx = sectionStart[s];
                int end = s + 1 < sectionStart.Count ? sectionStart[s + 1] : cards.Count;

                float headerTop = y;
                CreateDivider(sectionGroup[s], headerTop);
                y += HeaderH;

                int n = end - idx;
                int rowsInSection = (n + Columns - 1) / Columns;
                // A section too small to fill one row is centred rather than left
                // in the corner — a lone SSX 3 map under a full row of Tricky ones
                // reads as a layout bug otherwise. Multi-row sections stay left
                // aligned so their columns line up with the section above.
                float indent = rowsInSection == 1 ? (Columns - n) * CellW * 0.5f : 0f;
                int firstRow = rows.Count;
                for (int k = 0; k < n; k++)
                {
                    int r = k / Columns, c = k % Columns;
                    float top = y + r * CellH;
                    var rt = cards[idx + k];
                    rt.anchoredPosition = new Vector2(
                        indent + c * CellW + CellW * 0.5f,
                        -(top + CellH * 0.5f));
                    cardTop.Add(r == 0 ? headerTop : top);
                    cardBottom.Add(top + CellH);
                    while (rows.Count <= firstRow + r) rows.Add(new List<Selectable>());
                    rows[firstRow + r].Add(rt.GetComponent<Selectable>());
                }

                y += rowsInSection * CellH + SectionGap;
            }
            contentHeight = y - SectionGap + BottomPad;
            content.sizeDelta = new Vector2(GridW, contentHeight);

            // Grow the strip into a panel — but never taller than what it holds, so
            // a two-section install doesn't get a screen of empty space under it.
            // Only sizeDelta changes: the Scroll View's anchors/pivot stay put (it
            // is pivoted at its bottom edge, so this grows upward off the nav bar),
            // and its Animation component (a 0.1 s anchoredPosition.x slide-in whose
            // clip is SHARED with character select — do not edit it) is left running
            // and untouched.
            float gridH = Mathf.Clamp(
                Plugin.levelSelectGridHeight != null ? Plugin.levelSelectGridHeight.Value : 1800f,
                CellH, MaxGridH);
            gridH = Mathf.Min(gridH, contentHeight);
            scrollView.sizeDelta = new Vector2(GridW, gridH);

            WireNavigation(rows);

            built = true;
            Plugin.Log.LogInfo($"[LevelSelect] grid: {cards.Count} card(s) in {dividers.Count} section(s), "
                + $"{rows.Count} row(s), content {contentHeight:0} px tall in a {gridH:0} px viewport");
        }

        private void CreateDivider(string label, float top)
        {
            var go = new GameObject("SectionDivider_" + label, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(content, false); // appends — never insert, see the class note
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(GridW, HeaderH);
            rt.anchoredPosition = new Vector2(0f, -top);
            dividers.Add(go);

            if (labelTemplate != null)
            {
                var lbl = Instantiate(labelTemplate.gameObject, rt);
                lbl.name = "Label";
                lbl.SetActive(true);
                // A heading must never be focusable: it would be a dead end for
                // navigation and a NullReference risk if anything indexed it.
                foreach (var s in lbl.GetComponentsInChildren<Selectable>(true)) Destroy(s);
                var lrt = (RectTransform)lbl.transform;
                lrt.anchorMin = Vector2.zero;
                lrt.anchorMax = Vector2.one;
                lrt.pivot = new Vector2(0.5f, 0.5f);
                lrt.offsetMin = new Vector2(HeaderInset, 0f);
                lrt.offsetMax = new Vector2(-HeaderInset, 0f);
                var tmp = lbl.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    tmp.enableAutoSizing = false;
                    tmp.fontSize = 56f;
                    tmp.alignment = TextAlignmentOptions.MidlineLeft;
                    tmp.enableWordWrapping = false;
                    tmp.raycastTarget = false;
                    tmp.text = label;
                }
            }

            // Rule under the heading, so a section reads as a band even where the
            // cards below it are still thumbnail-less grey boxes.
            var line = new GameObject("Rule", typeof(RectTransform), typeof(Image));
            var lineRt = (RectTransform)line.transform;
            lineRt.SetParent(rt, false);
            lineRt.anchorMin = new Vector2(0f, 0f);
            lineRt.anchorMax = new Vector2(1f, 0f);
            lineRt.pivot = new Vector2(0.5f, 0f);
            lineRt.sizeDelta = new Vector2(-2f * HeaderInset, 4f);
            lineRt.anchoredPosition = new Vector2(0f, 14f);
            var img = line.GetComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.25f);
            img.raycastTarget = false;
        }

        // Explicit four-way navigation on EVERY card, computed from its grid slot.
        //
        // Two reasons this isn't left to the engine. (1) LevelSelectMenu::Populate
        // rewrites the FIRST and LAST card with a zeroed Navigation set to Explicit
        // and only selectOnLeft/selectOnRight filled — up and down are null, so in a
        // grid those two cards are dead ends. (2) Every other card is authored at
        // mode -1, which Selectable treats bitwise as automatic on both axes; that
        // works, but its neighbour search is geometric and can pick diagonals, which
        // we have not tested at 79 cards. Explicit removes the question.
        private static void WireNavigation(List<List<Selectable>> rows)
        {
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                for (int c = 0; c < row.Count; c++)
                {
                    var s = row[c];
                    if (s == null) continue;
                    var nav = new Navigation { mode = Navigation.Mode.Explicit };
                    // Left/right walk the list linearly, wrapping across rows, so
                    // holding a direction traverses every course in menu order.
                    nav.selectOnLeft = c > 0
                        ? row[c - 1]
                        : (r > 0 ? rows[r - 1][rows[r - 1].Count - 1] : null);
                    nav.selectOnRight = c < row.Count - 1
                        ? row[c + 1]
                        : (r < rows.Count - 1 ? rows[r + 1][0] : null);
                    // Up/down hold the column, clamping into short rows (the last
                    // row of a section, and any section with fewer than 5 courses).
                    nav.selectOnUp = r > 0
                        ? rows[r - 1][Mathf.Min(c, rows[r - 1].Count - 1)]
                        : null;
                    nav.selectOnDown = r < rows.Count - 1
                        ? rows[r + 1][Mathf.Min(c, rows[r + 1].Count - 1)]
                        : null;
                    s.navigation = nav;
                }
            }
        }

        private void LateUpdate()
        {
            if (!built || content == null || viewport == null) return;

            var es = EventSystem.current;
            GameObject sel = es == null ? null : es.currentSelectedGameObject;
            if (sel == null || !sel.transform.IsChildOf(content)) return;

            // Walk up to the card root (the child whose parent is Content).
            Transform card = sel.transform;
            while (card != null && card.parent != content) card = card.parent;
            int i = cards.IndexOf(card as RectTransform);
            if (i < 0) return;

            float vh = viewport.rect.height;
            if (vh < 1f) return; // menu not laid out yet; try again next frame

            float cur = content.anchoredPosition.y;
            float target = cur;
            if (target > cardTop[i] - EdgeMargin) target = cardTop[i] - EdgeMargin;
            if (target < cardBottom[i] - vh + EdgeMargin) target = cardBottom[i] - vh + EdgeMargin;
            target = Mathf.Clamp(target, 0f, Mathf.Max(0f, contentHeight - vh));
            if (Mathf.Abs(target - cur) < 0.5f) return; // in view — don't fight a drag

            float ny = Mathf.Lerp(cur, target, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, ny);
        }
    }

    // Keeps a skydome mesh centered on the camera and sized to envelop the whole visible
    // course, so a finite (map-scaled) dome reads as a distant sky. The importer emits an
    // unlit, Background-queue, ZWrite-off, cull-off dome named "Skydome" with no collider.
    //
    // Two jobs, both in URP's beginCameraRendering (fires AFTER every Update/LateUpdate,
    // right before the camera culls — the exact render viewpoint, zero lag):
    //
    // 1) POSITION: snap the dome onto the camera every render. A plain LateUpdate follow
    //    lags a frame behind the chase-cam rig; at 200 mph that ~1.5 m offset swims the
    //    dome edge / makes the near ground "emerge like water." beginCameraRendering has
    //    no lag. Only position follows; rotation stays world-fixed (compass-stable sky).
    //
    // 2) SCALE: the dome is a FINITE bowl baked at map scale (tens of units). If its radius
    //    is smaller than the distance to far course terrain, that terrain pokes past the
    //    dome edge and the dome reads as a nearby wall (user report). Centering alone can't
    //    fix this — the course is a fixed size the dome must ENVELOP. So auto-fit the dome
    //    radius to ~90% of the camera's far-clip plane each frame: as distant as possible
    //    while staying inside the frustum (overshoot → the far plane clips the dome into
    //    chunks). [Skydome] Scale is a residual multiplier on that fit (default 1).
    //
    // Also clears the camera to [Skydome] FillColor so looking up past the open rim (~+20
    // deg) reads as sky, not black. Done on the pipeline-supplied camera, NOT Camera.main
    // (null on these maps — the game camera isn't the enabled MainCamera the whole time).
    public class SkydomeFollow : MonoBehaviour
    {
        Transform _cam;
        Color _fill;
        bool _fillValid;
        float _baseRadius = -1f;   // unscaled dome radius (map units), computed once
        bool _fitLogged;

        void OnEnable()
        {
            // Per-level override (Overrides.json lighting.skyFillColor, via the SkyFill_
            // marker Plugin.LoadLevelManager parses before attaching us) wins over the
            // global [Skydome] FillColor config.
            if (Plugin.SceneSkyFill.HasValue) { _fill = Plugin.SceneSkyFill.Value; _fillValid = true; }
            else
            {
                string hex = Plugin.skydomeFillColor != null ? Plugin.skydomeFillColor.Value : "#3373C3";
                if (ColorUtility.TryParseHtmlString(hex, out _fill)) _fillValid = true;
                else { _fill = new Color(0.20f, 0.45f, 0.76f); _fillValid = true;
                       Plugin.Log.LogWarning($"[Skydome] FillColor '{hex}' not parseable; using #3373C3"); }
            }

            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }
        void OnDisable()
        {
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        }

        // Farthest dome vertex from the pivot, in the dome's UNSCALED local frame — the
        // InverseTransformPoint round-trip cancels the root's own localScale, so this is
        // scale-independent and only needs computing once.
        float ComputeBaseRadius()
        {
            float maxSq = 0f;
            foreach (var mf in GetComponentsInChildren<MeshFilter>())
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                var b = mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + new Vector3(
                        (i & 1) == 0 ? b.extents.x : -b.extents.x,
                        (i & 2) == 0 ? b.extents.y : -b.extents.y,
                        (i & 4) == 0 ? b.extents.z : -b.extents.z);
                    Vector3 rootLocal = transform.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    float sq = rootLocal.sqrMagnitude;
                    if (sq > maxSq) maxSq = sq;
                }
            }
            return Mathf.Sqrt(maxSq);
        }

        void OnBeginCamera(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == null || !cam.CompareTag("MainCamera")) return;

            transform.position = cam.transform.position;

            if (_baseRadius < 0f) _baseRadius = ComputeBaseRadius();
            if (_baseRadius > 0.001f && cam.farClipPlane > 1f)
            {
                float mult = Plugin.skydomeScale != null ? Mathf.Max(0.01f, Plugin.skydomeScale.Value) : 1f;
                float targetR = cam.farClipPlane * 0.95f * mult;
                float s = targetR / _baseRadius;
                transform.localScale = Vector3.one * s;
                if (!_fitLogged)
                {
                    Plugin.Log.LogInfo($"[Skydome] auto-fit: baseR={_baseRadius:F1} farClip={cam.farClipPlane:F0} mult={mult:F2} -> scale={s:F1}");
                    _fitLogged = true;
                }
            }

            if (_fillValid && (cam.clearFlags != CameraClearFlags.SolidColor || cam.backgroundColor != _fill))
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = _fill;
            }
        }

        // First-frame / built-in-pipeline fallback; harmless under URP where OnBeginCamera
        // repositions before the render.
        void LateUpdate()
        {
            if (_cam == null)
            {
                var c = Camera.main;
                if (c == null) return;
                _cam = c.transform;
            }
            transform.position = _cam.position;
        }
    }

    // Camera-follow atmospheric snowfall. The importer bakes an empty "SnowFX_<intensity>"
    // marker; we build the ParticleSystem here at runtime because (a) a bundle-baked
    // ParticleSystem is silently stripped unless the module is in the import project, and
    // (b) it must stay wrapped around the chase cam. Simulated in WORLD space so flakes
    // fall past a turning camera naturally (a camera-parented/local system would rotate the
    // whole snowfield with the view); the emitter box just re-centres on the camera each
    // LateUpdate so there is always fresh snow around the rider even at 190 mph. Geometry
    // is fixed and playtest-tuned; intensity scales only emission rate + particle cap.
    public class SnowFall : MonoBehaviour
    {
        public static bool Active;
        Transform _cam;

        public static void Spawn(float intensity)
        {
            var go = new GameObject("HookSnowFall");
            go.AddComponent<SnowFall>().Build(Mathf.Clamp(intensity, 0.05f, 8f));
            Active = true;
        }

        void Build(float intensity)
        {
            var ps = gameObject.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4f, 6.5f);  // desync the fall
            main.startSpeed = 0f;                    // motion comes from velocityOverLifetime
            // Wide size spread so flakes read as near/far, not one uniform grade — some fine
            // spindrift (0.05), a few fatter foreground flakes (0.55; 0.8 read as dust bunnies).
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.55f);
            main.startColor = new Color(0.93f, 0.96f, 1f, 0.85f);
            main.maxParticles = Mathf.CeilToInt(3400 * intensity);
            main.gravityModifier = 0f;
            main.playOnAwake = false;

            var emission = ps.emission;
            emission.rateOverTime = 650f * intensity;   // ~2x the first pass

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(70f, 45f, 70f); // band around the camera (world units)

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(-1.5f, 1.5f);  // drift
            vel.y = new ParticleSystem.MinMaxCurve(-7f, -4f);     // fall
            vel.z = new ParticleSystem.MinMaxCurve(-1.5f, 1.5f);

            // Non-uniform density: soft low-frequency turbulence swirls flakes into clumps
            // and gaps instead of an even rain grid — the "make it not so uniform" fix.
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 1.6f;      // world-unit drift amplitude
            noise.frequency = 0.22f;    // large soft swirls, not jitter
            noise.scrollSpeed = 0.35f;
            noise.damping = true;
            noise.quality = ParticleSystemNoiseQuality.Medium;

            var pr = GetComponent<ParticleSystemRenderer>();
            pr.renderMode = ParticleSystemRenderMode.Billboard;
            pr.alignment = ParticleSystemRenderSpace.View;
            pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pr.receiveShadows = false;
            pr.material = FlakeMaterial();

            ps.Play();
        }

        void LateUpdate()
        {
            if (_cam == null)
            {
                var c = Camera.main;
                if (c == null) return;
                _cam = c.transform;
            }
            // Centre the emission band on the camera; world-sim keeps the falling motion.
            transform.position = _cam.position;
        }

        void OnDestroy() { Active = false; }

        static Material _flakeMat;
        static Material FlakeMaterial()
        {
            if (_flakeMat != null) return _flakeMat;
            var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                     ?? Shader.Find("Universal Render Pipeline/Unlit");
            var m = new Material(sh) { name = "HookSnowFlake" };
            m.SetTexture("_BaseMap", FlakeSprite());
            m.SetColor("_BaseColor", Color.white);
            // Transparent alpha-blend (the URP incantation ApplyTexMode uses on the importer side).
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            _flakeMat = m;
            return m;
        }

        static Texture2D _flakeTex;
        static Texture2D FlakeSprite()
        {
            if (_flakeTex != null) return _flakeTex;
            const int N = 32;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { name = "HookSnowFlakeTex" };
            float c = (N - 1) / 2f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    float a = Mathf.Clamp01(1f - d);
                    a *= a;   // soft round falloff, no hard rim
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            t.Apply();
            t.wrapMode = TextureWrapMode.Clamp;
            _flakeTex = t;
            return t;
        }
    }

    // URP post-process bloom. The importer bakes an empty "Bloom_<intensity>_<threshold>"
    // marker; we create a runtime global Volume carrying a Bloom override and flip the
    // render camera's post-processing on in beginCameraRendering (the game camera is null
    // via Camera.main during load, same reason SkydomeFollow uses the pipeline-supplied
    // camera). If the game's URP renderer has post-processing disabled at the feature level
    // this is inert — verified by playtest, not assertable here.
    public class PostFX : MonoBehaviour
    {
        public static bool Active;
        float _intensity = 0.7f, _threshold = 0.9f;

        public static void Enable(float intensity, float threshold)
        {
            if (Active) return;
            var go = new GameObject("HookPostFX");
            var fx = go.AddComponent<PostFX>();
            fx._intensity = Mathf.Max(0f, intensity);
            fx._threshold = Mathf.Max(0f, threshold);
            fx.Build();
            Active = true;
        }

        void Build()
        {
            var vol = gameObject.AddComponent<UnityEngine.Rendering.Volume>();
            vol.isGlobal = true;
            vol.priority = 100f;   // win over any game volume
            var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
            vol.sharedProfile = profile;

            var bloom = profile.Add<UnityEngine.Rendering.Universal.Bloom>(true);
            bloom.intensity.Override(_intensity);
            bloom.threshold.Override(_threshold);
            bloom.scatter.Override(0.72f);
            bloom.tint.Override(new Color(0.9f, 0.95f, 1f));

            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }

        void OnBeginCamera(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == null || !cam.CompareTag("MainCamera")) return;
            var data = cam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            if (data != null && !data.renderPostProcessing)
                data.renderPostProcessing = true;
        }

        void OnDisable()
        {
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        }
        void OnDestroy() { Active = false; }
    }

    // A wind push volume. Attached by the Hook's scene scan to every "Wind_<strength>" GO
    // the importer emits. While a Snowboarder's origin is inside the (oriented) box, each
    // physics step adds WorldDir * Strength * dt to that rider's private Snowboarder.velocity
    // — the field Integrate() writes and UpdateGrounded() feeds to CharacterController.Move.
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
        public float Strength;     // acceleration magnitude, m/s^2

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
                Vector3 v = t.GetValue<Vector3>();
                v += WorldDir * Strength * dt;
                t.SetValue(v);
            }
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
                        "[UVScroll] diag t={0:F0}s '{1}': shader='{2}' wrote=({3:F3},{4:F3}) baseMapST=({5:F3},{6:F3}) mainOfs=({7:F3},{8:F3}) bound={9} rate=({10:R},{11:R})",
                        Time.timeSinceLevelLoad, name, m0.shader != null ? m0.shader.name : "null",
                        o.x, o.y, rb.x, rb.y, m0.mainTextureOffset.x, m0.mainTextureOffset.y,
                        bound, RatePerFrame.x, RatePerFrame.y));
                }
            }
        }
    }

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

    // Relay for a trigger-driven animated door (feature: gameplay.animDoors). The
    // importer emits the door GO's Animation separately (N triggers can share one door,
    // which the game's own PlayAnimationOnTriggerEnter can't express — it force-binds to
    // its own GO). While a Snowboarder's origin enters this box we Play() the referenced
    // door once per entry (edge-triggered). Analytic OBB containment (box disabled) like
    // TeleportVolume/WindVolume — thin trigger volumes tunnel physics trigger callbacks.
    // ClampForever clips: the door opens and stays open; a replay on re-entry is harmless.
    public class AnimDoorTrigger : MonoBehaviour
    {
        public Animation target;
        public string clipName;
        // Doors clear the path by switching their colliders OFF on fire. A moving RIDEABLE
        // surface (gameplay.movingColliders) must keep them so the animation drags the
        // collider — that motion IS the feature. See ssx/MOVING_COLLIDER_FEASIBILITY.md.
        public bool keepColliders;

        Vector3 _halfLocal = new Vector3(0.5f, 0.5f, 0.5f);
        Vector3 _center = Vector3.zero;   // box.center: model-bounds offset from the instance origin
        Snowboarder[] _riders;
        readonly HashSet<Snowboarder> _inside = new HashSet<Snowboarder>();
        readonly Dictionary<Snowboarder, Vector3> _prev = new Dictionary<Snowboarder, Vector3>();
        bool _opened;   // door colliders disabled once (idempotent), and log emitted once

        void Awake()
        {
            var box = GetComponent<BoxCollider>();
            if (box != null)
            {
                _halfLocal = box.size * 0.5f;   // local half-extents; containment is in local space
                _center = box.center;
                box.enabled = false;            // geometry carrier only
            }
            // NOTE: do NOT self-disable on a null target here. AddComponent runs Awake
            // synchronously, BEFORE the wiring assigns `target` on the next line — so target is
            // ALWAYS null at this point and disabling would switch off every relay permanently
            // (the "attached N, fired 0" bug). FixedUpdate already early-returns on a null target.
        }

        void FixedUpdate()
        {
            if (target == null) return;
            if (_riders == null || _riders.Length == 0)
            {
                _riders = UnityEngine.Object.FindObjectsOfType<Snowboarder>();
                if (_riders.Length == 0) return; // riders not spawned yet
            }
            foreach (var sb in _riders)
            {
                if (sb == null) continue;
                // Swept test: the trigger boxes are only ~6 m across and the rider crosses at
                // 70-85 m/s (>1 box-width per fixed tick), so a point-in-box sample tunnels clean
                // through and never fires. Test the segment from the rider's PREVIOUS origin to its
                // current one against the box (local-space slab test) — this can't tunnel. Mirrors
                // the Smashable swept design for the same reason.
                Vector3 cur = transform.InverseTransformPoint(sb.transform.position);
                Vector3 prev = _prev.TryGetValue(sb, out var p) ? p : cur;
                _prev[sb] = cur;
                bool hit = SegmentHitsBox(prev, cur, _center, _halfLocal);
                if (!hit) { _inside.Remove(sb); continue; }
                if (!_inside.Add(sb)) continue;   // still crossing — edge-trigger only
                Fire();
            }
        }

        void Fire()
        {
            if (!string.IsNullOrEmpty(clipName)) target.Play(clipName);
            else target.Play();
            if (_opened) return;
            _opened = true;
            if (keepColliders)
            {
                Plugin.Log.LogInfo($"[MoveSurf] trigger '{name}' fired -> moving '{target.name}'");
                return;
            }
            // Open the path independent of how far the baked animation actually travels: disable the
            // closed door's blocking colliders. The trigger sits ahead of the door, so this clears
            // before the rider arrives; the animation then plays purely for show. No deadlock.
            foreach (var col in target.GetComponentsInChildren<Collider>())
                col.enabled = false;
            Plugin.Log.LogInfo($"[AnimDoor] trigger '{name}' fired -> opened '{target.name}'");
        }

        // Segment p0->p1 vs axis-aligned box (center, half) in the box's own local space. Slab
        // method: clamp the [0,1] parametric interval against each axis' slab; empty => miss.
        static bool SegmentHitsBox(Vector3 p0, Vector3 p1, Vector3 center, Vector3 half)
        {
            Vector3 d = p1 - p0;
            float tmin = 0f, tmax = 1f;
            for (int a = 0; a < 3; a++)
            {
                float o = p0[a] - center[a];
                float di = d[a];
                float mn = -half[a], mx = half[a];
                if (Mathf.Abs(di) < 1e-8f)
                {
                    if (o < mn || o > mx) return false; // parallel to slab and outside it
                }
                else
                {
                    float t1 = (mn - o) / di, t2 = (mx - o) / di;
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    if (t1 > tmin) tmin = t1;
                    if (t2 < tmax) tmax = t2;
                    if (tmin > tmax) return false;
                }
            }
            return true;
        }
    }

    // Drives the game's own Destructible.Break on a smashable pane/prop when the rider
    // rides through it. WHY NOT the game's trigger path: Snowboarder.OnTriggerEnter DOES
    // call Destructible.Break(velocity) — but only when a live trigger fires an enter
    // event for the rider's CharacterController, and that path is unreliable for us: the
    // rider crosses at 150-190 mph (~70-85 m/s → ~1.5 m per fixed tick) so a thin pane
    // trigger is tunnelled between discrete overlap samples, and a bundle-added trigger on
    // the game's glass layer isn't guaranteed to fire against the rider's layer. So we
    // drive Break ourselves with a SWEPT test (segment from the rider's previous origin to
    // its current one vs the box), which can't tunnel. Same collider-free design as Wind/
    // Teleport: the BoxCollider is a geometry carrier, DISABLED at runtime (a live trigger
    // on a cast-included layer would perturb the rider) — but kept present, because
    // Destructible.Break calls GetComponent<Collider>().enabled=false and would NPE without
    // one. One-shot: Break has no re-entry guard (it re-launches every fragment), so we
    // Destroy this component after the first hit.
    public class Smashable : MonoBehaviour
    {
        const float RiderRadiusWorld = 0.6f; // ~capsule radius; catch margin, scale-folded

        Destructible _destr;
        Vector3 _center;                     // box centre, local (unscaled) space
        Vector3 _half = new Vector3(0.15f, 0.15f, 0.15f);
        Snowboarder[] _riders;
        readonly Dictionary<Snowboarder, Vector3> _prev = new Dictionary<Snowboarder, Vector3>();

        void Awake()
        {
            _destr = GetComponent<Destructible>();
            var box = GetComponent<BoxCollider>();
            if (box != null)
            {
                _center = box.center;
                _half = box.size * 0.5f;
                box.enabled = false; // geometry carrier only — never a live collider
            }
            if (_destr == null) enabled = false;
        }

        void FixedUpdate()
        {
            if (_destr == null) { Destroy(this); return; }
            if (_riders == null || _riders.Length == 0)
            {
                _riders = UnityEngine.Object.FindObjectsOfType<Snowboarder>();
                if (_riders.Length == 0) return; // riders not spawned yet
            }
            // Fold the rider's world catch-radius into local (unscaled) units so the margin
            // is a constant ~0.6 m in the world regardless of map WorldScale (the box is
            // authored at unit scale; the transform carries the scale-up).
            float ls = transform.lossyScale.x;
            float m = ls > 1e-4f ? RiderRadiusWorld / ls : RiderRadiusWorld;
            Vector3 half = _half + new Vector3(m, m, m);

            foreach (var sb in _riders)
            {
                if (sb == null) continue;
                Vector3 cur = transform.InverseTransformPoint(sb.transform.position);
                if (!_prev.TryGetValue(sb, out var prev)) prev = cur; // first tick: point test
                _prev[sb] = cur;
                if (!SegmentHitsBox(prev, cur, _center, half)) continue;

                Vector3 vel = Traverse.Create(sb).Field("velocity").GetValue<Vector3>();
                _destr.Break(vel);
                Destroy(this); // one-shot — Break re-launches every fragment if called twice
                return;
            }
        }

        // Slab test: does segment p0->p1 intersect the AABB centred at c with half-extents h?
        static bool SegmentHitsBox(Vector3 p0, Vector3 p1, Vector3 c, Vector3 h)
        {
            Vector3 d = p1 - p0;
            float tmin = 0f, tmax = 1f;
            for (int a = 0; a < 3; a++)
            {
                float o = p0[a] - c[a], di = d[a], ha = h[a];
                if (Mathf.Abs(di) < 1e-8f)
                {
                    if (o < -ha || o > ha) return false; // parallel to slab and outside it
                }
                else
                {
                    float t1 = (-ha - o) / di, t2 = (ha - o) / di;
                    if (t1 > t2) { var tmp = t1; t1 = t2; t2 = tmp; }
                    if (t1 > tmin) tmin = t1;
                    if (t2 < tmax) tmax = t2;
                    if (tmin > tmax) return false;
                }
            }
            return true;
        }
    }

    // Respawn / out-of-bounds instrumentation for custom maps (Debug/PerfHarness,
    // off by default). Written for the SSX 3 whole-mountain chain, whose frame rate
    // turned out to be a respawn storm rather than a rendering cost: riders in
    // contact with a converted reset zone were respawned every frame of contact,
    // back into the same volume. See HOOK_NOTES.md §23.
    //
    // What used to live here — the shadow/MSAA/render-scale/cascade toggles, the
    // region streaming, the camera-off and script-census probes, the Ctrl+B sweep
    // and Ctrl+T trend, and the on-screen readout that drove them — is GONE. Every
    // rendering ablation it measured came back inside the noise floor, and the
    // answer was on the physics side. Don't reinstate it to re-chase draw calls;
    // CLAUDE.md records that dead end explicitly.
    //
    // What survives is the part that found the bug and is still the way to tell
    // whether a fix worked: the game's own respawns, split by route, clustered by
    // location, with the offending OOB colliders named so the LEVEL DATA can be
    // fixed at source instead of the Hook papering over it forever.
    public class PerfHarness : MonoBehaviour
    {
        static PerfHarness _inst;

        public static void Spawn()
        {
            if (_inst != null) return;
            // Static, so they outlive the harness they belong to: a second map would
            // otherwise open with the previous map's respawns already in the report.
            GameRespawns.Clear();
            OobHits.Clear();
            OobWhere.Clear();
            var go = new GameObject("HookPerfHarness");
            // Deliberately NOT DontDestroyOnLoad: dying with the scene is what
            // guarantees OnDestroy runs and the report is written on level exit.
            _inst = go.AddComponent<PerfHarness>();
        }

        void Update()
        {
            HandleKeys();
            TrackRespawns();
        }

        void HandleKeys()
        {
            if (!Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl)) return;
            // Ctrl+U dumps the report mid-ride. OnDestroy dumps it again on exit, so
            // this is only for reading it without ending the run.
            if (Input.GetKeyDown(KeyCode.U)) DumpRespawns();
        }

        // Every rider, plus which index is the human.
        //
        // ⚠️ The human is `LevelManager.player`, read off the game — NOT the rider
        // nearest the camera. The camera heuristic this replaced is wrong exactly
        // when it matters: the whole field is bunched at the start gate when the
        // watch list locks, so it can name any of them, and a report that says
        // "rider 0" without knowing whether rider 0 is the player is unreadable.
        List<Snowboarder> FindRiders(out int human)
        {
            var all = new List<Snowboarder>(FindObjectsOfType<Snowboarder>());
            human = -1;
            var lm = FindObjectOfType<LevelManager>();
            var player = lm != null
                ? Traverse.Create(lm).Field("player").GetValue() as Snowboarder
                : null;
            if (player != null)
                for (int i = 0; i < all.Count; i++)
                    if (ReferenceEquals(all[i], player)) { human = i; break; }
            return all;
        }

        // ---- respawn tracking ---------------------------------------------------
        // Where riders respawn, and whether the loops cluster. A respawn is detected
        // as a POSITION JUMP — a discontinuity far larger than the rider could have
        // travelled in one frame — so this needs to know nothing about the game's
        // respawn code and cannot be broken by an update renaming it.
        //
        // This exists to answer one question before any fix is written: if the loops
        // pile up at two or three places, they are bad respawn nodes in OUR merged
        // chain data and the fix is the data. If they are scattered, it is the
        // game's respawn logic and the fix is escalation at runtime.
        //
        // ⚠️ This detector UNDER-counts badly and has twice agreed with a wrong
        // conclusion: it reported 4 events against a run that turned out to have
        // 3231 real respawns. Believe the game-truth list below over this one; it
        // is kept only as the calibration the threshold never had.
        struct Respawn { public int rider; public Vector3 to; public float t; }

        Transform[] _watch;
        Snowboarder[] _sb;      // parallel to _watch — the wedge probe reads their state
        int _human = -1;        // index into _watch of LevelManager.player
        Vector3[] _watchPrev;
        float[] _watchStep;
        float _watchRetry;
        bool _watchLocked;
        readonly List<Respawn> _respawns = new List<Respawn>();

        // GAME-TRUTH respawns, recorded by Snowboarder_Respawn's postfix. The game
        // telling us it respawned somebody cannot be wrong.
        // `from` is where the rider was STUCK, which is the location a fix has to act
        // on; `to` is only where the game put them. `crashed` distinguishes the two
        // routes into Respawn, which need completely different fixes:
        //   crashed=true  -> Snowboarder.FixedUpdate, gated on CanRespawn() (state
        //                    Dead + deathTimer > 1.5). The rider wrecked on geometry.
        //   crashed=false -> either Snowboarder.OnControllerColliderHit on a layer-10
        //                    contact (NO cooldown at all — fires every frame of
        //                    contact, and Respawn picks the NEAREST marker, which for
        //                    a rider inside an OOB volume is the one that puts them
        //                    back in it), or AISnowboarder.GetSteeringInput after
        //                    1.5 s of finding no AI path. The OOB tally below is what
        //                    separates those two, by measurement rather than by
        //                    deduction from the state field.
        public struct GameRespawn
        { public Transform who; public Vector3 from, to; public float t; public bool crashed; }
        public static readonly List<GameRespawn> GameRespawns = new List<GameRespawn>();

        // Which OOB collider each respawn loop is actually firing on. The whole point
        // is to name the objects: the level data can then be fixed at source instead
        // of the Hook papering over a misplaced volume forever.
        public static readonly Dictionary<string, int> OobHits = new Dictionary<string, int>();
        public static readonly Dictionary<string, Vector3> OobWhere = new Dictionary<string, Vector3>();

        public static void NoteOobHit(string name, Vector3 at)
        {
            if (_inst == null || name == null) return;
            int n;
            OobHits.TryGetValue(name, out n);
            // Bounded: a level with pathological naming must not grow this without end.
            if (n == 0 && OobHits.Count >= 512) return;
            OobHits[name] = n + 1;
            OobWhere[name] = at;
        }

        static Vector3 _preFrom;
        static bool _preCrashed;

        public static void NotePreRespawn(Vector3 from, bool crashed)
        { _preFrom = from; _preCrashed = crashed; }

        public static void NoteRespawn(Transform who, Vector3 to)
        {
            // Nothing to report to when the harness is off; don't accumulate for a
            // reader that will never run.
            if (_inst == null) return;
            // Bounded: a pathological loop must not turn a diagnostic into a leak.
            if (GameRespawns.Count < 8192)
                GameRespawns.Add(new GameRespawn
                {
                    who = who, from = _preFrom, to = to,
                    t = Time.unscaledTime, crashed = _preCrashed,
                });
        }

        // ---- stuck / looping discrimination -------------------------------------
        // The jump detector answers "where do they respawn". It cannot answer the
        // question the first report raised: riders visibly stop racing far more
        // often than they teleport, so most of them are not respawning at all.
        //
        // Over a fixed window, compare each rider's PATH LENGTH against their NET
        // DISPLACEMENT. Healthy: both large. Wedged against geometry: both ~zero.
        // Looping (crash -> recover -> crash the same rock): path large, net ~zero.
        // Those three are indistinguishable to any single-position probe, which is
        // exactly why the stall column read them all as "stalled".
        const float StuckWindow = 10f;      // s
        const float StuckNet = 50f;         // u of net travel below which a window is "not racing"
        const float LoopPath = 150f;        // u of path length above which "not racing" means looping

        struct Stuck { public int rider; public Vector3 at; public float t; public bool looping; }

        Vector3[] _winStart;
        float[] _winPath;
        float _winClock;
        readonly List<Stuck> _stuck = new List<Stuck>();

        void TrackRespawns()
        {
            if (!_watchLocked)
            {
                // Riders are spawned after the scene builds, so retry until they
                // exist — then LOCK and never scan again.
                //
                // ⚠️ This scan is a FindObjectsOfType over a 59k-object scene. It first
                // shipped on a fixed 5 s timer, which put a periodic hitch and steady GC
                // pressure into every ride and read as "the map got slower" — the
                // instrument degrading the thing it was measuring. It may run while
                // resolving, and after that only if a rider is destroyed.
                _watchRetry -= Time.unscaledDeltaTime;
                if (_watchRetry > 0f) return;
                _watchRetry = 2f;
                int human;
                var all = FindRiders(out human);
                if (all.Count == 0) return;
                // Don't lock until LevelManager.player resolves. Locking early would
                // save a watch list that cannot say which rider is the player, which
                // is the one question the wedge report has to answer.
                if (human < 0) return;
                _human = human;
                _sb = all.ToArray();
                _watch = new Transform[_sb.Length];
                for (int i = 0; i < _sb.Length; i++) _watch[i] = _sb[i].transform;
                _watchPrev = new Vector3[_watch.Length];
                _watchStep = new float[_watch.Length];
                _winStart = new Vector3[_watch.Length];
                _winPath = new float[_watch.Length];
                for (int i = 0; i < _watch.Length; i++)
                {
                    _watchPrev[i] = _watch[i] != null ? _watch[i].position : Vector3.zero;
                    _winStart[i] = _watchPrev[i];
                }
                _winClock = 0f;
                _watchLocked = true;
                return;     // no previous frame to compare against yet
            }

            bool endWindow = (_winClock += Time.unscaledDeltaTime) >= StuckWindow;

            for (int i = 0; i < _watch.Length; i++)
            {
                var tr = _watch[i];
                if (tr == null) { _watchLocked = false; return; }
                Vector3 p = tr.position;
                float step = (p - _watchPrev[i]).magnitude;
                // A teleport is a jump both absolutely large AND far bigger than the
                // rider's own recent per-frame step, so a genuinely fast rider on a
                // slow frame does not read as a respawn. 40 units and 5x are guesses
                // sized off the ~60 u/s descent speed seen in the trend runs — not
                // calibrated against a known respawn, which is why NoteRespawn now
                // records the game's own respawns alongside this.
                bool jump = step > 40f && step > _watchStep[i] * 5f && _watchStep[i] > 0f;
                if (jump)
                {
                    _respawns.Add(new Respawn { rider = i, to = p, t = Time.unscaledTime });
                    // A teleport is not travel: counting it as path length would make
                    // a respawning rider look like a healthy one.
                    _winStart[i] = p;
                    _winPath[i] = 0f;
                }
                else _winPath[i] += step;

                if (endWindow)
                {
                    float net = (p - _winStart[i]).magnitude;
                    if (net < StuckNet)
                    {
                        bool looping = _winPath[i] > LoopPath;
                        _stuck.Add(new Stuck
                        {
                            rider = i, at = p, t = Time.unscaledTime,
                            looping = looping,
                        });
                        if (!looping) ProbeWedge(i, p);
                    }
                    _winStart[i] = p;
                    _winPath[i] = 0f;
                }

                _watchStep[i] = step;
                _watchPrev[i] = p;
            }

            if (endWindow) _winClock = 0f;
        }

        // ---- wedge probe --------------------------------------------------------
        // A WEDGED rider (net travel ~0, path length ~0) is a different bug from a
        // looping one and the respawn report is blind to it: it generates no
        // respawns at all, so nothing in GameRespawns names it and the loop guard
        // never sees it. This says what the rider IS and what it is touching.
        //
        // The three readings that separate the candidate causes, per the IL:
        //   * HUMAN vs AI — the crash respawn is `CanRespawn() &&
        //     snowboardController.GetRespawnInput()`. The AI's GetRespawnInput
        //     self-arms on a 0.5–1 s / competence timer, but the player's is a
        //     button, so a wedged player generating no respawns is the game working
        //     as designed and NOT something the Hook should be fixing.
        //   * state / deathTimer / finished — CanRespawn() needs state Dead (4),
        //     !finished, deathTimer > 1.5. A rider stuck in Riding never qualifies
        //     however long it sits there, and one that has `finished` is parked on
        //     purpose.
        //   * what it is touching — a solid collider holding the rider in place. If
        //     these come back named `Mdl_Resetzone*` on layer 10 this is the same
        //     bug as §23; anything else is new.
        const float ProbeRadius = 12f;      // u — a rider capsule is a couple of units
        const int ProbeMaxPerRider = 3;     // a whole-run wedge must not flood the log
        int[] _probed;

        void ProbeWedge(int i, Vector3 p)
        {
            if (_probed == null || _probed.Length != _watch.Length)
                _probed = new int[_watch.Length];
            if (_probed[i] >= ProbeMaxPerRider) return;
            _probed[i]++;

            var sb = _sb != null && i < _sb.Length ? _sb[i] : null;
            string who = i == _human ? "HUMAN" : "AI";
            string detail = "state ?";
            if (sb != null)
            {
                var tv = Traverse.Create(sb);
                object st = tv.Field("state").GetValue();
                object dt = tv.Field("deathTimer").GetValue();
                object fin = tv.Field("finished").GetValue();
                var cc = sb.GetComponent<CharacterController>();
                detail = $"state {st} deathTimer {dt} finished {fin}"
                    + (cc != null ? $" grounded {cc.isGrounded} vel {cc.velocity.magnitude:F1}" : "");
            }
            Plugin.Log.LogInfo($"[Perf] WEDGED rider {i} ({who}) at "
                + $"({p.x:F0},{p.y:F0},{p.z:F0})  {detail}  "
                + $"[probe {_probed[i]}/{ProbeMaxPerRider}]");

            // QueryTriggerInteraction.Collide on purpose: a trigger cannot hold a
            // rider, but knowing one is overlapping is exactly what distinguishes
            // "wedged in geometry" from "sat in a volume that keeps acting on it".
            var hits = Physics.OverlapSphere(p, ProbeRadius, ~0, QueryTriggerInteraction.Collide);
            if (hits == null || hits.Length == 0)
            {
                Plugin.Log.LogInfo($"[Perf]   nothing within {ProbeRadius:F0} u — "
                    + "not held by a collider; look at the rider's own state instead");
                return;
            }
            Array.Sort(hits, (a, b) =>
                (a.ClosestPoint(p) - p).sqrMagnitude.CompareTo((b.ClosestPoint(p) - p).sqrMagnitude));
            int shown = Math.Min(hits.Length, 8);
            Plugin.Log.LogInfo($"[Perf]   {hits.Length} collider(s) within {ProbeRadius:F0} u, nearest {shown}:");
            for (int h = 0; h < shown; h++)
            {
                var c = hits[h];
                if (c == null) continue;
                float d = (c.ClosestPoint(p) - p).magnitude;
                Plugin.Log.LogInfo($"[Perf]     {d,6:F1} u  layer {c.gameObject.layer,2}"
                    + $"{(c.isTrigger ? " trigger" : " solid  ")}  {c.gameObject.name}");
            }
        }

        // Cluster events by proximity and report the hot spots. Greedy single-pass
        // clustering: good enough to answer "two spots or twenty", which is the only
        // question being asked of it.
        void ReportClusters(string label, List<Vector3> pos, List<int> rider, List<float> time)
        {
            if (pos.Count == 0)
            {
                Plugin.Log.LogInfo($"[Perf] {label}: none");
                return;
            }
            const float R = 200f;
            var centres = new List<Vector3>();
            var counts = new List<int>();
            var who = new List<HashSet<int>>();
            var first = new List<float>();
            var last = new List<float>();
            for (int e = 0; e < pos.Count; e++)
            {
                int hit = -1;
                for (int c = 0; c < centres.Count; c++)
                    if ((centres[c] - pos[e]).sqrMagnitude < R * R) { hit = c; break; }
                if (hit < 0)
                {
                    centres.Add(pos[e]); counts.Add(1);
                    who.Add(new HashSet<int> { rider[e] });
                    first.Add(time[e]); last.Add(time[e]);
                }
                else
                {
                    counts[hit]++; who[hit].Add(rider[e]); last[hit] = time[e];
                }
            }
            var idx = new List<int>();
            for (int i = 0; i < centres.Count; i++) idx.Add(i);
            idx.Sort((a, b) => counts[b].CompareTo(counts[a]));

            Plugin.Log.LogInfo($"[Perf] {label}: {pos.Count} event(s) in "
                + $"{centres.Count} cluster(s) (radius {R:F0} u), worst first:");
            foreach (int c in idx)
            {
                var ids = new List<int>(who[c]); ids.Sort();
                var names = new List<string>();
                // The player is marked, because "rider 0" means nothing on its own —
                // whether a hot spot is the human's or the AI field's decides
                // whether it is a bug worth fixing at all.
                foreach (int id in ids)
                    names.Add(id < 0 ? "?" : (id == _human ? id + "(P)" : id.ToString()));
                Plugin.Log.LogInfo($"[Perf]   {counts[c],4} x near ({centres[c].x,8:F0},"
                    + $"{centres[c].y,7:F0},{centres[c].z,8:F0})  rider(s) {string.Join(",", names.ToArray())}"
                    + $"  over {last[c] - first[c],5:F0}s");
            }
        }

        int WatchIndex(Transform tr)
        {
            if (_watch == null || tr == null) return -1;
            for (int i = 0; i < _watch.Length; i++)
                if (ReferenceEquals(_watch[i], tr)) return i;
            return -1;
        }

        void DumpRespawns()
        {
            // Several reports, deliberately separate, because the first ride showed they
            // do NOT measure the same thing: 4 position jumps against riders that
            // visibly stop racing for most of a run. Whichever of these is large is
            // the one the fix has to address.
            var pos = new List<Vector3>();
            var rid = new List<int>();
            var tim = new List<float>();

            Plugin.Log.LogInfo($"[Perf] {(_watch != null ? _watch.Length : 0)} rider(s) watched; "
                + (_human >= 0 ? $"rider {_human} is the player (marked (P) below)"
                               : "player NOT identified — LevelManager.player never resolved"));

            // Split by route, because they are different bugs. Reported by the FROM
            // position: where the rider was stuck is what a fix has to act on.
            int crashed = 0;
            foreach (var g in GameRespawns) if (g.crashed) crashed++;

            foreach (var g in GameRespawns)
            {
                if (!g.crashed) continue;
                pos.Add(g.from); rid.Add(WatchIndex(g.who)); tim.Add(g.t);
            }
            ReportClusters($"CRASHED respawns ({crashed}) — wrecked on geometry, from", pos, rid, tim);

            pos.Clear(); rid.Clear(); tim.Clear();
            foreach (var g in GameRespawns)
            {
                if (g.crashed) continue;
                pos.Add(g.from); rid.Add(WatchIndex(g.who)); tim.Add(g.t);
            }
            // NOT "pathless". The first split labelled every non-Dead respawn as the
            // AI's pathless route; that was an inference and it was wrong. Both the
            // OOB-contact route and the pathless route leave state != Dead, and the
            // pathless one is hard-gated at one respawn per 1.5 s per rider — so it
            // cannot account for 2817 events in 114 s from a single rider. The OOB
            // tally below is what separates them, by measurement rather than by
            // deduction from the state field.
            ReportClusters($"NON-CRASH respawns ({GameRespawns.Count - crashed}) — OOB "
                + "contact or AI pathless, from", pos, rid, tim);

            if (OobHits.Count > 0)
            {
                var names = new List<string>(OobHits.Keys);
                names.Sort((a, b) => OobHits[b].CompareTo(OobHits[a]));
                int shown = Math.Min(names.Count, 15);
                Plugin.Log.LogInfo($"[Perf] OOB collider contacts: {OobHits.Count} distinct "
                    + $"collider(s), worst {shown}:");
                for (int i = 0; i < shown; i++)
                {
                    Vector3 w = OobWhere[names[i]];
                    Plugin.Log.LogInfo($"[Perf]   {OobHits[names[i]],7} x  {names[i]}"
                        + $"  at ({w.x:F0}, {w.y:F0}, {w.z:F0})");
                }
            }
            else Plugin.Log.LogInfo("[Perf] OOB collider contacts: none");

            pos.Clear(); rid.Clear(); tim.Clear();
            foreach (var g in GameRespawns)
            { pos.Add(g.to); rid.Add(WatchIndex(g.who)); tim.Add(g.t); }
            ReportClusters("respawn DESTINATIONS (both routes)", pos, rid, tim);

            pos.Clear(); rid.Clear(); tim.Clear();
            foreach (var r in _respawns)
            { pos.Add(r.to); rid.Add(r.rider); tim.Add(r.t); }
            ReportClusters("respawns (position-jump detector)", pos, rid, tim);

            pos.Clear(); rid.Clear(); tim.Clear();
            int loops = 0;
            foreach (var s in _stuck)
            {
                if (s.looping) loops++;
                pos.Add(s.at); rid.Add(s.rider); tim.Add(s.t);
            }
            ReportClusters($"not-racing {StuckWindow:F0}s window(s) "
                + $"[{loops} looping / {_stuck.Count - loops} wedged]", pos, rid, tim);
        }

        void OnDestroy()
        {
            DumpRespawns();   // free, and the whole reason the run was ridden
            if (_inst == this) _inst = null;
        }
    }
}
