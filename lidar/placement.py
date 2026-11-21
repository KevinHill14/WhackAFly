"""Find real surfaces in a scan, then place lights, sound sources, props and fly spots.

Every position comes from the scan's geometry. The rules below only say which
kind of surface each item may use and how sparse a layout should be. Results are
repeatable for the same file because the seed is derived from its contents.
"""
import math
import re
from collections import Counter

import numpy as np

SURFACE_CLASS = {
    "table": "table", "desk": "table", "counter": "table", "island": "table", "nightstand": "table",
    "chair": "seat", "stool": "seat", "bench": "seat", "sofa": "seat", "couch": "seat",
    "storage": "storage", "shelf": "storage", "shelving": "storage", "bookshelf": "storage",
    "cabinet": "storage", "dresser": "storage",
    "bed": "bed",
}
NOT_A_SURFACE = {"window", "door", "opening"}

MIN_SURFACE_AREA = 0.015
MIN_MAIN_SURFACE_AREA = 0.06
SEAT_HEIGHT = (0.25, 0.75)
WALL_CLEARANCE = 0.06
NEAR_WALL = 0.8
ITEM_GAP = 0.05
SAME_LEVEL = 0.35

# Every placeable type and the surfaces it is allowed on.
PROP_TYPES = {
    "fruit_bowl": {"surfaces": ("table",), "radius": 0.15, "height": 0.16, "limit": 2},
    "books": {"surfaces": ("table", "storage"), "radius": 0.17, "height": 0.24, "limit": 2},
    "plate": {"surfaces": ("table",), "radius": 0.13, "height": 0.04, "limit": 2},
    "potted_plant": {"surfaces": ("floor",), "radius": 0.38, "height": 0.66, "limit": 2, "near_wall": True},
    "cup": {"surfaces": ("table", "storage"), "radius": 0.09, "height": 0.26, "limit": 2},
    "takeout_box": {"surfaces": ("table",), "radius": 0.13, "height": 0.07, "limit": 1},
    "jar": {"surfaces": ("floor",), "radius": 0.13, "height": 0.44, "limit": 1},
    "rotten_banana": {"surfaces": ("table",), "radius": 0.13, "height": 0.18, "limit": 1},
    "trash_bag": {"surfaces": ("floor",), "radius": 0.28, "height": 0.58, "limit": 1, "near_wall": True},
    "pillow": {"surfaces": ("bed",), "radius": 0.30, "height": 0.42, "limit": 2},
    "crumbs": {"surfaces": ("table",), "radius": 0.06, "height": 0.01, "limit": 1},
    "spilled_juice": {"surfaces": ("floor",), "radius": 0.22, "height": 0.01, "limit": 1},
    "napkin": {"surfaces": ("seat",), "radius": 0.08, "height": 0.02, "limit": 1},
    "fruit_bowl_lemon": {"surfaces": ("table",), "radius": 0.15, "height": 0.16, "limit": 1},
    "fruit_bowl_pear": {"surfaces": ("table",), "radius": 0.17, "height": 0.15, "limit": 1},
    "fruit_basket": {"surfaces": ("table", "storage"), "radius": 0.14, "height": 0.24, "limit": 1},
    "small_plant": {"surfaces": ("table", "storage"), "radius": 0.11, "height": 0.28, "limit": 2},
    "jug": {"surfaces": ("table", "storage"), "radius": 0.13, "height": 0.20, "limit": 1},
    "place_setting": {"surfaces": ("table",), "radius": 0.20, "height": 0.04, "limit": 2},
    "cutlery": {"surfaces": ("table",), "radius": 0.11, "height": 0.03, "limit": 1},
    "goblets": {"surfaces": ("table", "storage"), "radius": 0.08, "height": 0.17, "limit": 1},
    "drinking_glass": {"surfaces": ("table", "storage"), "radius": 0.04, "height": 0.12, "limit": 2},
    "water_bottle": {"surfaces": ("table", "storage"), "radius": 0.06, "height": 0.20, "limit": 2},
    "phone": {"surfaces": ("table", "storage", "seat", "bed"), "radius": 0.08, "height": 0.012, "limit": 1},
    "airpods": {"surfaces": ("table", "storage"), "radius": 0.035, "height": 0.026, "limit": 1},
    "laptop": {"surfaces": ("table",), "radius": 0.20, "height": 0.29, "limit": 1},
    "notebook": {"surfaces": ("table", "storage"), "radius": 0.15, "height": 0.025, "limit": 1},
    "picture_frame": {"surfaces": ("table", "storage"), "radius": 0.08, "height": 0.20, "limit": 1},
    "brass_vase": {"surfaces": ("table", "storage"), "radius": 0.05, "height": 0.13, "limit": 1},
}
# One of these anchors the middle of each table.
CENTERPIECES = ("fruit_bowl", "fruit_bowl_lemon", "small_plant", "fruit_bowl_pear", "fruit_basket", "jug")
TABLE_ITEMS = (
    "place_setting", "drinking_glass", "laptop", "water_bottle", "phone", "books", "goblets", "cutlery", "notebook",
    "plate", "picture_frame", "airpods", "brass_vase", "cup", "rotten_banana", "takeout_box", "crumbs",
)
MIN_CENTERPIECE_AREA = 0.35
AREA_PER_OTHER_PROP = 16.0
OTHER_PROP_BUDGET = (2, 8)
# Surface kind: m2 needed per prop, max props on one surface, min spacing between props.
PROP_CAPACITY = {
    "table": (0.42, 8, 0.26),
    "storage": (0.35, 4, 0.26),
    "bed": (1.50, 2, 0.60),
    "seat": (0.12, 1, 0.50),
    "floor": (12.0, 8, 1.60),
}

LIGHT_TYPES = {
    "ceiling_lamp": {"surfaces": ("ceiling",), "radius": 0.20, "height": 0.35, "range": 4.2, "intensity": 0.9, "color": "#fff1d6"},
    "table_lamp": {"surfaces": ("table", "storage"), "radius": 0.13, "height": 0.45, "range": 2.8, "intensity": 0.6, "color": "#ffd27a"},
    "floor_lamp": {"surfaces": ("floor",), "radius": 0.18, "height": 1.60, "range": 3.0, "intensity": 0.7, "color": "#ffd9a0", "near_wall": True},
    "candle": {"surfaces": ("table",), "radius": 0.04, "height": 0.14, "range": 1.5, "intensity": 0.25, "color": "#ff8a3d"},
    "window": {"surfaces": ("window",), "radius": 0.0, "height": 0.0, "range": 3.0, "intensity": 0.4, "color": "#b9d4ff"},
}
AREA_PER_CEILING_LIGHT = 35.0
MAX_CEILING_LIGHTS = 10
AREA_PER_LAMP = 40.0
MAX_LAMPS = 6
MIN_LAMP_SURFACE = 0.8
WINDOW_MIN_WALL = 1.4
WINDOW_MIN_HEIGHT = 1.9
DOOR_MIN_WALL = 1.2
DOOR_MIN_HEIGHT = 2.0
WALL_DECOR = {"picture_c": (0.60, 0.46), "picture_d": (0.56, 0.65), "picture_e": (0.42, 0.55), "mirror": (0.46, 0.70)}
AREA_PER_WALL_DECOR = 4.5  # Metres of wall per decoration.
MAX_WALL_DECOR = 8
MAX_CANDLES = 2

# Real scanned objects that plausibly make a sound, best first.
SCANNED_SOUNDS = {
    "television": ("tv_static", 0.45, 7.0), "tv": ("tv_static", 0.45, 7.0),
    "refrigerator": ("fridge_hum", 0.40, 6.0), "fridge": ("fridge_hum", 0.40, 6.0),
    "sink": ("dripping_tap", 0.35, 5.0),
    "washerdryer": ("washer_rumble", 0.40, 6.0), "washer": ("washer_rumble", 0.40, 6.0),
    "dishwasher": ("dishwasher_slosh", 0.30, 5.0),
    "oven": ("oven_tick", 0.25, 4.0), "stove": ("oven_tick", 0.25, 4.0),
    "fireplace": ("fire_crackle", 0.45, 6.0),
    "toilet": ("toilet_drip", 0.30, 4.0), "bathtub": ("toilet_drip", 0.30, 4.0),
    "window": ("wind_outside", 0.25, 5.0),
}
SOUND_PROPS = {
    "radio": {"surfaces": ("storage", "table"), "radius": 0.22, "height": 0.28, "sound": "radio_static", "volume": 0.35, "range": 5.0},
    "clock": {"surfaces": ("wall", "storage", "table"), "radius": 0.16, "height": 0.16, "sound": "clock_tick", "volume": 0.30, "range": 4.0},
}
AREA_PER_SOUND = 35.0
SOUND_COUNT = (2, 3)
CLOCK_HEIGHT = 1.9

# Fly spots per m2, max per surface and spacing, by surface kind.
LANDING_RULES = {
    "table": (6.0, 40, 0.18), "seat": (12.0, 4, 0.14), "ledge": (20.0, 3, 0.14), "storage": (5.0, 15, 0.18),
    "bed": (3.0, 15, 0.25), "object": (5.0, 6, 0.18), "floor": (0.8, 120, 0.60), "wall": (1.0, 40, 0.45),
    "ceiling": (0.5, 60, 0.60),
}
MAX_LANDING_SPOTS = 400
LANDING_RADIUS = 0.025

PLAYER_RADIUS = 0.25
PLAYER_HEIGHT = 1.8
SWING_ORIGIN_HEIGHT = 1.6  # Matches the player's eye, where Unity starts each swing.
SWAT_REACH = 2.0
START_CLEARANCE = 0.45
WALK_PASSES = ((PLAYER_RADIUS, 0.25), (0.15, 0.125))  # (body radius, grid step)
WALL_COLLIDER_HALF = 0.1
MAX_WALK_CELLS = 8000


def rounded(values):
    return [round(float(v), 3) + 0.0 for v in values]


def horizontal_components(vertices, faces):
    """Connected groups of up-facing triangles."""
    triangles = vertices[faces]
    cross = np.cross(triangles[:, 1] - triangles[:, 0], triangles[:, 2] - triangles[:, 0])
    length = np.linalg.norm(cross, axis=1)
    valid = length > 2e-5
    mask = np.zeros(len(triangles), bool)
    mask[valid] = cross[valid, 1] / length[valid] > 0.95
    upward = triangles[mask]
    if len(upward) == 0:
        return []
    keys = np.round(upward.reshape(-1, 3) * 1000).astype(np.int64)
    _, ids = np.unique(keys, axis=0, return_inverse=True)
    ids = ids.reshape(-1, 3)
    parent = list(range(int(ids.max()) + 1))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for a, b, c in ids.tolist():
        parent[find(b)] = find(a)
        parent[find(c)] = find(a)
    roots = np.array([find(int(i)) for i in ids[:, 0]])
    return [upward[roots == root] for root in np.unique(roots)]


def triangle_areas(triangles):
    return 0.5 * np.linalg.norm(np.cross(triangles[:, 1] - triangles[:, 0], triangles[:, 2] - triangles[:, 0]), axis=1)


def boundary_edges(triangles):
    keys = np.round(triangles[:, :, [0, 2]] * 1000).astype(np.int64)
    counts = {}
    for tri in keys.tolist():
        for i in range(3):
            a, b = tuple(tri[i]), tuple(tri[(i + 1) % 3])
            if a != b:
                edge = (min(a, b), max(a, b))
                counts[edge] = counts.get(edge, 0) + 1
    edges = [edge for edge, count in counts.items() if count == 1]
    if not edges:
        return np.zeros((0, 2)), np.zeros((0, 2))
    array = np.array(edges, float) / 1000
    return array[:, 0], array[:, 1]


def segment_distance(point, starts, ends):
    if len(starts) == 0:
        return math.inf
    delta = ends - starts
    t = np.clip(((point - starts) * delta).sum(1) / np.maximum((delta * delta).sum(1), 1e-9), 0, 1)
    return float(np.linalg.norm(starts + delta * t[:, None] - point, axis=1).min())


def part_role(part):
    """What a mesh is. Its group decides; the name is only a fallback for ungrouped meshes."""
    lowered = part["name"].lower()
    group = part["group"]
    if group == "Walls":
        return "joint" if lowered.startswith("joint") else "wall"
    if group == "Floors":
        return "floor"
    if group == "Ceilings":
        return "ceiling"
    if group is not None:
        return "object"
    match = re.match(r"(wall|floor|ceiling|joint)(?![a-z])", lowered)
    return match.group(1) if match else "object"


def ceiling_triangles(vertices, faces):
    """The ceiling's underside when it has one, otherwise all of its faces."""
    triangles = vertices[faces]
    cross = np.cross(triangles[:, 1] - triangles[:, 0], triangles[:, 2] - triangles[:, 0])
    length = np.linalg.norm(cross, axis=1)
    valid = length > 2e-5
    triangles, cross, length = triangles[valid], cross[valid], length[valid]
    if len(triangles) == 0:
        return triangles
    down = cross[:, 1] / length < -0.9
    return triangles[down] if down.any() else triangles


def inside(polygon, point):
    x, z = point
    result = False
    for i in range(len(polygon)):
        ax, az = polygon[i]
        bx, bz = polygon[i - 1]
        if (az > z) != (bz > z) and x < (bx - ax) * (z - az) / (bz - az) + ax:
            result = not result
    return result


class Surface:
    def __init__(self, kind, owner, triangles, normal, use_edges=True):
        self.id = ""
        self.kind = kind
        self.owner = owner
        self.triangles = triangles
        self.normal = np.array(normal, float)
        self.weights = triangle_areas(triangles)
        self.area = float(self.weights.sum())
        self.height = float(np.average(triangles[:, :, 1].mean(1), weights=self.weights))
        self.center = np.average(triangles.mean(1), axis=0, weights=self.weights)
        self.edges = boundary_edges(triangles) if use_edges else None
        self.count = 0
        self.has_light = False

    def sample(self, rng):
        triangle = self.triangles[rng.choice(len(self.triangles), p=self.weights / self.area)]
        u, v = rng.random(2)
        if u + v > 1:
            u, v = 1 - u, 1 - v
        # Rounded first so what gets validated is exactly what room.json stores.
        return np.round(triangle[0] + u * (triangle[1] - triangle[0]) + v * (triangle[2] - triangle[0]), 3)

    def edge_margin(self, point):
        if self.edges is None:
            return math.inf
        return segment_distance(point[[0, 2]], *self.edges)

    def contains(self, xz):
        a = self.triangles[:, 0, [0, 2]]
        v0 = self.triangles[:, 2, [0, 2]] - a
        v1 = self.triangles[:, 1, [0, 2]] - a
        v2 = np.asarray(xz) - a
        d00, d01, d11 = (v0 * v0).sum(1), (v0 * v1).sum(1), (v1 * v1).sum(1)
        d20, d21 = (v2 * v0).sum(1), (v2 * v1).sum(1)
        denominator = d00 * d11 - d01 * d01
        valid = np.abs(denominator) > 1e-12
        if not valid.any():
            return False
        u = (d11[valid] * d20[valid] - d01[valid] * d21[valid]) / denominator[valid]
        v = (d00[valid] * d21[valid] - d01[valid] * d20[valid]) / denominator[valid]
        return bool(np.any((u >= -1e-6) & (v >= -1e-6) & (u + v <= 1 + 1e-6)))


def classify(part, label, components):
    """Name the useful surfaces of one object, using its label and real heights."""
    found = [(float(np.average(c[:, :, 1].mean(1), weights=triangle_areas(c))), float(triangle_areas(c).sum()), c)
             for c in components]
    found = [f for f in found if f[1] >= MIN_SURFACE_AREA]
    if not found:
        return []
    group = SURFACE_CLASS.get(label, "object")
    main = None
    if group == "seat":
        seats = [f for f in found if SEAT_HEIGHT[0] <= f[0] <= SEAT_HEIGHT[1]]
        main = max(seats or found, key=lambda f: f[1])
    elif group == "bed":
        main = max(found, key=lambda f: f[1])
    else:
        large = [f for f in found if f[1] >= MIN_MAIN_SURFACE_AREA]
        main = max(large, key=lambda f: f[0]) if large else None
    return [Surface(group if f is main else "ledge", part["name"], f[2], (0, 1, 0)) for f in found]


def wall_surface(part, wall, vertices, polygon):
    start, end = np.array(wall["start"]), np.array(wall["end"])
    direction = end - start
    length = np.linalg.norm(direction)
    if length < 0.3 or wall["height"] <= 0.05:
        return None
    direction /= length
    normal = np.array([-direction[1], direction[0]])
    middle = (start + end) / 2
    centre = polygon.mean(0)
    sides = [side for side in (normal, -normal) if inside(polygon, middle + side * 0.25)]
    if not sides:
        return None
    inward = max(sides, key=lambda side: float((centre - middle) @ side))
    face = float(((vertices[:, [0, 2]] - middle) @ inward).max())
    a, b = start + inward * face, end + inward * face
    height = wall["height"]
    quad = np.array([
        [[a[0], 0, a[1]], [b[0], 0, b[1]], [b[0], height, b[1]]],
        [[a[0], 0, a[1]], [b[0], height, b[1]], [a[0], height, a[1]]],
    ])
    return Surface("wall", part["name"], quad, (inward[0], 0, inward[1]), use_edges=False)


def floor_components(vertices, faces):
    return [c for c in horizontal_components(vertices, faces) if abs(float(c[:, :, 1].max())) < 0.03]


def detect_surfaces(parts, room, offset):
    polygon = np.array(room["floor"]["polygon"])
    walls = {wall["node"]: wall for wall in room["walls"]}
    labels = {item["node"]: item["label"] for item in room["objects"]}
    surfaces = []
    for part in parts:
        name, role = part["name"], part_role(part)
        vertices = part["vertices"] + offset
        if role == "floor":
            level = floor_components(vertices, part["faces"])
            if not level:  # The mesh may be inside out.
                level = floor_components(vertices, part["faces"][:, ::-1])
            if level:
                surfaces.append(Surface("floor", name, np.vstack(level), (0, 1, 0)))
        elif role == "ceiling":
            triangles = ceiling_triangles(vertices, part["faces"])
            if len(triangles):
                surfaces.append(Surface("ceiling", name, triangles, (0, -1, 0), use_edges=False))
        elif name in walls:
            surface = wall_surface(part, walls[name], vertices, polygon)
            if surface:
                surfaces.append(surface)
        elif name in labels and labels[name] not in NOT_A_SURFACE:
            surfaces.extend(classify(part, labels[name], horizontal_components(vertices, part["faces"])))

    if not any(s.kind == "ceiling" for s in surfaces):
        floor_faces = [s.triangles for s in surfaces if s.kind == "floor"]
        if floor_faces:
            generated = np.vstack(floor_faces).copy()
            generated[:, :, 1] = room["ceilingHeight"]
            surfaces.append(Surface("ceiling", "generated_ceiling", generated, (0, -1, 0), use_edges=False))
    for index, surface in enumerate(surfaces):
        surface.id = f"S{index}"
    return surfaces


class Placer:
    def __init__(self, room, surfaces, rng):
        self.rng = rng
        self.surfaces = surfaces
        self.polygon = np.array(room["floor"]["polygon"])
        self.poly_a, self.poly_b = self.polygon, np.roll(self.polygon, -1, axis=0)
        self.wall_a = np.array([w["start"] for w in room["walls"]], float).reshape(-1, 2)
        self.wall_b = np.array([w["end"] for w in room["walls"]], float).reshape(-1, 2)
        self.wall_nodes = np.array([w["node"] for w in room["walls"]], dtype=object)
        self.openings = []
        self.ceiling = room["ceilingHeight"]
        objects = room["objects"]
        self.nodes = np.array([o["node"] for o in objects], dtype=object)
        self.centres = np.array([[o["position"][0], o["position"][2]] for o in objects], float).reshape(-1, 2)
        self.half = np.array([[o["size"][0] / 2, o["size"][2] / 2] for o in objects], float).reshape(-1, 2)
        self.bottom = np.array([o["position"][1] - o["size"][1] / 2 for o in objects], float)
        self.top = np.array([o["position"][1] + o["size"][1] / 2 for o in objects], float)
        angles = np.radians([o["rotationY"] for o in objects])
        self.u = np.column_stack([np.cos(angles), -np.sin(angles)]) if len(objects) else np.zeros((0, 2))
        self.w = np.column_stack([np.sin(angles), np.cos(angles)]) if len(objects) else np.zeros((0, 2))
        self.placed = []

    def footprint(self, xz, margin=0.0):
        delta = np.asarray(xz) - self.centres
        return (np.abs((delta * self.u).sum(1)) < self.half[:, 0] + margin) \
            & (np.abs((delta * self.w).sum(1)) < self.half[:, 1] + margin)

    def hits_object(self, point, radius, height, owner):
        if len(self.nodes) == 0:
            return False
        return bool(np.any((self.nodes != owner) & (point[1] < self.top) & (point[1] + height > self.bottom)
                           & self.footprint(point[[0, 2]], radius)))

    def hits_item(self, point, radius):
        return any(abs(point[1] - q[1]) < SAME_LEVEL and np.linalg.norm(point[[0, 2]] - q[[0, 2]]) < radius + r + ITEM_GAP
                   for q, r in self.placed)

    def valid(self, point, surface, radius, height):
        xz = point[[0, 2]]
        if surface.kind == "wall":
            ahead = point + surface.normal * (radius + 0.02)
            for opening in self.openings:
                if opening["wall"] != surface.owner:
                    continue
                offset = point - np.array(opening["center"])
                if abs(float(offset @ np.array(opening["along"]))) < opening["width"] / 2 + radius + 0.06 \
                        and abs(float(offset[1])) < opening["height"] / 2 + radius + 0.06:
                    return False
            others = self.wall_nodes != surface.owner
            if others.any() and segment_distance(xz, self.wall_a[others], self.wall_b[others]) < radius + WALL_CLEARANCE:
                return False
            return inside(self.polygon, ahead[[0, 2]]) and not self.hits_object(ahead, radius, height, None) \
                and not self.hits_item(point, radius)
        if not inside(self.polygon, xz):
            return False
        if surface.kind == "ceiling":
            return segment_distance(xz, self.poly_a, self.poly_b) >= radius and not self.hits_item(point, radius)
        if surface.edge_margin(point) < radius or segment_distance(xz, self.poly_a, self.poly_b) < radius:
            return False
        if segment_distance(xz, self.wall_a, self.wall_b) < radius + WALL_CLEARANCE:
            return False
        if point[1] + height > self.ceiling:
            return False
        return not self.hits_object(point, radius, height, surface.owner) and not self.hits_item(point, radius)

    def best_point(self, surface, radius, height, score, accept=None, tries=32, fixed_y=None, near_wall=False):
        best, best_score = None, -math.inf
        for _ in range(tries):
            point = surface.sample(self.rng)
            if fixed_y is not None:
                point[1] = round(min(fixed_y, float(surface.triangles[:, :, 1].max()) - 0.3), 3)
            if near_wall and segment_distance(point[[0, 2]], self.poly_a, self.poly_b) > NEAR_WALL:
                continue
            if accept is not None and not accept(point):
                continue
            if not self.valid(point, surface, radius, height):
                continue
            value = score(point)
            if value > best_score:
                best, best_score = point, value
        return best


class Reach:
    """Where the player can stand, and whether a swing from there can hit a point."""

    def __init__(self, placer, room):
        self.placer = placer
        self.cells = np.zeros((0, 3))
        for radius, step in WALK_PASSES:  # Tight rooms get a second, narrower pass.
            cells = self.walkable_cells(radius, step)
            if cells:
                self.keep_largest_area(cells, room)
                return
        room["warnings"].append("No walkable floor was found, so the fly has no landing spots.")

    def walkable_cells(self, radius, step):
        placer = self.placer
        polygon = placer.polygon
        floors = [s for s in placer.surfaces if s.kind == "floor"]
        probes = np.array([[0, 0], [radius, 0], [-radius, 0], [0, radius], [0, -radius]], float)
        low, high = polygon.min(0), polygon.max(0)
        step = max(step, math.sqrt(float(np.prod(high - low)) / MAX_WALK_CELLS))  # Keeps huge floors fast.
        cells = {}
        for i, x in enumerate(np.arange(low[0], high[0] + step, step)):
            for j, z in enumerate(np.arange(low[1], high[1] + step, step)):
                xz = np.array([x, z])
                if not inside(polygon, xz) or segment_distance(xz, placer.poly_a, placer.poly_b) < radius:
                    continue
                # Unity's wall colliders extend this far from the wall's centre line.
                if segment_distance(xz, placer.wall_a, placer.wall_b) < radius + WALL_COLLIDER_HALF:
                    continue
                if placer.hits_object(np.array([x, 0.01, z]), radius, PLAYER_HEIGHT, None):
                    continue
                # The outline ignores holes, so check there is real floor underfoot.
                if floors and not all(any(s.contains(xz + p) for s in floors) for p in probes):
                    continue
                cells[(i, j)] = xz
        return cells

    def keep_largest_area(self, cells, room):
        start = np.array([room["playerStart"]["position"][0], room["playerStart"]["position"][2]])
        # The player belongs in the largest connected walkable area.
        unvisited, best = set(cells), set()
        while unvisited:
            first = min(unvisited)
            seen, stack = {first}, [first]
            while stack:
                i, j = stack.pop()
                for key in ((i + 1, j), (i - 1, j), (i, j + 1), (i, j - 1)):
                    if key in cells and key not in seen:
                        seen.add(key)
                        stack.append(key)
            unvisited -= seen
            if len(seen) > len(best):
                best = seen
        ordered = sorted(best)
        nearest = min(ordered, key=lambda key: (float(np.linalg.norm(cells[key] - start)), key))
        room["playerStart"]["position"] = rounded([cells[nearest][0], room["playerStart"]["position"][1], cells[nearest][1]])
        self.cells = np.array([[cells[k][0], SWING_ORIGIN_HEIGHT, cells[k][1]] for k in ordered])

    def covered(self, point, surface):
        """True when something sits above the point, so a swatter could not get to it."""
        if surface.kind == "ceiling":
            return False
        placer = self.placer
        tip = point + surface.normal * 0.02
        if len(placer.nodes):
            over = placer.footprint(tip[[0, 2]]) & (placer.top > tip[1] + 0.02) & (placer.nodes != surface.owner)
            if over.any():
                return True
        if surface.kind == "wall":
            return False
        return any(other is not surface and other.owner == surface.owner and other.kind not in ("wall", "ceiling")
                   and other.height > tip[1] + 0.04 and other.contains(tip[[0, 2]]) for other in placer.surfaces)

    def clear_line(self, start, end, owner):
        placer = self.placer
        a, b = start[[0, 2]], end[[0, 2]]
        if len(placer.wall_a):
            wall = placer.wall_b - placer.wall_a
            line = b - a
            side_a = wall[:, 0] * (a[1] - placer.wall_a[:, 1]) - wall[:, 1] * (a[0] - placer.wall_a[:, 0])
            side_b = wall[:, 0] * (b[1] - placer.wall_a[:, 1]) - wall[:, 1] * (b[0] - placer.wall_a[:, 0])
            side_c = line[0] * (placer.wall_a[:, 1] - a[1]) - line[1] * (placer.wall_a[:, 0] - a[0])
            side_d = line[0] * (placer.wall_b[:, 1] - a[1]) - line[1] * (placer.wall_b[:, 0] - a[0])
            if np.any((side_a * side_b < 0) & (side_c * side_d < 0)):
                return False
        if len(placer.nodes) == 0:
            return True
        keep = placer.nodes != owner
        if not keep.any():
            return True
        delta = np.column_stack([((a - placer.centres[keep]) * placer.u[keep]).sum(1), start[1] * np.ones(keep.sum()),
                                 ((a - placer.centres[keep]) * placer.w[keep]).sum(1)])
        finish = np.column_stack([((b - placer.centres[keep]) * placer.u[keep]).sum(1), end[1] * np.ones(keep.sum()),
                                  ((b - placer.centres[keep]) * placer.w[keep]).sum(1)])
        low = np.column_stack([-placer.half[keep, 0] + 0.01, placer.bottom[keep] + 0.01, -placer.half[keep, 1] + 0.01])
        high = np.column_stack([placer.half[keep, 0] - 0.01, placer.top[keep] - 0.01, placer.half[keep, 1] - 0.01])
        step = finish - delta
        with np.errstate(divide="ignore", invalid="ignore"):
            first, second = (low - delta) / step, (high - delta) / step
        flat = step == 0
        outside = flat & ((delta < low) | (delta > high))
        first = np.where(flat, -np.inf, first)
        second = np.where(flat, np.inf, second)
        enter = np.maximum(np.minimum(first, second).max(1), 0)
        leave = np.minimum(np.maximum(first, second).min(1), 1)
        return not np.any((enter <= leave) & ~outside.any(1))

    def reachable(self, point, surface):
        if len(self.cells) == 0 or self.covered(point, surface):
            return False
        tip = point + surface.normal * 0.04
        distance = np.linalg.norm(self.cells - tip, axis=1)
        near = np.where(distance <= SWAT_REACH)[0]
        if len(near) == 0:
            return False
        order = near[np.argsort(distance[near], kind="stable")][:10]
        return any(self.clear_line(self.cells[i], tip, surface.owner) for i in order)


def coverage_grid(polygon, step=0.5):
    low, high = polygon.min(0), polygon.max(0)
    points = [(x, z) for x in np.arange(low[0] + step / 2, high[0], step) for z in np.arange(low[1] + step / 2, high[1], step)
              if inside(polygon, (x, z))]
    return np.array(points) if points else polygon.mean(0, keepdims=True)


def light_coverage(polygon, lights):
    grid = coverage_grid(polygon)
    if not lights:
        return {"coveredPercent": 0.0, "darkestDistance": None}
    positions = np.array([[l["position"][0], l["position"][2]] for l in lights])
    ranges = np.array([l.get("range", LIGHT_TYPES.get(l["type"], {}).get("range", 3.0)) for l in lights])
    distance = np.linalg.norm(grid[:, None] - positions[None], axis=2)
    return {
        "coveredPercent": round(float(np.mean((distance <= ranges).any(1))) * 100, 1),
        "darkestDistance": round(float(distance.min(1).max()), 2),
    }


def cluster_centres(grid, count):
    """Evenly spread centres: farthest-point start, then k-means. Uses no randomness."""
    centres = [grid[int(np.argmin(np.linalg.norm(grid - grid.mean(0), axis=1)))]]
    while len(centres) < min(count, len(grid)):
        distance = np.linalg.norm(grid[:, None] - np.array(centres)[None], axis=2).min(1)
        centres.append(grid[int(np.argmax(distance))])
    centres = np.array(centres, float)
    for _ in range(25):
        nearest = np.linalg.norm(grid[:, None] - centres[None], axis=2).argmin(1)
        updated = np.array([grid[nearest == i].mean(0) if np.any(nearest == i) else centres[i] for i in range(len(centres))])
        if np.allclose(updated, centres, atol=1e-4):
            break
        centres = updated
    return centres


def make_light(kind, point, surface, item_id):
    rule = LIGHT_TYPES[kind]
    lift = rule["height"] * (0.9 if surface.kind in ("floor", "table", "storage") else 1)
    return {
        "id": item_id,
        "type": kind,
        "position": rounded(point + surface.normal * lift),
        "mount": rounded(point),
        "normal": rounded(surface.normal),
        "surface": surface.owner,
        "surfaceId": surface.id,
        "surfaceKind": surface.kind,
        "intensity": rule["intensity"],
        "range": rule["range"],
        "color": rule["color"],
    }


def make_prop(kind, point, surface, rotation, item_id):
    rule = PROP_TYPES[kind]
    return {
        "id": item_id,
        "type": kind,
        "position": rounded(point),
        "rotationY": round(float(rotation), 1),
        "surface": surface.owner,
        "surfaceId": surface.id,
        "surfaceKind": surface.kind,
        "radius": rule["radius"],
        "height": rule["height"],
    }


def make_sound_prop(kind, point, surface, item_id):
    rule = SOUND_PROPS[kind]
    return {
        "id": item_id,
        "sound": rule["sound"],
        "position": rounded(point),
        "normal": rounded(surface.normal),
        "source": "prop",
        "prop": kind,
        "sourceObject": surface.owner,
        "surfaceId": surface.id,
        "surfaceKind": surface.kind,
        "radius": rule["radius"],
        "height": rule["height"],
        "volume": rule["volume"],
        "range": rule["range"],
        "loop": True,
    }


def prop_capacity(surface):
    rule = PROP_CAPACITY.get(surface.kind)
    if rule is None or surface.area < rule[0] * 0.5:
        return 0
    return min(rule[1], max(1, int(surface.area / rule[0])))


class PlacementContext:
    def __init__(self, room, surfaces, seed):
        self.room = room
        self.surfaces = surfaces
        self.by_id = {s.id: s for s in surfaces}
        self.seed = seed
        self.placer = Placer(room, surfaces, np.random.default_rng(seed))
        self.polygon = self.placer.polygon
        self.reach = Reach(self.placer, room)

    def darkness(self, point, lights):
        if not lights:
            return 1.0
        return min(float(np.linalg.norm(point[[0, 2]] - np.array([l["position"][0], l["position"][2]]))) / l["range"]
                   for l in lights)

    def scanned_opening(self, item):
        """A scanned door, window or opening, tied to its wall and facing into the room."""
        angle = math.radians(item["rotationY"])
        along = np.array([math.cos(angle), 0.0, -math.sin(angle)])
        normal = np.array([math.sin(angle), 0.0, math.cos(angle)])
        middle = np.array([item["position"][0], item["position"][2]])
        walls = [s for s in self.surfaces if s.kind == "wall"]
        nearest = min(walls, key=lambda s: (segment_distance(middle, s.triangles[0][0][[0, 2]][None],
                                                             s.triangles[0][1][[0, 2]][None]), s.id), default=None)
        if nearest is not None:
            if float(normal @ nearest.normal) < 0:
                normal = -normal
        elif not inside(self.polygon, middle + normal[[0, 2]] * 0.25):
            normal = -normal
        return {"source": "scanned", "wall": nearest.owner if nearest else None, "center": item["position"],
                "normal": rounded(normal), "along": rounded(along), "width": item["size"][0], "height": item["size"][1]}

    def scanned_doors(self):
        doors = []
        for item in sorted((o for o in self.room["objects"] if o["label"] == "door"), key=lambda o: o["node"]):
            door = self.scanned_opening(item)
            face = np.array(item["position"], float) + np.array(door["normal"]) * item["size"][2] / 2
            face[1] = item["size"][1] / 2
            door["center"] = rounded(face)
            doors.append(door)
        return doors

    def choose_window(self, scanned_doors):
        """Uses a scanned window, or fits a new one to the clearest stretch of wall."""
        scanned = sorted((o for o in self.room["objects"] if o["label"] == "window"), key=lambda o: o["node"])
        if scanned:
            return self.scanned_opening(scanned[0])

        best, best_score = None, -math.inf
        for surface in (s for s in self.surfaces if s.kind == "wall"):
            start, end = surface.triangles[0][0], surface.triangles[0][1]
            length = float(np.linalg.norm(end - start))
            wall_height = float(surface.triangles[:, :, 1].max())
            if length < WINDOW_MIN_WALL or wall_height < WINDOW_MIN_HEIGHT:
                continue
            along = (end - start) / length
            width = float(np.clip(length * 0.3, 0.7, min(2.2, length - 0.6)))
            height = float(np.clip(wall_height * 0.42, 0.8, 1.7))
            sill = float(np.clip(wall_height * 0.34, 0.55, wall_height - height - 0.22))
            for offset in np.arange(width / 2 + 0.25, length - width / 2 - 0.25 + 1e-6, 0.2):
                base = start + along * offset
                if any(door["wall"] == surface.owner and abs(float((base - np.array(door["center"])) @ along))
                       < (width + door["width"]) / 2 + 0.2 for door in scanned_doors):
                    continue
                blocked = 0
                for side in np.linspace(-width / 2, width / 2, 5):
                    probe = base + along * side + surface.normal * 0.25
                    if len(self.placer.nodes) and np.any(self.placer.footprint(probe[[0, 2]], 0.15) & (self.placer.top > sill)):
                        blocked += 1
                score = -10.0 * blocked + length * 0.4 - abs(float(offset) - length / 2) * 0.25
                if score > best_score:
                    best_score = score
                    best = {"source": "generated", "wall": surface.owner,
                            "center": rounded([base[0], sill + height / 2, base[2]]), "normal": rounded(surface.normal),
                            "along": rounded(along), "width": round(width, 3), "height": round(height, 3)}
        return best

    def choose_doors(self, scanned_doors):
        """Uses every door found in the scan, or fits one to a clear stretch of wall."""
        if scanned_doors:
            return scanned_doors
        walls = [s for s in self.surfaces if s.kind == "wall"]
        best, best_score = None, -math.inf
        for surface in walls:
            start, end = surface.triangles[0][0], surface.triangles[0][1]
            length = float(np.linalg.norm(end - start))
            wall_height = float(surface.triangles[:, :, 1].max())
            if length < DOOR_MIN_WALL or wall_height < DOOR_MIN_HEIGHT:
                continue
            along = (end - start) / length
            width = float(np.clip(length * 0.4, 0.75, min(0.95, length - 0.4)))
            height = float(min(2.05, wall_height - 0.12))
            on_wall = [o for o in self.placer.openings if o["wall"] == surface.owner]
            same_wall = bool(on_wall)
            for offset in np.arange(width / 2 + 0.15, length - width / 2 - 0.15 + 1e-6, 0.15):
                base = start + along * offset
                if any(abs(float((base - np.array(o["center"])) @ along)) < (width + o["width"]) / 2 + 0.3 for o in on_wall):
                    continue
                blocked = 0
                for side in np.linspace(-width / 2, width / 2, 4):
                    for depth in (0.35, 0.75):
                        probe = base + along * side + surface.normal * depth
                        if len(self.placer.nodes) and np.any(self.placer.footprint(probe[[0, 2]], 0.08) & (self.placer.top > 0.2)):
                            blocked += 1
                    # A door needs open floor in front of it, not a cramped alcove.
                    front = (base + along * side + surface.normal * 1.1)[[0, 2]]
                    others = self.placer.wall_nodes != surface.owner
                    if not inside(self.polygon, front) or (others.any() and segment_distance(
                            front, self.placer.wall_a[others], self.placer.wall_b[others]) < 0.4):
                        blocked += 2
                score = -10.0 * blocked + (0.0 if same_wall else 1.0) + min(length, 6.0) * 0.22 \
                    - min(float(offset), length - float(offset)) * 0.04
                if score > best_score:
                    best_score = score
                    best = {"source": "generated", "wall": surface.owner, "center": rounded([base[0], height / 2, base[2]]),
                            "normal": rounded(surface.normal), "along": rounded(along), "width": round(width, 3), "height": round(height, 3)}
        return [best] if best is not None else []

    def place_wall_decor(self):
        """A few pictures and mirrors, sized to fit each wall at eye level."""
        placer = self.placer
        walls = sorted((s for s in self.surfaces if s.kind == "wall"),
                       key=lambda s: (-float(np.linalg.norm(s.triangles[0][1] - s.triangles[0][0])), s.id))
        total = sum(float(np.linalg.norm(s.triangles[0][1] - s.triangles[0][0])) for s in walls)
        target = int(np.clip(round(total / AREA_PER_WALL_DECOR), 1, MAX_WALL_DECOR)) if walls else 0
        order = [list(WALL_DECOR)[int(i)] for i in placer.rng.permutation(len(WALL_DECOR))]
        decor = []
        for attempt in range(target * 4):
            if len(decor) >= target or not walls:
                break
            surface = walls[attempt % len(walls)]
            kind = order[len(decor) % len(order)]
            start, end = surface.triangles[0][0], surface.triangles[0][1]
            length = float(np.linalg.norm(end - start))
            wall_height = float(surface.triangles[:, :, 1].max())
            width, height = WALL_DECOR[kind]
            fit = min(1.0, (wall_height - 0.7) / height, (length - 0.5) / width)
            if fit < 0.5:
                continue
            width, height = width * fit, height * fit
            along = (end - start) / length
            y = float(np.clip(wall_height * 0.56, height / 2 + 0.85, wall_height - height / 2 - 0.2))
            best, best_gap = None, -math.inf
            for _ in range(28):
                offset = float(placer.rng.uniform(width / 2 + 0.2, length - width / 2 - 0.2))
                point = start + along * offset
                point[1] = y
                if not placer.valid(point, surface, max(width, height) / 2 + 0.03, 0.06):
                    continue
                gap = min((float(np.linalg.norm(point - np.array(d["center"]))) for d in decor), default=length)
                if gap > best_gap:
                    best, best_gap = point, gap
            if best is None or best_gap < 1.0:
                continue
            item = {"id": f"W{len(decor)}", "type": kind, "wall": surface.owner, "center": rounded(best),
                    "normal": rounded(surface.normal), "along": rounded(along), "width": round(width, 3), "height": round(height, 3)}
            decor.append(item)
            placer.openings.append(item)
            placer.placed.append((best, max(width, height) / 2))
        return decor

    def place_lights(self):
        placer, lights = self.placer, []

        def add(kind, point, surface):
            lights.append(make_light(kind, point, surface, f"L{len(lights)}"))
            placer.placed.append((point, LIGHT_TYPES[kind]["radius"]))
            surface.has_light = True

        for item in sorted(self.room["objects"], key=lambda o: o["node"]):
            if item["label"] == "window":
                rule = LIGHT_TYPES["window"]
                lights.append({
                    "id": f"L{len(lights)}", "type": "window", "position": item["position"], "mount": item["position"],
                    "normal": [0.0, 0.0, 0.0], "surface": item["node"], "surfaceId": "", "surfaceKind": "window",
                    "intensity": rule["intensity"], "range": rule["range"], "color": rule["color"],
                })

        area = self.room["floor"]["area"]
        grid = coverage_grid(self.polygon)
        ceilings = [s for s in self.surfaces if s.kind == "ceiling"]
        if ceilings:
            rule = LIGHT_TYPES["ceiling_lamp"]
            count = int(np.clip(math.ceil(area / AREA_PER_CEILING_LIGHT), 1, MAX_CEILING_LIGHTS))
            for centre in cluster_centres(grid, count):
                for index in np.argsort(np.linalg.norm(grid - centre, axis=1), kind="stable"):
                    xz = grid[index]
                    surface = next((s for s in ceilings if s.contains(xz)), None)
                    if surface is None:
                        continue
                    point = np.round([xz[0], surface.height, xz[1]], 3)
                    if placer.valid(point, surface, rule["radius"], rule["height"]):
                        add("ceiling_lamp", point, surface)
                        break

        def place_near_centre(kind, options, wanted):
            rule = LIGHT_TYPES[kind]
            placed = 0
            while placed < wanted:
                remaining = [s for s in options if not s.has_light]
                remaining.sort(key=lambda s: (-self.darkness(s.center, lights), s.id))
                for surface in remaining:
                    point = placer.best_point(surface, rule["radius"], rule["height"],
                                              lambda p: -float(np.linalg.norm(p - surface.center)))
                    if point is not None:
                        add(kind, point, surface)
                        placed += 1
                        break
                else:
                    break
            return placed

        large = [s for s in self.surfaces if s.kind in LIGHT_TYPES["table_lamp"]["surfaces"] and s.area >= MIN_LAMP_SURFACE]
        lamps = place_near_centre("table_lamp", large, int(np.clip(round(area / AREA_PER_LAMP), 1, MAX_LAMPS))) if large else 0

        tables = [s for s in self.surfaces if s.kind == "table" and not s.has_light]
        if tables:
            place_near_centre("candle", tables, min(MAX_CANDLES, math.ceil(len(tables) / 3)))

        if not lamps:
            rule = LIGHT_TYPES["floor_lamp"]
            for surface in (s for s in self.surfaces if s.kind == "floor"):
                point = placer.best_point(surface, rule["radius"], rule["height"], lambda p: self.darkness(p, lights),
                                          tries=64, near_wall=True)
                if point is not None:
                    add("floor_lamp", point, surface)
                    break
        return lights

    def place_sounds(self):
        placer, anchors, used = self.placer, [], set()
        area = self.room["floor"]["area"]
        target = int(np.clip(round(area / AREA_PER_SOUND) + 1, *SOUND_COUNT))
        order = {label: index for index, label in enumerate(SCANNED_SOUNDS)}
        scanned = sorted((o for o in self.room["objects"] if o["label"] in SCANNED_SOUNDS),
                         key=lambda o: (order[o["label"]], o["node"]))
        for item in scanned:
            sound, volume, reach = SCANNED_SOUNDS[item["label"]]
            if len(anchors) >= target or sound in used:
                continue
            used.add(sound)
            anchors.append({
                "id": f"A{len(anchors)}", "sound": sound, "position": item["position"], "normal": [0.0, 1.0, 0.0],
                "source": "scanned", "prop": None, "sourceObject": item["node"], "surfaceId": "",
                "surfaceKind": item["label"], "radius": 0.0, "height": 0.0, "volume": volume, "range": reach, "loop": True,
            })

        def spread(point):
            if not anchors:
                return float(np.linalg.norm(point[[0, 2]] - self.polygon.mean(0)))
            return min(float(np.linalg.norm(point[[0, 2]] - np.array([a["position"][0], a["position"][2]]))) for a in anchors)

        for kind, rule in SOUND_PROPS.items():
            if len(anchors) >= target:
                break
            options = [s for s in self.surfaces if s.kind in rule["surfaces"]]
            options.sort(key=lambda s: (s.kind == "table" and s.has_light, -spread(s.center), s.id))
            for surface in options:
                fixed = CLOCK_HEIGHT if surface.kind == "wall" else None
                point = placer.best_point(surface, rule["radius"], rule["height"], spread, fixed_y=fixed)
                if point is not None:
                    anchors.append(make_sound_prop(kind, point, surface, f"A{len(anchors)}"))
                    placer.placed.append((point, rule["radius"]))
                    break
        return anchors

    def place_props(self):
        placer, props, counts = self.placer, [], Counter()
        for surface in self.surfaces:
            surface.count = 0
        tables = sorted((s for s in self.surfaces if s.kind == "table"), key=lambda s: (-s.area, s.id))
        limit_scale = 1 + len(tables) // 2
        chosen = []

        chosen_kinds = []

        def far_enough(point, spacing, surface_kind):
            return all(other != surface_kind or abs(point[1] - q[1]) >= SAME_LEVEL
                       or np.linalg.norm(point[[0, 2]] - q[[0, 2]]) >= spacing
                       for q, other in zip(chosen, chosen_kinds))

        def isolation(point):
            if not chosen:
                return 0.0
            return min(float(np.linalg.norm(point[[0, 2]] - q[[0, 2]])) for q in chosen)

        def add(kind, surface, central=False):
            rule = PROP_TYPES[kind]
            if counts[kind] >= rule["limit"] * limit_scale or surface.kind not in rule["surfaces"] \
                    or surface.count >= prop_capacity(surface):
                return False
            spacing = PROP_CAPACITY[surface.kind][2]
            score = (lambda p: -float(np.linalg.norm(p - surface.center))) if central else isolation
            point = placer.best_point(surface, rule["radius"], rule["height"], score,
                                      accept=lambda p: far_enough(p, spacing, surface.kind), near_wall=rule.get("near_wall", False))
            if point is None:
                return False
            props.append(make_prop(kind, point, surface, placer.rng.uniform(0, 360), f"P{len(props)}"))
            placer.placed.append((point, rule["radius"]))
            chosen.append(point)
            chosen_kinds.append(surface.kind)
            surface.count += 1
            counts[kind] += 1
            return True

        for index, surface in enumerate(tables):
            if surface.area < MIN_CENTERPIECE_AREA:
                continue
            for step in range(len(CENTERPIECES)):
                if add(CENTERPIECES[(index + step) % len(CENTERPIECES)], surface, central=True):
                    break

        for surface in tables:
            for item in placer.rng.permutation(len(TABLE_ITEMS)):
                if surface.count >= prop_capacity(surface):
                    break
                add(TABLE_ITEMS[int(item)], surface)

        others = sorted((s for s in self.surfaces if s.kind in PROP_CAPACITY and s.kind != "table"), key=lambda s: (-s.area, s.id))
        budget = int(np.clip(round(self.room["floor"]["area"] / AREA_PER_OTHER_PROP), *OTHER_PROP_BUDGET))
        placed_others = 0
        progress = True
        while progress and placed_others < budget:
            progress = False
            for kind, rule in PROP_TYPES.items():
                if placed_others >= budget:
                    break
                options = [s for s in others if s.kind in rule["surfaces"] and s.count < prop_capacity(s)]
                options.sort(key=lambda s: (s.count / prop_capacity(s), -s.area, s.id))
                for surface in options:
                    if add(kind, surface):
                        placed_others += 1
                        progress = True
                        break
        return props

    def place_landing_spots(self):
        placer, spots = self.placer, []
        kinds = list(LANDING_RULES)
        ordered = sorted((s for s in self.surfaces if s.kind in LANDING_RULES), key=lambda s: (kinds.index(s.kind), s.id))

        def wanted(surface):
            density, maximum, _ = LANDING_RULES[surface.kind]
            return min(maximum, max(1, math.ceil(surface.area * density)))

        expected = sum(wanted(s) for s in ordered)
        factor = min(1.0, MAX_LANDING_SPOTS / expected) if expected else 1.0
        for surface in ordered:
            target = max(1, int(wanted(surface) * factor))
            spacing = LANDING_RULES[surface.kind][2]
            chosen = []
            for _ in range(target * 25):
                if len(chosen) >= target or len(spots) >= MAX_LANDING_SPOTS:
                    break
                point = surface.sample(placer.rng)
                if surface.kind == "wall" and not 0.15 <= point[1] <= surface.triangles[:, :, 1].max() - 0.1:
                    continue
                if any(np.linalg.norm(point - q) < spacing for q in chosen):
                    continue
                if not placer.valid(point, surface, LANDING_RADIUS, 0.03) or not self.reach.reachable(point, surface):
                    continue
                chosen.append(point)
                spots.append({
                    "id": f"F{len(spots)}",
                    "position": rounded(point),
                    "normal": rounded(surface.normal),
                    "surface": surface.owner,
                    "surfaceId": surface.id,
                    "surfaceKind": surface.kind,
                })
        return spots

    def start_clearance(self):
        start = self.room["playerStart"]["position"]
        return np.array([start[0], 0.0, start[2]]), START_CLEARANCE

    def footprint_items(self, layout, exclude=None):
        items = [self.start_clearance()]
        for prop in layout["props"]:
            if prop["id"] != exclude:
                items.append((np.array(prop["position"], float), prop["radius"]))
        for light in layout["lights"]:
            if light["id"] != exclude and light["type"] != "window":
                items.append((np.array(light["mount"], float), LIGHT_TYPES[light["type"]]["radius"]))
        for anchor in layout["soundAnchors"]:
            if anchor["id"] != exclude and anchor["source"] == "prop":
                items.append((np.array(anchor["position"], float), anchor["radius"]))
        return items

    def item_rule(self, item):
        if "sound" in item:
            return SOUND_PROPS.get(item.get("prop"))
        return PROP_TYPES.get(item["type"]) or LIGHT_TYPES.get(item["type"])

    def item_problem(self, item, layout, exclude=None):
        """Why this item breaks the placement rules, or None if it is valid."""
        rule = self.item_rule(item)
        if rule is None:
            return "unknown item type"
        surface = self.by_id.get(item.get("surfaceId"))
        if surface is None:
            return "unknown surface"
        if surface.kind not in rule["surfaces"]:
            return f"not allowed on {surface.kind}"
        point = np.array(item.get("mount", item["position"]), float)
        if surface.kind != "wall":
            heights = surface.triangles[:, :, 1]
            if not heights.min() - 0.03 <= point[1] <= heights.max() + 0.03 or not surface.contains(point[[0, 2]]):
                return "not on its surface"
        placer = self.placer
        saved = placer.placed
        placer.placed = self.footprint_items(layout, exclude)
        try:
            if not placer.valid(point, surface, rule["radius"], rule["height"]):
                return "fails clearance, collision or inside-room check"
        finally:
            placer.placed = saved
        if rule.get("near_wall") and segment_distance(point[[0, 2]], placer.poly_a, placer.poly_b) > NEAR_WALL:
            return "must be near a wall"
        if "sound" not in item and item["type"] in PROP_TYPES:
            capacity = prop_capacity(surface)
            on_surface = sum(1 for p in layout["props"] if p["id"] != exclude and p["surfaceId"] == surface.id)
            if on_surface >= capacity:
                return "surface is at its prop limit"
            spacing = PROP_CAPACITY[surface.kind][2]
            for other in layout["props"]:
                q = np.array(other["position"], float)
                if other["id"] != exclude and other["surfaceKind"] == surface.kind and abs(point[1] - q[1]) < SAME_LEVEL \
                        and np.linalg.norm(point[[0, 2]] - q[[0, 2]]) < spacing:
                    return "too close to another prop"
        return None

    def validate_layout(self, layout):
        problems = []
        groups = [("props", layout["props"]), ("lights", [l for l in layout["lights"] if l["type"] != "window"]),
                  ("soundAnchors", [a for a in layout["soundAnchors"] if a["source"] == "prop"])]
        for name, items in groups:
            for item in items:
                problem = self.item_problem(item, layout, exclude=item["id"])
                if problem:
                    problems.append(f"{name} {item['id']}: {problem}")
        for spot in layout["landingSpots"]:
            surface = self.by_id.get(spot["surfaceId"])
            if surface is None or not self.reach.reachable(np.array(spot["position"], float), surface):
                problems.append(f"landingSpots {spot['id']}: not reachable")
        return problems

    def drop_blocked_spots(self, layout):
        items = self.footprint_items(layout)
        kept = []
        for spot in layout["landingSpots"]:
            point = np.array(spot["position"], float)
            if all(abs(point[1] - q[1]) >= SAME_LEVEL
                   or np.linalg.norm(point[[0, 2]] - q[[0, 2]]) >= LANDING_RADIUS + r + ITEM_GAP for q, r in items):
                kept.append(spot)
        layout["landingSpots"] = kept

    def candidates(self, per_surface=4):
        """Pre-checked positions an AI edit may move an item to."""
        rng = np.random.default_rng(self.seed ^ 0x5F5F5F5F)
        pool = []
        for surface in self.surfaces:
            if surface.kind not in ("table", "storage", "seat", "bed", "floor", "wall"):
                continue
            count = per_surface * (3 if surface.kind == "floor" else 1)
            found = 0
            for _ in range(count * 12):
                if found >= count:
                    break
                point = surface.sample(rng)
                if surface.kind == "wall":
                    point[1] = min(CLOCK_HEIGHT, float(surface.triangles[:, :, 1].max()) - 0.3)
                if self.placer.valid(point, surface, 0.1, 0.1):
                    pool.append({"id": f"C{len(pool)}", "surfaceId": surface.id, "surfaceKind": surface.kind,
                                 "position": rounded(point)})
                    found += 1
        return pool

    def surface_summary(self):
        return [{
            "id": s.id,
            "node": s.owner,
            "kind": s.kind,
            "height": round(s.height, 3),
            "area": round(s.area, 3),
            "normal": rounded(s.normal),
        } for s in self.surfaces]


def place_items(parts, room, offset, seed):
    surfaces = detect_surfaces(parts, room, offset)
    context = PlacementContext(room, surfaces, seed)
    placer = context.placer
    placer.placed.append(context.start_clearance())
    scanned_doors = context.scanned_doors()
    window = context.choose_window(scanned_doors)
    fixed = [context.scanned_opening(o) for o in room["objects"] if o["label"] in ("window", "opening")]
    if window is not None and window["source"] == "generated":
        fixed.append(window)
    placer.openings.extend(o for o in fixed + scanned_doors if o["wall"] is not None)
    doors = context.choose_doors(scanned_doors)
    if not scanned_doors:
        placer.openings.extend(d for d in doors if d["wall"] is not None)
    lights = context.place_lights()
    wall_decor = context.place_wall_decor()
    sounds = context.place_sounds()
    props = context.place_props()
    landing = context.place_landing_spots()
    layout = {
        "placementSeed": seed,
        "surfaces": context.surface_summary(),
        "landingSpots": landing,
        "props": props,
        "lights": lights,
        "soundAnchors": sounds,
        "window": window,
        "doors": doors,
        "wallDecor": wall_decor,
        "lightCoverage": light_coverage(context.polygon, lights),
    }
    return layout, context
