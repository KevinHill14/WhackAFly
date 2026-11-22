#!/bin/sh
# Starts WhackAFly when the Pi boots: the fly brain and the game backend (backend/start.sh), which the swatter and
# Unity connect to by themselves, plus the black-box monitor if ~/tmp/pi_mon.py is there.
# Called as qnxuser from one line added to /system/etc/startup/post_startup.sh (the original is saved next to it as
# post_startup.sh.before-whackafly). To stop it starting on boot, put that file back.
# Keep this file with Unix line endings: QNX's sh does not run a script with CRLF ones.

HOME_DIR=/data/home/qnxuser
export HOME="$HOME_DIR"    # "on -u qnxuser" changes the user but leaves HOME as root's
cd "$HOME_DIR/WhackAFly" || exit 1
mkdir -p backend/.run
sleep 5    # let Wi-Fi and the clock settle

if [ -f "$HOME_DIR/tmp/pi_mon.py" ]; then
    nohup python3 "$HOME_DIR/tmp/pi_mon.py" > "$HOME_DIR/tmp/mon.err" 2>&1 < /dev/null &
    echo $! > "$HOME_DIR/tmp/mon.pid"
fi

nohup sh backend/start.sh > backend/.run/boot.log 2>&1 < /dev/null &
