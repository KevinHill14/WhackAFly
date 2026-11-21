"""Fly game logic. Pure Python, no I/O, so it is easy to test and runs on QNX.

The Unity client owns the room, physics, rendering and audio. This module owns
the fly: where it sits, when it takes off, how it escapes, and the score.
All positions are Unity world coordinates in metres (x right, y up, z forward).
"""
import math
import random

TICK_HZ = 30
SWAT_REACH = 1.6        # metres from the head; Unity sends its own value too
HIT_RADIUS = 0.28       # how close the swing line must pass to the fly
LOOM_RANGE = 2.2        # swatter tip closer than this starts to look threatening
RESPAWN_S = 2.0
FLY_SPEED = 1.4         # m/s, idle hop between spots
ESCAPE_SPEED = 4.5      # m/s at escape strength 1


def _sub(a, b): return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
def _add(a, b): return [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
def _mul(a, k): return [a[0] * k, a[1] * k, a[2] * k]
def _dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def _len(a): return math.sqrt(_dot(a, a))


def _unit(a):
    n = _len(a)
    return [0.0, 0.0, 1.0] if n < 1e-6 else [a[0] / n, a[1] / n, a[2] / n]


def _dist_point_segment(p, a, b):
    ab = _sub(b, a)
    t = max(0.0, min(1.0, _dot(_sub(p, a), ab) / max(_dot(ab, ab), 1e-9)))
    return _len(_sub(p, _add(a, _mul(ab, t))))


class FlyGame:
    def __init__(self, rng=None):
        self.rng = rng or random.Random()
        self.spots = []              # list of {"id", "position", "normal"}
        self.reset()

    def reset(self):
        self.score = 0
        self.swings = 0
        self.escapes = 0
        self.state = "idle" if not self.spots else "landed"
        self.pos = list(self.spots[0]["position"]) if self.spots else [0.0, 1.0, 0.0]
        self.spot = 0
        self.heading = [0.0, 0.0, 1.0]
        self.target = None
        self.speed = FLY_SPEED
        self.timer = 1.0
        self.head = [0.0, 1.6, 0.0]
        self.tip = None
        self.tip_speed = 0.0
        self.spikes = None
        self.events = []             # drained by the server each tick

    # ---- inputs from the client ---------------------------------------
    def set_room(self, spots, head=None):
        self.spots = [s for s in spots if len(s.get("position", [])) == 3]
        if head:
            self.head = list(head)
        self.reset()

    def set_pose(self, head, aim, reach=SWAT_REACH, dt=1.0 / TICK_HZ):
        self.head = list(head)
        tip = _add(head, _mul(_unit(aim), reach))
        if self.tip is not None and dt > 0:
            self.tip_speed = _len(_sub(tip, self.tip)) / dt
        self.tip = tip

    def swing(self, origin, direction, strength, reach=SWAT_REACH):
        """Returns True if the fly was squished."""
        self.swings += 1
        if self.state in ("dead", "idle"):
            return False
        end = _add(origin, _mul(_unit(direction), reach))
        if _dist_point_segment(self.pos, origin, end) <= HIT_RADIUS:
            self.state = "dead"
            self.timer = RESPAWN_S
            self.score += 1
            self.events.append({"kind": "squish", "pos": list(self.pos), "score": self.score})
            return True
        return False

    # ---- looming sent to the brain ------------------------------------
    def looming(self):
        """(value 0-1, side) describing how threatening the swatter looks."""
        if self.tip is None or self.state in ("dead", "idle"):
            return 0.0, "center"
        to_tip = _sub(self.tip, self.pos)
        d = _len(to_tip)
        if d >= LOOM_RANGE:
            return 0.0, "center"
        closeness = 1.0 - d / LOOM_RANGE
        motion = min(1.0, 0.25 + self.tip_speed / 3.0)
        value = max(0.0, min(1.0, closeness * motion * 1.6))
        # Which eye sees it: sign of the swatter relative to the fly's heading.
        cross_y = self.heading[2] * to_tip[0] - self.heading[0] * to_tip[2]
        side = "left" if cross_y > 0.05 else "right" if cross_y < -0.05 else "center"
        return value, side

    def on_escape(self, strength, side):
        """Brain reply. `side` is the direction the fly jumps."""
        if self.state in ("dead", "idle") or strength < 0.3 or not self.spots:
            return
        away = _sub(self.pos, self.tip if self.tip else self.head)
        away[1] = 0.0
        away = _unit(away)
        # Turn the jump toward the brain's chosen side (+/-35 degrees).
        ang = math.radians(35) * (1 if side == "right" else -1)
        c, s = math.cos(ang), math.sin(ang)
        want = _unit([away[0] * c - away[2] * s, 0.0, away[0] * s + away[2] * c])
        best, best_score = None, -9.0
        for i, sp in enumerate(self.spots):
            if i == self.spot:
                continue
            v = _sub(sp["position"], self.pos)
            d = _len(v)
            if d < 0.4 or d > 4.0:
                continue
            # Prefer spots in the chosen direction and away from the swatter.
            tip = self.tip if self.tip else self.head
            score = _dot(_unit([v[0], 0.0, v[2]]), want) + 0.15 * _len(_sub(sp["position"], tip))
            if score > best_score:
                best, best_score = i, score
        if best is None:
            return
        self.spot = best
        self.target = list(self.spots[best]["position"])
        self.speed = FLY_SPEED + (ESCAPE_SPEED - FLY_SPEED) * max(0.0, min(1.0, strength))
        self.state = "fleeing"
        self.escapes += 1
        self.events.append({"kind": "escape", "side": side, "strength": strength})

    # ---- simulation ----------------------------------------------------
    def tick(self, dt=1.0 / TICK_HZ):
        if not self.spots:
            return
        if self.state == "dead":
            self.timer -= dt
            if self.timer <= 0:
                self._respawn()
            return
        if self.state == "landed":
            self.timer -= dt
            if self.timer <= 0:
                self._hop()
            return
        if self.target is not None:                       # flying or fleeing
            v = _sub(self.target, self.pos)
            d = _len(v)
            step = self.speed * dt
            if d <= step:
                self.pos = list(self.target)
                self.target = None
                self.state = "landed"
                self.timer = self.rng.uniform(1.5, 4.0)
                return
            self.heading = _unit(v)
            self.pos = _add(self.pos, _mul(self.heading, step))
            # A little buzz so it does not travel in a perfect line.
            self.pos[1] += math.sin(self.rng.random() * 6.28) * 0.004

    def _hop(self):
        options = [i for i, s in enumerate(self.spots)
                   if i != self.spot and 0.3 < _len(_sub(s["position"], self.pos)) < 3.0]
        if not options:
            self.timer = 2.0
            return
        self.spot = self.rng.choice(options)
        self.target = list(self.spots[self.spot]["position"])
        self.speed = FLY_SPEED
        self.state = "flying"

    def _respawn(self):
        self.spot = self.rng.randrange(len(self.spots))
        self.pos = list(self.spots[self.spot]["position"])
        self.target = None
        self.state = "landed"
        self.timer = self.rng.uniform(1.0, 2.5)

    def snapshot(self):
        return {
            "type": "fly",
            "state": self.state,
            "pos": [round(v, 3) for v in self.pos],
            "heading": [round(v, 3) for v in self.heading],
            "distance": round(_len(_sub(self.pos, self.head)), 3),
            "score": self.score,
            "swings": self.swings,
            "spikes": self.spikes,
        }
