using UnityEngine;

namespace TrickyMaddnessLevelHook
{
    // Implementations of the hooks Core/GameHooks.cs declares. A partial method can be
    // implemented only once, so every game's part of a hook lives here, side by side.
    // The four LoadLevelManager blocks are implemented in their own files.
    public partial class Plugin
    {
        static partial void ResetGameState()
        {
            AvalancheHazard.ResetState();
        }

        static partial void AttachGameMoverParts(Transform t, Animation anim, ref int hazards)
        {
            // Contact with this mover wrecks the rider. Flagged by a marker CHILD
            // rather than by the mover's own name, because the trigger join below
            // parses "MoveSurfTrig_<mover>_<trig>" positionally — decorating
            // "MoveSurf_<idx>" would silently unwire every trigger on the course.
            if (t.Find("MoverHazard") != null && t.GetComponent<AvalancheHazard>() == null)
            {
                t.gameObject.AddComponent<AvalancheHazard>().target = anim;
                hazards++;
            }
        }

        static partial void GameSnowActive(ref bool active)
        {
            active = active || SsxSnowFall.Active || Ssx3SnowFall.Active;
        }

        static partial void TrySpawnGameSnow(Transform t, ref string kind, ref bool done)
        {
            if (t.name.StartsWith("SnowSSX_"))
            {
                SpawnSsxSnow(t);
                kind = "ssx";
                done = true;
                return;
            }
            if (t.name.StartsWith("WeatherSSX3_"))
            {
                if (Ssx3SnowFall.TrySpawn(t, ssx3SnowFlakeSize.Value, out string why))
                    Log.LogInfo($"[SnowFall] SSX 3 weather snow spawned: {why}");
                else
                    Log.LogWarning($"[SnowFall] SSX 3 weather: {why}");
                kind = "ssx3";
                done = true;
            }
        }
    }
}
