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
    source <slug>      <- optional provenance slug: which game this map's content
                          came from. Purely informational (logged by the Hook so a
                          bug report tells you the origin). Slugs in use:
                          "ssx_tricky" (SSX Tricky ports + community customs),
                          "ssx3" (SSX 3 ports). Absent == "tricky_madness": the
                          Hook defaults sidecar-less / header-less maps to OG,
                          which is what the game's own built-in courses are.
    path               <- starts a new path
    <x> <z> <0|1>      <- world XZ position + windup flag

Usage:
    python3 gen_aipaths.py [--scale <f>] [--gravity <f>] \
        [--platform <mac|windows|none>] [--source <slug>] \
        [--spawnsnap <both|raise|off>] <map1.asset> [map2.asset ...]

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
    platform = None
    source = None
    spawnsnap = None
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
        if not paths:
            print(f"{os.path.basename(asset_path)}: no usable AI paths found, "
                  f"not writing {os.path.basename(out_path)}")
            continue
        with open(out_path, "w") as f:
            f.write("# AI paths extracted from %s by gen_aipaths.py\n"
                    "# format: 'path' starts a path; then 'x z windup' per point\n"
                    % os.path.basename(asset_path))
            if scale is not None:
                f.write("scale %r\n" % scale)
            if gravity is not None:
                f.write("gravity %r\n" % gravity)
            if plat is not None and plat != "none":
                f.write("platform %s\n" % plat)
            if source is not None:
                f.write("source %s\n" % source)
            if spawnsnap is not None:
                f.write("spawnsnap %s\n" % spawnsnap)
            for pts in paths:
                f.write("path\n")
                for x, z, w in pts:
                    f.write("%r %r %d\n" % (x, z, w))
        npts = sum(len(p) for p in paths)
        print(f"{os.path.basename(asset_path)}: wrote {len(paths)} path(s), "
              f"{npts} point(s) -> {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
