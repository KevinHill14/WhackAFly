"""The fly's escape reflex: a leaky integrate-and-fire simulation of the
LPLC2/LC4 -> DNp01 (Giant Fiber) subnetwork of the FlyWire v783 connectome.

NumPy only, so it runs on the QNX Raspberry Pi. Constants follow Shiu et al.
2024 (model.py in philshiu/Drosophila_brain_model):

    dv/dt = (v_0 - v + g) / t_mbr     (unless refractory)
    dg/dt = -g / tau                  (unless refractory)
    spike when v > v_th  ->  v = v_rst, g = 0, refractory t_rfc
    presynaptic spike  ->  g_post += w_syn * (sign x synapse count), after t_dly
    Poisson input      ->  v += w_syn * f_poi  (input neurons, no refractory)

One decision = DECISION_MS of simulated time. Network state (v, g, in-flight
synaptic events) carries over between decisions, so the fly "remembers" what
it just saw for a few tens of ms, like a real circuit.
"""
import json
from pathlib import Path

import numpy as np

# --- Shiu et al. 2024 constants (mV, ms) ---
V_0 = -52.0
V_RST = -52.0
V_TH = -45.0
T_MBR = 20.0
TAU = 5.0
T_RFC = 2.2
T_DLY = 1.8
W_SYN = 0.275
F_POI = 250.0

DT = 0.1
DECISION_MS = 50.0

# Max Poisson rate of the looming detectors at looming = 1. Shiu et al. use
# 150 Hz to activate a population; we go lower so that at full looming the
# Giant Fiber fires in bursts rather than continuously. Tuned with sweep.py.
DEFAULT_MAX_RATE_HZ = 60.0

SUBNET_DIR = Path(__file__).resolve().parent / "subnet"


class FlyBrain:
    def __init__(self, path=None, max_rate_hz=None, seed=None):
        path = Path(path) if path else SUBNET_DIR / "subnet_t5.npz"
        s = np.load(path, allow_pickle=False)
        self.path = path
        self.threshold = int(s["threshold"])
        self.cell_type = s["cell_type"]
        self.side = s["side"]
        role = s["role"]
        self.group_id = s["group_id"].astype(np.intp)
        self.group_names = [str(x) for x in s["group_names"]]
        self.n = n = len(role)
        self.n_groups = len(self.group_names)

        # Dense weight matrix in mV (row = presynaptic). 1,602^2 float32 ~ 10 MB;
        # gathering rows for the few neurons that spike each step is the fastest
        # pure-NumPy option at this size.
        w = np.zeros((n, n), dtype=np.float32)
        np.add.at(w, (s["pre"], s["post"]), s["weight"].astype(np.float32) * W_SYN)
        self.w = w

        inp = np.where(role == "input")[0]
        self.inputs_left = inp[self.side[inp] == "left"]
        self.inputs_right = inp[self.side[inp] == "right"]
        self.is_input = role == "input"

        def one(name):
            return self.group_names.index(name)
        gid = self.group_id
        self.gf_left = int(np.where(gid == one("DNp01_L"))[0][0])
        self.gf_right = int(np.where(gid == one("DNp01_R"))[0][0])
        out = np.where(role == "output")[0]
        self.dn_left = out[self.side[out] == "left"]
        self.dn_right = out[self.side[out] == "right"]

        # Per-subnetwork calibration written by calibrate.py: input rate and the
        # escape drive at one-eye full looming (used to normalise strength).
        calib = load_calibration(path)
        self.max_rate_hz = float(max_rate_hz if max_rate_hz is not None
                                 else calib.get("max_rate_hz", DEFAULT_MAX_RATE_HZ))
        self.full_drive = float(calib.get("full_drive", 10.0))
        # The right-eye pathway in this connectome is ~2x stronger than the
        # left; a per-eye input gain equalises full-looming drive.
        self.eye_gain = dict(calib.get("eye_gain", {"left": 1.0, "right": 1.0}))
        self.rng = np.random.default_rng(seed)
        self.steps = int(round(DECISION_MS / DT))
        self.delay = int(round(T_DLY / DT))
        self.rfc_steps = int(round(T_RFC / DT))
        self.decay_g = np.float32(np.exp(-DT / TAU))
        self.k_v = np.float32(DT / T_MBR)
        self.poisson_kick = np.float32(W_SYN * F_POI)
        self.reset()

    def reset(self):
        n = self.n
        self.v = np.full(n, V_0, dtype=np.float32)
        self.g = np.zeros(n, dtype=np.float32)
        self.refr = np.zeros(n, dtype=np.int32)
        # Ring buffer of synaptic input arriving T_DLY after a spike.
        self.pending = np.zeros((self.delay, n), dtype=np.float32)
        self.t = 0
        self.drive_ema = 0.0
        self.jump = "left" if self.rng.random() < 0.5 else "right"

    def _drive(self, value, side):
        """Looming -> Poisson rate per input neuron, one eye at a time."""
        value = float(min(max(value, 0.0), 1.0))
        rate = np.zeros(self.n, dtype=np.float32)
        r = value * self.max_rate_hz
        if side in ("left", "center"):
            rate[self.inputs_left] = r * self.eye_gain["left"]
        if side in ("right", "center"):
            rate[self.inputs_right] = r * self.eye_gain["right"]
        return rate

    def run(self, value, side="center"):
        """Simulate one decision window. Returns per-neuron spike counts."""
        rate = self._drive(value, side)
        p = rate * np.float32(DT / 1000.0)
        active = np.where(p > 0)[0]
        kicks = None
        if len(active):
            kicks = self.rng.random((self.steps, len(active)), dtype=np.float32) < p[active]

        # Kick indices per step, precomputed (one vectorised pass).
        hits = [active[row] for row in kicks] if kicks is not None else None

        v, g, refr, w, pending = self.v, self.g, self.refr, self.w, self.pending
        tmp = np.empty_like(v)
        counts = np.zeros(self.n, dtype=np.int32)
        is_input = self.is_input
        a_v = np.float32(1.0) - self.k_v             # v <- a_v*v + k_v*(V_0 + g)
        k_v, v0k, decay = self.k_v, np.float32(V_0) * self.k_v, self.decay_g
        rfc = self.rfc_steps
        # Neurons still refractory: small index array, so the per-step cost of
        # refractoriness is ~0 when little is spiking. Exactly equivalent to
        # Brian2's "(unless refractory)": v sits at v_rst (it was just reset),
        # g keeps synaptic input but doesn't decay.
        ref = np.flatnonzero(refr > 0)
        for step in range(self.steps):
            slot = self.t % self.delay
            g += pending[slot]
            pending[slot].fill(0.0)

            if len(ref):
                g_ref = g[ref]
            np.multiply(g, k_v, out=tmp)
            tmp += v0k
            v *= a_v
            v += tmp
            g *= decay
            if len(ref):
                v[ref] = V_RST
                g[ref] = g_ref
                refr[ref] -= 1
            if hits is not None:
                hit = hits[step]
                if len(hit):
                    v[hit] += self.poisson_kick

            # Refractory neurons sit at v_rst < v_th, so they can't spike.
            spk = np.flatnonzero(v > V_TH)
            if len(spk):
                v[spk] = V_RST
                g[spk] = 0.0
                # Inputs have no refractory period (Shiu et al.).
                refr[spk] = np.where(is_input[spk], 0, rfc)
                counts[spk] += 1
                # Arrives T_DLY later: that slot is the one we just cleared.
                pending[slot] += w[spk].sum(axis=0)
                ref = np.flatnonzero(refr > 0)
            elif len(ref) and refr[ref].min() <= 0:
                ref = ref[refr[ref] > 0]
            self.t += 1
        return counts

    def decide(self, value, side="center"):
        """One looming message in -> one escape decision out."""
        counts = self.run(value, side)
        g = np.bincount(self.group_id, weights=counts,
                        minlength=self.n_groups).astype(int)
        name = self.group_names.index

        # Strength: all escape DNs, Giant Fiber weighted most (it is the fast
        # jump command). Low-pass filtered like a motor integrator, then
        # normalised so one-eye full looming ~= 1.
        gf = g[name("DNp01_L")] + g[name("DNp01_R")]
        dn = sum(g[name(f"{t}_{s}")] for t in ("DNp02", "DNp04", "DNp06", "DNp11")
                 for s in "LR")
        drive = gf + DN_WEIGHT * dn
        self.drive_ema += EMA_ALPHA * (drive - self.drive_ema)
        strength = min(1.0, self.drive_ema / self.full_drive)

        # Side: the brain decides. In this connectome both Giant Fibers prefer
        # right-eye looming (see README), so DNp01 alone can't tell sides.
        # DNp02/04/11 fire only for looming on their own side, at every level
        # and threshold we tested, so they vote. (DNp06 flips laterality with
        # input rate, so it's left out.) The fly jumps AWAY from the threat.
        # A tie (e.g. no spikes) keeps the previous choice.
        left_threat = sum(g[name(f"{t}_L")] for t in IPSI_DNS)
        right_threat = sum(g[name(f"{t}_R")] for t in IPSI_DNS)
        if left_threat != right_threat:
            self.jump = "right" if left_threat > right_threat else "left"
        return {"type": "escape", "strength": round(float(strength), 3),
                "side": self.jump, "spikes": g.tolist(),
                "active": np.flatnonzero(counts).tolist(),   # indices of neurons that fired (for the game's brain view)
                "gf": [int(g[name("DNp01_L")]), int(g[name("DNp01_R")])],
                "vote": [int(left_threat), int(right_threat)]}


def calibration_path(subnet_path):
    return Path(subnet_path).with_suffix(".calib.json")


def load_calibration(subnet_path):
    p = calibration_path(subnet_path)
    if p.exists():
        return json.loads(p.read_text())
    return {}


IPSI_DNS = ("DNp02", "DNp04", "DNp11")
DN_WEIGHT = 0.5
EMA_ALPHA = float(1.0 - np.exp(-DECISION_MS / 100.0))   # 100 ms readout filter
