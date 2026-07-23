using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

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
        public static string MapsDir;
        private void Awake()
        {
            // Plugin startup logic
            Log = Logger;
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
            MapsDir = ResolveMapsDir();
            Logger.LogInfo($"Maps directory resolved to: {MapsDir}");
            if (!Directory.Exists(MapsDir))
            {
                Directory.CreateDirectory(MapsDir);
            }
            AddTracks();
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

        public void AddTracks()
        {
            var List = Directory.GetFiles(MapsDir, "*.asset");
            for (int i = 0; i < List.Length; i++)
            {
                Paths.Add(List[i]);
                TrackNames.Add(Path.GetFileNameWithoutExtension(List[i]));
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

            // MakerAIPath.m_points is a List of a user-defined [Serializable]
            // nested class, and Unity fails to deserialize those for
            // runtime-loaded (BepInEx) assemblies — on custom maps the list
            // frequently comes back empty (engine limitation; lists of engine
            // value types, like the race line's List<Vector3> below, are
            // unaffected). The AI enumerates DIRECT children of pathsParent, so
            // every AIPath must sit immediately under AIParent.
            var MakerAIPaths = FindObjectsByType(typeof(MakerAIPath), FindObjectsSortMode.None) as MakerAIPath[];
            for (int i = 0; i < MakerAIPaths.Length; i++)
            {
                // An empty maker path would become an empty AIPath, which is what
                // sends AI into the 1.5s respawn loop — skip it entirely.
                if (MakerAIPaths[i].m_points == null || MakerAIPaths[i].m_points.Count == 0)
                    continue;

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

    // Custom thumbnails. The game sets each level's menu thumbnail via
    // Resources.Load<Sprite>(thumbnailPath), which can only read sprites baked into
    // the game/bundle — not a loose PNG on disk — and it runs before the map bundle
    // is even loaded. So instead, after the menu is built, we override the thumbnail
    // Image for any custom ("$"-marked) level with a PNG dropped next to its .asset
    // (e.g. Maps/MyMap.png), loading the bytes ourselves via Texture2D.LoadImage.
    [HarmonyPatch(typeof(LevelSelectMenu), "Populate")]
    public class LevelSelectMenu_Populate
    {
        // Cache so we don't re-read the file / leak a texture every time the menu opens.
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
        }

        private static Sprite LoadSprite(string pngPath)
        {
            Sprite cached;
            if (cache.TryGetValue(pngPath, out cached)) return cached;
            cache[pngPath] = null; // remember misses too, so we don't retry disk every menu open
            if (!File.Exists(pngPath)) return null;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(File.ReadAllBytes(pngPath))) return null;
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                cache[pngPath] = sprite;
                return sprite;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LevelHook] thumbnail load failed for " + pngPath + ": " + e.Message);
                return null;
            }
        }
    }
}
