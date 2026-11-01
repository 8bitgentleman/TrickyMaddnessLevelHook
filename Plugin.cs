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
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public partial class Plugin : BaseUnityPlugin
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

        internal static ConfigEntry<bool> aiBracePoints;

        internal static ConfigEntry<bool> lowerScaledGems;

        internal static ConfigEntry<float> gemHeightDivisor;

        internal static ConfigEntry<float> startLaunchSpeedScale;

        internal static ConfigEntry<bool> mapPhysicsRate;

        internal static ConfigEntry<bool> respawnLoopGuard;

        internal static ConfigEntry<bool> respawnLoopGuardPlayer;

        internal static ConfigEntry<int> respawnLoopAdvance;

        internal static ConfigEntry<float> respawnCrashLoopWindow;

        internal static ConfigEntry<bool> respawnFallClamp;

        internal static ConfigEntry<bool> airborneStuckGuard;

        internal static ConfigEntry<float> airborneStuckSeconds;

        // Fine-tune multiplier on the skydome's auto-fit (feature 13). SkydomeFollow
        // sizes the dome to ~95% of the camera far-clip plane each frame so it envelops
        // the whole visible course; this scales that target (1 = the fit). Tunable, no
        // rebuild.
        internal static ConfigEntry<float> skydomeScale;

        internal static ConfigEntry<float> ssxSnowFlakeSize;

        internal static ConfigEntry<float> ssx3SnowFlakeSize;

        // SSX domes only reach ~+20 deg elevation — an open-topped bowl. Above the rim
        // the camera shows its clear color, which is black by default (feature-5's blue
        // clear only fires for skybox-remapped maps, and a domed map's RenderSettings
        // .skybox is null → that fallback stays dormant). So for a domed map SkydomeFollow
        // clears the camera to this sky color so looking up past the rim reads as sky, not
        // a black void. Hex; tunable without a rebuild.
        internal static ConfigEntry<string> skydomeFillColor;

        // Time-of-day ramp for whole-mountain chains (LightingRamp). Data-gated: only maps
        // that bake "LightStop_*" markers have a ramp at all, and today that is the SSX3
        // Peak-to-Valley chain alone. The switch exists so the ramp can be A/B'd against the
        // map's single baked grade without a rebuild — off = exactly what the bundle bakes.
        internal static ConfigEntry<bool> lightingRamp;

        internal static ConfigEntry<bool> ssx3WorldPainter;

        internal static ConfigEntry<float> ssx3FogReach;

        // Animated-texture playback speed (feature 14). The importer decodes SSX's
        // UVScrolling effect (SSFLogic MainType 0 / SubType 10) and bakes the two
        // scroll rates into the GO name ("UVScroll_<u>_<v>_<orig>"); those rates are
        // UV-units PER FRAME at the PS2's 60 Hz, so UVScrollBehaviour multiplies by
        // this to get UV-units/second. 60 = authentic speed. Tunable, no rebuild.
        internal static ConfigEntry<float> uvScrollFrameRate;

        internal static ConfigEntry<float> terrainDetail;

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
        // deterministic headless testing. Empty in shipped builds.
        // ⚠️ The ONLY way into a map without a menu click: the game has no
        // attract mode, whatever these comments used to say. HOOK_NOTES.md §10.
        private static ConfigEntry<string> debugAutoLoadMap;

        // When true, after building rail colliders a SphereCast probe verifies the
        // game-side grind-detection contract (layer-9 hit + GetComponent<Rail>).
        // Off in shipped builds.
        private static ConfigEntry<bool> railColliderProbe;

        // When true, the sidecar's SSX branch paths are handed to the AI alongside
        // the complete race-line path, so riders pick among alternate routes instead
        // of running single file. See AI_PATHS_PLAN.md.
        private static ConfigEntry<bool> multiPathAI;

        // When true, custom maps spawn PerfHarness: respawn / out-of-bounds
        // instrumentation, reported to the log on level exit or on demand with
        // Ctrl+U. Off in shipped builds.
        //
        // ⚠️ The rendering half this used to describe — an on-screen frame-rate
        // readout plus hotkeys toggling each suspected cost mid-ride — is GONE.
        // Every ablation it measured came back inside the noise floor and the
        // answer was on the physics side; the class header says why it must not
        // come back.
        internal static ConfigEntry<bool> perfHarness;

        // When true, custom maps sample the PLAYER's motion every physics step and
        // write it to Telemetry/<map> <stamp>.csv on level exit. Off in shipped
        // builds. Read it with authoring/tools/ride_report.py.
        internal static ConfigEntry<bool> riderTelemetry;

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
            riderTelemetry = Config.Bind("Debug", "RiderTelemetry", false,
                "On custom maps, sample the player's position, velocity and state " +
                "every physics step and write them to Telemetry/<map> <stamp>.csv on " +
                "level exit. This is how a course's rail spacing, jump sizing and " +
                "landing zones get measured in seconds of riding rather than guessed " +
                "from the level file. Diagnostic only; leave false for normal play.");
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
            aiBracePoints = Config.Bind("Gameplay", "AIBracePoints", true,
                "On converted maps, flag the AI route run-up before each detected jump lip " +
                "as a windup (brace) span, the way TM's own courses do, so COM riders " +
                "crouch and pre-load a spin into the takeoff. Converted courses carry no " +
                "windup data of their own. Takes effect on the next map load.");
            scaleGravityWithMap = Config.Bind("Gameplay", "ScaleGravityWithMap", false,
                "Opt-in feel tuning: on custom maps that declare a world scale (a " +
                "'scale' line in their .aipaths.txt sidecar), multiply the " +
                "rider/caster gravity by that scale, shrinking jump arcs back " +
                "toward original proportions. Off by default — scaled-up maps are " +
                "meant to keep stock gravity so jumps cover proportionally less " +
                "ground. No effect on built-in levels or unscaled maps.");
            lowerScaledGems = Config.Bind("Gameplay", "LowerScaledGems", true,
                "On SSX Tricky maps built bigger than the original (a 'scale' line " +
                "above 1 in their .aipaths.txt sidecar), lower the trick-multiplier " +
                "gems toward TM's own gem heights (see GemHeightDivisor), so they sit " +
                "within stock jump reach. Stacked gems move together, gems only ever " +
                "go down and never into the ground, and gems over a gap, a pit or out " +
                "of bounds are left alone. Takes effect on the next map load.");
            gemHeightDivisor = Config.Bind("Gameplay", "GemHeightDivisor", 1.75f,
                "With LowerScaledGems on: how much to shrink a gem's height above the " +
                "ground (never more than the map's own scale). 1.75 matches TM's own " +
                "courses' gem heights; raise it to bring gems lower, 1 turns lowering off.");
            startLaunchSpeedScale =Config.Bind("Gameplay", "StartLaunchSpeedScale", 0.7f,
                "Custom-map start-launch fix (see StartLaunchRedirect): the game " +
                "kicks every rider straight DOWN at the countdown's end and lets the " +
                "ground redirect it. On custom maps whose start sits on a sideways " +
                "slope that flings the rider off-line, the Hook re-steers the launch " +
                "down-course; this scales the launch speed so it doesn't come out " +
                "hot (the sideways projection preserves nearly the full kick, which a " +
                "gentle down-course start would not). 1.0 = keep the full speed. " +
                "Human rider only; no effect on built-in levels.");
            multiPathAI = Config.Bind("Gameplay", "MultiPathAI", true,
                "Give the AI the map's alternate racing lines instead of marching " +
                "every rider down one route. The source games authored these — SSX 3 " +
                "ships ~88 per course, Merqury City 152, Pipedream 18 — and our " +
                "conversions have always extracted them into the .aipaths.txt " +
                "sidecar, but the Hook threw them away: the AI needs a route under it " +
                "at all times, no single SSX fragment spans a course, and an early " +
                "build of this had riders run off a fragment end and respawn-loop " +
                "without finishing (MECHANICS_DIAGNOSIS Bug 3). The fix then was to " +
                "hand them ONE complete race-line path and accept single-file racing. " +
                "That traded away the wrong thing: measured across all 121 deployed " +
                "maps and 4191 paths, only FOUR fragment endpoints anywhere sit " +
                "farther than the old AIPath 150-unit acquire radius from another " +
                "path — the fragments overlap like lanes and hand off to each other. " +
                "Dropping them left the AI with no candidate to recover onto, which " +
                "is the opposite of safe. This builds BOTH: the branches to race on " +
                "and the race line as a guaranteed complete fallback. Off restores " +
                "the old single-file behaviour with no rebuild. No effect on maps " +
                "whose sidecar has no branches (SSX 2012, Sled Storm, On Tour) or on " +
                "built-in levels.");
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
                "Apply the OUT-OF-BOUNDS half of RespawnLoopGuard to the human rider " +
                "too. Off by default: skipping a stuck CPU rider forward costs nothing, " +
                "but doing it to the player moves them down the course through terrain " +
                "they did not ride, which is a gift in a race. Turn it on if you would " +
                "rather be nudged past a bad spot than sit in a respawn loop. This does " +
                "NOT gate the crash half of the guard (see RespawnCrashLoopWindow), " +
                "which always covers the player and the CPU alike — a rider dying on " +
                "the same piece of geometry forever is not making progress to protect.");
            respawnLoopAdvance = Config.Bind("Gameplay", "RespawnLoopAdvance", 3,
                "How many race-line respawn markers forward the loop guard moves a " +
                "wedged rider each time it fires. Markers sit roughly 25 m apart, so " +
                "3 is about 75 m down-course — far enough to clear a reset volume, " +
                "short enough that a rider who was only unlucky loses little. The " +
                "guard re-fires if that landing is also bad, so this compounds rather " +
                "than having to be right first time.");
            respawnCrashLoopWindow = Config.Bind("Gameplay", "RespawnCrashLoopWindow", 10f,
                "Seconds within which repeated CRASH respawns count as one loop. The " +
                "out-of-bounds loop fires once per rendered frame, so a one-second " +
                "window catches it; a crash loop cannot, because the game's own " +
                "CanRespawn() will not respawn a wrecked rider until deathTimer passes " +
                "1.5 s. Measured on the SSX3 chain, a rider dying on the same rock " +
                "came back every ~2.4 s — invisible to a one-second window, which is " +
                "why the guard originally could not see it. Raise this if a real loop " +
                "is still slipping through; lower it if the guard is shunting riders " +
                "who were merely having a bad run.");
            respawnFallClamp = Config.Bind("Gameplay", "RespawnFallClamp", true,
                "Never let an out-of-bounds respawn carry a rider further down the " +
                "course than where they last touched snow. The game respawns onto the " +
                "NEAREST marker to wherever the rider ended up, so a rider who drops " +
                "through the terrain and falls for half a minute lands next to a " +
                "marker near the BOTTOM of the mountain — measured on SSX3 The Throne: " +
                "one CPU rider fell through at the start line every race, respawned " +
                "92% of the way down and 'won' in ~46 s against a ~150 s field. This " +
                "puts them back where they last rode instead. If the same spot keeps " +
                "dropping them, it steps them forward by RespawnLoopAdvance so a bad " +
                "piece of collision cannot trap anyone. Crash respawns are left alone " +
                "(a wreck already respawns where the rider actually was). Custom maps " +
                "with race-line respawn markers only.");
            airborneStuckGuard = Config.Bind("Gameplay", "AirborneStuckGuard", true,
                "Break riders out of the 'flipping in place and going nowhere' wedge. " +
                "Getting pinned — on a Wall-layer prop, or against another rider — " +
                "leaves the game's state machine stuck in Airborne, which is also " +
                "its trick state, so the rider spins forever with no input that " +
                "escapes: the game only ever lands you from a ground contact the " +
                "pin is preventing, and only ever wrecks you from a hard impact a " +
                "pinned rider cannot produce. This watchdog forces the landing, and " +
                "wrecks the rider if that does not free them, so a respawn carries " +
                "them clear. Custom maps only — the mechanism is the base game's, " +
                "but the geometry that trips it is ours, so built-in levels are " +
                "left exactly as they ship.");
            airborneStuckSeconds = Config.Bind("Gameplay", "AirborneStuckSeconds", 2f,
                "Seconds per window in which an airborne rider must cover at least 25 " +
                "units of NET travel, or the wedge watchdog acts — and then again " +
                "before it escalates from forcing a landing to wrecking them. Net " +
                "rather than per-frame, because the wedge that matters is not " +
                "motionless: a rider can be oscillating hard and still be going " +
                "nowhere. A healthy descent covers ~60 units a second, so this is " +
                "~5x clear of anything actually racing. A judgement call, not a " +
                "measured one: raise it if a legitimate trick is ever cut short.");
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
            ssxSnowFlakeSize = Config.Bind("SnowFall", "SsxFlakeSize", 1f,
                "Multiplier on the flake size of SSX Tricky's decoded snowfall (maps built with " +
                "snowStyle ssx: Snowdream, Merqury City). Everything else about that snow comes " +
                "from the game's own code; the size does not: SSX gives each flake a 5-7 that " +
                "could be centimetres or screen pixels, and 1 reads it as 5-7 cm. Raise it if the " +
                "flakes are too faint to see, lower it if they read as blobs. Relaunch to apply.");
            ssx3SnowFlakeSize = Config.Bind("SnowFall", "Ssx3FlakeSize", 5f,
                "Multiplier on the flake size of SSX 3's per-zone weather snow (maps built with " +
                "snowStyle ssx3). Counts, colours, wind and the zone map all come from the game's " +
                "own data; the size does not: each zone gives a flake size (1-9) whose unit is " +
                "unknown: 1 reads it as a quad that many source units across, which rode as nearly " +
                "invisible; 5 is the playtested default (6 read slightly big). Raise it if the " +
                "flakes are too faint to see, lower it if they read as blobs. Relaunch to apply.");
            lightingRamp = Config.Bind("Visual", "TimeOfDayRamp", true,
                "On whole-mountain chain maps, blend the lighting between each source " +
                "course's grade as you descend — SSX 3's final run starts in daylight at " +
                "the summit and is night by Metro-City, but a Unity scene can only bake " +
                "one grade. Only affects maps whose bundle carries the ramp data; every " +
                "other map, and every built-in level, is untouched either way. Set false " +
                "to see the map exactly as its bundle bakes it. Relaunch to apply.");
            ssx3WorldPainter = Config.Bind("Visual", "Ssx3WorldPainter", true,
                "On SSX 3 maps, change the fog and the screen colour grade as you ride from " +
                "one area of the mountain to the next, from the game's own per-zone data " +
                "(the map's .painter.txt file). Set false to get the single fog each map " +
                "bakes and no colour grade — how SSX 3 maps looked before this existed. " +
                "Maps without the file are untouched either way. Relaunch to apply.");
            ssx3FogReach = Config.Bind("Visual", "Ssx3FogReach", 0f,
                "SSX 3 per-zone fog only. SSX 3's fog never gets thicker than a set amount " +
                "(up to 86%, 18-50% in most areas) however far away something is; Unity's fog " +
                "always ends fully opaque. So the Hook makes the fog reach that set amount at " +
                "this distance, in Unity units, and keeps thickening past it. 0 = where the " +
                "PS2 stopped drawing (750 on SSX 3 maps), which matches the original up close " +
                "but fades terrain the PS2 never drew, and the sky dome, toward solid fog. " +
                "Raise it (2500 = TM's draw distance) to keep far mountains and sky clearer, " +
                "at the cost of less haze in the middle distance. It cannot go below the " +
                "area's own fog distance. Relaunch to apply.");
            uvScrollFrameRate = Config.Bind("Visual", "UVScrollFrameRate", 60f,
                "Playback speed for animated (UV-scrolling) textures on custom maps — " +
                "LCD scanlines, jumbotrons, boost-pad chevrons, river water. The " +
                "authored rates are per-frame at the PS2's 60 Hz, so 60 = authentic " +
                "speed, 0 = frozen. No rebuild needed, just relaunch.");
            terrainDetail = Config.Bind("Visual", "TerrainDetail", 1f,
                "Strength of the surface relief and snow sheen added to some converted " +
                "courses' ground textures (generated normal maps — not in the original " +
                "games). 1 = as the map ships, 0 = off (the original flat look), in " +
                "between scales both together. Only touches that added detail: a source " +
                "game's own normal maps (SSX 2012) and TM's own courses are unaffected. " +
                "Applies on the next map load.");

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
            // A restart reloads the custom scene WITHOUT the LoadScene prefix, so
            // start the load clock when that scene unloads instead.
            SceneManager.sceneUnloaded += s =>
            {
                if (!LoadClock.Running && s.name == CustomSceneName())
                    LoadClock.Begin(LoadedTrack != null ? LoadedTrack + " (reload)" : s.name + " (reload)");
            };

            if (debugBundleLoad.Value)
            {
                RunDebugBundleLoad();
            }
            // Launch flags first, so an editor playtest run overrides whatever the
            // config happens to hold rather than fighting it.
            string cliMap = ParseLaunchArgs();
            if (TestPin != null && !string.IsNullOrEmpty(cliMap)) TestPinMap = cliMap;
            if (TestPin != null)
                Log.LogWarning(TestPinMap != null
                    ? $"[TestPin] scoped to '{TestPinMap}' — every other map starts at its gate"
                    : "[TestPin] no --tm-map given, so EVERY custom map this session starts at the pin");
            string autoMap = !string.IsNullOrEmpty(cliMap) ? cliMap : debugAutoLoadMap.Value;
            if (!string.IsNullOrEmpty(autoMap))
            {
                StartCoroutine(AutoLoadMap(autoMap));
            }

            DoPatching();
        }

        // --tm-map <track> / --tm-pin <x,y,z[,yaw]> — what the editor's Playtest From
        // Pin launches the game with, so a test run needs no config edit and leaves
        // nothing behind when the process exits. Unity ignores args it does not know,
        // and run_bepinex.sh execs the player with "$@" (its own first-arg special
        // case only fires for an EXECUTABLE path), so these arrive intact.
        private static string ParseLaunchArgs()
        {
            string map = null;
            string[] argv;
            try { argv = Environment.GetCommandLineArgs(); }
            catch (Exception e) { Log.LogWarning($"[TestPin] command line unreadable: {e.Message}"); return null; }
            for (int i = 0; i + 1 < argv.Length; i++)
            {
                if (argv[i] == "--tm-map") map = argv[i + 1];
                else if (argv[i] == "--tm-pin") SetTestPin(argv[i + 1], "--tm-pin");
            }
            return map;
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
                // LevelEntry.name is BOTH the tile/results text AND the game's
                // best-times key (SaveManager: Records/<name>.pbs), so a
                // displayname retrofitted onto a played map orphans its times,
                // and two maps sharing one share a record file.
                string shown = ReadBundleDisplayName(List[i]);
                TrackNames.Add(shown ?? Path.GetFileNameWithoutExtension(List[i]));
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
                    // Default (0) makes ResolveWeather roll a random blizzard/fog,
                    // whose ApplyFog overrides the map's own fog, sky and far clip.
                    defaultWeather = MenuManager.Weather.Clear,
                    canChangeWeather = false,
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
    }
}
