using System.Collections.Generic;
using UnityEngine;

namespace TrickyMaddnessLevelHook
{
    // The one place Core/ and Features/ reach into source-game code. Each hook is a
    // partial method: Games/ implements it, and with Games/ deleted the compiler drops
    // the call (arguments included), so core still builds and simply does nothing
    // game-specific. Partial methods must return void and take no `out` parameters —
    // results come back through `ref`.
    public partial class Plugin
    {
        // LoadLevelManager's game blocks, called in place in the build order.
        static partial void StripGlassColliders(BuildContext ctx, List<string> blocks);
        static partial void LowerScaledGems(UnityEngine.SceneManagement.Scene scene);
        static partial void AttachDrawDistance(BuildContext ctx, List<string> blocks);
        static partial void SpawnSsx3Painter(BuildContext ctx);

        // Per-level reset of game-side static state, beside the core resets.
        static partial void ResetGameState();

        // A MoveSurf_ mover found by AttachMovingSurfaces; a game may add its own
        // component to it (and count it in `hazards`).
        static partial void AttachGameMoverParts(Transform t, Animation anim, ref int hazards);

        // Falling snow: whether a game's snow system is already running, and a chance
        // to claim a marker before the TM SnowFX_ fallback. Set `done` to stop the scan.
        static partial void GameSnowActive(ref bool active);
        static partial void TrySpawnGameSnow(Transform t, ref string kind, ref bool done);
    }

    // Fog that a game's own system drives, read by the Features that would otherwise
    // set fog themselves. Written by Games/ (today only Ssx3Painter); all zero = none.
    public static class GameFog
    {
        // True while a game system owns RenderSettings fog; LightingRamp stands aside.
        public static bool Owned;
        // The sky's share of the fog, read by SkydomeFollow for the unfogged clear colour
        // above the dome's open rim (Unity fogs the dome mesh but never the clear colour).
        // 0 = no game fog.
        public static float SkyAmount;
        public static Color SkyColor;
    }
}
