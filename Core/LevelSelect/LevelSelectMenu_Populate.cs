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

        // AccessTools, not a Traverse string chain, so tm_re_baseline.py's binding
        // scan can see it — Plugin.SetGameField has the why.
        static readonly System.Reflection.FieldInfo fEntries =
            AccessTools.Field(typeof(LevelSelectMenu), "levelSelectEntries");

        [HarmonyPostfix]
        public static void Postfix(LevelSelectMenu __instance)
        {
            if (fEntries == null)
            {
                Plugin.Log.LogError("[Reflection] LevelSelectMenu.levelSelectEntries no longer resolves — "
                    + "custom-map menu thumbnails are off; run `python3 tm_re_baseline.py check`.");
                return;
            }
            var entries = fEntries.GetValue(__instance) as List<LevelSelectEntry>;
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
}
