"""Trace log for hunting delays. Off unless FLYAI_TRACE=1.

Every line starts with the monotonic clock in ms (one clock for the whole Pi, so the
backend's and the brain's lines line up). Where the lines go:
  * on QNX, a dedicated slog2 buffer set named "flyai_<process>": buffer "events" for every
    message, buffer "summary" for one line a second. Read with
        slog2info -b flyai_backend        (or flyai_brain)
    slog2 lives in RAM and wraps, so
  * every line is also written to backend/.run/trace_<process>.log, which survives.
Used by backend/server.py and brain/server.py. Safe to import anywhere.
"""
import ctypes
import os
import sys
import time
from collections import defaultdict
from pathlib import Path

ON = os.environ.get("FLYAI_TRACE", "") not in ("", "0", "off")
IS_QNX = hasattr(os, "uname") and os.uname().sysname == "QNX"
RUN_DIR = Path(__file__).resolve().parent.parent / "backend" / ".run"
SLOG2_INFO = 5

_slog = None            # (lib, events handle, summary handle) on QNX
_file = None
_keep = []              # C strings slog2 holds pointers to
_counts = defaultdict(lambda: [0, 0])   # key -> [messages, bytes] this second
_peaks = {}                             # key -> largest value this second
_last_cpu = None


def now_ms():
    return time.monotonic_ns() / 1e6


def _register_slog2(name):
    class BufCfg(ctypes.Structure):
        _fields_ = [("buffer_name", ctypes.c_char_p), ("num_pages", ctypes.c_int)]

    class SetCfg(ctypes.Structure):
        _fields_ = [("num_buffers", ctypes.c_int), ("buffer_set_name", ctypes.c_char_p),
                    ("verbosity_level", ctypes.c_uint8), ("buffer_config", BufCfg * 4),
                    ("max_retries", ctypes.c_uint32)]

    lib = ctypes.CDLL("libslog2.so.1")
    lib.slog2_register.argtypes = [ctypes.POINTER(SetCfg), ctypes.POINTER(ctypes.c_void_p), ctypes.c_uint32]
    lib.slog2c.argtypes = [ctypes.c_void_p, ctypes.c_uint16, ctypes.c_uint8, ctypes.c_char_p]
    for pages in (1024, 512, 256, 64):      # 4 KB pages; take the biggest the system allows
        cfg = SetCfg()
        cfg.num_buffers = 2
        _keep.extend([b"flyai_" + name.encode(), b"events", b"summary"])
        cfg.buffer_set_name = _keep[-3]
        cfg.verbosity_level = 7
        cfg.buffer_config[0] = BufCfg(_keep[-2], pages)
        cfg.buffer_config[1] = BufCfg(_keep[-1], 64)
        handles = (ctypes.c_void_p * 4)()
        if lib.slog2_register(ctypes.byref(cfg), handles, 0) == 0:
            return lib, handles[0], handles[1], pages
    return None


def start(name):
    """Open this process's trace. Returns a line saying where it goes, or None when off."""
    global _slog, _file
    if not ON:
        return None
    RUN_DIR.mkdir(parents=True, exist_ok=True)
    path = RUN_DIR / f"trace_{name}.log"
    _file = open(path, "w", buffering=1 << 16)
    where = [str(path)]
    if IS_QNX:
        try:
            r = _register_slog2(name)
            if r:
                _slog = r[:3]
                where.append(f"slog2 buffer set flyai_{name} ({r[3] * 4} KB of events)")
        except Exception as e:
            where.append(f"no slog2 ({e!r})")
    summary(f"trace start pid={os.getpid()} python={sys.version.split()[0]}")
    return ", ".join(where)


def _write(handle_index, text):
    line = f"{now_ms():.1f} {text}"
    if _file:
        _file.write(line + "\n")
    if _slog:
        try:
            _slog[0].slog2c(_slog[handle_index], 0, SLOG2_INFO, line.encode("utf-8", "replace"))
        except Exception:
            pass


def event(text):
    """One thing that happened (a message, a gap, a decision)."""
    if ON:
        _write(1, text)


def summary(text):
    if ON:
        _write(2, "SUM " + text)
        if _file:
            _file.flush()


def count(key, nbytes=0):
    """Tally a message for the per-second summary."""
    if ON:
        c = _counts[key]
        c[0] += 1
        c[1] += nbytes


def peak(key, value):
    """Keep the largest value of `key` seen this second."""
    if ON and value > _peaks.get(key, float("-inf")):
        _peaks[key] = value


def flush_second(extra=""):
    """Write and clear this second's tallies, with this process's CPU use."""
    global _last_cpu
    if not ON:
        return
    t = os.times()
    cpu, wall = t.user + t.system, time.monotonic()
    cpu_pct = ""
    if _last_cpu:
        cpu_pct = f" cpu={100 * (cpu - _last_cpu[0]) / max(wall - _last_cpu[1], 1e-3):.0f}%"
    _last_cpu = (cpu, wall)
    counts = " ".join(f"{k}={n}/{b}B" for k, (n, b) in sorted(_counts.items()))
    peaks = " ".join(f"{k}={v:.1f}" for k, v in sorted(_peaks.items()))
    summary(f"{cpu_pct.strip()} {extra} | {counts} | peak {peaks}")
    _counts.clear()
    _peaks.clear()


def short(text, limit=180):
    return text if len(text) <= limit else text[:limit] + f"...(+{len(text) - limit})"
