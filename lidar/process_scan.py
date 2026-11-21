"""Turn a Polycam Floorplan export (.glb or .zip) into scan.glb + room.json.

Usage: python process_scan.py <scan.zip|scan.glb> --out <folder>

room.json uses glTF space: metres, Y up, right-handed. The floor's top surface
is y=0 and the floor is centred on x=0, z=0. Object positions are box centres.
rotationY is a glTF rotation about +Y. Unity converts with (x,y,z) -> (-x,y,z)
and rotationY -> -rotationY, in RoomLoader.cs only.
"""
import argparse
import hashlib
import json
import math
import os
import re
import struct
import zipfile
from collections import Counter, defaultdict

import numpy as np

from placement import NOT_A_SURFACE, SCANNED_SOUNDS, SURFACE_CLASS, ceiling_triangles, place_items

MAX_GLB_BYTES = 300 * 1024 * 1024
GROUPS = {"Walls", "Floors", "Ceilings", "Furniture", "Objects"}
COMPONENT_TYPES = {5120: "<i1", 5121: "<u1", 5122: "<i2", 5123: "<u2", 5125: "<u4", 5126: "<f4"}
TYPE_SIZES = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}
PLAYER_EYE_HEIGHT = 1.6


class ScanError(ValueError):
    """The upload is not a usable Floorplan scan."""


def read_input(path):
    if path.lower().endswith(".zip"):
        with zipfile.ZipFile(path) as archive:
            entries = [e for e in archive.infolist() if e.filename.lower().endswith(".glb") and not e.is_dir()]
            if not entries:
                raise ScanError("The zip has no .glb file. Export the Floorplan scan as GLB.")
            entry = max(entries, key=lambda e: e.file_size)
            if entry.file_size > MAX_GLB_BYTES:
                raise ScanError("The GLB inside the zip is too large.")
            return archive.read(entry)
    if os.path.getsize(path) > MAX_GLB_BYTES:
        raise ScanError("The GLB is too large.")
    with open(path, "rb") as handle:
        return handle.read()


def parse_glb(data):
    if len(data) < 20 or data[:4] != b"glTF":
        raise ScanError("This is not a GLB file.")
    version, length = struct.unpack_from("<II", data, 4)
    if version != 2 or length > len(data):
        raise ScanError("Unsupported or truncated GLB.")
    offset, gltf, blob = 12, None, b""
    while offset + 8 <= length:
        size, kind = struct.unpack_from("<I4s", data, offset)
        chunk = data[offset + 8:offset + 8 + size]
        if len(chunk) != size:
            raise ScanError("Truncated GLB chunk.")
        if kind == b"JSON":
            gltf = json.loads(chunk.decode("utf-8"))
        elif kind == b"BIN\0":
            blob = chunk
        offset += 8 + size
    if gltf is None:
        raise ScanError("The GLB has no JSON chunk.")
    if gltf.get("extensionsRequired"):
        raise ScanError(f"Unsupported GLB extensions: {gltf['extensionsRequired']}")
    return gltf, blob


def write_glb(gltf, blob):
    payload = json.dumps(gltf, separators=(",", ":")).encode("utf-8")
    payload += b" " * (-len(payload) % 4)
    blob = blob + b"\0" * (-len(blob) % 4)
    total = 12 + 8 + len(payload) + (8 + len(blob) if blob else 0)
    out = struct.pack("<4sII", b"glTF", 2, total) + struct.pack("<I4s", len(payload), b"JSON") + payload
    if blob:
        out += struct.pack("<I4s", len(blob), b"BIN\0") + blob
    return out


def read_accessor(gltf, blob, index):
    accessor = gltf["accessors"][index]
    if "bufferView" not in accessor or "sparse" in accessor:
        raise ScanError("Unsupported GLB accessor.")
    view = gltf["bufferViews"][accessor["bufferView"]]
    base = np.dtype(COMPONENT_TYPES[accessor["componentType"]])
    components = TYPE_SIZES[accessor["type"]]
    stride = view.get("byteStride") or base.itemsize * components
    offset = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
    layout = np.dtype({"names": ["v"], "formats": [(base, (components,))], "offsets": [0], "itemsize": stride})
    return np.frombuffer(blob, layout, accessor["count"], offset)["v"].reshape(accessor["count"], components).copy()


def node_matrix(node):
    if "matrix" in node:
        return np.array(node["matrix"], float).reshape(4, 4).T
    x, y, z, w = node.get("rotation", [0, 0, 0, 1])
    rotation = np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])
    matrix = np.eye(4)
    matrix[:3, :3] = rotation @ np.diag(node.get("scale", [1, 1, 1]))
    matrix[:3, 3] = node.get("translation", [0, 0, 0])
    return matrix


def collect_parts(gltf, blob):
    parts, seen = [], set()

    def walk(index, parent, group):
        if index in seen:
            raise ScanError("The GLB node graph has a cycle.")
        seen.add(index)
        node = gltf["nodes"][index]
        world = parent @ node_matrix(node)
        name = node.get("name", f"node_{index}")
        group = name if name in GROUPS else group
        if "mesh" in node:
            vertices, triangles, material, base = [], [], None, 0
            for primitive in gltf["meshes"][node["mesh"]]["primitives"]:
                if primitive.get("mode", 4) != 4:
                    continue
                points = read_accessor(gltf, blob, primitive["attributes"]["POSITION"]).astype(float)
                if "indices" in primitive:
                    faces = read_accessor(gltf, blob, primitive["indices"]).reshape(-1, 3).astype(np.int64)
                else:
                    faces = np.arange(len(points)).reshape(-1, 3)
                vertices.append(points)
                triangles.append(faces + base)
                base += len(points)
                material = primitive.get("material", material)
            if vertices:
                local = np.vstack(vertices)
                points = (world[:3, :3] @ local.T).T + world[:3, 3]
                faces = np.vstack(triangles)
                if np.linalg.det(world[:3, :3]) < 0:
                    faces = faces[:, ::-1]
                parts.append(dict(name=name, group=group, vertices=points, faces=faces, material=material))
        for child in node.get("children", []):
            walk(child, world, group)

    scene = gltf["scenes"][gltf.get("scene", 0)]
    for root in scene.get("nodes", []):
        walk(root, np.eye(4), None)
    return parts


def convex_hull(points):
    unique = sorted({(round(float(x), 4), round(float(z), 4)) for x, z in points})
    if len(unique) < 3:
        return np.array(unique)

    def half(sequence):
        chain = []
        for p in sequence:
            while len(chain) >= 2:
                a, b = chain[-2], chain[-1]
                if (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0]) > 0:
                    break
                chain.pop()
            chain.append(p)
        return chain[:-1]

    return np.array(half(unique) + half(reversed(unique)))


def oriented_box(vertices):
    """Smallest yaw-rotated box. Returns centre, long axis (x,z), length, width."""
    hull = convex_hull(vertices[:, [0, 2]])
    if len(hull) == 0:
        raise ScanError("Empty mesh.")
    if len(hull) < 3:
        start, end = hull[0], hull[-1]
        delta = end - start
        length = float(np.linalg.norm(delta))
        axis = delta / length if length > 0 else np.array([1.0, 0.0])
        return (start + end) / 2, axis, length, 0.0
    best = None
    for i in range(len(hull)):
        edge = hull[(i + 1) % len(hull)] - hull[i]
        size = np.linalg.norm(edge)
        if size < 1e-6:
            continue
        u = edge / size
        v = np.array([-u[1], u[0]])
        a, b = hull @ u, hull @ v
        area = (a.max() - a.min()) * (b.max() - b.min())
        if best is None or area < best[0]:
            best = (area, u, v, a.min(), a.max(), b.min(), b.max())
    _, u, v, a0, a1, b0, b1 = best
    centre = u * (a0 + a1) / 2 + v * (b0 + b1) / 2
    length, width = a1 - a0, b1 - b0
    if width > length:
        u, length, width = v, width, length
    return centre, u, float(length), float(width)


def polygon_area(polygon):
    x, z = polygon[:, 0], polygon[:, 1]
    return 0.5 * float(np.dot(x, np.roll(z, -1)) - np.dot(z, np.roll(x, -1)))


def simplify(polygon, tolerance=0.01):
    points = [p for p in polygon]

    def deviation(index):
        a, b, c = points[index - 1], points[index], points[(index + 1) % len(points)]
        ac = c - a
        length = np.linalg.norm(ac)
        if length == 0:
            return float(np.linalg.norm(b - a))
        return abs(ac[0] * (b[1] - a[1]) - ac[1] * (b[0] - a[0])) / length

    while len(points) > 3:
        values = [deviation(i) for i in range(len(points))]
        index = int(np.argmin(values))
        if values[index] > tolerance:
            break
        del points[index]
    return np.array(points)


def floor_level(floors):
    """The main walkable height and its triangles, across every floor part."""
    triangles = np.vstack([p["vertices"][p["faces"]] for p in floors])
    cross = np.cross(triangles[:, 1] - triangles[:, 0], triangles[:, 2] - triangles[:, 0])
    areas = 0.5 * np.linalg.norm(cross, axis=1)
    valid = areas > 1e-6
    facing = np.zeros(len(triangles), bool)
    facing[valid] = cross[valid, 1] / (2 * areas[valid]) > 0.9
    if not facing.any():
        facing[valid] = np.abs(cross[valid, 1]) / (2 * areas[valid]) > 0.9
    if not facing.any():
        raise ScanError("Could not find the floor's top surface.")
    heights = triangles[facing][:, :, 1].mean(1)
    weights = areas[facing]
    bins = np.round(heights / 0.02).astype(np.int64)
    values = np.unique(bins)
    main = values[int(np.argmax([weights[bins == v].sum() for v in values]))]
    level = float(np.average(heights[bins == main], weights=weights[bins == main]))
    return level, triangles[facing][np.abs(heights - level) < 0.03]


def boundary_graph(triangles):
    """Edges used by one triangle, after splitting long edges at T-junctions."""
    keys = np.round(triangles[:, :, [0, 2]] * 1000).astype(np.int64)
    points = np.unique(keys.reshape(-1, 2), axis=0)
    counts = defaultdict(int)
    for tri in keys:
        for i in range(3):
            a, b = tri[i], tri[(i + 1) % 3]
            delta = b - a
            length = float(delta @ delta)
            if length == 0:
                continue
            offsets = points - a
            along = (offsets @ delta) / length
            across = offsets[:, 0] * delta[1] - offsets[:, 1] * delta[0]
            on_edge = (np.abs(across) <= np.sqrt(length) * 1.5) & (along > 0) & (along < 1)
            chain = [a] + [points[j] for j in np.where(on_edge)[0][np.argsort(along[on_edge], kind="stable")]] + [b]
            for p, q in zip(chain, chain[1:]):
                p, q = tuple(int(v) for v in p), tuple(int(v) for v in q)
                if p != q:
                    counts[(min(p, q), max(p, q))] += 1
    neighbours = defaultdict(list)
    for (a, b), count in sorted(counts.items()):
        if count == 1:
            neighbours[a].append(b)
            neighbours[b].append(a)
    return neighbours


def trace_outer(neighbours, component):
    """Walks the outside of one connected boundary, counter-clockwise."""
    start = min(component, key=lambda p: (p[1], p[0]))
    path, current, heading = [start], start, (1.0, 0.0)
    for _ in range(sum(len(neighbours[p]) for p in component) + 2):
        back = math.atan2(-heading[1], -heading[0])

        def turn(candidate):
            value = (math.atan2(candidate[1] - current[1], candidate[0] - current[0]) - back) % math.tau
            return value if value > 1e-9 else math.tau

        following = min(neighbours[current], key=lambda q: (turn(q), q))
        if following == start:
            return np.array(path, float) / 1000
        heading = (following[0] - current[0], following[1] - current[1])
        path.append(following)
        current = following
    return None


def floor_outline(floors):
    """Height and outer boundary of the floor, counter-clockwise in (x,z)."""
    level, upper = floor_level(floors)
    warnings = []
    neighbours = boundary_graph(upper)
    loops, remaining = [], set(neighbours)
    while remaining:
        seed = min(remaining)
        component, stack = {seed}, [seed]
        while stack:
            for other in neighbours[stack.pop()]:
                if other not in component:
                    component.add(other)
                    stack.append(other)
        remaining -= component
        loop = trace_outer(neighbours, component)
        if loop is not None and len(loop) >= 3:
            loops.append(loop)

    if loops:
        polygon = max(loops, key=lambda p: abs(polygon_area(p)))
        if len(loops) > 1:
            warnings.append(f"Floor has {len(loops)} separate areas; using the largest.")
    else:
        polygon = convex_hull(upper.reshape(-1, 3)[:, [0, 2]])
        warnings.append("Floor outline could not be traced; using its convex hull.")
    polygon = simplify(polygon)
    if polygon_area(polygon) < 0:
        polygon = polygon[::-1]
    return level, polygon, warnings


def normalize_label(name):
    """A plain category from names like table_dining_0, 'Dining Table 1' or Chair.002."""
    words = re.findall(r"[a-z]+", name.lower())
    known = set(SURFACE_CLASS) | NOT_A_SURFACE | set(SCANNED_SOUNDS)
    return next((word for word in words if word in known), words[0] if words else "object")


def inside(polygon, x, z):
    result = False
    for i in range(len(polygon)):
        ax, az = polygon[i]
        bx, bz = polygon[i - 1]
        if (az > z) != (bz > z) and x < (bx - ax) * (z - az) / (bz - az) + ax:
            result = not result
    return result


def edge_distance(polygon, point):
    a, b = polygon, np.roll(polygon, -1, axis=0)
    delta = b - a
    t = np.clip(((point - a) * delta).sum(1) / np.maximum((delta * delta).sum(1), 1e-9), 0, 1)
    return float(np.linalg.norm(a + delta * t[:, None] - point, axis=1).min())


def choose_player_start(polygon, objects):
    low, high = polygon.min(0), polygon.max(0)
    obstacles = [(np.array([o["position"][0], o["position"][2]]), math.hypot(o["size"][0], o["size"][2]) / 2) for o in objects]
    best, best_score = None, -1.0
    for x in np.arange(low[0], high[0], 0.25):
        for z in np.arange(low[1], high[1], 0.25):
            if not inside(polygon, x, z):
                continue
            point = np.array([x, z])
            clearance = edge_distance(polygon, point)
            for centre, radius in obstacles:
                clearance = min(clearance, float(np.linalg.norm(point - centre)) - radius)
            if clearance > best_score:
                best, best_score = point, clearance
    if best is None:
        best = polygon.mean(0)
    target = polygon.mean(0) - best
    yaw = math.degrees(math.atan2(target[0], target[1])) if np.linalg.norm(target) > 0.01 else 0.0
    return {"position": rounded([best[0], PLAYER_EYE_HEIGHT, best[1]]), "yaw": round(yaw, 2)}


def rounded(values, digits=3):
    return [round(float(v), digits) + 0.0 for v in values]


def material_hex(gltf, index):
    if index is None:
        return None
    factor = gltf.get("materials", [])[index].get("pbrMetallicRoughness", {}).get("baseColorFactor")
    if not factor:
        return None

    def to_srgb(c):
        c = max(0.0, min(1.0, c))
        return 12.92 * c if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055

    return "#" + "".join(f"{round(to_srgb(c) * 255):02x}" for c in factor[:3])


def build_room(gltf, parts):
    floors = [p for p in parts if p["group"] == "Floors" or p["name"].lower().startswith("floor")]
    if not floors:
        raise ScanError("No labelled floor found. Scan with Polycam Floorplan mode.")
    floor_ids = {id(p) for p in floors}
    floor_y, polygon, warnings = floor_outline(floors)
    centre = (polygon.min(0) + polygon.max(0)) / 2
    offset = np.array([-centre[0], -floor_y, -centre[1]])
    polygon = polygon - centre

    walls, objects, ceiling_heights = [], [], []
    for part in parts:
        name, group = part["name"], part["group"]
        lowered = name.lower()
        vertices = part["vertices"] + offset
        if lowered.startswith("joint") or id(part) in floor_ids:
            continue
        if group == "Ceilings" or lowered.startswith("ceiling"):
            faces = ceiling_triangles(vertices, part["faces"])
            if len(faces):
                sides = np.cross(faces[:, 1] - faces[:, 0], faces[:, 2] - faces[:, 0])
                ceiling_heights.append(float(np.average(faces[:, :, 1].mean(1), weights=np.linalg.norm(sides, axis=1))))
            continue
        label = normalize_label(name)
        box_centre, axis, length, width = oriented_box(vertices)
        top = float(vertices[:, 1].max())
        if (group == "Walls" or lowered.startswith("wall")) and label not in NOT_A_SURFACE:
            walls.append({
                "node": name,
                "start": rounded(box_centre - axis * length / 2),
                "end": rounded(box_centre + axis * length / 2),
                "height": round(top, 3),
                "thickness": round(width, 3),
            })
            continue
        bottom = float(vertices[:, 1].min())
        rotation = math.degrees(math.atan2(-axis[1], axis[0])) % 180
        objects.append({
            "label": label,
            "node": name,
            "position": rounded([box_centre[0], (bottom + top) / 2, box_centre[1]]),
            "size": rounded([length, top - bottom, width]),
            "rotationY": round(rotation, 2),
            "color": material_hex(gltf, part["material"]),
        })

    if not walls:
        raise ScanError("No labelled walls found. Scan with Polycam Floorplan mode.")
    ceiling = max(ceiling_heights) if ceiling_heights else max(w["height"] for w in walls)
    outline = [rounded(p) for p in polygon]
    room = {
        "units": "m",
        "coordinateSystem": "gltf-y-up-right-handed",
        "warnings": warnings,
        "floor": {
            "polygon": outline,
            "polygonFlat": [value for point in outline for value in point],
            "area": round(abs(polygon_area(polygon)), 2),
        },
        "ceilingHeight": round(ceiling, 3),
        "walls": walls,
        "objects": objects,
        "landingSpots": [],
        "lights": [],
        "soundAnchors": [],
        "playerStart": choose_player_start(polygon, objects),
    }
    return room, offset


def recentre_glb(gltf, blob, offset):
    """Wrap the scene in one root node so the original meshes and materials stay untouched."""
    scene = gltf["scenes"][gltf.get("scene", 0)]
    gltf["nodes"].append({"name": "FlyAI_Root", "translation": [float(v) for v in offset], "children": list(scene.get("nodes", []))})
    scene["nodes"] = [len(gltf["nodes"]) - 1]
    return write_glb(gltf, blob)


def replace_file(path, data):
    temporary = path + ".tmp"
    with open(temporary, "wb") as handle:
        handle.write(data)
    os.replace(temporary, path)


def write_room(path, room):
    replace_file(path, json.dumps(room, indent=1).encode("utf-8"))


def process_scan(input_path, out_dir, ai=False):
    try:
        raw = read_input(input_path)
        gltf, blob = parse_glb(raw)
        parts = collect_parts(gltf, blob)
    except ScanError:
        raise
    except (zipfile.BadZipFile, KeyError, IndexError, TypeError, ValueError) as error:
        raise ScanError(f"Could not read this scan: {error}") from error
    if not parts:
        raise ScanError("The scan has no meshes.")
    room, offset = build_room(gltf, parts)
    seed = int.from_bytes(hashlib.sha256(raw).digest()[:4], "big")
    layout, context = place_items(parts, room, offset, seed)
    room.update(layout)
    room["layoutSource"] = "rules"
    os.makedirs(out_dir, exist_ok=True)
    replace_file(os.path.join(out_dir, "scan.glb"), recentre_glb(gltf, blob, offset))
    for stale in ("room.ai.json", "ai_report.json"):
        if os.path.exists(os.path.join(out_dir, stale)):
            os.remove(os.path.join(out_dir, stale))
    write_room(os.path.join(out_dir, "room.rules.json"), room)
    ai_status = "off"
    if ai:
        from ai_cleanup import run_ai_cleanup

        cleaned, report = run_ai_cleanup(room, context)
        ai_status = report["status"]
        write_room(os.path.join(out_dir, "ai_report.json"), report)
        if cleaned is not None:
            write_room(os.path.join(out_dir, "room.ai.json"), cleaned)
            room = cleaned
    write_room(os.path.join(out_dir, "room.json"), room)
    xs, zs = np.array(room["floor"]["polygon"]).T
    return {
        "warnings": room["warnings"],
        "doors": dict(Counter(d["source"] for d in room["doors"])),
        "window": room["window"]["source"] if room["window"] else None,
        "wallDecor": len(room["wallDecor"]),
        "layoutSource": room["layoutSource"],
        "aiCleanup": ai_status,
        "soundAnchors": dict(Counter(a["sound"] for a in room["soundAnchors"])),
        "lightCoverage": room["lightCoverage"],
        "floorArea": room["floor"]["area"],
        "floorExtent": rounded([np.ptp(xs), np.ptp(zs)], 2),
        "ceilingHeight": room["ceilingHeight"],
        "walls": len(room["walls"]),
        "objects": len(room["objects"]),
        "labels": sorted({o["label"] for o in room["objects"]}),
        "surfaces": dict(Counter(s["kind"] for s in room["surfaces"])),
        "landingSpots": dict(Counter(s["surfaceKind"] for s in room["landingSpots"])),
        "props": dict(Counter(p["type"] for p in room["props"])),
        "lights": dict(Counter(l["type"] for l in room["lights"])),
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("input")
    parser.add_argument("--out", required=True)
    parser.add_argument("--ai", action="store_true", help="Run the optional Claude cleanup pass")
    arguments = parser.parse_args()
    try:
        print(json.dumps(process_scan(arguments.input, arguments.out, ai=arguments.ai), indent=1))
    except ScanError as error:
        raise SystemExit(f"Scan rejected: {error}")
