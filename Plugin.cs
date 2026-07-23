using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TrickyMaddnessLevelHook
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static Harmony harmony;
        public static MenuManager menuManager;

        //Static handle on the BepInEx logger so the patch classes can log too
        public static ManualLogSource Log;

        public static AssetBundle myLoadedAssetBundle;
        public static string LoadedAssetBundle = "";
        public static string LoadedTrack;
        public static string LoadedTrackName;

        public static List<string> TrackNames = new List<string>();
        public static List<string> Paths = new List<string>();

        //When the shader remap runs. See ShaderRemapEnabled() for the reasoning
        //behind Auto.
        public enum ShaderRemapMode { Auto, Always, Never }
        public static ConfigEntry<ShaderRemapMode> ShaderRemap;

        //The game's own shaders, snapshotted before any bundle is loaded so
        //nothing in here can be bundle-origin. name -> shader, the set of their
        //instance IDs (what tells a game shader from a bundle one), and per
        //shader the union of keywords seen on the game's own materials.
        public static Dictionary<string, Shader> GameShaders;
        public static HashSet<int> GameShaderIds;
        public static Dictionary<int, HashSet<string>> GameShaderKeywords;

        private void Awake()
        {
            // Plugin startup logic
            Log = Logger;
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
            ShaderRemap = Config.Bind("Rendering", "ShaderRemap", ShaderRemapMode.Auto,
                "Rebind a custom map's materials to the game's own shaders when the map's shaders were compiled for another platform (they render magenta otherwise). "
                + "Auto: only when the game is NOT running on Windows, since maps are overwhelmingly Windows-built. "
                + "Always: also on Windows - use this if a Mac/Linux-built map renders magenta. "
                + "Never: disable entirely.");
            Logger.LogInfo($"Shader remap: mode {ShaderRemap.Value} on {Application.platform} -> {(ShaderRemapEnabled() ? "active" : "inactive")}");
            if (!Directory.Exists(Directory.GetCurrentDirectory() + "\\Maps\\"))
            {
                Directory.CreateDirectory(Directory.GetCurrentDirectory() + "\\Maps\\");
            }
            SceneManager.sceneLoaded += OnSceneLoaded;
            AddTracks();
            DoPatching();
        }
        public void AddTracks()
        {
            var List = Directory.GetFiles(Directory.GetCurrentDirectory() + "\\Maps\\", "*.asset");
            for (int i = 0; i < List.Length; i++)
            {
                Paths.Add(List[i]);
                TrackNames.Add(Path.GetFileName(List[i]).Split(".")[0]);
            }
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

        public static void LoadLevelManager()
        {
            //Find Main Starting Objects
            var levelManagerMaker = FindAnyObjectByType<MakerLevelData>();
            var MakerSpawnPoint = FindAnyObjectByType<MakerSpawnPoint>();
            var MakerRespawnPoint = FindAnyObjectByType<MakerRespawnPoints>();
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
            for (int i = 0; i < RailObjects.Length; i++)
            {
                var NewRail = RailObjects[i].gameObject.AddComponent<RailInvis>();

                var Segments = RailObjects[i].GetSegments();
                List<Rail.Segment> NewSegments = new List<Rail.Segment>();

                for (int j = 0; j < Segments.Count; j++)
                {
                    Rail.Segment NewSegment = new Rail.Segment(Vector3.zero, Vector3.up);

                    NewSegment.position = Segments[j].Point;
                    NewSegment.rotation = Segments[j].Rotation;
                    NewSegment.distance = Segments[j].Distance;

                    NewSegments.Add(NewSegment);
                }

                Enum.TryParse(RailObjects[i].Materials.ToString(), out Rail.RailMaterial material1);

                NewRail.material = material1;

                Traverse.Create(NewRail).Field("segments").SetValue(NewSegments);
            }

            GameObject AIParent = new GameObject("AI Paths Parent");
            AIParent.transform.parent = levelManager.transform;

            var MakerAIPaths = FindObjectsByType(typeof(MakerAIPath), FindObjectsSortMode.None) as MakerAIPath[];
            for (int i = 0; i < MakerAIPaths.Length; i++)
            {
                //For all path object
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
            }
            Traverse.Create(levelManager).Field("pathsParent").SetValue(AIParent.transform);


            GameObject AIPDividerParent = new GameObject("AI Paths Divider Parent");
            AIPDividerParent.transform.parent = levelManager.transform;
            var MakerAIPathsDivider = FindObjectsByType(typeof(MakerAIPathDivider), FindObjectsSortMode.None) as MakerAIPathDivider[];
            for (int i = 0; i < MakerAIPathsDivider.Length; i++)
            {
                var NewPath = MakerAIPathsDivider[i].gameObject.AddComponent<AIPathDivider>();

                MakerAIPathsDivider[i].transform.parent = AIParent.transform;

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
        }

        // ---------------------------------------------------------------------
        // Cross-platform shader remap
        //
        // An AssetBundle carries shader BYTECODE for the platform it was built
        // on. A Windows-built map on the macOS/Linux build of the game (Metal /
        // Vulkan) has no usable bytecode for any of its materials and the whole
        // map renders magenta. The game itself ships platform-correct shaders
        // with the SAME names (Universal Render Pipeline/Lit etc.), so the fix is
        // to rebind the map's materials onto the game's copies.
        //
        // Deciding WHEN to distrust a bundle's own shaders is the hard part:
        //  * Shader.isSupported is not usable — it reports true for foreign
        //    platform bytecode, which is exactly the case we need to catch.
        //  * There is no managed API that reports which platform a loaded
        //    AssetBundle was built for (the target is buried in the bundle's
        //    compressed serialized-file header; parsing that ourselves is a lot
        //    of fragile format code for one bit of information).
        //  * Asking map authors to declare it (a sidecar or a marker asset) only
        //    helps maps built after such a convention exists — every map already
        //    published would still need a guess.
        // So we guess from the running platform, which costs the user nothing:
        // custom maps are overwhelmingly built on Windows, so a map running on
        // Windows is native (leave it completely alone — remapping is lossy: it
        // resets keywords and can drop shader features the map actually shipped),
        // while a map running on macOS or Linux is almost certainly foreign and
        // gets remapped. The minority case a guess cannot cover — a Mac-built map
        // on Windows — is what the ShaderRemap config entry is for.
        public static bool ShaderRemapEnabled()
        {
            if (ShaderRemap == null) return false;
            if (ShaderRemap.Value == ShaderRemapMode.Never) return false;
            if (ShaderRemap.Value == ShaderRemapMode.Always) return true;
            //Auto: everything except Windows, whose bundles are already native.
            var platform = Application.platform;
            return platform != RuntimePlatform.WindowsPlayer
                && platform != RuntimePlatform.WindowsEditor;
        }

        // Called from the LoadScene prefix immediately BEFORE the bundle is
        // loaded, so everything captured here is guaranteed game-origin. The
        // game's shaders never change, so this only has to run once a session.
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

        // Scene name of the active custom map, derived from LoadedTrack the same
        // way the LoadScene prefix does (never Path.* — on macOS '\' is an
        // ordinary filename character). Null when no custom bundle is active.
        public static string CustomSceneName()
        {
            if (string.IsNullOrEmpty(LoadedAssetBundle) || string.IsNullOrEmpty(LoadedTrack))
                return null;
            var parts = LoadedTrack.Split('\\', '/');
            return parts[parts.Length - 1].Split('.')[0];
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!ShaderRemapEnabled()) return;

            // Only ever touch the custom bundle's OWN scene. Gating on "a custom
            // bundle is loaded" is not enough: returning to the menu does not go
            // through MenuManager.LoadScene(LevelEntry), so that state is still
            // set and we would rebind MENU materials while the bundle's
            // duplicate-named shaders are still resident — shader lookup by name
            // is unspecified with duplicates, so that can leave a pink menu until
            // the game is restarted.
            string customScene = CustomSceneName();
            if (customScene == null || scene.name != customScene) return;

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

                string miss = missing.Count == 0 ? "none" : string.Join(", ", missing);
                Log.LogInfo($"[ShaderRemap] scene '{scene.name}': scanned {scanned} material(s), remapped {remapped}, lit-fallbacks {litFallbacks}, keyword-pruned {pruned}, unfixable: {miss}");
            }
            catch (Exception e)
            {
                //A throw in a sceneLoaded handler would break entering the level;
                //a map that renders wrong is still better than one you can't play.
                Log.LogError($"[ShaderRemap] failed on scene '{scene.name}': {e}");
            }
        }

        private static void FixMaterial(Material mat, HashSet<string> missing,
            ref int remapped, ref int litFallbacks, ref int pruned)
        {
            var sh = mat.shader;
            if (sh == null) return;
            //Shipped with the game, so it runs here by definition.
            if (GameShaderIds != null && GameShaderIds.Contains(sh.GetInstanceID())) return;

            // A bundle built with a broken shader bakes in Unity's error shader,
            // whose name the game ALSO has — name-matching it would rebind
            // magenta to magenta. Send it to the Lit fallback instead.
            bool bakedError = sh.name.Contains("FallbackError");

            // Assigning Material.shader RESETS renderQueue to the new shader's
            // default (documented Unity behavior). Maps rely on custom queues for
            // their transparent/cutout materials; losing them drops a ZWrite-off
            // blend material into the opaque pass where later draws overwrite it,
            // and fences/foliage go invisible. Restore it at BOTH assignments.
            int queue = mat.renderQueue;

            Shader replacement;
            if (!bakedError && GameShaders != null && GameShaders.TryGetValue(sh.name, out replacement))
            {
                mat.shader = replacement;
                mat.renderQueue = queue;
                remapped++;
                if (PruneKeywords(mat, replacement)) pruned++;
                return;
            }

            // No same-named shader in the game build: substitute URP Lit. Property
            // values (_BaseMap/_BaseColor/_BumpMap and tiling) live on the material
            // and survive the shader swap, so this is plain but not pink.
            if (GameShaders != null && GameShaders.TryGetValue("Universal Render Pipeline/Lit", out replacement))
            {
                mat.shader = replacement;
                mat.renderQueue = queue;
                //Down to the base variant, which every URP build has, plus the
                //safe-listed feature keywords — clearing those un-cuts cutout
                //foliage.
                var keep = new List<string>();
                foreach (var k in mat.shaderKeywords)
                    if (SafeKeywords.Contains(k)) keep.Add(k);
                mat.shaderKeywords = keep.ToArray();
                litFallbacks++;
            }
            else
            {
                missing.Add(sh.name);
            }
        }

        // Feature keywords the game build is KNOWN to compile even though the
        // snapshot cannot see them: the game materials that use them live in the
        // built-in LEVEL scenes (trees carry _ALPHATEST_ON, glass carries
        // _SURFACE_TYPE_TRANSPARENT), and the snapshot is taken from the menu,
        // where no level scene is loaded. Without this allow-list the prune below
        // strips alpha clip from every cutout material in the map and foliage
        // renders as solid blocks.
        private static readonly HashSet<string> SafeKeywords = new HashSet<string>
        {
            "_ALPHATEST_ON", "_SURFACE_TYPE_TRANSPARENT",
        };

        // Drop material keywords never observed on any game material using the
        // same shader: a build only compiles the shader variants its own content
        // uses, so a keyword combination from outside that set can select a
        // variant that was stripped (magenta at draw time). Losing a keyword
        // costs one feature; keeping a stripped combination costs the whole
        // material. Returns true if anything was removed.
        private static bool PruneKeywords(Material mat, Shader gameShader)
        {
            HashSet<string> union = null;
            if (GameShaderKeywords != null)
                GameShaderKeywords.TryGetValue(gameShader.GetInstanceID(), out union);

            var kws = mat.shaderKeywords;
            if (kws == null || kws.Length == 0) return false;
            var kept = new List<string>();
            bool removed = false;
            foreach (var k in kws)
            {
                if (SafeKeywords.Contains(k) || (union != null && union.Contains(k))) kept.Add(k);
                else removed = true;
            }
            if (!removed) return false;
            mat.shaderKeywords = kept.ToArray();
            return true;
        }
    }


    // The level-load entry point is MenuManager.LoadScene(LevelEntry), NOT
    // SelectLevel. Retail MenuManager exposes SelectLevel(int index) — a different
    // signature — so a patch aimed at SelectLevel(LevelEntry) silently never binds
    // and every custom map loads to a black screen. Do not "fix" this back.
    [HarmonyPatch(typeof(MenuManager), "LoadScene", new System.Type[] { typeof(LevelEntry) })]
    public class MenuManager_LoadScene
    {
        // Returning false skips MenuManager.LoadScene entirely — the clean no-op
        // abort for any failure: the menu simply stays up instead of loading a
        // black screen. Every abort path logs why first. State (myLoadedAssetBundle,
        // LoadedAssetBundle, LoadedTrack) is only committed AFTER a bundle has been
        // confirmed loaded with at least one scene, so a failed load can never
        // leave a null handle behind for a later selection to NRE on.
        [HarmonyPrefix]
        public static bool Prefix(ref LevelEntry levelEntry)
        {
            //Built-in level (a null sceneName is not ours either)
            if (levelEntry.sceneName == null || !levelEntry.sceneName.Contains("$"))
            {
                //No custom bundle should stay loaded. Drive the unload off the
                //handle, not off LoadedAssetBundle — the two can be out of sync.
                Plugin.LoadedAssetBundle = "";
                Plugin.LoadedTrack = null;
                if (Plugin.myLoadedAssetBundle != null)
                {
                    Plugin.myLoadedAssetBundle.Unload(true);
                    Plugin.myLoadedAssetBundle = null;
                }
                return true;
            }

            try
            {
                string path = levelEntry.sceneName.TrimStart('$');
                Plugin.Log.LogInfo($"LoadScene prefix: loading custom map bundle '{levelEntry.name}' from {path}");

                if (Plugin.LoadedAssetBundle == path && Plugin.myLoadedAssetBundle != null)
                {
                    //Same map re-selected and its bundle is still loaded: reuse it.
                    Plugin.Log.LogInfo($"LoadScene prefix: reusing already-loaded bundle {path}");
                }
                else
                {
                    //Switching maps: drop the old bundle and clear its state first,
                    //so an abort below leaves no stale path pointing at a dead handle.
                    if (Plugin.myLoadedAssetBundle != null)
                    {
                        Plugin.myLoadedAssetBundle.Unload(true);
                        Plugin.myLoadedAssetBundle = null;
                        Plugin.Log.LogInfo("LoadScene prefix: unloaded previous custom bundle");
                    }
                    Plugin.LoadedAssetBundle = "";
                    Plugin.LoadedTrack = null;

                    //Snapshot the game's shaders once per session, while nothing
                    //but the game is loaded — after LoadFromFile the bundle's own
                    //same-named shaders are indistinguishable from the game's.
                    //Not gated on ShaderRemapEnabled(): this instant is the only
                    //chance to take it, and it reads without changing anything, so
                    //turning the setting on later still has data to work from.
                    if (Plugin.GameShaders == null)
                        Plugin.SnapshotGameShaders();

                    //Null means the file is missing/corrupt, built for another Unity
                    //version, or already loaded elsewhere.
                    var bundle = AssetBundle.LoadFromFile(path);
                    if (bundle == null)
                    {
                        Plugin.Log.LogError($"LoadScene prefix: bundle load failed (file missing/corrupt or already loaded): {path}");
                        return false;
                    }

                    //A bundle with no scene is unusable — unload and abort.
                    string[] scenePath = bundle.GetAllScenePaths();
                    if (scenePath == null || scenePath.Length == 0)
                    {
                        Plugin.Log.LogError($"LoadScene prefix: bundle has no scene paths, unusable: {path}");
                        bundle.Unload(true);
                        return false;
                    }

                    //Commit only now that the load is known good.
                    Plugin.myLoadedAssetBundle = bundle;
                    Plugin.LoadedAssetBundle = path;
                    Plugin.LoadedTrack = scenePath[0];
                }

                //Derive the bundle-internal scene name from LoadedTrack. NOT Path.*:
                //on macOS '\' is an ordinary filename character, not a separator.
                var TempSplitName = Plugin.LoadedTrack.Split('\\', '/');
                string Name = TempSplitName[TempSplitName.Length - 1];
                levelEntry.sceneName = Name.Split('.')[0];
                return true;
            }
            catch (Exception e)
            {
                //Never let a prefix throw: that leaves the menu in a broken state
                //instead of simply declining the selection.
                Plugin.Log.LogError($"LoadScene prefix: exception loading custom map: {e}");
                return false;
            }
        }
    }
}
