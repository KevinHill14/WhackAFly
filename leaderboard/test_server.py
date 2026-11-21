"""End-to-end test: starts server.py against a real Postgres and drives it over HTTP.

Database, in order of preference:
  1. LEADERBOARD_TEST_DATABASE_URL  (any Postgres, e.g. a throwaway Tiger Cloud service; game_events is dropped first,
     so never point this at the real board)
  2. a temporary local Postgres from the `pgserver` package (pip install pgserver), if installed
Run:  python leaderboard/test_server.py
"""

import json
import os
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

import psycopg

HERE = Path(__file__).resolve().parent
checks = 0


def check(condition, label):
    global checks
    assert condition, label
    checks += 1
    print("  ok ", label)


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def get_database():
    url = os.environ.get("LEADERBOARD_TEST_DATABASE_URL")
    if url:
        return url, None, "LEADERBOARD_TEST_DATABASE_URL"
    try:
        import pgserver
    except ImportError:
        sys.exit("No test database: set LEADERBOARD_TEST_DATABASE_URL or pip install pgserver.")
    pg = pgserver.get_server(tempfile.mkdtemp(prefix="whackafly-pg-"), cleanup_mode="stop")
    return pg.get_uri(), pg, "local pgserver Postgres"


def call(base, method, path, body=None, timeout=10):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(base + path, data=data, method=method, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read())


def start_server(url):
    port = free_port()
    env = dict(os.environ, LEADERBOARD_DATABASE_URL=url, LEADERBOARD_PORT=str(port))
    process = subprocess.Popen([sys.executable, str(HERE / "server.py")], env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    base = f"http://127.0.0.1:{port}"
    for _ in range(200):
        try:
            if call(base, "GET", "/health")[0] == 200:
                return process, base
        except OSError:
            time.sleep(0.1)
    process.kill()
    raise AssertionError("server did not start: " + process.communicate()[0])


def stop_server(process):
    process.terminate()
    output = process.communicate(timeout=10)[0]
    if "Traceback" in output:
        print(output)
        sys.exit(1)
    return output


def score(name, damage, seconds, theme=None, round_id=None):
    body = {"name": name, "damage": damage, "seconds": seconds, "theme": theme}
    if round_id:
        body["round_id"] = round_id
    return body


def test_database_down():
    # Nothing listens on this port, so the database is "down" for the whole run.
    process, base = start_server(f"postgresql://nobody:nothing@127.0.0.1:{free_port()}/none?connect_timeout=2")
    try:
        status, body = call(base, "GET", "/health")
        check(status == 200 and body["database"] is False, "server starts with the database down")
        started = time.time()
        status, body = call(base, "POST", "/scores", score("x", 1, 1))
        check(status == 503 and body == {"error": "leaderboard database unavailable"} and time.time() - started < 8,
              "database down -> quick 503, no details leaked")
    finally:
        stop_server(process)


def main():
    url, pg, source = get_database()
    with psycopg.connect(url, autocommit=True) as conn:
        conn.execute("DROP VIEW IF EXISTS leaderboard")
        conn.execute("DROP TABLE IF EXISTS game_events")
        version = conn.execute("SELECT version()").fetchone()[0]
    print(f"Database: {source} ({version.split(',')[0]})")

    process, base = start_server(url)
    try:
        status, body = call(base, "GET", "/scores")
        check(status == 200 and body["scores"] == [], "empty board")
        check(call(base, "GET", "/health")[1]["database"] is True, "health reports database up")

        # A time that is not exact in binary (as Unity sends it) must not rank behind itself.
        status, body = call(base, "POST", "/scores", score("Solo", 0, 40.53125))
        check(status == 201 and body["rank"] == 1 and body["scores"][0]["seconds"] == 40.53, "inexact time ranks #1 alone")
        status, body = call(base, "POST", "/scores", score("Twin", 0, 40.53))
        check(body["rank"] == 1, "equal damage and time tie for the same rank")
        with psycopg.connect(url, autocommit=True) as conn:
            conn.execute("DELETE FROM game_events")

        runs = [("Eesha", 120, 40.5, "Old House"), ("Kevin", 0, 75.0, "Medieval"),
                ("Ava", 120, 22.25, "Futuristic"), ("Bo", 2620, 9.0, "Old House")]
        ranks = []
        for name, damage, seconds, theme in runs:
            status, body = call(base, "POST", "/scores", score(name, damage, seconds, theme))
            check(status == 201, f"submit {name}")
            ranks.append(body["rank"])
        check(ranks == [1, 1, 2, 4], f"ranks at submit time {ranks}")

        status, body = call(base, "GET", "/scores?limit=10")
        order = [(s["name"], s["damage"], s["seconds"]) for s in body["scores"]]
        check(order == [("Kevin", 0, 75.0), ("Ava", 120, 22.25), ("Eesha", 120, 40.5), ("Bo", 2620, 9.0)],
              "ordered by damage, then time")
        check(body["scores"][0]["theme"] == "Medieval" and body["scores"][0]["at"].endswith("Z"), "theme and UTC timestamp")

        for i in range(12):
            call(base, "POST", "/scores", score(f"p{i}", 5000 + i, 1))
        status, body = call(base, "POST", "/scores", score("  Late   Comer  ", 1, 3))
        check(len(body["scores"]) == 10 and body["rank"] == 2, "submit returns top 10 and rank")
        check(body["scores"][1]["name"] == "Late Comer", "name whitespace collapsed")
        check(len(call(base, "GET", "/scores?limit=3")[1]["scores"]) == 3, "limit param")

        # Same round submitted twice counts once.
        rid = str(uuid.uuid4())
        call(base, "POST", "/scores", score("Dup", 7, 7, round_id=rid))
        call(base, "POST", "/scores", score("Dup", 7, 7, round_id=rid))
        with psycopg.connect(url) as conn:
            dupes = conn.execute("SELECT count(*) FROM leaderboard WHERE round_id = %s", (rid,)).fetchone()[0]
        check(dupes == 1, "one result per round")

        # Event log: a batch for one round, then read it back the way a dashboard would.
        rid = str(uuid.uuid4())
        events = [
            {"kind": "round_start", "t": 0, "theme": "Old House"},
            {"kind": "swing", "t": 3.2, "damage": 450, "data": {"hit": True, "target": "tv", "strength": 0.8}},
            {"kind": "fly_escape", "t": 3.3, "data": {"strength": 0.71, "side": "left"}},
            {"kind": "fly_swatted", "t": 12.5, "damage": 450, "seconds": 12.5},
        ]
        status, body = call(base, "POST", "/events", {"round_id": rid, "events": events})
        check(status == 201 and body["stored"] == 4, "event batch stored")
        with psycopg.connect(url) as conn:
            rows = conn.execute("SELECT kind, round_time, damage, data->>'target' FROM game_events WHERE round_id = %s ORDER BY round_time",
                                (rid,)).fetchall()
        check([r[0] for r in rows] == ["round_start", "swing", "fly_escape", "fly_swatted"] and rows[1][3] == "tv",
              "events readable per round, in order, with JSON data")
        check(all(not s["name"].startswith("fly") for s in call(base, "GET", "/scores")[1]["scores"]), "events stay off the leaderboard")

        bad_events = [
            {"round_id": rid, "events": []},
            {"round_id": "not-a-uuid", "events": [{"kind": "swing"}]},
            {"events": [{"kind": "swing"}]},
            {"round_id": rid, "events": [{"kind": "Bad Kind"}]},
            {"round_id": rid, "events": [{"kind": "round_result", "name": "cheat", "damage": 0, "seconds": 0}]},
            {"round_id": rid, "events": [{"kind": "swing", "data": {"x": "y" * 5000}}]},
        ]
        check(all(call(base, "POST", "/events", b)[0] == 400 for b in bad_events), "rejects bad event batches")

        bad = [{"damage": 1, "seconds": 1}, score("x", -5, 1), {"name": "x", "damage": "lots", "seconds": 1},
               score("x", 1, 1e9), score("x", 1, 1, round_id="nope"), [1, 2]]
        check(all(call(base, "POST", "/scores", b)[0] == 400 for b in bad), "rejects bad submissions")
        check(call(base, "GET", "/nope")[0] == 404, "404 for unknown path")
        check(call(base, "POST", "/scores", score("N" * 50, 99999, 1))[0] == 201, "long name trimmed to 20 and accepted")

        call(base, "POST", "/scores", score("x'); DROP TABLE game_events;--", 99999, 2))
        check(call(base, "GET", "/scores")[0] == 200, "injection attempt is harmless")
    finally:
        stop_server(process)
        if pg is not None:
            pg.cleanup()

    test_database_down()
    print(f"PASS: {checks} checks against {source}")


if __name__ == "__main__":
    main()
