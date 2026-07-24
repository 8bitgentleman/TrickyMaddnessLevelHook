namespace TrickyMaddnessLevelHook
{
    internal static class PluginInfo
    {
        public const string PLUGIN_GUID = "TrickyMaddnessLevelHook";
        public const string PLUGIN_NAME = "TrickyMaddnessLevelHook";
        // Bumped for the SSX ports release. 1.0.0 is what shipped publicly in
        // the Garibaldi Beta V3 and Ssx3 Ecourse packs — everything since (world
        // scale, rail colliders, physics props, wind/teleport/skydome, moving
        // surfaces, lightmap overlays, …) went out under that same number, so a
        // user's log could not tell those builds apart. Bump this on every
        // release from here on; the BepInEx boot line is the only version a bug
        // report carries for the Hook itself.
        public const string PLUGIN_VERSION = "2.0.0";
    }
}
