using HarmonyLib;
using TMPro;

namespace TrickyMaddnessLevelHook
{
    // TM 26365637 added a "Weather / <setting>" readout to level select. For a
    // course with canChangeWeather = false the game only greys it out, and every
    // custom map is registered that way (Clear, locked), so a grey CLEAR sat over
    // every custom card. Hide the readout outright on any locked course; the game
    // re-runs UpdateWeatherLabel on every selection change, so moving back to a
    // course that can change weather shows it again.
    [HarmonyPatch(typeof(LevelSelectMenu), "UpdateWeatherLabel")]
    public class LevelSelectMenu_WeatherLabel
    {
        static readonly System.Reflection.FieldInfo fIndex =
            AccessTools.Field(typeof(LevelSelectMenu), "selectedLevelIndex");
        static readonly System.Reflection.FieldInfo fLabel =
            AccessTools.Field(typeof(LevelSelectMenu), "weatherLabel");
        static readonly System.Reflection.FieldInfo fNameLabel =
            AccessTools.Field(typeof(LevelSelectMenu), "weatherNameLabel");

        [HarmonyPostfix]
        public static void Postfix(LevelSelectMenu __instance)
        {
            if (fIndex == null || fLabel == null || fNameLabel == null) return;
            int index = (int)fIndex.GetValue(__instance);
            if (index < 0 || index >= LevelEntries.GetLevelCount()) return;
            bool show = LevelEntries.GetEntry(index).canChangeWeather;

            var label = fLabel.GetValue(__instance) as TextMeshProUGUI;
            var nameLabel = fNameLabel.GetValue(__instance) as TextMeshProUGUI;
            if (label != null) label.enabled = show;
            if (nameLabel != null) nameLabel.enabled = show;
        }
    }
}
