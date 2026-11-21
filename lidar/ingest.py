"""Process a scan file already on this computer and make it the current room.

Usage: python lidar/ingest.py <scan.zip|scan.glb>
"""
import argparse
import datetime
import json
import os
import secrets
import shutil

from process_scan import ScanError, process_scan, replace_file

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(ROOT, "data")
UPLOADS = os.path.join(DATA, "uploads")
SCANS = os.path.join(DATA, "scans")
ALLOWED = {".glb", ".zip"}


def new_scan_id():
    return datetime.datetime.now().strftime("%Y%m%d-%H%M%S-") + secrets.token_hex(2)


def publish(scan_id):
    latest = {
        "id": scan_id,
        "glb": f"scans/{scan_id}/scan.glb",
        "room": f"scans/{scan_id}/room.json",
        "updatedAt": datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
    }
    replace_file(os.path.join(DATA, "latest.json"), json.dumps(latest, indent=1).encode("utf-8"))


def discard(upload_path, scan_dir):
    """Removes what a failed scan left behind."""
    if os.path.exists(upload_path):
        os.remove(upload_path)
    shutil.rmtree(scan_dir, ignore_errors=True)


def ingest_file(path, ai=False):
    extension = os.path.splitext(path)[1].lower()
    if extension not in ALLOWED:
        raise ScanError("Use a .glb or .zip export.")
    scan_id = new_scan_id()
    scan_dir = os.path.join(SCANS, scan_id)
    copy_path = os.path.join(UPLOADS, scan_id + extension)
    try:
        summary = process_scan(path, scan_dir, ai=ai)
        os.makedirs(UPLOADS, exist_ok=True)
        shutil.copyfile(path, copy_path)
        publish(scan_id)
    except Exception:
        discard(copy_path, scan_dir)
        raise
    return scan_id, summary


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("file")
    parser.add_argument("--ai", action="store_true", help="Run the optional Claude cleanup pass")
    arguments = parser.parse_args()
    try:
        scan, result = ingest_file(arguments.file, ai=arguments.ai)
    except (ScanError, OSError) as error:
        raise SystemExit(f"Scan rejected: {error}")
    print(json.dumps({"id": scan, "summary": result}, indent=1))
