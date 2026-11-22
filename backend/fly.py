"""The full fly, run on the Raspberry Pi. Standard library only.

Everything the fly decides or does lives here: its personality, perching, grooming, walking along surfaces, what it
perceives, what the brain's reply makes it do, and its flight routes. Unity draws it and reports what only Unity can
know: how bright the room is at the fly, the flashlight beam, room events, and which flight paths are clear of walls.

    Input   looming (0-1, which eye) and sound (0-1, which ear), sent to brain/server.py at 20 Hz
    Output  escape strength and jump side, from the connectome
              strength >= ESCAPE_THRESHOLD  takes off; side sets the first turn, strength the speed and distance
              strength >= FLEE_THRESHOLD    walks away from the threat, toward the side the brain chose
All positions are Unity world coordinates in metres.
"""
import math

from game import FlyGame, RESPAWN_S, SWAT_REACH, _add, _dot, _len, _mul, _sub, _unit, vec3

ESCAPE_THRESHOLD = 0.4
FLEE_THRESHOLD = 0.12
WALK_SPEED = 0.1            # metres per second while strolling
LIFT = 0.012                # how far above a surface it stands
MIN_VISION = 0.12
NEIGHBOUR_RADIUS = 0.6      # landing spots this close on one surface are joined for walking
SOUND_SECONDS = 0.35
SENSES_MAX_AGE = 0.6        # seconds before Unity's light report counts as missing
UP = [0.0, 1.0, 0.0]


def _cross(a, b):
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]


def _project(v, n):
    return _sub(v, _mul(n, _dot(v, n)))


def _clamp01(x):
    return max(0.0, min(1.0, x))


def _lerp(a, b, t):
    return a + (b - a) * _clamp01(t)


def _inverse_lerp(a, b, x):
    return _clamp01((x - a) / (b - a)) if a != b else 0.0


def _angle(a, b):
    la, lb = _len(a), _len(b)
    if la < 1e-6 or lb < 1e-6:
        return 0.0
    return math.degrees(math.acos(max(-1.0, min(1.0, _dot(a, b) / (la * lb)))))


def _rotate(v, axis, degrees):
    """Rotates v about a unit axis (the same result as Unity's Quaternion.AngleAxis(degrees, axis) * v)."""
    r = math.radians(degrees)
    c, s = math.cos(r), math.sin(r)
    return _add(_add(_mul(v, c), _mul(_cross(axis, v), s)), _mul(axis, _dot(axis, v) * (1.0 - c)))


def _rotate_towards(a, b, max_radians):
    a, b = _unit(a), _unit(b)
    angle = math.acos(max(-1.0, min(1.0, _dot(a, b))))
    if angle <= max_radians or angle < 1e-5:
        return b
    axis = _cross(a, b)
    if _len(axis) < 1e-6:
        return a
    return _rotate(a, _unit(axis), math.degrees(max_radians))


def _move_towards(a, b, step):
    d = _sub(b, a)
    n = _len(d)
    return list(b) if n <= step or n < 1e-9 else _add(a, _mul(d, step / n))


def _slerp_dir(a, b, t):
    return _unit(_add(_mul(_unit(a), 1.0 - t), _mul(_unit(b), t)))


class AdvancedGame(FlyGame):
    def __init__(self, rng=None, rounds=False, countdown_s=3.0, round_s=60.0):
        self.clock = 0.0
        self.level = 1
        self.neighbours = []
        self.air, self.links, self.spot_air = [], [], []
        self.ceiling = 2.5
        self.aim = [0.0, 0.0, 1.0]
        self.senses = {}
        self.senses_at = -100.0
        self.hold = False           # Unity runs the 3-2-1 and says when the hunt starts
        super().__init__(rng=rng, rounds=rounds, countdown_s=countdown_s, round_s=round_s)

    # ---- round and personality --------------------------------------------
    def set_difficulty(self, level):
        self.level = max(0, min(2, int(level)))

    def _trait(self, easy_lo, easy_hi, hard_lo, hard_hi):
        hard = self.level == 2 or (self.level == 1 and self.rng.random() < 0.5)
        return self.rng.uniform(hard_lo, hard_hi) if hard else self.rng.uniform(easy_lo, easy_hi)

    def roll_personality(self):
        self.skittish = self._trait(0.6, 0.9, 1.2, 1.6)
        self.restless = self._trait(0.5, 0.8, 1.4, 1.9)
        self.night_sight = self._trait(0.6, 0.9, 1.5, 2.2)
        self.fly_pace = self._trait(0.65, 0.9, 1.25, 1.6)
        self.hearing = self._trait(0.5, 0.85, 1.3, 1.8)
        traits = []
        for value, high, low, high_name, low_name in (
                (self.skittish, 1.18, 0.9, "Jittery", "Calm"), (self.restless, 1.35, 0.8, "Restless", "Lazy"),
                (self.night_sight, 1.45, 0.9, "Night owl", "Day flyer"), (self.fly_pace, 1.2, 0.9, "Speedy", "Slow"),
                (self.hearing, 1.25, 0.85, "Sharp ears", "Hard of hearing")):
            if value > high:
                traits.append(high_name)
            elif value < low:
                traits.append(low_name)
        self.rng.shuffle(traits)
        self.personality = " & ".join(traits[:2]) if traits else "Steady"

    def reset(self):
        super().reset()
        self.roll_personality()
        self.normal, self.forward = list(UP), [0.0, 0.0, 1.0]
        self.perch_timer = self.rng.uniform(1.5, 4.0)
        self.groom_until = self.cooldown_until = self.flee_until = 0.0
        self.flee_side = "right"
        self.walk_target = self.walk_previous = -1
        self.walk_steps = 0
        self.route = []
        self.route_index = 0
        self.landing = 0
        self.landing_normal = list(UP)
        self.velocity = [0.0, 0.0, 0.0]
        self.flight_speed = 1.5
        self.flight_agility = 14.0
        self.flight_started = 0.0
        self.wobble = self.rng.random() * 10.0
        self.escaping = False
        self.loom, self.eye, self.source = 0.0, "center", "none"
        self.last_threat = list(self.head)
        self.last_distance = self.last_body = -1.0
        self.closing = self.body_closing = 0.0
        self.swing_threat, self.swing_threat_until = 0.0, 0.0
        self.next_ambient = self.clock + self.rng.uniform(4.0, 8.0) / self.restless
        self.ambient_until = 0.0
        self.ambient_value, self.ambient_point = 0.0, list(self.pos)
        self.sound_value, self.sound_until, self.sound_ear = 0.0, 0.0, "center"
        self.pulse_value, self.pulse_until, self.pulse_point, self.pulse_label = 0.0, 0.0, list(self.pos), ""
        self.prev_power = 1.0
        self.lightning_on = self.lightning_right = self.surge_right = False
        self.next_surge_flip = 0.0
        self.strength, self.side, self.action = 0.0, "-", "waiting"
        if self.spots:
            self._perch(self.rng.randrange(len(self.spots)), self.forward)

    def start_hunt(self):
        super().start_hunt()
        self.cooldown_until = self.clock + 0.5
        self.next_ambient = self.clock + self.rng.uniform(4.0, 8.0) / self.restless

    # ---- inputs from Unity --------------------------------------------------
    def set_room(self, spots, head=None, air=None, links=None, spot_air=None, ceiling=None, resume=False, playing=False, hold=False):
        good = []
        for s in spots if isinstance(spots, list) else []:
            try:
                good.append({"id": str(s.get("id", "")), "position": vec3(s.get("position")),
                             "normal": _unit(vec3(s.get("normal", UP)))})
            except (ValueError, AttributeError):
                continue
        self.hold = hold
        same = [s["position"] for s in good] == [s["position"] for s in self.spots]
        if head is not None:
            self.head = vec3(head)
        if resume and same and good:     # Unity reconnected to a backend that still has this round.
            if playing and self.phase == "countdown":
                self.start_hunt()
            return False
        self.spots = good
        self.air = []
        for p in air if isinstance(air, list) else []:
            try:
                self.air.append(vec3(p))
            except ValueError:
                self.air = []
                break
        self.links = [str(x) for x in links] if isinstance(links, list) and len(links) == len(self.air) else []
        self.spot_air = [int(x) for x in spot_air] if isinstance(spot_air, list) and len(spot_air) == len(good) else []
        if not self.links:
            self.air, self.spot_air = [], []
        if isinstance(ceiling, (int, float)) and ceiling > 1.0:
            self.ceiling = float(ceiling)
        self._build_graph()
        self.reset()
        if head is not None:
            self.head = vec3(head)
        if resume and playing:
            self.start_hunt()
        return True

    def _build_graph(self):
        self.neighbours = [[] for _ in self.spots]
        for i, a in enumerate(self.spots):
            for j, b in enumerate(self.spots):
                if i == j:
                    continue
                d = _sub(b["position"], a["position"])
                if _dot(d, d) > NEIGHBOUR_RADIUS ** 2 or _dot(a["normal"], b["normal"]) < 0.95:
                    continue
                if abs(_dot(d, a["normal"])) <= 0.04:
                    self.neighbours[i].append(j)

    def set_pose(self, head, aim, reach=SWAT_REACH, dt=1.0 / 30):
        super().set_pose(head, aim, reach, dt)
        self.aim = _unit(vec3(aim))

    def set_senses(self, m):
        out = {}
        for k in ("vision", "beam", "surge", "lightning", "headlights", "power"):
            v = m.get(k)
            if isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v):
                out[k] = float(v)
        out["sneak"] = m.get("sneak") is True
        for k in ("swatter", "flashlight"):
            try:
                out[k] = vec3(m.get(k))
            except ValueError:
                pass
        self.senses, self.senses_at = out, self.clock

    def hear(self, value, side):
        """A sound at the fly: Unity works out the distance and the ear, the fly's hearing scales it."""
        if self.state == "dead" or not self.active():
            return
        value = _clamp01(value * self.hearing)
        if value < 0.05:
            return
        self.sound_ear = side
        if value > self.sound_value or self.clock >= self.sound_until:
            self.sound_value = value
        self.sound_until = self.clock + SOUND_SECONDS

    def sound(self):
        if self.state == "dead" or not self.active() or self.clock >= self.sound_until:
            return 0.0, "center"
        return self.sound_value, self.sound_ear

    def swing(self, origin, direction, strength, reach=SWAT_REACH, swept=None, threat_only=False):
        """A swing is a looming shape for the fly. With threat_only, Unity decides the hit (it knows what the swatter
        struck first) and reports it with swat(); otherwise the swing is scored here."""
        if not threat_only:
            hit = super().swing(origin, direction, strength, reach, swept)
        else:
            hit = False
            if self.active():
                self.swings += 1
        if self.state != "dead" and self.active():
            o, d = vec3(origin), vec3(direction)
            to_fly = _sub(self.pos, o)
            s = float(strength) if isinstance(strength, (int, float)) else 0.6
            self.swing_threat = _inverse_lerp(28.0, 4.0, _angle(d, to_fly)) * (0.7 + 0.3 * s) if _len(to_fly) < reach + 0.6 else 0.0
            self.swing_threat_until = self.clock + 0.3
        return hit

    def swat(self):
        if self.state in ("dead", "idle") or not self.active():
            return False
        self.state = "dead"
        self.timer = RESPAWN_S
        self.score += 1
        self.events.append({"kind": "squish", "pos": list(self.pos), "score": self.score})
        if self.rounds:
            self.phase = "won"
            self.events.append({"kind": "round_end", "result": "won", "time_left": round(self.phase_time, 1)})
        return True

    # ---- what the fly perceives ---------------------------------------------
    def _right(self):
        return _unit(_cross(self.normal, self.forward))

    def _sense(self, key, default):
        return self.senses.get(key, default) if self.clock - self.senses_at < SENSES_MAX_AGE else default

    def _update_stimulus(self, dt):
        p = self.pos
        swatter = self._sense("swatter", self.tip if self.tip else self.head)
        beam = self._sense("beam", 0.0)
        # Unity reports how well the room lets it see; its own night vision sets the floor.
        light = self._sense("vision", 1.0)
        vision = _lerp(min(0.5, MIN_VISION * self.night_sight), 1.0, light)
        stealth = 0.35 if self._sense("sneak", False) else 1.0
        best, source, threat = 0.0, "none", self.last_threat

        def consider(value, label, point):
            nonlocal best, source, threat
            if value > best:
                best, source, threat = value, label, point

        right = self._right()

        def side_point(is_right):
            return _add(_add(p, _mul(right, 0.6 if is_right else -0.6)), [0.0, 0.2, 0.0])

        distance = _len(_sub(swatter, p))
        if self.last_distance >= 0.0 and dt > 0.0:
            self.closing = _lerp(self.closing, (self.last_distance - distance) / dt, dt * 12.0)
        self.last_distance = distance
        value = (_inverse_lerp(2.2, 0.4, distance) * 0.45
                 + _clamp01(self.closing / 3.0) * _inverse_lerp(2.6, 0.3, distance) * 0.9) * stealth
        to_fly = _sub(p, self.head)
        if _len(to_fly) < 2.0:
            value = max(value, _inverse_lerp(14.0, 3.0, _angle(self.aim, to_fly))
                        * _inverse_lerp(2.0, 0.5, _len(to_fly)) * 0.3 * stealth)
        if self.clock < self.swing_threat_until:
            value = max(value, self.swing_threat)
        consider(value * vision, "swatter", swatter)

        body = _len(to_fly)
        if self.last_body >= 0.0 and dt > 0.0:
            self.body_closing = _lerp(self.body_closing, (self.last_body - body) / dt, dt * 8.0)
        self.last_body = body
        walking_at = _inverse_lerp(1.6, 0.3, body) * 0.3 + _clamp01(self.body_closing / 2.5) * _inverse_lerp(2.0, 0.3, body) * 0.7
        consider(walking_at * stealth * vision, "player", self.head)

        if beam > 0.02:
            consider(beam * 0.4, "flashlight", self._sense("flashlight", self.head))
        if beam > 0.2:
            self.next_ambient -= dt * beam * 2.0
        if self.state not in ("flying", "fleeing") and self.clock >= self.next_ambient:
            self.next_ambient = self.clock + self.rng.uniform(6.0, 14.0) / self.restless
            self.ambient_until = self.clock + 0.3
            self.ambient_value = self.rng.uniform(0.6, 1.0)
            self.ambient_point = side_point(self.rng.random() < 0.5)
        if self.clock < self.ambient_until:
            consider(self.ambient_value * _lerp(0.4, 1.0, vision), "shadow", self.ambient_point)

        surge = self._sense("surge", 0.0)
        if surge > 0.15:
            if self.clock >= self.next_surge_flip:
                self.surge_right = not self.surge_right
                self.next_surge_flip = self.clock + 0.12
            consider(surge, "light surge", side_point(self.surge_right))
        lightning = self._sense("lightning", 0.0)
        if lightning > 0.05:
            if not self.lightning_on:
                self.lightning_right = self.rng.random() < 0.5
            self.lightning_on = True
            consider(min(1.0, lightning * 1.2), "lightning", side_point(self.lightning_right))
        else:
            self.lightning_on = False
        headlights = self._sense("headlights", 0.0)
        if headlights > 0.1:
            consider(headlights * 0.3, "headlights", side_point(math.sin(self.clock * 1.4) > 0.0))
        power = self._sense("power", 1.0)
        if self.prev_power > 0.6 and power <= 0.6:
            self.pulse_value, self.pulse_until = 0.55, self.clock + 0.3
            self.pulse_point, self.pulse_label = side_point(self.rng.random() < 0.5), "blackout"
        self.prev_power = power
        if self.clock < self.pulse_until:
            consider(self.pulse_value, self.pulse_label, self.pulse_point)

        self.loom = _clamp01(best * self.skittish)
        self.source = source
        self.last_threat = threat
        to = _sub(threat, p)
        across = _dot(to, right) / max(0.001, _len(to))
        self.eye = "center" if abs(across) < 0.2 else ("right" if across > 0.0 else "left")

    def looming(self):
        if self.state in ("dead", "idle") or not self.active():
            return 0.0, "center"
        return self.loom, self.eye

    # ---- the brain decides ---------------------------------------------------
    def on_escape(self, strength, side):
        self.strength, self.side = strength, side
        if self.state in ("dead", "idle", "flying", "fleeing") or not self.spots or not self.active():
            return
        if self.clock < self.cooldown_until:
            return
        if strength >= ESCAPE_THRESHOLD:
            self.action = f"TAKE OFF ({side})"
            self._take_off(strength, side)
        elif strength >= FLEE_THRESHOLD:
            self.action = f"walking away ({side})"
            self.flee_until = self.clock + 0.6
            self.flee_side = side
            if self.state == "landed":
                self._start_walk(self.rng.randint(2, 4))

    # ---- simulation ----------------------------------------------------------
    def tick(self, dt=1.0 / 30):
        if not self.spots:
            return
        self.clock += dt
        if self.rounds:
            if self.phase == "countdown":
                self.phase_time -= 0.0 if self.hold else dt
                if self.phase_time <= 0:
                    self.phase, self.phase_time = "playing", self.round_s
                    self.events.append({"kind": "round_start", "seconds": self.round_s})
            elif self.phase == "playing":
                self.phase_time -= dt
                if self.phase_time <= 0:
                    self.phase_time = 0.0
                    self.phase = "lost"
                    self.events.append({"kind": "round_end", "result": "lost", "time_left": 0.0})
        if self.state == "dead":
            if not self.rounds:
                self.timer -= dt
                if self.timer <= 0:
                    self._perch(self.rng.randrange(len(self.spots)), self.forward)
                    self.perch_timer = self.rng.uniform(1.0, 2.5)
            return
        if self.active():
            self._update_stimulus(dt)
        else:
            self.loom = 0.0     # Countdown or round over: it goes about its business and senses nothing.
        if self.state == "landed":
            self._update_perched(dt)
        elif self.state == "walking":
            self._update_walking(dt)
        elif self.state in ("flying", "fleeing"):
            for _ in range(4):          # Small steps, so it makes its turns at 30 ticks a second.
                if self.state in ("flying", "fleeing"):
                    self._update_flying(dt / 4.0)

    def _perch(self, index, heading):
        self.spot = index
        s = self.spots[index]
        self.normal = list(s["normal"])
        self.forward = self._tangent(heading, self.normal)
        self.pos = _add(s["position"], _mul(s["normal"], LIFT))
        self.state = "landed"
        self.escaping = False
        self.target = None
        self.velocity = [0.0, 0.0, 0.0]

    @staticmethod
    def _tangent(heading, up):
        for candidate in (heading, [0.0, 0.0, 1.0], [1.0, 0.0, 0.0]):
            f = _project(candidate, up)
            if _dot(f, f) > 1e-4:
                return _unit(f)
        return [0.0, 0.0, 1.0]

    def _update_perched(self, dt):
        self.perch_timer -= dt
        if self.perch_timer > 0.0:
            return
        if self.rng.random() < 0.35 / self.restless or not self.neighbours[self.spot]:
            self.forward = _rotate(self.forward, self.normal, self.rng.uniform(-110.0, 110.0))
            self.groom_until = self.clock + self.rng.uniform(0.8, 2.2)
            self.perch_timer = self.rng.uniform(1.5, 4.0)
        else:
            self._start_walk(self.rng.randint(3, 9))

    def _start_walk(self, steps):
        if not self.neighbours[self.spot]:
            return
        self.walk_steps, self.walk_previous = steps, -1
        self.state = "walking"
        self.groom_until = 0.0
        self._next_step()

    def _next_step(self):
        fleeing = self.clock < self.flee_until
        wanted = self.forward
        if fleeing:
            away = _project(_sub(self.pos, self.last_threat), self.normal)
            if _dot(away, away) < 1e-4:
                away = self.forward
            wanted = _rotate(_unit(away), self.normal, -50.0 if self.flee_side == "left" else 50.0)
        here = self.spots[self.spot]["position"]
        best, best_score = -1, -1e9
        for c in self.neighbours[self.spot]:
            step = _project(_sub(self.spots[c]["position"], here), self.normal)
            if _dot(step, step) < 1e-6:
                continue
            score = _dot(_unit(step), _unit(wanted)) + self.rng.random() * (0.2 if fleeing else 0.9)
            if c == self.walk_previous:
                score -= 1.2
            if score > best_score:
                best, best_score = c, score
        if best < 0:
            self.state = "landed"
            self.perch_timer = self.rng.uniform(1.0, 3.0)
            return
        self.walk_target = best

    def _update_walking(self, dt):
        fleeing = self.clock < self.flee_until
        s = self.spots[self.walk_target]
        target = _add(s["position"], _mul(s["normal"], LIFT))
        to = _sub(target, self.pos)
        distance = _len(to)
        direction = _mul(to, 1.0 / distance) if distance > 1e-5 else self.forward
        tangent = _project(direction, self.normal)
        facing = 1.0
        if _dot(tangent, tangent) > 1e-6:
            tangent = _unit(tangent)
            self.forward = self._tangent(_rotate_towards(self.forward, tangent, (9.0 if fleeing else 4.0) * dt), self.normal)
            facing = _clamp01(_dot(self.forward, tangent) * 1.4 - 0.1)
        wobble = 0.6 + 0.4 * (0.5 + 0.5 * math.sin(self.clock * 3.7 + self.wobble))
        speed = WALK_SPEED * self.fly_pace * (3.5 if fleeing else 1.0) * wobble * facing
        self.pos = _add(self.pos, _mul(direction, min(speed * dt, distance)))
        if distance > 0.012:
            return
        self.walk_previous, self.spot = self.spot, self.walk_target
        self.normal = list(self.spots[self.spot]["normal"])
        self.forward = self._tangent(self.forward, self.normal)
        self.walk_steps -= 1
        if self.walk_steps <= 0:
            self.state = "landed"
            self.perch_timer = self.rng.uniform(1.5, 4.0)
        else:
            self._next_step()

    def _clamp_air(self, p):
        return [p[0], max(0.45, min(max(0.8, self.ceiling - 0.45), p[1])), p[2]]

    def _air_path(self, start, goal):
        """Shortest hop path through air nodes that Unity confirmed have a clear line between them."""
        came = {start: None}
        queue = [start]
        for node in queue:
            if node == goal:
                break
            for other, bit in enumerate(self.links[node]):
                if bit == "1" and other not in came:
                    came[other] = node
                    queue.append(other)
        if goal not in came:
            return []
        path = []
        node = goal
        while node is not None:
            path.append(node)
            node = came[node]
        return path[::-1]

    def _take_off(self, strength, side):
        side_dir = _mul(self._right(), -1.0 if side == "left" else 1.0)
        away = _sub(self.pos, self.last_threat)
        away = _unit(away) if _dot(away, away) > 1e-4 else _mul(self.forward, -1.0)
        burst = _unit(_add(_add(_mul(side_dir, 0.6), _mul(away, 0.8)), [0.0, 0.5, 0.0]))

        min_travel = _lerp(1.5, 3.0, strength)
        chosen, best = -1, -1e9
        for _ in range(24):
            c = self.rng.randrange(len(self.spots))
            if c == self.spot:
                continue
            travel = _len(_sub(self.spots[c]["position"], self.pos))
            score = min(travel, min_travel * 1.6) + self.rng.random() * 0.6
            if travel < min_travel:
                score -= 3.0
            if _len(_sub(self.spots[c]["position"], self.last_threat)) < 1.0:
                score -= 5.0
            if score > best:
                chosen, best = c, score
        if chosen < 0:
            return

        hops = 1 + round(strength * 3.0)
        self.route = [self._clamp_air(_add(_add(self.pos, _mul(self.normal, 0.35)), _mul(burst, 0.25)))]
        if self.air and self.spot_air:
            node = self.spot_air[self.spot]
            nodes = [node]
            for hop in range(hops):
                options = [i for i, b in enumerate(self.links[node]) if b == "1"
                           and _len(_sub(self.air[i], self.air[node])) >= 1.2]
                if not options:
                    break
                if hop == 0:     # The first turn follows the side the brain chose.
                    node = max(options, key=lambda i: _dot(_unit(_sub(self.air[i], self.air[node])), burst) + self.rng.random() * 0.4)
                else:
                    node = self.rng.choice(options)
                nodes.append(node)
            nodes += self._air_path(node, self.spot_air[chosen])[1:]
            self.route += [list(self.air[i]) for i in nodes]
        else:
            for _ in range(hops):
                s = self.spots[self.rng.randrange(len(self.spots))]
                self.route.append(self._clamp_air(_add(s["position"], [0.0, self.rng.uniform(0.5, 1.2), 0.0])))
        self.route.append(self._clamp_air(_add(self.spots[chosen]["position"], [0.0, 0.9, 0.0])))

        self.landing = chosen
        landing = self.spots[chosen]
        self.landing_normal = list(landing["normal"])
        point = _add(landing["position"], _mul(landing["normal"], LIFT))
        self.route += [_add(point, _mul(landing["normal"], 0.3)), point]
        self.spot = chosen
        self.target = point
        self.route_index = 0
        self.flight_speed = (2.0 + 3.5 * strength) * self.fly_pace
        self.flight_agility = 14.0
        self.flight_started = self.clock
        self.wobble = self.rng.random() * 10.0
        self.escaping = True
        self.state = "fleeing"
        self.escapes += 1
        self.groom_until = 0.0
        self.walk_target = -1
        self.cooldown_until = self.clock + 0.4
        self.velocity = _mul(burst, self.flight_speed * 0.7)
        self.events.append({"kind": "escape", "side": side, "strength": strength})

    def _update_flying(self, dt):
        last = self.route_index == len(self.route) - 1
        target = self.route[self.route_index]
        to = _sub(target, self.pos)
        distance = _len(to)
        speed = _lerp(0.3, self.flight_speed, distance / 0.6) if last else self.flight_speed
        desired = _mul(to, speed / max(0.001, distance))
        wander = _clamp01(distance / 0.6) if last else 1.0
        sideways = _mul(_cross(_unit(desired), UP), math.sin(self.clock * 7.0 + self.wobble) * 0.35 * wander)
        lift = _mul(UP, math.sin(self.clock * 5.0 + self.wobble * 2.0) * 0.25 * wander)
        self.velocity = _move_towards(self.velocity, _add(_add(desired, sideways), lift), dt * self.flight_agility)
        step = _mul(self.velocity, dt)
        if last and _len(step) >= distance:      # At 30 Hz a step can pass the landing point: land instead.
            self.pos = list(target)
            distance = 0.0
        else:
            self.pos = _add(self.pos, step)
        if not last and distance < 0.25:
            self.route_index += 1
        settle = _inverse_lerp(0.5, 0.05, distance) if last else 0.0
        self.normal = _slerp_dir(UP, self.landing_normal, settle)
        if _dot(self.velocity, self.velocity) > 1e-4:
            self.forward = self._tangent(self.velocity, self.normal)
        if (last and distance < 0.04) or self.clock - self.flight_started > 40.0:
            self._perch(self.landing, self.forward)
            self.perch_timer = self.rng.uniform(1.5, 4.0)
            self.cooldown_until = self.clock + 0.8

    def snapshot(self):
        snap = super().snapshot()
        snap.update({
            "heading": [round(v, 3) for v in self.forward],
            "up": [round(v, 3) for v in self.normal],
            "vel": [round(v, 3) for v in self.velocity],
            "grooming": self.clock < self.groom_until,
            "hurrying": self.clock < self.flee_until,
            "personality": self.personality,
            "looming": round(self.loom, 3),
            "eye": self.eye,
            "source": self.source,
            "action": self.action,
            "strength": round(float(self.strength), 3),
            "side": self.side,
        })
        return snap
