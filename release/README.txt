Tricky Madness Level Hook v2.4.0 — required for every custom map pack
=======================================================================

This is the plugin that makes custom map packs (SSX Tricky Ports, SSX 3 Ports,
SSX 2012 Ports, SSX Blur Ports, and any other course pack posted for this
game) actually playable: it teaches Tricky Madness how to load a course that
isn't one of the game's own, remaps materials so they render correctly, and
adds the physics/collision fixes those maps rely on.

  THIS ZIP HAS NO MAPS IN IT. Grab a course pack separately — this is just the
  plugin every one of them needs sitting in your BepInEx/plugins folder.

  >>> ALREADY HAVE A MAP PACK INSTALLED? <<<
  Older map-pack zips used to include their own copy of this Hook. This one is
  now the single, current copy — newer than whatever came bundled in any pack
  you already downloaded, and it stays backward-compatible with every map pack
  ever released, so updating it doesn't break anything you already have
  installed. If a map pack you download in the future doesn't include a
  plugins/ folder, this is where its Hook comes from.

WHAT'S NEW IN v2.4.0
---------------------
  - Fixes a respawn loop that could leave you stuck cycling through a reset
    instead of landing.
  - Adds a guard for getting stuck airborne indefinitely.
  - Stops thin-surface handling from deleting the collision on tree trunks.

  All three apply to every map, old and new — there's no reason to stay on an
  older Hook once you've got this one.

WHAT'S IN THIS ZIP
------------------
  BepInEx/plugins/TrickyMaddnessLevelHook.dll    <- the Hook itself
  BepInEx/plugins/TrickyMaddnessLevelMaker.dll   <- runtime types the Hook
                                                     rebuilds levels from;
                                                     required alongside it

  Drop the BepInEx folder on top of your Tricky Madness install and let it
  merge — that's the whole install.

INSTALL
-------
  1. Install BepInEx 5.4.x if you don't have it (built and tested against
     5.4.23.2), and run the game once so it creates its BepInEx folder.
     (Windows note: BepInEx injects through winhttp.dll — if you're running
     the game through Wine/Proton you need a native winhttp DLL override, or
     BepInEx silently never starts.)
  2. Copy both .dll files from this zip's BepInEx/plugins/ into your game's
     BepInEx/plugins/ folder, overwriting any older
     TrickyMaddnessLevelHook.dll / TrickyMaddnessLevelMaker.dll you have.
     Don't keep an old one alongside this one.
  3. Launch the game once, then quit. This creates (or reuses) a Maps folder:
       - Windows: next to the game's .exe
       - macOS:   next to run_bepinex.sh
  4. Go install a course pack — copy its Maps/ contents into that same
     folder. This zip doesn't ship any maps itself.

REQUIREMENTS
------------
  - Tricky Madness
  - BepInEx 5.4.x (built and tested against 5.4.23.2)

VERSIONS
--------
  This is a BEPINEX BOOT-LINE version — it's the only version number the game
  itself surfaces, so if you're reporting a problem with any map, include the
  Hook version from your BepInEx/LogOutput.log along with that map's own
  version (recorded in its .aipaths.txt / the course pack's README).

CREDITS
-------
  Tricky Madness Level Hook / Level Maker originally by GlitcherOG, rebuilt
  and maintained here against the current game version.
