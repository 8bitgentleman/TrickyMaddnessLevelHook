using System.Collections.Generic;
using HarmonyLib;
using TMPro;

namespace TrickyMaddnessLevelHook
{
    // TM 26365637 added a "Weather / <setting>" readout to level select's bottom
    // bar, positioned in screen space, so with enough custom maps it sat over the
    // grid. We fold the setting into the CHANGE WEATHER prompt instead
    // ("CHANGE WEATHER (FOG)") and hide the separate readout. On a course with
    // canChangeWeather = false (every custom map: Clear, locked) the game only
    // greys the prompt out; we deactivate it. The game re-runs
    // UpdateWeatherLabel on every selection change and every weather cycle, so
    // this postfix keeps the prompt in step.
    [HarmonyPatch(typeof(LevelSelectMenu), "UpdateWeatherLabel")]
    public class LevelSelectMenu_WeatherLabel
    {
        static readonly System.Reflection.FieldInfo fIndex =
            AccessTools.Field(typeof(LevelSelectMenu), "selectedLevelIndex");
        static readonly System.Reflection.FieldInfo fLabel =
            AccessTools.Field(typeof(LevelSelectMenu), "weatherLabel");
        static readonly System.Reflection.FieldInfo fNameLabel =
            AccessTools.Field(typeof(LevelSelectMenu), "weatherNameLabel");
        static readonly System.Reflection.FieldInfo fPrompt =
            AccessTools.Field(typeof(LevelSelectMenu), "weatherPrompt");
        static readonly System.Reflection.FieldInfo fPromptText =
            AccessTools.Field(typeof(NavSecondary), "labelString");

        // The prompt's scene-authored text, captured before we first rewrite it.
        static readonly Dictionary<int, string> originalPrompt = new Dictionary<int, string>();

        [HarmonyPostfix]
        public static void Postfix(LevelSelectMenu __instance)
        {
            if (fIndex == null || fLabel == null || fNameLabel == null) return;
            int index = (int)fIndex.GetValue(__instance);
            if (index < 0 || index >= LevelEntries.GetLevelCount()) return;
            bool can = LevelEntries.GetEntry(index).canChangeWeather;

            var label = fLabel.GetValue(__instance) as TextMeshProUGUI;
            var nameLabel = fNameLabel.GetValue(__instance) as TextMeshProUGUI;
            var prompt = fPrompt?.GetValue(__instance) as NavSecondary;

            // Without the prompt we can't show the setting inline, so leave the
            // readout to the game rather than hide the only place it shows.
            bool inline = prompt != null && fPromptText != null && label != null;
            if (label != null) label.enabled = can && !inline;
            if (nameLabel != null) nameLabel.enabled = can && !inline;
            if (!inline) return;

            int id = prompt.GetInstanceID();
            if (!originalPrompt.TryGetValue(id, out string baseText))
            {
                baseText = fPromptText.GetValue(prompt) as string ?? "";
                originalPrompt[id] = baseText;
            }

            if (prompt.gameObject.activeSelf != can) prompt.gameObject.SetActive(can);
            if (can) prompt.SetLabel(baseText + " (" + label.text + ")");
        }
    }
}
