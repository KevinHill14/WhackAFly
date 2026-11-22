#!/bin/sh
# Starts everything the Pi needs: the fly brain, then the game backend.
#
#   sh backend/start.sh           start both (Ctrl+C stops both)
#   sh backend/start.sh stop      stop servers left running by this script
#   sh backend/start.sh status    show whether they are running (and their QNX priorities)
#   sh backend/start.sh logs      recent backend events from the QNX system log (slog2info)
#
# Options (environment variables):
#   SUBNET=brain/subnet/subnet_t10.npz   which brain size to use (t5 / t10 / t20)
#   BRAIN_MODE=auto                      auto, live or fallback
#   BRAIN_PORT=8765  BACKEND_PORT=8766  UDP_PORT=9000
#   RT_PRIORITY=0                        QNX real-time priority (0 = leave at the default of 10). 20 starved
#                                        Wi-Fi and the system on the Pi 5 until it froze, so it is off by default.
#   PRACTICE=1                           free play: no countdown or timer, the fly respawns
#   ESP32_PORT=8767                      WebSocket port the swatter streams its IMU samples to
#
# Works with plain POSIX sh and Python 3 on QNX or Linux. NumPy is optional: without
# it the brain answers from its lookup table.

ROOT=$(cd "$(dirname "$0")/.." && pwd)
RUN="$ROOT/backend/.run"
SUBNET=${SUBNET:-brain/subnet/subnet_t10.npz}
BRAIN_MODE=${BRAIN_MODE:-auto}
BRAIN_PORT=${BRAIN_PORT:-8765}
BACKEND_PORT=${BACKEND_PORT:-8766}
UDP_PORT=${UDP_PORT:-9000}
ESP32_PORT=${ESP32_PORT:-8767}
RT_PRIORITY=${RT_PRIORITY:-0}
mkdir -p "$RUN"

port_open() {  # port_open PORT -> 0 if something accepts connections
    python3 -c "import socket,sys; s=socket.socket(); s.settimeout(0.5); sys.exit(0 if s.connect_ex(('127.0.0.1',$1))==0 else 1)"
}

alive() { [ -f "$RUN/$1.pid" ] && kill -0 "$(cat "$RUN/$1.pid")" 2>/dev/null; }

stop_one() {
    if alive "$1"; then
        kill "$(cat "$RUN/$1.pid")" 2>/dev/null
        echo "stopped $1"
    fi
    rm -f "$RUN/$1.pid"
}

my_ip() {
    # The address used to reach the outside (default route); sends no packets.
    ip=$(python3 -c "import socket; s=socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.connect(('8.8.8.8', 53)); print(s.getsockname()[0])" 2>/dev/null)
    if [ -z "$ip" ] && command -v ifconfig >/dev/null 2>&1; then
        ip=$(ifconfig 2>/dev/null | awk '/inet / && $2 != "127.0.0.1" {print $2; exit}')
    fi
    echo "$ip"
}

case "$1" in
    stop)
        stop_one backend; stop_one brain; exit 0 ;;
    status)
        for n in brain backend; do
            if alive $n; then echo "$n: running (pid $(cat "$RUN/$n.pid"))"; else echo "$n: not running"; fi
        done
        if command -v pidin >/dev/null 2>&1; then     # QNX: show priority and scheduling policy (e.g. 20r)
            for n in brain backend; do
                alive $n && echo "$n QNX priority: $(pidin | awk -v p="$(cat "$RUN/$n.pid")" '$1==p {print $4; exit}')"
            done
        fi
        port_open "$BRAIN_PORT" && echo "port $BRAIN_PORT: open" || echo "port $BRAIN_PORT: closed"
        port_open "$BACKEND_PORT" && echo "port $BACKEND_PORT: open" || echo "port $BACKEND_PORT: closed"
        port_open "$ESP32_PORT" && echo "port $ESP32_PORT: open (swatter stream)" || echo "port $ESP32_PORT: closed"
        exit 0 ;;
    logs)
        if command -v slog2info >/dev/null 2>&1; then slog2info 2>/dev/null | grep "flyai:" | tail -n 30
        else echo "slog2info not found (this is not QNX); see backend/.run/backend.log"; fi
        exit 0 ;;
    ""|start) ;;
    *) echo "usage: sh backend/start.sh [start|stop|status|logs]"; exit 2 ;;
esac

# ---- checks ---------------------------------------------------------------
command -v python3 >/dev/null 2>&1 || { echo "ERROR: python3 not found"; exit 1; }
cd "$ROOT" || exit 1

if python3 -c "import numpy" 2>/dev/null; then
    echo "NumPy: found, the live brain can run"
    SUBNET_ARG="--subnet $SUBNET"
    [ -f "$SUBNET" ] || { echo "ERROR: $SUBNET is missing"; exit 1; }
else
    echo "NumPy: NOT found, the brain will use its lookup-table fallback"
    echo "       (install it with: python3 -m pip install numpy)"
    SUBNET_ARG=""
    [ "$BRAIN_MODE" = "live" ] && { echo "ERROR: BRAIN_MODE=live needs NumPy"; exit 1; }
fi

# Stop copies this script started earlier, and refuse if something else owns a port.
stop_one backend; stop_one brain
sleep 1
if [ "$ESP32_PORT" = "$BRAIN_PORT" ] || [ "$ESP32_PORT" = "$BACKEND_PORT" ]; then
    echo "ERROR: ESP32_PORT ($ESP32_PORT) must differ from BRAIN_PORT ($BRAIN_PORT) and BACKEND_PORT ($BACKEND_PORT)."
    exit 1
fi
for p in "$BRAIN_PORT" "$BACKEND_PORT" "$ESP32_PORT"; do
    if port_open "$p"; then
        echo "ERROR: port $p is already in use by another program. Stop it first."
        exit 1
    fi
done

# ---- start ----------------------------------------------------------------
cleanup() { stop_one backend; stop_one brain; exit "${1:-0}"; }
trap cleanup INT TERM

# On QNX, `on -p 20r` starts the brain under round-robin real-time priority 20 (default is 10).
RT=""
if [ "$RT_PRIORITY" -gt 0 ] 2>/dev/null && command -v on >/dev/null 2>&1 && on -p "${RT_PRIORITY}r" true 2>/dev/null; then
    RT="on -p ${RT_PRIORITY}r"
    echo "QNX: starting the brain with: $RT"
fi
$RT python3 brain/server.py --mode "$BRAIN_MODE" --port "$BRAIN_PORT" $SUBNET_ARG >"$RUN/brain.log" 2>&1 &
echo $! >"$RUN/brain.pid"

i=0
until port_open "$BRAIN_PORT"; do
    i=$((i + 1))
    if [ $i -gt 30 ] || ! alive brain; then
        echo "ERROR: the brain did not start. Last log lines:"; tail -n 15 "$RUN/brain.log"
        stop_one brain; exit 1
    fi
    sleep 1
done
echo "brain:   ws://0.0.0.0:$BRAIN_PORT  (log: backend/.run/brain.log)"

PRACTICE_ARG=""
[ "${PRACTICE:-0}" = "1" ] && PRACTICE_ARG="--practice"
python3 backend/server.py $PRACTICE_ARG --rt-priority "$RT_PRIORITY" --port "$BACKEND_PORT" --esp-port "$ESP32_PORT" --udp-port "$UDP_PORT" --brain "ws://127.0.0.1:$BRAIN_PORT" >"$RUN/backend.log" 2>&1 &
echo $! >"$RUN/backend.pid"
i=0
until port_open "$BACKEND_PORT"; do
    i=$((i + 1))
    if [ $i -gt 15 ] || ! alive backend; then
        echo "ERROR: the backend did not start. Last log lines:"; tail -n 15 "$RUN/backend.log"
        cleanup 1
    fi
    sleep 1
done
echo "backend: ws://0.0.0.0:$BACKEND_PORT  swatter stream ws://0.0.0.0:$ESP32_PORT  udp :$UDP_PORT  (log: backend/.run/backend.log)"

IP=$(my_ip)
echo
echo "Ready. On the laptop running Unity, set:"
echo "  export FLYAI_BACKEND=ws://${IP:-<pi-ip>}:$BACKEND_PORT"
echo "The ESP32 firmware must stream to ws://${IP:-<pi-ip>}:$ESP32_PORT (not the brain's port $BRAIN_PORT)."
echo "Press Ctrl+C to stop."

# Stay in the foreground; if either server dies, report it and stop the other.
while alive brain && alive backend; do sleep 2; done
echo "A server stopped unexpectedly:"
alive brain || { echo "--- brain log"; tail -n 10 "$RUN/brain.log"; }
alive backend || { echo "--- backend log"; tail -n 10 "$RUN/backend.log"; }
cleanup 1
