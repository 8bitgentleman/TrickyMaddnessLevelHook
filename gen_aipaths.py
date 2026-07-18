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
    platform <p>       <- optional native-GPU platform: "mac" or "windows".
                          Tells the Hook the bundle's own shaders are native to
                          that backend so it trusts them (no shader remap). Our
                          bundles are Mac-built, so Mac ports want "platform mac"
                          — WITHOUT it every URP/Unlit material (emissive glow,
                          lightmap-overlay decal) collapses to the Lit fallback.
    path               <- starts a new path
    <x> <z> <0|1>      <- world XZ position + windup flag

Usage:
    python3 gen_aipaths.py [--scale <f>] [--platform <mac|windows>] \
        <map1.asset> [map2.asset ...]

--scale/--platform apply to every asset listed in the same invocation.
"""
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
    return paths


def main(argv):
    args = argv[1:]
    scale = None
    platform = None
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
        elif flag == "--platform":
            if val not in ("mac", "windows"):
                print("--platform must be 'mac' or 'windows', got %r" % val,
                      file=sys.stderr)
                return 2
            platform = val
        else:
            print("unknown flag %r" % flag, file=sys.stderr)
            return 2
    if not args:
        print(__doc__)
        return 2
    for asset_path in args:
        paths = extract(asset_path)
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
            if platform is not None:
                f.write("platform %s\n" % platform)
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
