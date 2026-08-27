#!/usr/bin/env python3
"""Assemble the standalone Level Hook release pack.

This is the mod that map packs (SSX Tricky Ports, SSX 3 Ports, SSX 2012 Ports,
SSX Blur Ports, ...) all depend on, and it used to be COPIED INTO every one of
those zips. That stopped being a good idea once the Hook started changing on
its own schedule (bugfixes, kept backward-compatible with old maps) faster than
any given map pack does -- every existing map-pack zip was shipping whatever
Hook version happened to exist the day it was built, with no way for someone
who already has a pack installed to pick up a Hook fix short of re-downloading
the whole map zip. Cutting the Hook loose onto its own release, versioned by
its own PLUGIN_VERSION, is the fix: grab this whenever it updates, no map
re-download required, because the Hook stays backward-compatible with every map
pack already shipped (that promise is why this decoupling is safe at all).

Version comes from PluginInfo.cs, not a CLI arg -- there is no world where the
pack should claim a version the DLL doesn't have.
"""
import io, os, re, shutil, subprocess, sys

REPO = "/Users/mtvogel/Downloads/claude_scratch/tricky_mods"
HOOK_SRC = os.path.join(REPO, "LevelHook-src")
HOOK_DLL = os.path.join(HOOK_SRC, "TrickyMaddnessLevelHook.dll")
MAKER_DLL = os.path.join(REPO, "GaribaldiRebuild/Assets/Plugins/TrickyMaddnessLevelMaker.dll")
README = os.path.join(HOOK_SRC, "release/README.txt")

m = re.search(r'PLUGIN_VERSION\s*=\s*"([^"]+)"', io.open(os.path.join(HOOK_SRC, "PluginInfo.cs"), encoding="utf-8").read())
if not m:
    sys.exit("could not read PLUGIN_VERSION from LevelHook-src/PluginInfo.cs")
VERSION = m.group(1)

# The DLL is what ships -- prove it actually carries this version rather than
# trusting that whoever last built it remembered to. Same `strings` exact-line
# check make_release.sh does for the course packs' README stamp.
if not os.path.isfile(HOOK_DLL):
    sys.exit("missing %s -- build it: LevelHook-src/build.sh --mac" % HOOK_DLL)
lines = subprocess.run(["strings", HOOK_DLL], capture_output=True, text=True).stdout.splitlines()
if VERSION not in lines:
    sys.exit("%s does not contain version %s -- rebuild it: LevelHook-src/build.sh --mac"
             % (os.path.basename(HOOK_DLL), VERSION))
if not os.path.isfile(MAKER_DLL):
    sys.exit("missing %s -- the Hook rebuilds levels from its Maker* types, "
              "this DLL is required alongside it" % MAKER_DLL)

# ⚠️ The README is COMMITTED at LevelHook-src/release/README.txt, same pattern as
# CharacterHook-src/release/ and ssx/release/. Refuse rather than pack stale prose.
if not os.path.isfile(README):
    sys.exit("missing %s -- the release prose is committed, not written into the pack" % README)
head = io.open(README, encoding="utf-8").readline()
if VERSION not in head:
    sys.exit("release/README.txt first line says %r but PluginInfo.cs says v%s -- update the prose"
              % (head.strip(), VERSION))

NAME = "Tricky Madness Level Hook v%s - Tricky Madness Mod" % VERSION
DEST = os.path.join(REPO, "Discord Releases", NAME)
if os.path.isdir(DEST):
    shutil.rmtree(DEST)
os.makedirs(os.path.join(DEST, "BepInEx/plugins"))

shutil.copy2(HOOK_DLL, os.path.join(DEST, "BepInEx/plugins", os.path.basename(HOOK_DLL)))
shutil.copy2(MAKER_DLL, os.path.join(DEST, "BepInEx/plugins", os.path.basename(MAKER_DLL)))
shutil.copy2(README, os.path.join(DEST, "README.txt"))

print("packed Level Hook v%s into %s" % (VERSION, DEST))
