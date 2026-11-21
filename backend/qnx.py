"""QNX features used by the backend. Every function is safe to call anywhere:
on a laptop (Linux, macOS, Windows) they do nothing or fall back to plain behaviour.

What this uses, and why it matters for a real-time game:
  * priority-based real-time scheduling (SCHED_RR) so the 30 Hz fly loop is not
    pre-empted by ordinary background work
  * slog2, the QNX system logger, so events show up in `slog2info` next to the OS's own
  * ClockPeriod, to report the kernel timer tick that limits timing accuracy
  * mlockall, to keep the process out of swap (needs elevated rights; best effort)
  * monotonic nanosecond clocks and a small latency tracker for deadline monitoring
See backend/QNX_FEATURES.md for what was verified on the Raspberry Pi 5.
"""
import ctypes
import os
import sys
import time

IS_QNX = hasattr(os, "uname") and os.uname().sysname == "QNX"

# sys/slog.h severities
SLOG_ERROR, SLOG_WARNING, SLOG_NOTICE, SLOG_INFO = 2, 3, 4, 5
_NAMES = {SLOG_ERROR: "ERROR", SLOG_WARNING: "WARN", SLOG_NOTICE: "NOTICE", SLOG_INFO: "INFO"}
_SLOG_OPCODE = 0x00020000      # an application-specific code; shows as the message code
_libc = None
_last_logged = {}


def _c():
    global _libc
    if _libc is None:
        _libc = ctypes.CDLL(None, use_errno=True)
    return _libc


def log(severity, message, every=0.0, key=None):
    """Print to stdout and, on QNX, write to the system logger (read with `slog2info`).
    `every` rate-limits repeats of the same `key` to one per that many seconds."""
    if every:
        k = key or message
        now = time.monotonic()
        if now - _last_logged.get(k, -1e9) < every:
            return
        _last_logged[k] = now
    print(f"[{_NAMES.get(severity, 'INFO')}] {message}", flush=True)
    if IS_QNX:
        try:
            _c().slogf(ctypes.c_int(_SLOG_OPCODE), ctypes.c_int(severity), b"flyai: %s", message.encode("utf-8", "replace"))
        except Exception:
            pass


def set_realtime(priority=20):
    """Run this process under QNX round-robin real-time scheduling. QNX's default is
    priority 10 round-robin, so 20 pre-empts ordinary programs without starving the OS.
    Returns a short description of what was done."""
    if not IS_QNX or priority <= 0:
        return "not changed"
    try:
        os.sched_setscheduler(0, os.SCHED_RR, os.sched_param(priority))
        return f"SCHED_RR priority {os.sched_getparam(0).sched_priority}"
    except OSError as e:
        return f"unchanged (cannot set priority: {e.strerror})"


def lock_memory():
    """Keep all pages resident so a page fault cannot cause a missed deadline."""
    if not IS_QNX:
        return "not changed"
    ok = _c().mlockall(0x1 | 0x2) == 0     # MCL_CURRENT | MCL_FUTURE
    return "locked" if ok else f"not locked ({os.strerror(ctypes.get_errno())})"


def tick_ns():
    """The kernel timer tick in nanoseconds (QNX ClockPeriod), or None."""
    if not IS_QNX:
        return None

    class _ClockPeriod(ctypes.Structure):
        _fields_ = [("nsec", ctypes.c_uint32), ("fract", ctypes.c_int32)]
    old = _ClockPeriod()
    try:
        return int(old.nsec) if _c().ClockPeriod(0, None, ctypes.byref(old), 0) == 0 else None
    except Exception:
        return None


def startup(priority=20):
    """Apply the real-time settings and log what happened. Returns a dict for status replies."""
    info = {"os": "QNX" if IS_QNX else sys.platform,
            "scheduling": set_realtime(priority),
            "memory": lock_memory(),
            "tick_ns": tick_ns()}
    log(SLOG_INFO, f"backend start: os={info['os']} scheduling={info['scheduling']} "
                   f"memory={info['memory']} timer_tick_ns={info['tick_ns']}")
    return info


def now_ms():
    """Monotonic milliseconds (QNX ClockTime / CLOCK_MONOTONIC underneath)."""
    return time.monotonic_ns() / 1e6


class Latency:
    """Keeps the last `size` samples (milliseconds) and summarises them."""

    def __init__(self, size=300):
        self.size, self.samples, self.total, self.worst = size, [], 0, 0.0

    def add(self, ms):
        self.samples.append(ms)
        if len(self.samples) > self.size:
            self.samples.pop(0)
        self.total += 1
        self.worst = max(self.worst, ms)

    def summary(self):
        if not self.samples:
            return {"count": 0}
        s = sorted(self.samples)
        pick = lambda p: round(s[min(len(s) - 1, int(p * len(s)))], 2)
        return {"count": self.total, "p50": pick(0.50), "p95": pick(0.95), "p99": pick(0.99),
                "max_recent": round(s[-1], 2), "max_ever": round(self.worst, 2)}
