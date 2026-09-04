Tricky Madness Level Hook v2.5.2 — required for every custom map pack
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

WHAT'S NEW IN v2.5.2
---------------------
  A fix for the starting gate. If you installed v2.5.1, please replace it with
  this one — v2.5.1 made this worse, not better.

  - The starting gate now lines up with the riders properly. The gate is a row
    of upright posts with eight slots between them, one per rider, and the Hook
    had always been dropping it at a fixed spot that didn't match where the
    game actually puts anybody — so it sat visibly off to one side on every
    custom map. v2.5.1 tried to fix that by centring the gate on the field,
    which happens to be right for eight riders but is exactly half a slot out
    for nine: every post ended up planted through a rider. It now snaps to the
    rider spacing instead, so posts always land between riders, at any number.

  - One thing that can't be fixed by moving it: the gate has eight slots and a
    full field is nine. The ninth rider starts just past the right-hand end.
    That's the prop the game itself ships, so the only options were "one rider
    stands off the end" or "stretch the gate and misalign all nine" — the Hook
    now also makes sure the rider left out is always a CPU racer and never you.

  Nothing here changes any map file, and the Hook stays compatible with every
  course pack already released. If you're on v2.5.0 or v2.5.1, this is a
  straight copy-over — no map re-download, no config changes.

PREVIOUSLY, IN v2.5.0
---------------------
  - AI riders now use the course's alternate routes. Every SSX game authored
    several racing lines per course (SSX 3 ships around 88, Merqury City 152),
    but the Hook was handing the CPU a single one, so the whole field ran the
    same line nose-to-tail. They now get the branches as well, with the race
    line kept underneath as a guaranteed fallback so nobody strands on a dead
    end. If you preferred the old single-file racing, or you hit anything odd
    with the AI, set MultiPathAI = false under [Gameplay] in
    BepInEx/config/TrickyMaddnessLevelHook.cfg — no rebuild needed.
  - New launch options for anyone testing or building courses: pass
    --tm-map <map name> and --tm-pin <x,y,z[,yaw]> on the command line and the
    game starts you at that spot on that map instead of at the start gate.
    Command-line only on purpose, so it can never quietly persist into a normal
    play session.

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
