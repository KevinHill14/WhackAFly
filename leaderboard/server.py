"""WhackAFly game log and leaderboard: a tiny HTTP API in front of Postgres on Tiger Cloud (Tiger Data).

Everything the game reports is a row in one time-series table, `game_events` (a TimescaleDB hypertable on
Tiger Cloud). The leaderboard is the `leaderboard` view over the `round_result` rows. A spectator dashboard (live
feed, replays) can read `game_events` directly later.

The game never sees the database credentials. It calls this server instead:

    GET  /scores?limit=10   -> {"scores": [...]}                     best first
    POST /scores            <- {"round_id", "name", "damage", "seconds", "theme"}
                            -> {"rank": 3, "scores": [...]}          rank of the run, plus the top 10
    POST /events            <- {"round_id", "events": [{"kind", "t", "data": {...}}, ...]}
                            -> {"stored": n}
    GET  /health            -> {"ok": true, "database": true|false}

Ranking: lowest damage bill wins; equal bills go to the faster swat.

The connection string comes from LEADERBOARD_DATABASE_URL (or DATABASE_URL), read from the environment or from
leaderboard/.env, which is git-ignored. The server starts even when the database is unreachable and answers 503
until it comes back, so the game never waits on it. Run:  python leaderboard/server.py
"""

import json
import os
import re
import sys
import threading
import uuid
from datetime import timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

import psycopg
from psycopg.types.json import Jsonb

HERE = Path(__file__).resolve().parent
DEFAULT_PORT = 8768
TOP_N = 10
MAX_LIMIT = 100
MAX_NAME = 20
MAX_BODY = 256 * 1024
MAX_EVENTS = 500
MAX_EVENT_DATA = 4096
CONNECT_TIMEOUT = 5  # seconds; the game gives up on a request after 8
KIND = re.compile(r"^[a-z][a-z0-9_]{0,31}$")

SCHEMA = """
CREATE TABLE IF NOT EXISTS game_events (
    time        TIMESTAMPTZ   NOT NULL DEFAULT now(),
    round_id    UUID          NOT NULL,
    kind        TEXT          NOT NULL,                 -- round_start, swing, fly_escape, fly_flee, fly_swatted, round_result (named runs only)
    round_time  REAL,                                   -- seconds since the round started, from the game clock
    player_name TEXT          CHECK (char_length(player_name) BETWEEN 1 AND 20),
    damage      INTEGER       CHECK (damage >= 0),
    seconds     NUMERIC(8, 2) CHECK (seconds >= 0),     -- exact, so equal times tie instead of float drift
    theme       TEXT,
    data        JSONB         NOT NULL DEFAULT '{}'
);
"""

# Indexes and the view come after the hypertable conversion, which TimescaleDB prefers on a bare table.
SCHEMA_AFTER = """
CREATE INDEX IF NOT EXISTS game_events_round ON game_events (round_id, time);
CREATE INDEX IF NOT EXISTS game_events_rank ON game_events (damage, seconds, time)
    WHERE kind = 'round_result' AND player_name IS NOT NULL;
CREATE OR REPLACE VIEW leaderboard AS
    SELECT round_id, player_name, damage, seconds, theme, time AS created_at
    FROM game_events
    WHERE kind = 'round_result' AND player_name IS NOT NULL;
"""

# On Tiger Cloud the table becomes a hypertable. Plain Postgres (local tests) has no TimescaleDB, so this is skipped.
HYPERTABLE = "SELECT create_hypertable('game_events', 'time', if_not_exists => TRUE, migrate_data => TRUE)"

TOP_QUERY = """
SELECT player_name, damage, seconds, theme, created_at
FROM leaderboard
ORDER BY damage, seconds, created_at
LIMIT %s
"""

# One result per round: a resubmission of the same round is ignored.
INSERT_RESULT = """
INSERT INTO game_events (round_id, kind, round_time, player_name, damage, seconds, theme)
SELECT %(round_id)s, 'round_result', %(seconds)s, %(name)s, %(damage)s, %(seconds)s, %(theme)s
WHERE NOT EXISTS (SELECT 1 FROM game_events WHERE round_id = %(round_id)s AND kind = 'round_result')
"""

# Runs strictly better than this round's, plus one, is its rank (ties share the better rank).
# Compared inside the database against the stored row, so no value round-trips through Python.
RANK_QUERY = """
SELECT count(*) + 1
FROM leaderboard other, leaderboard mine
WHERE mine.round_id = %s AND (other.damage, other.seconds) < (mine.damage, mine.seconds)
"""

INSERT_EVENT = """
INSERT INTO game_events (round_id, kind, round_time, player_name, damage, seconds, theme, data)
VALUES (%s, %s, %s, %s, %s, %s, %s, %s)
"""


def load_env_file(path):
    """Minimal .env reader: KEY=VALUE lines, # comments, optional quotes. Never overrides real env vars."""
    if not path.exists():
        return
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip().removeprefix("export ").strip()
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]
        os.environ.setdefault(key, value)


def database_url():
    load_env_file(HERE / ".env")
    url = os.environ.get("LEADERBOARD_DATABASE_URL") or os.environ.get("DATABASE_URL")
    if not url:
        sys.exit("Set LEADERBOARD_DATABASE_URL (in leaderboard/.env or the environment) to your Tiger Cloud connection string.")
    return url


class Store:
    """One shared connection, serialised by a lock. Connects lazily and creates the schema on first success."""

    def __init__(self, url):
        self.url = url
        self.lock = threading.Lock()
        self.conn = None
        self.ready = False
        self.hypertable = False

    def _connect(self):
        if self.conn is not None:
            try:
                self.conn.close()
            except Exception:
                pass
        self.conn = None
        # Short timeout and TCP keepalives so a dead database fails fast instead of hanging the game's request.
        self.conn = psycopg.connect(self.url, autocommit=True, connect_timeout=CONNECT_TIMEOUT,
                                    keepalives=1, keepalives_idle=30, keepalives_interval=10, keepalives_count=3)
        if not self.ready:
            with self.conn.cursor() as cur:
                cur.execute(SCHEMA)
                cur.execute("SELECT 1 FROM pg_extension WHERE extname = 'timescaledb'")
                if cur.fetchone():
                    cur.execute(HYPERTABLE)
                    self.hypertable = True
                cur.execute(SCHEMA_AFTER)
            self.ready = True
            print(f"[leaderboard] database ready (game_events {'hypertable' if self.hypertable else 'plain table'})", flush=True)

    def connect_now(self):
        """Best-effort connect at startup; failure is fine, requests retry later."""
        try:
            self._run(lambda cur: None)
            return True
        except psycopg.Error as error:
            print(f"[leaderboard] database not reachable yet ({type(error).__name__}); will retry on each request", flush=True)
            return False

    def _run(self, work):
        with self.lock:
            # Retry once only when an existing connection dropped (Tiger Cloud closes idle links);
            # a failed fresh connect is reported straight away.
            reused = self.conn is not None and not self.conn.closed
            for attempt in range(2 if reused else 1):
                try:
                    if self.conn is None or self.conn.closed:
                        self._connect()
                    with self.conn.cursor() as cur:
                        return work(cur)
                except psycopg.OperationalError:
                    self.conn = None
                    if attempt == 1 or not reused:
                        raise

    def online(self):
        with self.lock:
            return self.conn is not None and not self.conn.closed

    def top(self, limit):
        def work(cur):
            cur.execute(TOP_QUERY, (limit,))
            return [row_to_json(row) for row in cur.fetchall()]
        return self._run(work)

    def submit(self, round_id, name, damage, seconds, theme):
        def work(cur):
            cur.execute(INSERT_RESULT, {"round_id": round_id, "name": name, "damage": damage, "seconds": seconds, "theme": theme})
            cur.execute(RANK_QUERY, (round_id,))
            rank = cur.fetchone()[0]
            cur.execute(TOP_QUERY, (TOP_N,))
            return rank, [row_to_json(row) for row in cur.fetchall()]
        return self._run(work)

    def log(self, round_id, events):
        def work(cur):
            cur.executemany(INSERT_EVENT, [
                (round_id, e["kind"], e["t"], e["name"], e["damage"], e["seconds"], e["theme"], Jsonb(e["data"]))
                for e in events])
            return len(events)
        return self._run(work)


def row_to_json(row):
    name, damage, seconds, theme, created = row
    return {
        "name": name,
        "damage": damage,
        "seconds": round(float(seconds), 2),
        "theme": theme or "",
        "at": created.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }


class BadRequest(ValueError):
    pass


def clean_name(value):
    name = " ".join(str(value or "").split())[:MAX_NAME]
    return name or None


def number(value, kind, low, high, label):
    if value is None:
        return None
    try:
        value = kind(value)
    except (TypeError, ValueError):
        raise BadRequest(f"{label} must be a number")
    if not low <= value <= high:  # Also rejects NaN.
        raise BadRequest(f"{label} out of range")
    return value


def round_id_of(payload, required):
    value = payload.get("round_id")
    if value is None and not required:
        return str(uuid.uuid4())  # Older clients without round ids still get a row.
    try:
        return str(uuid.UUID(str(value)))
    except ValueError:
        raise BadRequest("round_id must be a UUID")


def parse_submission(payload):
    if not isinstance(payload, dict):
        raise BadRequest("expected a JSON object")
    name = clean_name(payload.get("name"))
    if not name:
        raise BadRequest("name is required")
    damage = number(payload.get("damage"), int, 0, 10_000_000, "damage")
    seconds = number(payload.get("seconds"), float, 0, 86_400, "seconds")
    if damage is None or seconds is None:
        raise BadRequest("damage and seconds are required")
    theme = str(payload.get("theme") or "")[:40] or None
    return round_id_of(payload, False), name, damage, round(seconds, 2), theme


def parse_events(payload):
    if not isinstance(payload, dict) or not isinstance(payload.get("events"), list):
        raise BadRequest("expected {round_id, events: [...]}")
    round_id = round_id_of(payload, True)
    events = payload["events"]
    if not 0 < len(events) <= MAX_EVENTS:
        raise BadRequest(f"send 1-{MAX_EVENTS} events")
    parsed = []
    for e in events:
        if not isinstance(e, dict) or not KIND.match(str(e.get("kind", ""))):
            raise BadRequest("each event needs a lowercase kind")
        if e["kind"] == "round_result":
            raise BadRequest("round results go to POST /scores")
        data = e.get("data") or {}
        if not isinstance(data, dict) or len(json.dumps(data)) > MAX_EVENT_DATA:
            raise BadRequest("event data must be a small JSON object")
        seconds = number(e.get("seconds"), float, 0, 86_400, "seconds")
        parsed.append({
            "kind": e["kind"],
            "t": number(e.get("t"), float, 0, 86_400, "t"),
            "name": clean_name(e.get("name")),
            "damage": number(e.get("damage"), int, 0, 10_000_000, "damage"),
            "seconds": None if seconds is None else round(seconds, 2),
            "theme": str(e.get("theme") or "")[:40] or None,
            "data": data,
        })
    return round_id, parsed


def make_handler(store):
    class Handler(BaseHTTPRequestHandler):
        server_version = "WhackAFlyLeaderboard/2"

        def log_message(self, fmt, *args):
            pass  # The game posts events every second; keep the console for real problems.

        def _send(self, status, body):
            data = json.dumps(body).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def _fail(self, error):
            # Database errors are printed here, never sent to the client (they can include host names).
            print(f"[leaderboard] database error: {type(error).__name__}: {error}".strip(), flush=True)
            self._send(503, {"error": "leaderboard database unavailable"})

        def _body(self):
            try:
                length = int(self.headers.get("Content-Length") or 0)
            except ValueError:
                length = -1
            if not 0 < length <= MAX_BODY:
                raise BadRequest("bad body size")
            try:
                return json.loads(self.rfile.read(length))
            except (json.JSONDecodeError, UnicodeDecodeError) as error:
                raise BadRequest(str(error))

        def do_GET(self):
            url = urlparse(self.path)
            if url.path == "/health":
                return self._send(200, {"ok": True, "database": store.online()})
            if url.path != "/scores":
                return self._send(404, {"error": "not found"})
            try:
                limit = int(parse_qs(url.query).get("limit", [TOP_N])[0])
            except ValueError:
                return self._send(400, {"error": "limit must be a number"})
            try:
                self._send(200, {"scores": store.top(max(1, min(limit, MAX_LIMIT)))})
            except psycopg.Error as error:
                self._fail(error)

        def do_POST(self):
            path = urlparse(self.path).path
            if path not in ("/scores", "/events"):
                return self._send(404, {"error": "not found"})
            try:
                payload = self._body()
                if path == "/scores":
                    submission = parse_submission(payload)
                else:
                    round_id, events = parse_events(payload)
            except BadRequest as error:
                return self._send(400, {"error": str(error)})
            try:
                if path == "/scores":
                    rank, scores = store.submit(*submission)
                    self._send(201, {"rank": rank, "scores": scores})
                else:
                    self._send(201, {"stored": store.log(round_id, events)})
            except psycopg.Error as error:
                self._fail(error)

    return Handler


def main():
    port = int(os.environ.get("LEADERBOARD_PORT", DEFAULT_PORT))
    host = os.environ.get("LEADERBOARD_HOST", "127.0.0.1")
    store = Store(database_url())
    store.connect_now()
    server = ThreadingHTTPServer((host, port), make_handler(store))
    print(f"[leaderboard] listening on http://{host}:{port}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
