#!/usr/bin/env python3
"""Extract MakerAIPath data from a Tricky Madness map bundle into a sidecar.

Unity cannot deserialize List<MakerAIPath.Point> (a user-defined nested
[Serializable] class) for runtime-loaded (BepInEx) assemblies, so on custom
maps every AI path arrives empty at runtime. The data is present in the
bundle, though — this script reads it with UnityPy and writes
"<map>.aipaths.txt" next to each .asset, which the Hook's sidecar loader
(Plugin.cs BuildAIPathsFromSidecar) picks up at level build time.

Sidecar format (one token line per point, invariant-culture floats):
    # comment
    scale <f>          <- optional map world-scale header (Hook gravity scaling)
    gravity <f>        <- optional per-map gravity multiplier (Hook feature 8).
                          Independent of scale: >1 = heavier, <1 = lighter,
                          applied at any world scale. Absent = 1.0 (stock).
    physicshz <f>      <- optional physics tick rate, 10-1000 Hz. Absent = the
                          game's own 100 Hz. Unity drags the physics clock up to
                          the frame clock by running FixedUpdate repeatedly, so a
                          map heavy enough to drop below ~20 fps pins at FIVE full
                          physics passes per rendered frame and each slow frame
                          buys more of them. Declaring 50 here escapes that (it is
                          also Unity's own default rate). Costs collision
                          precision: a rider at speed is likelier to clip through
                          thin geometry, so this is for very large maps only.
    railcatch <f>      <- optional rail grind-collider half-width, in METRES,
                          0.02-100. Absent = the Hook's own 0.25 * scale, which
                          is what every map shipped before this header existed
                          uses. Only declare it when the map's `scale` header is
                          NOT proportional to its finished size -- WildTangent
                          is the whole population: its raw units are already
                          metres, so scale 100 makes the importer pass geometry
                          through 1:1 while 0.25 * 100 asks for a 25 m catch
                          radius around a 0.24 m rail. The corpus value is 0.625
                          (what a 2.5x SSX map gets).
    routescale <f>     <- optional SSX-equivalent world scale for the Hook's
                          route/respawn/medal/rail-chain tolerances, 0.1-20.
                          Absent = the map's `scale`, which is right for every
                          SSX-family map. Declare it only when `scale` is a raw
                          unit ratio rather than an SSX port factor -- the
                          scale-139 family (Mad Trix, Raw Thrills): there those
                          tolerances come out ~55x too big (a 2 km respawn-line
                          cut, COM routes that never split). The corpus value
                          is 2.5 (an SSX Tricky port), matching railcatch 0.625.
    ridepace <f>       <- optional gold PACE in world units/second, 5-500. Absent =
                          the Hook's own 45 u/s fallback, which is what every map
                          shipped before this header existed uses. It is only read
                          when a map still carries the OLD placeholder medal times,
                          i.e. it is a rescue for already-deployed maps; a rebuilt
                          map bakes the same number in at import instead. Measured
                          per course from real telemetry -- get the value from
                          ssx/pace_table.json rather than picking one (45 is well
                          below what most source games are actually ridden at, and
                          that is how X-Games Falps shipped a 3:01 gold for a
                          course that rides in 1:43).
    platform <p>       <- native-GPU platform: "mac" or "windows". Tells the Hook
                          the bundle's own shaders are native to that backend so
                          it trusts them (no shader remap). WITHOUT it every
                          URP/Unlit material (emissive glow, lightmap-overlay
                          decal) collapses to the Lit fallback. AUTO-DETECTED
                          from the bundle's own build target, so a bare regen
                          can no longer silently drop it (that is how Alaska 2x
                          and Ssx3 A-D shipped headerless); --platform overrides
                          the detection, --platform none suppresses the header.
    spawnsnap <mode>   <- optional spawn/respawn ground-snap mode: "both"
                          (default when absent — seat markers exactly on the
                          terrain, raising OR lowering; kills both the immediate
                          drop and the sink-through), "raise" (only lift markers
                          clear of the ground, never lower), or "off" (leave every
                          marker as authored — for a hand-tuned start like gari).
    mapversion <v>     <- optional release version of THIS map build (e.g. "1.0",
                          "1.2.0"). Purely informational, like source: the Hook
                          logs it, so a bug report's log says exactly which build
                          of a course the player has without you asking. Absent ==
                          unversioned (every map shipped before this existed).
                          NOT the sidecar format version and NOT the Hook's own
                          PLUGIN_VERSION — those are separate things.
    source <slug>      <- optional provenance slug: which game this map's content
                          came from. Purely informational (logged by the Hook so a
                          bug report tells you the origin). Slugs in use:
                          "ssx_tricky" (SSX Tricky ports + community customs),
                          "ssx3" (SSX 3 ports). Absent == "tricky_madness": the
                          Hook defaults sidecar-less / header-less maps to OG,
                          which is what the game's own built-in courses are.
    group <label>      <- optional level-select section heading, e.g. "SSX 3".
                          Everything after the key is the label, so it MAY
                          contain spaces (so may displayname). Absent ==
                          the Hook's DefaultGroup, "CUSTOM".
    order <n>          <- optional level-select sort key, a whole number, GLOBAL
                          across every map in Maps/ (not per group). Sections are
                          runs of adjacent maps sharing a `group`, so two maps in
                          one group must not have another group's map ordered
                          between them. Absent == sorts after every map that
                          declares one, by name.
    displayname <text> <- optional level-select + results-screen title, when it
                          should differ from the .asset filename (X-Games' arcade
                          names: file "X-Games - Falps", shown "FRANCE"). Free
                          text like group. ⚠️ It is ALSO the game's best-times
                          file name (Records/<text>.pbs), so setting it on a map
                          already played orphans its times, and two maps sharing
                          one share records. Absent == the filename.
    path               <- starts a new path
    rail <name>        <- optional, per-path (after `path`): this fragment rides
                          the named rail. An older Hook counts it as malformed
                          and drops it, so the fragment dead-ends.
    <x> <z> <0|1>      <- world XZ position + windup flag

Usage:
    python3 gen_aipaths.py [--scale <f>] [--gravity <f>] [--physics-hz <f>] \
        [--rail-catch <f>] [--route-scale <f>] [--ride-pace <f>] \
        [--platform <mac|windows|none>] [--source <slug>] \
        [--spawnsnap <both|raise|off>] [--mapversion <v>] \
        [--group <label>] [--order <n>] [--displayname <text>] \
        <map1.asset> [map2.asset ...]

All flags apply to every asset in the same invocation.
"""
import re
import sys
import os

import UnityPy


def node_field_names(obj):
    """All field names in the object's serialized type tree.

    serialized_type.nodes is a tree in newer UnityPy (walk m_Children);
    older versions expose a flat list. Handle both.
    """
    names = set()

    def walk(node):
        names.add(getattr(node, "m_Name", None))
        for child in getattr(node, "m_Children", None) or []:
            walk(child)

    nodes = getattr(obj.serialized_type, "nodes", None) or []
    try:
        for n in nodes:  # flat list, or list of roots — walk covers both
            walk(n)
    except TypeError:
        walk(nodes)
    return names


def is_maker_ai_path(obj, tree):
    """MakerAIPath is the only m_points-bearing behaviour with a windup field."""
    if "m_points" not in tree:
        return False
    pts = tree["m_points"]
    if pts:
        return isinstance(pts[0], dict) and "windup" in pts[0]
    # Empty list: fall back to the type tree (can't sniff a sample point).
    return "windup" in node_field_names(obj)


# Unity BuildTarget -> the Hook's "platform" header. A bundle knows what it was
# built for (SerializedFile m_TargetPlatform), so nobody has to remember the flag.
BUILD_TARGETS = {2: "mac", 4: "mac", 27: "mac", 5: "windows", 19: "windows"}


def detect_platform(env):
    """The bundle's own build target, or None if it is not one we know."""
    found = set()
    for f in env.files.values():
        for sub in getattr(f, "files", {}).values():
            tp = getattr(sub, "target_platform", None)
            if tp is not None:
                found.add(BUILD_TARGETS.get(int(tp)))
    found.discard(None)
    return found.pop() if len(found) == 1 else None


def extract(asset_path):
    env = UnityPy.load(asset_path)
    paths = []
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            tree = obj.read_typetree()
        except Exception:
            continue
        if not isinstance(tree, dict) or not is_maker_ai_path(obj, tree):
            continue
        pts = [(p["position"]["x"], p["position"]["y"], 1 if p["windup"] else 0)
               for p in tree["m_points"]]
        if len(pts) >= 2:  # the Hook drops <2-point paths; don't emit them
            paths.append(pts)
    return paths, detect_platform(env)


def main(argv):
    args = argv[1:]
    scale = None
    gravity = None
    physics_hz = None
    rail_catch = None
    route_scale = None
    ride_pace = None
    platform = None
    source = None
    spawnsnap = None
    mapversion = None
    group = None
    order = None
    displayname = None
    while args and args[0].startswith("--"):
        flag = args[0]
        if len(args) < 2:
            print("%s requires a value" % flag, file=sys.stderr)
            return 2
        val = args[1]
        args = args[2:]
        if flag == "--scale":
            try:
                scale = float(val)
            except ValueError:
                print("--scale value must be a float, got %r" % val,
                      file=sys.stderr)
                return 2
        elif flag == "--gravity":
            try:
                gravity = float(val)
            except ValueError:
                print("--gravity value must be a float, got %r" % val,
                      file=sys.stderr)
                return 2
            if gravity <= 0:
                print("--gravity must be > 0, got %r" % val, file=sys.stderr)
                return 2
        elif flag == "--physics-hz":
            try:
                physics_hz = float(val)
            except ValueError:
                print("--physics-hz value must be a float, got %r" % val,
                      file=sys.stderr)
                return 2
            # Same bounds the Hook enforces on read, so a typo fails here at
            # build time rather than being silently ignored at load time.
            if not (10.0 <= physics_hz <= 1000.0):
                print("--physics-hz must be 10-1000, got %r" % val,
                      file=sys.stderr)
                return 2
        elif flag == "--rail-catch":
            try:
                rail_catch = float(val)
            except ValueError:
                print("--rail-catch value must be a float, got %r" % val,
                      file=sys.stderr)
                return 2
            # Same bounds the Hook enforces on read, so a typo fails here at
            # build time rather than being silently ignored at load time.
            if not (0.02 <= rail_catch <= 100.0):
                print("--rail-catch must be 0.02-100 metres, got %r" % val,
                      file=sys.stderr)
                return 2
        elif flag == "--route-scale":
            try:
                route_scale = float(val)
            except ValueError:
                print("--route-scale value must be a float, got %r" % val,
                      file=sys.stderr)
                return 2
            # Same bounds the Hook enforces on read.
            if not (0.1 <= route_scale <= 20.0):
                print("--route-scale must be 0.1-20, got %r" % val,
                      file=sys.stderr)
                return 2
        elif flag == "--ride-pace":
            try:
                ride_pace = float(val)
            except ValueError:
                print("--ride-pace value must be a float, got %r" % val,
                      file=sys.stderr)
                return 2
            # Same bounds the Hook enforces on read, so a typo fails here at
            # build time rather than being silently ignored at load time.
            if not (5.0 <= ride_pace <= 500.0):
                print("--ride-pace must be 5-500 units/second, got %r" % val,
                      file=sys.stderr)
                return 2
        elif flag == "--platform":
            if val not in ("mac", "windows", "none"):
                print("--platform must be 'mac', 'windows' or 'none', got %r"
                      % val, file=sys.stderr)
                return 2
            platform = val
        elif flag == "--source":
            # A machine-clean slug so it stays a single header token (the Hook
            # splits on whitespace): lowercase letters/digits/underscore only.
            if not re.fullmatch(r"[a-z0-9_]+", val):
                print("--source must be a lowercase slug [a-z0-9_], got %r"
                      % val, file=sys.stderr)
                return 2
            source = val
        elif flag == "--spawnsnap":
            if val not in ("both", "raise", "off"):
                print("--spawnsnap must be 'both', 'raise', or 'off', got %r"
                      % val, file=sys.stderr)
                return 2
            spawnsnap = val
        elif flag == "--mapversion":
            # Single header token (the Hook splits on whitespace), and loose
            # enough for whatever the release is called: "1.0", "1.2.0-rc1".
            if not re.fullmatch(r"[A-Za-z0-9._+-]{1,32}", val):
                print("--mapversion must be [A-Za-z0-9._+-], max 32 chars, "
                      "got %r" % val, file=sys.stderr)
                return 2
            mapversion = val
        elif flag == "--group":
            # Free prose: it is drawn verbatim as a section heading. Only
            # constraint is that it stays one line and is not blank.
            val = " ".join(val.split())
            if not val:
                print("--group must not be blank", file=sys.stderr)
                return 2
            group = val
        elif flag == "--displayname":
            # Free prose like --group: the level-select/results title when it
            # should differ from the .asset filename. Also the game's best-times
            # key, so don't reuse one across maps.
            val = " ".join(val.split())
            if not val:
                print("--displayname must not be blank", file=sys.stderr)
                return 2
            displayname = val
        elif flag == "--order":
            try:
                order = int(val)
            except ValueError:
                print("--order must be a whole number, got %r" % val,
                      file=sys.stderr)
                return 2
        else:
            print("unknown flag %r" % flag, file=sys.stderr)
            return 2
    if not args:
        print(__doc__)
        return 2
    for asset_path in args:
        paths, detected = extract(asset_path)
        plat = platform or detected
        if plat is None:
            print("%s: WARNING unknown build target, writing no 'platform' "
                  "header (Unlit materials will fall back to Lit)"
                  % os.path.basename(asset_path), file=sys.stderr)
        elif platform and detected and platform != detected:
            print("%s: WARNING --platform %s overrides the bundle's own %s "
                  "build target" % (os.path.basename(asset_path), platform,
                                    detected), file=sys.stderr)
        out_path = os.path.splitext(asset_path)[0] + ".aipaths.txt"
        # Headers (scale/gravity/platform/...) are independent of whether the
        # bundle has any real MakerAIPath data -- skipping the whole sidecar
        # when paths is empty used to also drop every header silently. That
        # is how a course with zero authored AI paths (no source data, e.g.
        # every WildTangent course so far) shipped riding at the Hook's
        # stock worldScale/gravity of 1.0 regardless of what --scale/--gravity
        # this script was called with: BuildAIPathsFromSidecar already
        # tolerates a header-only sidecar fine (falls through to
        # BuildAIPathFromRaceLine), so there is nothing gated on `paths` here
        # except the "path" blocks themselves.
        if not paths:
            print(f"{os.path.basename(asset_path)}: no usable AI paths found "
                  f"-- writing headers only to {os.path.basename(out_path)}")
        with open(out_path, "w") as f:
            f.write("# AI paths extracted from %s by gen_aipaths.py\n"
                    "# format: 'path' starts a path; then 'x z windup' per point\n"
                    % os.path.basename(asset_path))
            if scale is not None:
                f.write("scale %r\n" % scale)
            if gravity is not None:
                f.write("gravity %r\n" % gravity)
            if physics_hz is not None:
                f.write("physicshz %r\n" % physics_hz)
            if rail_catch is not None:
                f.write("railcatch %r\n" % rail_catch)
            if route_scale is not None:
                f.write("routescale %r\n" % route_scale)
            if ride_pace is not None:
                f.write("ridepace %r\n" % ride_pace)
            if plat is not None and plat != "none":
                f.write("platform %s\n" % plat)
            if source is not None:
                f.write("source %s\n" % source)
            if spawnsnap is not None:
                f.write("spawnsnap %s\n" % spawnsnap)
            if mapversion is not None:
                f.write("mapversion %s\n" % mapversion)
            if group is not None:
                f.write("group %s\n" % group)
            if order is not None:
                f.write("order %d\n" % order)
            if displayname is not None:
                f.write("displayname %s\n" % displayname)
            for pts in paths:
                f.write("path\n")
                for x, z, w in pts:
                    f.write("%r %r %d\n" % (x, z, w))
        if paths:
            npts = sum(len(p) for p in paths)
            print(f"{os.path.basename(asset_path)}: wrote {len(paths)} path(s), "
                  f"{npts} point(s) -> {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
