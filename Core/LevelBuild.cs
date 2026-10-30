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
    public partial class Plugin
    {
        // What LoadLevelManager's blocks share: the three maker objects found up
        // front, the LevelManager once it is instantiated, and the race line once
        // built (the respawn markers are sampled off it). Nothing else crosses blocks.
        internal sealed class BuildContext
        {
            public MakerLevelData Maker;
            public MakerSpawnPoint SpawnPoint;
            public MakerRespawnPoints RespawnPoints;
            public LevelManager LevelManager;
            public RaceLine RaceLine;
        }

        public static void LoadLevelManager()
        {
            LoadClock.Mark("Unity scene load (bundle -> MakerLevelData.Awake)");
            var ctx = FindMakers();

            // Order: pin -> ground-snap -> gate -> launch redirect. The pin moves the start before anything reads it (§25).
            ApplyTestPin(ctx);

            SnapStartMarkers(ctx);
            ApplyLevelEntry(ctx);

            PlaceStartingGate(ctx);

            AddStartLaunchRedirect(ctx);

            //Insert Level Manager
            var LevelManagerObject = Resources.Load<GameObject>("game/levelmanager");
            var gameObject = Instantiate(LevelManagerObject, ctx.Maker.transform);

            var levelManager = gameObject.GetComponent<LevelManager>();
            ctx.LevelManager = levelManager;

            //Set Level Manager Data Points
            SetGameField(fStartingGate, "LevelManager.startingGate", levelManager, ctx.SpawnPoint.transform);
            SetGameField(fRespawnPointsParent, "LevelManager.respawnPointsParent", levelManager, ctx.RespawnPoints.transform);
            SetGameField(fSnowflakes, "LevelManager.snowflakes", levelManager, new GameObject("Temp")); //FIX to get all snowflakes and apply

            //Create UI Hooks
            var TempObject = GetGameField(fSpeedLabel, "LevelManager.speedLabel", levelManager) as TextMeshProUGUI;
            var CanvasObjects = FindObjectsByType(typeof(MakerCanvas), FindObjectsSortMode.None) as MakerCanvas[];
            for (int i = 0; i < CanvasObjects.Length; i++)
            {
                GameObject gameObject1 = Instantiate(TempObject.transform.gameObject, levelManager.transform.GetChild(0));
                gameObject1.SetActive(true);
                gameObject1.GetComponent<TextMeshProUGUI>().text = "NewText";
                CanvasObjects[i].Generate(gameObject1.GetComponent<TextMeshProUGUI>());
            }

            ConvertRails(ctx);

            BuildAIPaths(ctx);

            // Before the respawn markers, which are sampled off ctx.RaceLine.
            BuildRaceLine(ctx);

            BuildRaceLineRespawns(ctx);

            // One count per scene-scan block, logged unconditionally at the end in call
            // order, so a block that stops running (or moves) shows in a log diff even on
            // a map where it finds nothing. The per-block lines above only fire on > 0.
            var blocks = new List<string>();
            StripGlassColliders(ctx, blocks);

            ConvertPhysProps(ctx, blocks);

            // Before LowerScaledGems, which only sees the flakes once they carry Snowflake.
            ConvertSnowflakes(ctx, blocks);
            LowerScaledGems(ctx.Maker.gameObject.scene);

            AttachWindVolumes(ctx, blocks);

            AttachTeleportVolumes(ctx, blocks);

            AttachLaunchVolumes(ctx, blocks);

            AttachTriggerAnims(ctx, blocks);

            AttachAnimDoors(ctx, blocks);

            AttachMovingSurfaces(ctx, blocks);

            AttachParticleTriggers(ctx, blocks);

            // SkyFill -> DrawDist -> Skydome: SkydomeFollow samples the fill on enable, and both hook beginCameraRendering in subscription order.
            ReadSkyFill(ctx, blocks);

            AttachDrawDistance(ctx, blocks);

            AttachSkydomeFollow(ctx, blocks);

            AttachUVScrollers(ctx, blocks);

            WireDestructibles(ctx, blocks);

            SpawnFallingSnow(ctx, blocks);

            AttachGlowSprites(ctx, blocks);

            EnableBloom(ctx, blocks);

            SpawnSsx3Painter(ctx);

            // After the skydome and terrain exist: the ramp drives their materials.
            SpawnLightingRamp(ctx, blocks);
            Log.LogInfo("[LevelBuild] blocks: " + string.Join(" ", blocks));

            // Last: it belongs to the scene the player rides, and dies with it.
            SpawnPerfHarnessIfArmed();

            // NB: the open-top sky-fill is started from OnSceneLoaded (not here) —
            // SolidColorSky captures the ACTIVE scene name, which isn't the custom scene
            // yet at LoadLevelManager time, so starting it here exits immediately.
            LoadClock.Mark("LoadLevelManager tail");
        }
    }
}
