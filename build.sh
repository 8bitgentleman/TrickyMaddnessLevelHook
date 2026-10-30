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
  GAME="${GAME:-$HOME/Library/Application Support/Steam/steamapps/common/Tricky Madness}"
  MANAGED="${MANAGED:-$GAME/TrickyMadness.app/Contents/Resources/Data/Managed}"
else
  GAME="${GAME:-$HOME/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Tricky Madness}"
  MANAGED="${MANAGED:-$GAME/Tricky Madness_Data/Managed}"
fi
CORE="${CORE:-$GAME/BepInEx/core}"
HERE="$(cd "$(dirname "$0")" && pwd)"
# The Maker DLL is platform-agnostic; fall back to the repo copy if the target
# build doesn't have it installed yet (e.g. a fresh Mac plugins dir).
MAKER="${MAKER:-$GAME/BepInEx/plugins/TrickyMaddnessLevelMaker.dll}"
if [ ! -f "$MAKER" ]; then
  MAKER="$HOME/Library/Application Support/CrossOver/Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/Tricky Madness/BepInEx/plugins/TrickyMaddnessLevelMaker.dll"
fi
# Nothing compiles without the game's assemblies and the Maker DLL, and mcs's
# own error for a missing -r: is a wall of CS0006 -- say which input is absent.
for f in "$MANAGED/Assembly-CSharp.dll" "$CORE/BepInEx.dll" "$MAKER"; do
  if [ ! -f "$f" ]; then
    echo "build.sh: missing $f -- set GAME (or MANAGED/CORE/MAKER) to your install" >&2
    exit 1
  fi
done

# Compile every .cs under this folder, so a new file can't be left out of the
# build by forgetting to list it here. Plugin.cs and PluginInfo.cs go first,
# then the rest sorted, so the type order (which sets Harmony's order for
# same-priority patches) only changes when files do. POSIX sh has no arrays,
# hence the positional-parameter list.
set -- "$HERE/Plugin.cs" "$HERE/PluginInfo.cs"
while IFS= read -r f; do
  case "$f" in
    ''|./Plugin.cs|./PluginInfo.cs) continue ;;
  esac
  set -- "$@" "$HERE/${f#./}"
done <<EOF
$(cd "$HERE" && find . -name '*.cs' -not -path './release/*' -not -path './obj/*' | LC_ALL=C sort)
EOF

mcs -target:library -out:"$HERE/TrickyMaddnessLevelHook.dll" \
  "$@" \
  -r:"$MANAGED/Assembly-CSharp.dll" \
  -r:"$MANAGED/UnityEngine.dll" \
  -r:"$MANAGED/UnityEngine.CoreModule.dll" \
  -r:"$MANAGED/UnityEngine.UI.dll" \
  -r:"$MANAGED/UnityEngine.UIModule.dll" \
  -r:"$MANAGED/UnityEngine.IMGUIModule.dll" \
  -r:"$MANAGED/UnityEngine.TextRenderingModule.dll" \
  -r:"$MANAGED/UnityEngine.AssetBundleModule.dll" \
  -r:"$MANAGED/UnityEngine.PhysicsModule.dll" \
  -r:"$MANAGED/UnityEngine.AnimationModule.dll" \
  -r:"$MANAGED/UnityEngine.ParticleSystemModule.dll" \
  -r:"$MANAGED/AK.Wwise.Unity.API.dll" \
  -r:"$MANAGED/UnityEngine.ImageConversionModule.dll" \
  -r:"$MANAGED/UnityEngine.InputLegacyModule.dll" \
  -r:"$MANAGED/Unity.RenderPipelines.Core.Runtime.dll" \
  -r:"$MANAGED/Unity.RenderPipelines.Universal.Runtime.dll" \
  -r:"$MANAGED/Unity.TextMeshPro.dll" \
  -r:"$MANAGED/Unity.Splines.dll" \
  -r:"$MANAGED/Unity.Mathematics.dll" \
  -r:"$MANAGED/netstandard.dll" \
  -r:"$CORE/BepInEx.dll" \
  -r:"$CORE/0Harmony.dll" \
  -r:"$MAKER"

echo "Built: $HERE/TrickyMaddnessLevelHook.dll"
