"""Habituation: short-term fatigue of the sensory synapses, so the fly reacts
less to a threat it keeps seeing or hearing and recovers after a few seconds.

Model (one shared efficacy per input channel, Tsodyks-Markram-style resource
depletion at the population level):

    channels: eye_left, eye_right, ear_left, ear_right
    x_c in (0, 1]       synaptic resource of channel c (1 = fresh)
    e_c in (0, 1]       efficacy of channel c's outgoing synapses; follows x_c
                        with a LAG_S delay
    each decision:      recover   x_c <- 1 - (1 - x_c) * exp(-elapsed / TAU_S)
                        follow    e_c <- e_c + (x_c - e_c) * (1 - exp(-elapsed / LAG_S))
                        use       this window runs at efficacy e_c
                        deplete   x_c <- x_c * (1 - USE * d_c)
    s_c = stimulus on that channel this window (looming value for the eyes,
          sound value for the ears; "center" stimulates both sides)
    d_c = max(0, s_c - THRESHOLD) / (1 - THRESHOLD)

Only strong input fatigues the synapses. The game's looming value is the
largest of several threats, and several are weak and sustained (swatter
held nearby <= 0.45, aiming 0.3, player close 0.3, flashlight <= 0.4,
headlights 0.3). Below THRESHOLD they never wear the fly down; swings,
loud hits and breaking glass (short pulses of ~0.6-1.0) do.

The lag makes the fatigue build up after a threat rather than during it:
without it, depletion inside a single 0.3 s swing already cut that swing's
own response by ~20%, so even a fresh fly reacted weakly. With it, the first
threat of a series gets the full response and the following ones get less.

Habituation is specific to the channel: a sound repeated on the left ear
doesn't dull the eyes or the right ear. Depletion depends on the stimulus
value, not on input spike counts, so channels with different calibrated
input rates (e.g. the left ear's large gain) fatigue at the same speed.

Parameters are chosen for gameplay (a few repeats wear the reflex down, a
few seconds of quiet restore it), not fitted to fly data.

Standard library only, so the lookup-table fallback uses the same model.
"""
import math

CHANNELS = ("eye_left", "eye_right", "ear_left", "ear_right")
USE = 0.12          # fraction of resource used per 50 ms window at stimulus 1
TAU_S = 3.0         # recovery time constant, seconds
THRESHOLD = 0.4     # stimulus at or below this causes no fatigue
LAG_S = 1.2         # efficacy follows the resource with this delay, seconds
# Measured on subnet_t5 (5 seeds, events 1.5 s apart): repeated breaking glass
# (sound 1.0, 0.35 s) 0.94 -> 0.40 strength by the 6th; repeated fake swings
# (looming 0.8, 0.3 s) 0.73 -> 0.27; a real strike after them 0.43 vs 0.99
# fresh; recovery to 0.69 of a fresh 0.73 after 6 s quiet. First responses
# match habituation off (0.94 / 0.77 / 0.97).
DECISION_S = 0.05


def stimulus(look, side, sound, sound_side):
    """Stimulus value per channel for one window."""
    look, sound = float(look), float(sound)
    return {
        "eye_left": look if side in ("left", "center") else 0.0,
        "eye_right": look if side in ("right", "center") else 0.0,
        "ear_left": sound if sound_side in ("left", "center") else 0.0,
        "ear_right": sound if sound_side in ("right", "center") else 0.0,
    }


class Habituation:
    def __init__(self, enabled=False, use=USE, tau_s=TAU_S, threshold=THRESHOLD, lag_s=LAG_S):
        self.use = float(use)
        self.tau_s = float(tau_s)
        self.threshold = float(threshold)
        self.lag_s = float(lag_s)
        self.enabled = bool(enabled)
        self.reset()

    def reset(self):
        self.x = {c: 1.0 for c in CHANNELS}
        self.e = {c: 1.0 for c in CHANNELS}

    def set_enabled(self, on):
        """Turning it on or off always starts from a fresh fly."""
        self.enabled = bool(on)
        self.reset()

    def advance(self, look, side, sound, sound_side, elapsed_s=None):
        """Advance by one decision window. Returns the efficacy to use for this
        window (before this window's depletion)."""
        if not self.enabled:
            return dict(self.e)          # all 1.0
        el = DECISION_S if elapsed_s is None else max(DECISION_S, float(elapsed_s))
        k = math.exp(-el / self.tau_s)
        f = 1.0 - math.exp(-el / self.lag_s) if self.lag_s > 0 else 1.0
        for c in CHANNELS:
            self.x[c] = 1.0 - (1.0 - self.x[c]) * k
            self.e[c] += (self.x[c] - self.e[c]) * f
        eff = dict(self.e)
        s = stimulus(look, side, sound, sound_side)
        for c in CHANNELS:
            sc = min(1.0, max(0.0, s[c]))
            d = max(0.0, sc - self.threshold) / (1.0 - self.threshold)
            self.x[c] *= 1.0 - self.use * d
        return eff

    def efficacy(self):
        """Current efficacy per channel (what the synapses run at)."""
        return dict(self.e)

    def snapshot(self):
        """Full state, for status and for live <-> fallback switches."""
        return {"x": {c: round(self.x[c], 6) for c in CHANNELS},
                "e": {c: round(self.e[c], 6) for c in CHANNELS}}

    def load(self, snap):
        """Take over another brain's state (live <-> fallback switches)."""
        for key, dst in (("x", self.x), ("e", self.e)):
            for c in CHANNELS:
                if c in snap.get(key, {}):
                    dst[c] = min(1.0, max(0.0, float(snap[key][c])))
