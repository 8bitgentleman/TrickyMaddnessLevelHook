Tricky Madness Level Hook v2.7.1 — required for every custom map pack
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
  - Gives AI racers a course's alternate racing lines instead of making every
    CPU rider run nose-to-tail on one single line.
  - Shows a proper thumbnail image for each course on the level-select screen,
    with an optional grid layout (grouped by source game) for once you've got
    a lot of custom courses installed.

WHAT'S NEW IN v2.7.1
---------------------
  A big one — this is three releases' worth of work (2.5.3, 2.6.0, 2.7.0 and
  2.7.1) rolled into the first Hook update since September 4th.

  Most of it needs nothing from you but the new DLL. Where something only shows
  up on a course pack built after this, it says so.

  THINGS THAT CHANGE ON MAPS YOU ALREADY HAVE INSTALLED
  (no map re-download — just drop in the new DLL)

  - Fences, signs, billboards and barriers are solid again. The Hook strips
    collision off thin upright panels so that smashable glass bounces you
    instead of stopping you dead — but "thin upright panel" also describes a
    chain-link fence, a road sign and a bridge support, so it had been quietly
    removing collision from those too, and you could ride straight through
    them. It now only does that to props whose name actually says glass or
    window. Swept across all 159 courses: 132 objects still get the treatment,
    and roughly 14,100 that used to be ghosts are solid again.

    Worth knowing: if you had found a shortcut that ran through a fence, it
    isn't there any more. That was a bug, not a feature, but it was your bug
    and now it's gone.

  - Animated doors block you until they open. Same cause as above — the door
    panels were having their collision removed at load, so the door would
    animate perfectly while you rode through it. All 48 door parts on the
    course that uses them work now.

  - CPU riders stop stalling and teleporting on the big converted courses.
    The game decides whether a racing line is usable by checking whether any
    point on it is within 150 units — a number that made sense for its own
    courses, but the converted ones are built at two and a half to five times
    that size, so a CPU rider who drifted off the line could see no path at
    all, and the game's own stuck-handler would respawn them. Over and over.
    That distance now scales with the course, so they just carry on. The
    game's own four courses run at normal scale and are unaffected.

  - Avalanche debris actually wrecks you. On the two SSX 3 courses with an
    avalanche, the rocks used to pass straight through you like scenery. Get
    caught now and you go down properly — ragdoll, combo lost, the lot.

    Honest note: SSX 3 keeps its avalanche rules in a part of its data nobody
    has decoded, so what being hit costs you is OUR decision, not the original
    game's recovered behaviour. The debris stays non-solid on purpose (so a
    descending avalanche wrecks you rather than shoving you around like a
    moving wall), it's only dangerous while it's actually moving, and there's
    a three-second grace period so you aren't flattened again the instant you
    get up.

  THINGS THAT NEED A COURSE PACK BUILT AFTER THIS

  - Glowing halos on lamps and signs. Light sources can now carry proper
    camera-facing glows that fade with distance, flash, and swell as you get
    close, instead of being flat unlit geometry. Only the X-Games courses ship
    this data today; every other course looks exactly as it did.

  - Launch pads. A course can now throw you with a one-shot burst of speed —
    distinct from the wind zones it already had, which push continuously. This
    is what turns the fan shaft on the arena map into a lap course. A course
    can limit how many times it fires, counted per rider so a CPU racer can't
    use up your go.

  - Short names on the level-select screen. A map file has to have a unique
    name so two packs can't collide, but that makes for some ugly tiles. A
    pack can now ship a display name, so an arcade course can read FRANCE on
    the tile while its file stays uniquely named underneath.

    >>> READ THIS IF YOU CARE ABOUT YOUR BEST TIMES <<<
    The game files your best times under the name shown on the tile. So if a
    course pack updates and renames a course you have already played, the game
    will look for your records under the new name and not find them. The old
    times aren't deleted, but they won't show. Nothing you can do about it
    from this end — it's how the game stores them.

  FOR PEOPLE BUILDING OR TESTING COURSES

  - New setting: RiderTelemetry, under [Debug] in
    BepInEx/config/TrickyMaddnessLevelHook.cfg, off by default. Turn it on and
    the Hook records what your rider actually did — speed, position, airtime,
    every physics step — to a CSV in a Telemetry folder next to the game, so
    course geometry can be sized against real numbers instead of by eye. It
    works on the game's own four courses too, so an authored course has
    something to be measured against.
  - The --tm-map launch option and the AutoLoadMap setting now also accept a
    map's filename, and stopped caring about capitalisation. Anything that
    worked before still works.
  - If AutoLoadMap can't find what you asked for, it now lists the game's own
    four course names, which aren't written down anywhere else.

  ONE THING DELIBERATELY HELD BACK

  - The freestyle clock stays at 150 seconds. While fixing something else we
    found that custom maps had never once used the freestyle time their own
    data asks for — the Hook's attempt to apply it had silently done nothing
    since forever, so every custom course has always run the stock 150-second
    clock. The fix works, but the times the courses carry were placeholder
    values that nobody has ever ridden, and switching it on would have taken a
    typical converted course from 150 seconds to 500 with no one having played
    a single run at that length. It's staying off until those times have been
    tuned against actual play. The Hook logs what the map asked for, so you
    can see what it would have been. Medal times are unaffected and, unlike
    the clock, have always worked.

  COMPATIBILITY

  No map file changes, and the Hook still works with every course pack ever
  released — no re-download of anything. Your config file is untouched too:
  one new setting was added, nothing was renamed or removed.

  Be aware, though, that four of the fixes above change how courses you
  already have installed actually play: solid fences and barriers, doors that
  block you, CPU riders that no longer stall, and avalanche debris that hurts.
  All improvements, but they are genuine changes, not just cosmetics.

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
