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
        // True for anything living under an AnimDoor_* / MoveSurf_* subtree. Those
        // GOs are emitted by the importer's animDoors / movingColliders passes and
        // their part colliders ARE the gameplay: the door is meant to block the
        // rider until its trigger opens it. The bundle nests parts one level deep,
        // but walk the whole chain so a future importer change can't quietly
        // reintroduce the pass-through.
        static bool IsAnimatedSurfacePart(Transform t)
        {
            for (var cur = t; cur != null; cur = cur.parent)
                if (cur.name.StartsWith("AnimDoor_") || cur.name.StartsWith("MoveSurf_"))
                    return true;
            return false;
        }
    }
}
