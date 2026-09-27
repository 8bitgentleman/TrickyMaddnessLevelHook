Tricky Madness Level Hook v2.16.0 — required for every custom map pack
========================================================================

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

WHAT IT DOES
------------
  - Loads custom course packs and gets them running in Tricky Madness's normal
    menu and level flow.
  - Fixes materials that would otherwise render bright pink or blocky, caused
    by shaders built for a different graphics API than the one your OS uses.
  - Adds proper sky handling for custom maps — a plain-color fallback when a
    course has no sky of its own, and full custom sky domes when it does.
  - Plays animated textures (waterfalls, banners, screens) at the speed they
    were authored to scroll, instead of sitting still.
  - Adds a day/night-style lighting change over long multi-peak descents,
    instead of one flat lighting look for the whole run.
  - Builds working grind rails, since course packs don't ship with rail
    collision built in.
  - Draws glowing halos around lamps, signs and light sources on courses whose
    packs carry that data.
  - Simulates wind zones, teleport zones, and launch pads that a course's
    level data defines.
  - Makes breakable props and glass, moving obstacles (kickers, pinball-style
    bumpers), and avalanche hazards actually interact with your rider.
  - Lets a course use different gravity, physics speed, or overall scale than
    the original game it came from.
  - Guards against riders getting stuck in endless respawn loops or wedged in
    the air.
  - Sets medal times, freestyle score targets and the freestyle clock from how
    long each converted course actually is, instead of one number for all of them.
  - Gives AI racers a course's alternate racing lines instead of making every
    CPU rider run nose-to-tail on one single line.
  - Shows a proper thumbnail image for each course on the level-select screen,
    with an optional grid layout (grouped by source game) for once you've got
    a lot of custom courses installed.

WHAT'S NEW IN v2.16.0
---------------------
  Eight releases' worth (2.9.0 through 2.16.0). Most of it is new things the
  Hook can draw for a course that asks for them, so it only shows up on maps
  built for it: SSX Tricky Ports v1.4, SSX 3 Ports v1.5 and SSX On Tour Ports
  v1.2 are the first packs that do. The last item fixes every custom map.

  ON EVERY CUSTOM MAP (no map re-download)

  - Every rider's start position is placed on the snow. Only the middle of the
    starting grid used to be, so on a course whose start sits on a slope a CPU
    rider's lane could be inside the hill. On SSX 3's The Throne one fell
    through the mountain at the start of every race, got reset 92% of the way
    down, and "won" in 45 seconds.
  - A rider who falls out of the world is put back where they last touched the
    snow. The game respawns you at the nearest marker to wherever you ended up,
    and after a long fall that can be near the bottom of the course. Turn it
    off with RespawnFallClamp = false under [Gameplay] if it ever misbehaves.

  NEW THINGS A COURSE CAN ASK FOR

  - Fireworks and one-shot effects that fire as you ride past, with sound
    (TM's own firework sound — SSX's sound ids don't exist in this game).
  - Props that react every time you hit them: hydrants spray, bumpers spark,
    balloons burst. A prop can also swap to its broken model when it fires.
  - SSX Tricky's own snowfall, including its per-race chance of snow.
  - A per-course draw distance, so a course can fade out where the original
    game stopped drawing instead of at TM's much longer default.
  - SSX 3's weather, fog and colour grade changing zone by zone as you ride,
    with the game's own fade between zones. The fog's fall-off was measured
    from the PS2 itself.

  NEW SETTINGS (BepInEx/config/TrickyMaddnessLevelHook.cfg)

  - [SnowFall] Ssx3FlakeSize (default 5) and SsxFlakeSize (default 1): the size
    of the snowflakes. Everything else about the snow comes from the game's
    data, but the unit the games give flake size in isn't known, so this is
    the one guessed number. SSX 3's default of 5 was picked by riding; SSX
    Tricky's 1 has not been tuned. Raise it if you can't see the snow, lower it
    if the flakes look like blobs.
  - [Visual] Ssx3WorldPainter (default on): set false to go back to one fog for
    the whole run and no colour grade on SSX 3 maps.
  - [Visual] Ssx3FogReach: how far away SSX 3's fog reaches full strength. 0
    (the default) matches the PS2.

  COMPATIBILITY

  Still works with every course pack ever released. Older packs just don't ask
  for any of the new effects, so apart from the start and respawn fixes above
  they play exactly as they did.

PREVIOUSLY, IN v2.8.2
---------------------
  (2.8.0, 2.8.1 and 2.8.2.)
  - Freestyle medals have to be earned. Every custom map shipped with zero
    score targets, which the game reads as "any score is gold"; the Hook now
    works them out from course length and pickups (how high gold sits is our
    estimate).
  - Medal times are timed off the actual route instead of the map's scale.
  - The freestyle clock grows on long courses; it never drops below the
    game's stock 150 seconds.
  - Courses can declare their own riding pace, and you can pin any of these
    values yourself in a course's Overrides.json.
  - X-Games' scrolling soft-edged planes actually scroll.

PREVIOUSLY, IN v2.7.1
---------------------
  (This was three releases' worth of work — 2.5.3, 2.6.0, 2.7.0 and 2.7.1.)
  - Fences, signs, billboards and barriers are solid again. The pass that strips
    collision off breakable glass had also been removing it from chain-link
    fences, road signs and bridge supports, so you could ride straight through
    them. It now only touches props whose name says glass or window: about
    14,100 objects across all 159 courses are solid again, and 132 still get
    the treatment.
  - Animated doors block you until they open (their collision was being removed
    the same way).
  - CPU riders stop stalling and teleporting on the big converted courses: the
    game's "is there a usable racing line near me" distance never scaled with
    the course, so a CPU rider who drifted off the line was respawned over and
    over. The game's own four courses are unaffected.
  - Avalanche debris wrecks you on the two SSX 3 courses that have one. What
    being hit costs is OUR decision, not recovered game behaviour: SSX 3 keeps
    its avalanche rules in data nobody has decoded.
  - New, needing a course pack built after that release: glowing halos on lamps
    and signs, one-shot launch pads, and short display names on the level-select
    screen. Heads-up on the last: the game files your best times under the name
    shown on the tile, so a pack that RENAMES a course you have already played
    won't find your old times (they aren't deleted, they just don't show).
  - New setting RiderTelemetry, under [Debug] in
    BepInEx/config/TrickyMaddnessLevelHook.cfg, off by default: records what
    your rider did, every physics step, to a CSV next to the game. --tm-map and
    AutoLoadMap also accept a map's filename and ignore capitalisation.

PREVIOUSLY, IN v2.5.2
---------------------
  - The starting gate lines up with the riders. The gate is a row of upright
    posts with eight slots between them, one per rider, and the Hook had been
    dropping it at a fixed spot matching no field size, so it sat visibly off
    to one side on every custom map. It now snaps to the rider spacing, so
    posts land between riders at any number. One limit that moving it cannot
    fix: the prop has eight slots and a full field is nine, so the ninth rider
    starts just past the right-hand end — the Hook makes sure the one left out
    is always a CPU racer and never you.
  - (If you somehow still have v2.5.1, this replaced it; v2.5.1 was withdrawn.)

PREVIOUSLY, IN v2.5.0
---------------------
  - AI riders use the course's alternate routes. Every SSX game authored
    several racing lines per course (SSX 3 ships around 88, Merqury City 152),
    but the Hook was handing the CPU a single one, so the whole field ran the
    same line nose-to-tail. They now get the branches as well, with the race
    line kept underneath as a guaranteed fallback so nobody strands on a dead
    end. If you preferred the old single-file racing, or you hit anything odd
    with the AI, set MultiPathAI = false under [Gameplay] in
    BepInEx/config/TrickyMaddnessLevelHook.cfg — no rebuild needed.
  - Launch options for anyone testing or building courses: pass
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

TROUBLESHOOTING: A CUSTOM MAP HANGS OR THE SKY LOOKS WRONG
------------------------------------------------------------
  If you've already had to do this for another Tricky Madness mod (a
  character mod is the one confirmed to need it), it's worth doing for the
  Hook too before you report a problem — they lean on the same BepInEx
  behavior.

  A few of the Hook's running features — the one that watches and corrects
  the sky color, and the one that catches a custom level's load if it stalls
  instead of finishing — stay attached to an object BepInEx itself creates
  and is supposed to protect across every scene change. On some Windows
  setups the game destroys that object anyway, and when it does, those
  features just quietly stop running: nothing in the log says so, a stuck
  load never gets rescued, and the sky can end up the wrong color.

  THE FIX:
    1. Close the game.
    2. Open BepInEx/config/BepInEx.cfg in a text editor (Notepad is fine).
    3. Find the [Chainloader] section and set:
         HideManagerGameObject = true
    4. Save and relaunch.

  Honest note: we haven't had a report that pins a Hook problem on this
  specifically, the way we have for the character mod. It's cheap to rule
  out, and if updating to the latest Hook version doesn't fix a hang on a
  custom map, check this next before filing a bug.

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
