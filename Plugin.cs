using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
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

        public static List<string> TrackNames = new List<string>();
        public static List<string> Paths = new List<string>();

        internal static ConfigEntry<bool> levelSelectScroll;

        private void Awake()
        {
            // Plugin startup logic
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
            levelSelectScroll = Config.Bind("UI", "LevelSelectScroll", true,
                "Scroll the level-select card strip when more cards exist than fit " +
                "on screen, auto-scrolling to the selected card. When all cards fit, " +
                "the menu is left pixel-identical to vanilla. Set false to disable.");
            if (!Directory.Exists(Directory.GetCurrentDirectory() + "\\Maps\\"))
            {
                Directory.CreateDirectory(Directory.GetCurrentDirectory() + "\\Maps\\");
            }
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
    }


    [HarmonyPatch(typeof(MenuManager), "SelectLevel", new System.Type[] { typeof(LevelEntry) })]
    public class MenuManager_SelectLevel
    {
        [HarmonyPrefix]
        public static void Prefix(ref LevelEntry levelEntry)
        {
            if(levelEntry.sceneName.Contains("$"))
            {
                levelEntry.sceneName = levelEntry.sceneName.TrimStart('$');

                if (Plugin.LoadedAssetBundle == "")
                {
                    Plugin.myLoadedAssetBundle = AssetBundle.LoadFromFile(levelEntry.sceneName);
                    Plugin.LoadedAssetBundle = levelEntry.sceneName;
                }
                else if (Plugin.LoadedAssetBundle != levelEntry.sceneName)
                {
                    Plugin.myLoadedAssetBundle.Unload(true);
                    Plugin.myLoadedAssetBundle = AssetBundle.LoadFromFile(levelEntry.sceneName);
                    Plugin.LoadedAssetBundle = levelEntry.sceneName;
                }

                //Load Level
                string[] scenePath = Plugin.myLoadedAssetBundle.GetAllScenePaths();
                Plugin.LoadedTrack = scenePath[0];

                var TempSplitName = scenePath[0].Split('\\', '/');
                string Name = TempSplitName[TempSplitName.Length - 1];
                levelEntry.sceneName = Name.Split('.')[0];
            }
            else
            {
                if (Plugin.LoadedAssetBundle != "")
                {
                    Plugin.LoadedAssetBundle = "";
                    Plugin.myLoadedAssetBundle.Unload(true);
                }
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

            // Attach the scroller once (it no-ops itself when all cards fit). Put it
            // on the Scroll View GameObject — never on Content (whose children the
            // menu indexes by position) or the Scroll View's RectTransform (it has
            // an Animation component we must not disturb).
            if (Plugin.levelSelectScroll != null && !Plugin.levelSelectScroll.Value) return;
            var scrollRect = __instance.GetComponentInChildren<ScrollRect>(true);
            if (scrollRect == null)
            {
                Debug.LogWarning("[LevelHook] no ScrollRect found under LevelSelectMenu; scrolling disabled");
                return;
            }
            if (scrollRect.gameObject.GetComponent<LevelSelectScroller>() == null)
            {
                var scroller = scrollRect.gameObject.AddComponent<LevelSelectScroller>();
                scroller.Init(scrollRect);
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

    // Makes the level-select card strip scroll when more cards exist than fit,
    // auto-scrolling to the selected card. The hook registers one card per .asset in
    // Maps/ with no limit, so a handful of custom maps pushes cards past the screen
    // edge where they are unreachable. Lives on the "Scroll View" GameObject.
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
}
