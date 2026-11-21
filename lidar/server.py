"""Phone upload server. Run it, scan the QR code, upload a Polycam Floorplan export.

Each scan is written to data/scans/<id>/ and data/latest.json is updated,
which Unity watches.
"""
import argparse
import asyncio
import hmac
import json
import os
import secrets
import socket
import sys
import traceback

import qrcode
from aiohttp import web

from ingest import ALLOWED, SCANS, UPLOADS, new_scan_id, publish
from process_scan import MAX_GLB_BYTES, ScanError, process_scan

PAGE = """<!doctype html><html><head><meta charset=utf-8>
<meta name=viewport content="width=device-width,initial-scale=1"><title>FlyAI Scan Upload</title>
<style>body{font:18px system-ui;margin:0;padding:24px;background:#14171c;color:#eef}
button,input{display:block;width:100%;box-sizing:border-box;font-size:18px;margin:14px 0;padding:14px}
button{background:#7fe08a;border:0;border-radius:10px;font-weight:700}pre{white-space:pre-wrap}</style></head>
<body><h2>Upload room scan</h2><p>Choose the Polycam Floorplan GLB or zip.</p>
<form id=form><input id=file type=file name=file required><button id=send>Upload</button></form><pre id=out></pre>
<script>form.onsubmit=async e=>{e.preventDefault();send.disabled=true;out.textContent='Uploading and processing...';
try{const r=await fetch('/upload'+location.search,{method:'POST',body:new FormData(form)});const j=await r.json();
out.textContent=r.ok?'Done. Unity will load it.\\n\\n'+JSON.stringify(j.summary,null,1):'Failed: '+j.error}
catch(x){out.textContent='Failed: '+x}send.disabled=false}</script></body></html>"""


def lan_ip():
    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        probe.connect(("10.255.255.255", 1))
        return probe.getsockname()[0]
    except OSError:
        return "127.0.0.1"
    finally:
        probe.close()


def authorised(request):
    return hmac.compare_digest(request.query.get("token", ""), request.app["token"])


async def index(request):
    if not authorised(request):
        raise web.HTTPForbidden(text="Scan the QR code shown in the terminal.")
    return web.Response(text=PAGE, content_type="text/html")


def failure(message, status):
    return web.json_response({"error": message}, status=status)


async def upload(request):
    if not authorised(request):
        return failure("Invalid upload link. Scan the current QR code.", 403)
    try:
        reader = await request.multipart()
        part = await reader.next()
        while part is not None and part.name != "file":
            part = await reader.next()
    except (ValueError, AssertionError):
        return failure("Expected a file upload.", 400)
    if part is None or not part.filename:
        return failure("No file was sent.", 400)
    extension = os.path.splitext(part.filename)[1].lower()
    if extension not in ALLOWED:
        return failure("Upload a .glb or .zip export.", 400)

    scan_id = new_scan_id()
    os.makedirs(UPLOADS, exist_ok=True)
    source = os.path.join(UPLOADS, scan_id + extension)
    size = 0
    try:
        with open(source, "wb") as handle:
            while chunk := await part.read_chunk(1 << 20):
                size += len(chunk)
                if size > MAX_GLB_BYTES:
                    raise ScanError("The file is too large.")
                handle.write(chunk)
        out_dir = os.path.join(SCANS, scan_id)
        summary = await asyncio.to_thread(process_scan, source, out_dir, request.app["ai"])
    except ScanError as error:
        if os.path.exists(source):
            os.remove(source)
        print(f"[upload] rejected: {error}")
        return failure(str(error), 422)
    except Exception:
        traceback.print_exc()
        return failure("Processing failed. Check the laptop terminal.", 500)

    publish(scan_id)
    print(f"[upload] {scan_id} ({size / 1e6:.2f} MB): {json.dumps(summary)}")
    return web.json_response({"id": scan_id, "summary": summary})


def create_app(token, ai=False):
    app = web.Application(client_max_size=MAX_GLB_BYTES + (1 << 20))
    app["token"] = token
    app["ai"] = ai
    app.add_routes([web.get("/", index), web.post("/upload", upload)])
    return app


def main():
    sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--token", help="Fixed upload token, for testing")
    parser.add_argument("--ai", action="store_true", help="Run the optional Claude cleanup pass on uploads")
    arguments = parser.parse_args()
    token = arguments.token or secrets.token_urlsafe(9)
    url = f"http://{lan_ip()}:{arguments.port}/?token={token}"
    code = qrcode.QRCode(border=1)
    code.add_data(url)
    code.make()
    print("\nScan with the iPhone camera (same Wi-Fi or hotspot):\n")
    code.print_ascii(invert=True)
    print(f"\n{url}\n\nWaiting for uploads. Press Ctrl+C to stop.")
    web.run_app(create_app(token, arguments.ai), host="0.0.0.0", port=arguments.port, print=None)


if __name__ == "__main__":
    main()
