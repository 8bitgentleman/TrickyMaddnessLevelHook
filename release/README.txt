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

HOW TO INSTALL
--------------
  Three parts, about 10 minutes the first time. Part 1 is the same in every
  Tricky Madness mod's readme and you only ever do it once.

---------------------------------------------------------------------------
 PART 1 - INSTALL BEPINEX  (one time only)
---------------------------------------------------------------------------
  BepInEx is the free, widely used mod loader that every Tricky Madness mod
  runs on. You install it once; after that, adding any mod is just copying
  files. Already have a Tricky Madness mod working? Skip to Part 2.

  Use BepInEx 5.4.23.5, from the links below. Not BepInEx 6 - that is a
  different loader and these mods will not load in it.

  STEP 1 - Find your GAME FOLDER
    In Steam: Library -> right-click Tricky Madness -> Manage ->
    Browse local files. The window that opens is your GAME FOLDER - every
    step in this readme means that folder. Leave it open.
      Windows: it has "Tricky Madness.exe" in it (Windows may hide the
               ".exe" and just show "Tricky Madness").
      Mac:     it has the "TrickyMadness" game app in it.

  ...then follow EITHER the Windows OR the Mac steps:

  WINDOWS
  -------
  STEP 2 - Download BepInEx. This link downloads the right file directly:
      https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip
    (It comes from BepInEx's official GitHub page, listed here:
      https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5 )

  STEP 3 - Double-click the downloaded zip to open it. Select everything
    inside it (Ctrl+A) - a "BepInEx" folder, "winhttp.dll",
    "doorstop_config.ini" and "changelog.txt" - and copy it into your
    GAME FOLDER.
      CHECK: "winhttp.dll" must sit right next to "Tricky Madness.exe",
      not inside another folder. If your game folder now has a folder
      called "BepInEx_win_x64_5.4.23.5", open it and move everything in it
      up into the game folder.

  STEP 4 - Start the game from Steam like normal. Wait for the main menu,
    then quit.

  Steam Deck / Linux - UNTESTED. Nobody has tried these mods on a Steam
  Deck yet. If you want to try: do steps 2-4 above, and also set
  Steam -> right-click Tricky Madness -> Properties -> Launch Options to
      WINEDLLOVERRIDES="winhttp=n,b" %command%
  Without that, BepInEx most likely never starts. Please report back
  whether it worked either way.

  MAC
  ---
  STEP 2 - Download BepInEx. This link downloads the right file directly:
      https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_macos_universal_5.4.23.5.zip
    (It comes from BepInEx's official GitHub page, listed here:
      https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5 )
    "universal" means it works on every Mac, Apple Silicon or Intel.

  STEP 3 - Double-click the downloaded zip (in Downloads) to unpack it.
    Open the folder it makes, select everything inside (Cmd+A) and drag it
    into your GAME FOLDER. You should now see "run_bepinex.sh" and
    "libdoorstop.dylib" sitting next to the TrickyMadness app.

  STEP 4 - One-time setup in Terminal. BepInEx has to be told the game's
    name, and macOS has to be told the files are safe to run. This is two
    copy-and-pastes:
      a) Open Terminal: press Cmd+Space, type Terminal, press Return.
      b) Paste this line and press Return:

   cd ~/"Library/Application Support/Steam/steamapps/common/Tricky Madness"

         If it says "no such file or directory", your Steam games live
         somewhere else. Type  cd  and a space, then go to your GAME
         FOLDER in Finder, press Cmd+Up Arrow to go up one folder, drag the
         "Tricky Madness" folder onto the Terminal window, and press Return.
      c) Paste this line and press Return:

   sed -i '' 's/^executable_name=.*/executable_name="TrickyMadness.app"/' run_bepinex.sh && chmod +x run_bepinex.sh && xattr -dr com.apple.quarantine . && echo "\"$PWD/run_bepinex.sh\" %command%"

         It prints one line that starts with a " and ends with %command%.
         Copy that whole line. You need it for the next step.

  STEP 5 - Make Steam's Play button start the game with mods. In Steam:
    right-click Tricky Madness -> Properties -> General -> Launch Options,
    and paste the line from step 4c into that box. (To play without mods
    later, clear the box. To play with mods again, redo step 4b and 4c
    to get the line back.)

  STEP 6 - Press Play in Steam. Wait for the main menu, then quit.

    If Play starts the game but step "CHECK" below fails, skip the Steam
    Launch Options and start the game from Terminal instead, every time:
    open Terminal, do step 4b again, then paste  ./run_bepinex.sh  and
    press Return.

  COPYING RULE ON A MAC - this matters for every mod after this one.
    If you drag a folder onto a folder that has the same name, Finder asks
    whether to "Replace" it. Replace DELETES everything that was already
    in there (your other mods, your other maps). Never click Replace on a
    folder. Instead, open the folder and drag the things inside it. (Or
    hold Option while you drag and click "Merge" - that keeps both.)
    Windows merges folders on its own, so this does not apply there. If
    Windows asks whether to replace a FILE, say yes.

  CHECK - did BepInEx install?
    Open the "BepInEx" folder in your GAME FOLDER. After starting the game
    once, it should have a "config" folder, a "plugins" folder and a file
    called "LogOutput.log". If they are not there, BepInEx did not start:
      Windows: winhttp.dll is not next to Tricky Madness.exe (step 3).
      Mac:     step 4 or step 5 did not take - redo them, or use the
               Terminal fallback in step 6.

  (end of Part 1)

---------------------------------------------------------------------------
 PART 2 - INSTALL THE LEVEL HOOK
---------------------------------------------------------------------------
  STEP 1 - Open this zip, then its "BepInEx" folder, then its "plugins"
    folder. There are two files: TrickyMaddnessLevelHook.dll and
    TrickyMaddnessLevelMaker.dll.

  STEP 2 - Copy both files into your GAME FOLDER -> BepInEx -> plugins.
    If asked whether to replace older copies, say yes. Don't keep two
    copies side by side (e.g. one renamed "... (1).dll").

  STEP 3 - Start the game (on a Mac, the way you set up in Part 1), wait
    for the main menu, then quit. This creates a folder called "Maps" in
    your GAME FOLDER. That is where courses go.

  STEP 4 - Get a course pack (a separate download - this zip has no
    courses) and follow Part 3 of ITS readme to copy its courses into
    that Maps folder.

---------------------------------------------------------------------------
 PART 3 - CHECK IT WORKED
---------------------------------------------------------------------------
  - Your GAME FOLDER now has a "Maps" folder.
  - Open GAME FOLDER -> BepInEx -> LogOutput.log in a text editor (Notepad
    or TextEdit) and search for  Loading [TrickyMaddnessLevelHook  - it
    should be followed by the version number at the top of this readme.
  - No Maps folder, or no such line? The DLLs are in the wrong folder
    (they go in BepInEx -> plugins, not in BepInEx itself), or BepInEx
    isn't running - redo the CHECK at the end of Part 1.

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
  - BepInEx 5.4.23.5 - see Part 1 of HOW TO INSTALL

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
