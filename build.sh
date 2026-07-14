#!/bin/sh
# Rebuild TrickyMaddnessLevelHook.dll against the current game assemblies.
# Requires mono's mcs (brew install mono). Output lands in this folder; copy it
# to <game>/BepInEx/plugins/ to install.
#
# The non-obvious references (netstandard, UIModule, ImageConversionModule,
# LevelMaker) are all load-bearing — drop one and mcs fails. See ../CLAUDE.md.
set -e

# Defaults target the Windows (CrossOver) build. Override for the native Mac
# build with:  ./build.sh --mac    (or set GAME/MANAGED/CORE/MAKER env vars).
if [ "$1" = "--mac" ]; then
  GAME="${GAME:-/Users/mtvogel/Library/Application Support/Steam/steamapps/common/Tricky Madness}"
  MANAGED="${MANAGED:-$GAME/TrickyMadness.app/Contents/Resources/Data/Managed}"
else
  GAME="${GAME:-/Users/mtvogel/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Tricky Madness}"
  MANAGED="${MANAGED:-$GAME/Tricky Madness_Data/Managed}"
fi
CORE="${CORE:-$GAME/BepInEx/core}"
HERE="$(cd "$(dirname "$0")" && pwd)"
# The Maker DLL is platform-agnostic; fall back to the repo copy if the target
# build doesn't have it installed yet (e.g. a fresh Mac plugins dir).
MAKER="${MAKER:-$GAME/BepInEx/plugins/TrickyMaddnessLevelMaker.dll}"
if [ ! -f "$MAKER" ]; then
  MAKER="/Users/mtvogel/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Tricky Madness/BepInEx/plugins/TrickyMaddnessLevelMaker.dll"
fi

mcs -target:library -out:"$HERE/TrickyMaddnessLevelHook.dll" \
  "$HERE/Plugin.cs" "$HERE/PluginInfo.cs" \
  -r:"$MANAGED/Assembly-CSharp.dll" \
  -r:"$MANAGED/UnityEngine.dll" \
  -r:"$MANAGED/UnityEngine.CoreModule.dll" \
  -r:"$MANAGED/UnityEngine.UI.dll" \
  -r:"$MANAGED/UnityEngine.UIModule.dll" \
  -r:"$MANAGED/UnityEngine.IMGUIModule.dll" \
  -r:"$MANAGED/UnityEngine.TextRenderingModule.dll" \
  -r:"$MANAGED/UnityEngine.AssetBundleModule.dll" \
  -r:"$MANAGED/UnityEngine.PhysicsModule.dll" \
  -r:"$MANAGED/UnityEngine.ImageConversionModule.dll" \
  -r:"$MANAGED/Unity.TextMeshPro.dll" \
  -r:"$MANAGED/netstandard.dll" \
  -r:"$CORE/BepInEx.dll" \
  -r:"$CORE/0Harmony.dll" \
  -r:"$MAKER"

echo "Built: $HERE/TrickyMaddnessLevelHook.dll"
