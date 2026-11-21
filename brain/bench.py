"""Test client for the brain server: plays the game's side of the protocol.

  python brain/bench.py smoke                 # a few messages, prints replies
  python brain/bench.py sweep                 # looming 0->1 per side: strength curve + side
  python brain/bench.py latency --seconds 20  # 20 Hz looming, round-trip times + ping during load
  python brain/bench.py long --seconds 600    # long run: RTT drift + server memory
  python brain/bench.py reconnect             # abrupt disconnects, then reconnect
  python brain/bench.py all                   # smoke+sweep+latency+reconnect+90s long
  python brain/bench.py fallback              # fallback-mode sweep vs live
  python brain/bench.py switch                # live/fallback/auto switches mid-game
  python brain/bench.py failover              # stall the live brain (server --debug)
  python brain/bench.py modes                 # fallback + switch + failover
  python brain/bench.py speed                 # no server: time each subnetwork (run on the Pi)

  --url ws://HOST:PORT  (default ws://127.0.0.1:8765; use the dev hub URL to test via the hub)
"""
import argparse
import asyncio
import json
import statistics
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import ws  # noqa: E402

FAIL = []


def check(ok, what):
    print(f"  [{'PASS' if ok else 'FAIL'}] {what}")
    if not ok:
        FAIL.append(what)


class Client:
    """Request/response over one socket, matching replies by id."""

    def __init__(self, sock):
        self.sock = sock
        self.next_id = 0
        self.waiting = {}
        self.unmatched = []
        self.reader = asyncio.ensure_future(self._read())

    @classmethod
    async def open(cls, url):
        return cls(await ws.connect(url))

    async def _read(self):
        try:
            while True:
                msg = json.loads(await self.sock.recv())
                fut = self.waiting.pop(msg.get("id"), None)
                if fut and not fut.done():
                    fut.set_result((time.perf_counter(), msg))
                else:
                    self.unmatched.append(msg)
        except ws.ConnectionClosed:
            for f in self.waiting.values():
                if not f.done():
                    f.set_exception(ws.ConnectionClosed())

    async def send(self, msg):
        """Send; return a future resolving to (t_reply, reply)."""
        self.next_id += 1
        msg = {**msg, "id": self.next_id}
        fut = asyncio.get_running_loop().create_future()
        self.waiting[self.next_id] = fut
        await self.sock.send(json.dumps(msg))
        return fut

    async def ask(self, msg, timeout=5.0):
        t0 = time.perf_counter()
        fut = await self.send(msg)
        t1, reply = await asyncio.wait_for(fut, timeout)
        return reply, (t1 - t0) * 1000

    async def close(self):
        self.reader.cancel()
        await self.sock.close()


def pct(xs, p):
    if not xs:
        return float("nan")
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(round(p / 100 * (len(xs) - 1))))]


async def smoke(url):
    print("smoke:")
    c = await Client.open(url)
    st, _ = await c.ask({"type": "status"})
    print("  status:", {k: st[k] for k in ("subnet", "neurons", "max_rate_hz") if k in st})
    for v, s in [(0, "center"), (1, "left"), (1, "right"), (0.5, "center")]:
        r, ms = await c.ask({"type": "looming", "value": v, "side": s})
        print(f"  looming {v} {s:6s} -> {json.dumps({k: r[k] for k in r if k != 'id'})} ({ms:.0f} ms)")
    r, _ = await c.ask({"type": "looming", "value": 1, "side": "left"})
    check(r.get("type") == "escape" and set(r) >= {"strength", "side", "spikes"}
          and 0 <= r["strength"] <= 1 and r["side"] in ("left", "right")
          and len(r["spikes"]) == len(st.get("group_names", r["spikes"])),
          "escape message has the agreed shape")
    r, _ = await c.ask({"type": "looming", "value": "x"})
    check(r.get("type") == "error", "bad value -> error, server keeps running")
    r, _ = await c.ask({"type": "looming", "value": 7, "side": "up"})
    check(r.get("type") == "escape", "out-of-range value / unknown side tolerated")
    await c.close()


async def sweep(url, reps=20, warm=4):
    print("sweep (mean strength over", reps, "decisions per level; jump side %):")
    c = await Client.open(url)
    levels = [round(i / 10, 1) for i in range(11)]
    curves = {}
    for side in ("left", "right", "center"):
        means, jumps = [], []
        for v in levels:
            await c.ask({"type": "reset"})
            for _ in range(warm):
                await c.ask({"type": "looming", "value": v, "side": side})
            rs = [(await c.ask({"type": "looming", "value": v, "side": side}))[0]
                  for _ in range(reps)]
            means.append(statistics.mean(r["strength"] for r in rs))
            jumps.append(sum(r["side"] == "right" for r in rs) / reps)
        curves[side] = (means, jumps)
        print(f"  {side:6s} strength: " + " ".join(f"{m:.2f}" for m in means))
        print(f"  {side:6s} jump R %: " + " ".join(f"{j * 100:3.0f}" for j in jumps))
    await c.close()

    for side in ("left", "right", "center"):
        m = curves[side][0]
        dips = sum(1 for a, b in zip(m, m[1:]) if b < a - 0.05)
        check(m[0] < 0.05 and m[-1] > 0.7 and dips == 0,
              f"{side}: strength rises 0 -> {m[-1]:.2f} without dips")
    # Side: only judge levels where the brain is clearly escaping.
    for side, want in (("left", "right"), ("right", "left")):
        m, j = curves[side]
        strong = [jj for mm, jj in zip(m, j) if mm >= 0.3]
        acc = statistics.mean((jj if want == "right" else 1 - jj) for jj in strong)
        check(acc >= 0.9, f"{side} looming -> jumps {want} {acc * 100:.0f}% (strength >= 0.3)")
    return curves


async def latency(url, seconds=20, hz=20):
    print(f"latency ({hz} Hz looming for {seconds}s, plus a ping every 100 ms):")
    c = await Client.open(url)
    await c.ask({"type": "reset"})
    rtts, pings, lost = [], [], 0
    t_end = time.perf_counter() + seconds
    period = 1 / hz
    nxt = time.perf_counter()
    k = 0
    pending = []
    while time.perf_counter() < t_end:
        v = 0.5 + 0.5 * ((k // 20) % 2)          # alternate 0.5 / 1.0 each second
        side = ("left", "right", "center")[(k // 40) % 3]
        t0 = time.perf_counter()
        pending.append((t0, await c.send({"type": "looming", "value": v, "side": side})))
        if k % 2 == 0:
            p0 = time.perf_counter()
            pending.append((-p0, await c.send({"type": "ping"})))
        k += 1
        nxt += period
        await asyncio.sleep(max(0, nxt - time.perf_counter()))
    await asyncio.wait([f for _, f in pending], timeout=2)
    for t0, fut in pending:
        if not fut.done() or fut.exception():
            lost += 1
            continue
        t1, _ = fut.result()
        (pings if t0 < 0 else rtts).append((t1 - abs(t0)) * 1000)
    st, _ = await c.ask({"type": "status"})
    await c.close()
    n_sent = k
    print(f"  looming: sent {n_sent}, replies {len(rtts)}, lost/coalesced {n_sent - len(rtts)}")
    print(f"  looming RTT ms: p50 {pct(rtts, 50):.1f}  p95 {pct(rtts, 95):.1f}  max {max(rtts):.1f}")
    print(f"  ping RTT ms (while brain busy): p50 {pct(pings, 50):.1f}  p95 {pct(pings, 95):.1f}"
          f"  max {max(pings, default=float('nan')):.1f}  (answered {len(pings)})")
    print(f"  server: avg step {st['avg_step_ms']} ms -> {1000 / st['avg_step_ms']:.0f} decisions/s max")
    check(1000 / st["avg_step_ms"] >= 10, "brain >= 10 decisions/s")
    check(len(rtts) >= 0.95 * n_sent, "keeps up with 20 Hz (>=95% of requests answered)")
    check(pct(rtts, 95) < 100, f"looming p95 RTT < 100 ms ({pct(rtts, 95):.0f})")
    check(len(pings) > 0 and pct(pings, 95) < 20,
          f"handler not blocked by sim: ping p95 < 20 ms ({pct(pings, 95):.1f})")
    return rtts, pings


async def long_run(url, seconds=600, hz=20):
    print(f"long run ({seconds}s at {hz} Hz):")
    c = await Client.open(url)
    st0, _ = await c.ask({"type": "status"})
    blocks, mems = [], [st0["rss_mb"]]
    t_start = time.perf_counter()
    block = []
    nxt = time.perf_counter()
    k = 0
    while time.perf_counter() - t_start < seconds:
        v = (k % 100) / 99
        side = ("left", "right", "center")[(k // 100) % 3]
        r, ms = await c.ask({"type": "looming", "value": v, "side": side})
        block.append(ms)
        k += 1
        if len(block) == hz * 30:
            st, _ = await c.ask({"type": "status"})
            blocks.append(statistics.median(block))
            mems.append(st["rss_mb"])
            print(f"  t={time.perf_counter() - t_start:5.0f}s  median RTT {blocks[-1]:.1f} ms"
                  f"  step {st['avg_step_ms']} ms  rss {st['rss_mb']} MB")
            block = []
        nxt += 1 / hz
        await asyncio.sleep(max(0, nxt - time.perf_counter()))
    await c.close()
    if len(blocks) >= 2:
        check(blocks[-1] < blocks[0] * 1.5 + 5,
              f"no slowdown: RTT {blocks[0]:.1f} -> {blocks[-1]:.1f} ms")
        check(mems[-1] - mems[1] < 20, f"no memory growth: {mems[1]} -> {mems[-1]} MB")
    else:
        print("  (run >= 60 s for drift checks)")


async def reconnect(url, rounds=10):
    print(f"reconnect ({rounds} abrupt disconnects mid-request):")
    for i in range(rounds):
        c = await Client.open(url)
        for _ in range(5):                      # fire and vanish without waiting
            await c.send({"type": "looming", "value": 1, "side": "left"})
        c.reader.cancel()
        c.sock.writer.transport.abort()          # no close handshake: like a crash
    await asyncio.sleep(0.5)
    c = await Client.open(url)
    r, ms = await c.ask({"type": "looming", "value": 1, "side": "right"})
    st, _ = await c.ask({"type": "status"})
    await c.close()
    check(r.get("type") == "escape", f"server answers after {rounds} crashes ({ms:.0f} ms)")
    check(st["clients"] == 1, f"dead clients cleaned up (clients={st['clients']})")


async def fallback_vs_live(url):
    print("fallback vs live (same sweep in each mode):")
    c = await Client.open(url)
    await c.ask({"type": "mode", "mode": "live"})
    live = await sweep(url)
    await c.ask({"type": "mode", "mode": "fallback"})
    st, _ = await c.ask({"type": "status"})
    check(st["source"] == "fallback", "mode fallback -> answering from fallback")
    fb = await sweep(url)
    await c.ask({"type": "mode", "mode": "auto"})
    await c.close()
    for side in ("left", "right", "center"):
        err = max(abs(a - b) for a, b in zip(live[side][0], fb[side][0]))
        check(err < 0.15, f"{side}: fallback strength within 0.15 of live (max diff {err:.2f})")


async def stream(c, seconds, hz, plan, value_fn):
    """Send looming at hz for `seconds`; plan = {t_offset: coroutine factory}.
    Returns [(t_sent, t_reply or None, reply)]."""
    log = []
    t_start = time.perf_counter()
    nxt = t_start
    k = 0
    done = set()
    while time.perf_counter() - t_start < seconds:
        el = time.perf_counter() - t_start
        for at, fn in plan.items():
            if at not in done and el >= at:
                done.add(at)
                await fn()
        v, side = value_fn(k)
        fut = await c.send({"type": "looming", "value": v, "side": side})
        log.append((time.perf_counter(), fut))
        k += 1
        nxt += 1 / hz
        await asyncio.sleep(max(0, nxt - time.perf_counter()))
    # One shared deadline (superseded requests never get a reply by design).
    await asyncio.wait([f for _, f in log], timeout=2)
    out = []
    for t0, fut in log:
        if fut.done() and not fut.exception():
            t1, r = fut.result()
            out.append((t0 - t_start, t1 - t_start, r))
        else:
            out.append((t0 - t_start, None, None))
    return out


def max_reply_gap(rows):
    ts = sorted(t1 for _, t1, _ in rows if t1 is not None)
    return max((b - a for a, b in zip(ts, ts[1:])), default=0) * 1000


async def switch_test(url, hz=20):
    print("mode switching mid-game (20 Hz, steady left looming 0.8):")
    c = await Client.open(url)
    await c.ask({"type": "mode", "mode": "live"})
    await c.ask({"type": "reset"})
    sources = []

    def to(mode):
        async def f():
            r, _ = await c.ask({"type": "mode", "mode": mode})
            sources.append((mode, r["source"]))
        return f
    switch_at = (2, 4, 6)
    rows = await stream(c, 8, hz, dict(zip(switch_at, (to("fallback"), to("auto"), to("live")))),
                        lambda k: (0.8, "left"))
    await c.close()
    answered = [r for _, t1, r in rows if t1 is not None]
    st = [(t0, r["strength"]) for t0, t1, r in rows
          if t1 is not None and r.get("type") == "escape"]
    steps = [(b[0], abs(b[1] - a[1])) for a, b in zip(st, st[1:])]
    # Live noise baseline: steps away from switches, after the 1 s warm-up.
    noise = max(s for t, s in steps if t > 1 and all(abs(t - w) > 0.3 for w in switch_at))
    at_switch = max(s for t, s in steps if any(abs(t - w) <= 0.3 for w in switch_at))
    sides = {r["side"] for r in answered[len(answered) // 4:]}
    print(f"  replies {len(answered)}/{len(rows)}, max gap {max_reply_gap(rows):.0f} ms, "
          f"largest strength step at a switch {at_switch:.2f} (live noise {noise:.2f}), "
          f"sources {sources}")
    check(len(answered) >= 0.95 * len(rows), "no requests dropped across switches")
    check(max_reply_gap(rows) < 150, "no reply gap > 150 ms across switches")
    check(at_switch <= max(0.25, noise),
          f"strength continuous across switches ({at_switch:.2f} <= normal noise)")
    check(sides == {"right"}, f"left looming -> jumps right in every mode ({sides})")
    check([s for _, s in sources] == ["fallback", "live", "live"], "each switch took effect")


async def failover_test(url, hz=20, stall_ms=1500):
    print(f"failover (server needs --debug): live brain stalls {stall_ms} ms mid-game:")
    c = await Client.open(url)
    await c.ask({"type": "mode", "mode": "auto"})
    seen = []

    async def stall():
        await c.send({"type": "debug_stall", "ms": stall_ms})

    async def peek():
        r, _ = await c.ask({"type": "status"})
        seen.append(r["source"])
    rows = await stream(c, 7, hz, {1.5: stall, 2.5: peek, 6.5: peek},
                        lambda k: (0.7, ("left", "right")[(k // 30) % 2]))
    st, _ = await c.ask({"type": "status"})
    await c.close()
    answered = sum(1 for _, t1, _ in rows if t1 is not None)
    gap = max_reply_gap(rows)
    print(f"  replies {answered}/{len(rows)}, max gap {gap:.0f} ms, sources mid/after {seen}, "
          f"switches {st['switches']}, live misses {st['live_misses']}")
    check(answered >= 0.9 * len(rows), "game kept getting replies during the stall")
    check(gap < 450, f"longest silence {gap:.0f} ms (< 450)")
    check(seen[:1] == ["fallback"], "switched to fallback while live brain stalled")
    check(seen[1:] == ["live"], "switched back to live after it recovered")


def speed():
    """In-process timing of every subnetwork (no server). Run this on the Pi."""
    import numpy as np
    from brain import SUBNET_DIR, FlyBrain
    print(f"speed (numpy {np.__version__}, {sys.platform}):")
    best = None
    for p in sorted(SUBNET_DIR.glob("subnet_t*.npz"), key=lambda p: int(p.stem[8:])):
        b = FlyBrain(p, seed=0)
        b.decide(0.5, "left")
        res = {}
        for label, gap in (("back-to-back", 0.0), ("20 Hz paced", 0.05)):
            ts = []
            for k in range(30):
                if gap:
                    time.sleep(gap)
                t0 = time.perf_counter()
                b.decide(0.8, ("left", "right", "center")[k % 3])
                ts.append((time.perf_counter() - t0) * 1000)
            res[label] = statistics.median(ts), pct(ts, 95)
        ok = 1000 / res["20 Hz paced"][1] >= 10
        print(f"  {p.name:16s} {b.n:5d} neurons  " + "  ".join(
            f"{k}: median {m:.1f} ms / p95 {q:.1f} ms" for k, (m, q) in res.items())
            + f"  -> {1000 / res['back-to-back'][0]:.0f}/s {'OK' if ok else 'TOO SLOW'}")
        if ok and best is None:
            best = p.name
    print(f"  largest subnetwork meeting 10 decisions/s: {best or 'none (use --mode fallback)'}")
    check(best is not None, "some subnetwork meets >= 10 decisions/s on this machine")


async def amain(a):
    if a.test == "speed":
        speed()
    if a.test in ("smoke", "all"):
        await smoke(a.url)
    if a.test in ("sweep", "all"):
        await sweep(a.url)
    if a.test in ("latency", "all"):
        await latency(a.url, a.seconds or 20)
    if a.test in ("reconnect", "all"):
        await reconnect(a.url)
    if a.test in ("fallback", "modes"):
        await fallback_vs_live(a.url)
    if a.test in ("switch", "modes"):
        await switch_test(a.url)
    if a.test in ("failover", "modes"):
        await failover_test(a.url)
    if a.test in ("long", "all"):
        await long_run(a.url, a.seconds or (600 if a.test == "long" else 90))
    print(f"\n{'ALL PASSED' if not FAIL else f'{len(FAIL)} FAILED: ' + '; '.join(FAIL)}")
    return 1 if FAIL else 0


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("test", choices=["smoke", "sweep", "latency", "long", "reconnect", "all",
                                     "fallback", "switch", "failover", "modes", "speed"])
    ap.add_argument("--url", default="ws://127.0.0.1:8765")
    ap.add_argument("--seconds", type=int, default=None)
    sys.exit(asyncio.run(amain(ap.parse_args())))
