Tricky Madness Level Hook v2.8.2 — required for every custom map pack
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

WHAT'S NEW IN v2.8.2
---------------------
  Three releases' worth (2.8.0, 2.8.1, 2.8.2), nearly all of it about one
  problem: medals on custom courses were quietly wrong, and nothing in the menu
  told you.

  It is all DLL-only — no map re-download. But two of the changes below alter how
  courses you already have installed play (freestyle medals and the freestyle
  clock), so read those first.

  THINGS THAT CHANGE ON MAPS YOU ALREADY HAVE INSTALLED
  (no map re-download — just drop in the new DLL)

  - Freestyle medals have to be earned now. Every custom map shipped with its
    three freestyle score targets set to zero, and the game reads a zero target
    as "any score is gold" — so every freestyle run on every custom course has
    been an automatic gold from the moment you left the start gate. The Hook now
    works out real targets when the course loads, from how long the course is
    and how many pickups are on it. The one part that is exact rather than a
    guess: bronze is half of gold and silver is three-quarters of it, which
    holds to the point on all four of the game's own courses.

    Honest note: how high GOLD itself sits is our estimate, not recovered data.
    The game's own four courses don't follow any formula (gold tracks neither
    length nor clock time), so this is a judgment call that wants tuning against
    real play. Most converted courses have few or no pickups placed on them, so
    their targets land at the low end. Across the 166 converted courses we have,
    gold works out to about 200,000 for the middle one, 100,000 for the lowest
    and 2.1 million for the highest.

  - Medal times fit the course. The old medal times were computed from the
    map's build scale — a conversion factor that says nothing about how long the
    course is — so every 2.5x-scale course got the same 337-second bronze
    whether its route was 610 units long or 84,667, and the X-Games courses got
    a gold time of over five hours. The Hook now times the actual route between
    the start and the finish.

    Two details worth knowing. It only measures the stretch you really ride:
    SSX 3's events start 38 to 60 percent of the way down their own line, and
    one SSX 2012 course's recorded line laps the course twice, which had been
    handing those courses a gold time nobody could reach. And a Time Trial has
    no clock — these times are medal targets only; nothing cuts your run short.

  - The freestyle clock is now the one each course asks for. This is the thing
    2.7.1 deliberately held back (see PREVIOUSLY below). What let it out: the
    clock can no longer get SHORTER than the game's stock 150 seconds, so short
    courses run exactly as before, and only the long ones — the ones that ran
    out of time halfway down the mountain — get more. Across the 166 courses,
    51 sit at 150 seconds and are unchanged; the very longest converted course
    comes out at about 47 minutes.

    Honest note: those clock lengths are calculated from route length, not
    played at that length. They only ever go up from 150 seconds, but if one
    feels absurd, that is data we want.

  - The X-Games courses' scrolling soft-edged planes actually scroll. Those
    planes use one of the game's particle shaders, which ignores the
    texture-offset the Hook was writing, so the scroll was recorded correctly
    and drew nothing. For renderers that use ONLY that shader, the Hook now
    shifts the mesh's own UVs instead. Every other course is untouched.

  WHAT IT WILL AND WON'T TOUCH

  The Hook only rewrites a course's times or score targets when the course still
  carries the old placeholder values. A course that was rebuilt with real values,
  or tuned by hand, is left exactly as it is. A course with no race line to
  measure is also left alone, with a warning in BepInEx/LogOutput.log. Look for
  lines starting [TimeBudget] to see what the Hook decided for any given course.

  THINGS THAT NEED A COURSE THAT DECLARES IT

  - A per-course pace. Gold time used to assume one riding speed for every
    course (45 units a second, fitted to the game's own courses). It's really
    about 45 for SSX Tricky-style courses and about 70 for the X-Games ones —
    which is how X-Games Falps ended up with a 3:01 gold on a course its first
    tester finished in 1:43. A course's map file can now carry its own measured
    pace, and the Hook uses it. Courses that don't carry one fall back to the
    flat 45, exactly as above. The Hook ignores any pace outside 5 to 500 as a
    typo rather than a fast course.

  FOR PEOPLE BUILDING COURSES

  - You can pin any of these yourself in a course's Overrides.json
    (gameplay.goldTime, goldPoints and their silver/bronze siblings,
    gameplay.freestyleTime), and a pinned value is used as given — including a
    freestyle clock shorter than 150 seconds, since pinning is a deliberate act.
    A zero or negative pin is refused at build time: a gold time of zero can't
    be rescued by the Hook later, and it would ship an unwinnable medal.

  COMPATIBILITY

  No map file changes, no config changes, and the Hook still works with every
  course pack ever released — no re-download of anything.

  But be aware that this one changes how courses you already have installed
  play: freestyle stops being an automatic gold, and long courses get a longer
  freestyle clock. Both are corrections, but you'll notice them.

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
