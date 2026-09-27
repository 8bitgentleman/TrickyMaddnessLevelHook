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
        // report carries for the Hook itself. That reasoning also makes it a
        // BEHAVIOUR number, not just a ship number: a feature that changes how a
        // map plays (2.2.0's per-map physics rate) has to be tellable from a build
        // without it, or the first "the chain feels different" report is
        // unattributable in exactly the way 1.0.0 was.
        public const string PLUGIN_VERSION = "2.16.0";
    }
}
