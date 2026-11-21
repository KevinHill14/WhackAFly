"""Minimal WebSocket (RFC 6455) over asyncio, standard library only.

Enough for JSON text messages between the brain, hub and game: handshake
(server and client side), text/binary frames, fragmentation, ping/pong, close.
Written so the brain needs nothing but Python + NumPy on the QNX Pi.
"""
import asyncio
import base64
import hashlib
import os
import struct
from urllib.parse import urlparse

GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
OP_CONT, OP_TEXT, OP_BIN, OP_CLOSE, OP_PING, OP_PONG = 0x0, 0x1, 0x2, 0x8, 0x9, 0xA
MAX_MESSAGE = 1 << 20


class ConnectionClosed(Exception):
    pass


def _accept_key(key):
    return base64.b64encode(hashlib.sha1((key + GUID).encode()).digest()).decode()


class WebSocket:
    def __init__(self, reader, writer, is_client):
        self.reader, self.writer = reader, writer
        self.is_client = is_client          # clients must mask frames
        self.closed = False
        self._send_lock = asyncio.Lock()
        peer = writer.get_extra_info("peername")
        self.peer = f"{peer[0]}:{peer[1]}" if peer else "?"

    async def _send_frame(self, opcode, payload):
        if self.closed and opcode != OP_CLOSE:
            raise ConnectionClosed()
        header = bytearray([0x80 | opcode])
        n = len(payload)
        mask_bit = 0x80 if self.is_client else 0
        if n < 126:
            header.append(mask_bit | n)
        elif n < 65536:
            header.append(mask_bit | 126)
            header += struct.pack("!H", n)
        else:
            header.append(mask_bit | 127)
            header += struct.pack("!Q", n)
        if self.is_client:
            mask = os.urandom(4)
            header += mask
            payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
        async with self._send_lock:
            self.writer.write(bytes(header) + payload)
            await self.writer.drain()

    async def send(self, text):
        try:
            await self._send_frame(OP_TEXT, text.encode())
        except (ConnectionError, OSError) as e:
            self.closed = True
            raise ConnectionClosed() from e

    async def _read_frame(self):
        try:
            b1, b2 = await self.reader.readexactly(2)
            n = b2 & 0x7F
            if n == 126:
                (n,) = struct.unpack("!H", await self.reader.readexactly(2))
            elif n == 127:
                (n,) = struct.unpack("!Q", await self.reader.readexactly(8))
            if n > MAX_MESSAGE:
                raise ConnectionClosed("frame too large")
            mask = await self.reader.readexactly(4) if b2 & 0x80 else None
            data = await self.reader.readexactly(n)
        except (asyncio.IncompleteReadError, ConnectionError, OSError) as e:
            self.closed = True
            raise ConnectionClosed() from e
        if mask:
            data = bytes(b ^ mask[i % 4] for i, b in enumerate(data))
        return bool(b1 & 0x80), b1 & 0x0F, data

    async def recv(self):
        """Return the next text message (str). Raises ConnectionClosed."""
        parts, opcode = [], None
        while True:
            fin, op, data = await self._read_frame()
            if op == OP_PING:
                await self._send_frame(OP_PONG, data)
                continue
            if op == OP_PONG:
                continue
            if op == OP_CLOSE:
                if not self.closed:
                    self.closed = True
                    try:
                        await self._send_frame(OP_CLOSE, data[:2])
                    except (ConnectionError, OSError):
                        pass
                raise ConnectionClosed()
            if op in (OP_TEXT, OP_BIN):
                opcode, parts = op, [data]
            elif op == OP_CONT:
                parts.append(data)
            if fin and opcode is not None:
                msg = b"".join(parts)
                return msg.decode("utf-8", "replace")

    async def close(self):
        if not self.closed:
            self.closed = True
            try:
                await self._send_frame(OP_CLOSE, struct.pack("!H", 1000))
            except (ConnectionError, OSError):
                pass
        try:
            self.writer.close()
            await self.writer.wait_closed()
        except (ConnectionError, OSError):
            pass


async def _read_headers(reader):
    raw = await reader.readuntil(b"\r\n\r\n")
    lines = raw.decode("latin-1").split("\r\n")
    headers = {}
    for line in lines[1:]:
        if ":" in line:
            k, v = line.split(":", 1)
            headers[k.strip().lower()] = v.strip()
    return lines[0], headers


async def serve(handler, host, port):
    """Start a WebSocket server; handler(ws, path) is awaited per connection."""
    async def on_connect(reader, writer):
        try:
            request, h = await asyncio.wait_for(_read_headers(reader), 5)
            key = h.get("sec-websocket-key")
            if "websocket" not in h.get("upgrade", "").lower() or not key:
                writer.write(b"HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n")
                await writer.drain()
                writer.close()
                return
            writer.write((
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n"
                "Connection: Upgrade\r\n"
                f"Sec-WebSocket-Accept: {_accept_key(key)}\r\n\r\n").encode())
            await writer.drain()
        except (asyncio.TimeoutError, asyncio.IncompleteReadError,
                asyncio.LimitOverrunError, ConnectionError, OSError):
            writer.close()
            return
        ws = WebSocket(reader, writer, is_client=False)
        path = request.split(" ")[1] if " " in request else "/"
        try:
            await handler(ws, path)
        finally:
            await ws.close()

    return await asyncio.start_server(on_connect, host, port)


async def connect(url, timeout=5):
    """Open a client WebSocket to ws://host:port/path."""
    u = urlparse(url)
    if u.scheme != "ws":
        raise ValueError("only ws:// is supported")
    reader, writer = await asyncio.wait_for(
        asyncio.open_connection(u.hostname, u.port or 80), timeout)
    key = base64.b64encode(os.urandom(16)).decode()
    path = (u.path or "/") + (f"?{u.query}" if u.query else "")
    writer.write((
        f"GET {path} HTTP/1.1\r\nHost: {u.hostname}:{u.port or 80}\r\n"
        "Upgrade: websocket\r\nConnection: Upgrade\r\n"
        f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n").encode())
    await writer.drain()
    status, h = await asyncio.wait_for(_read_headers(reader), timeout)
    if " 101 " not in status + " " or h.get("sec-websocket-accept") != _accept_key(key):
        writer.close()
        raise ConnectionError(f"handshake failed: {status}")
    return WebSocket(reader, writer, is_client=True)
