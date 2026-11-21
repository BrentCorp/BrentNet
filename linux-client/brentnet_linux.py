#!/usr/bin/env python3
"""BrentNET Linux Client, stage-1 preview.

Matches the v0.2.11 redirect-route schema. Loopback only by design.
Does not install a VPN, change DNS/hosts, or expose Jellyfin to the internet.
"""
from __future__ import annotations

import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import socket
import sys
import tempfile
from threading import Event, Lock, Thread
from typing import Any
from urllib.error import URLError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen

VERSION = "0.2.11-linux-preview.1"
MANIFEST_URL = "https://brentnet.net/routes.json"
LOCAL_ROUTES = Path(__file__).resolve().with_name("routes.json")
CACHE_ROUTES = Path.home() / ".local" / "share" / "brentnet-client" / "routes-cache.json"
MAX_MANIFEST_BYTES = 64 * 1024
HOST_RE = re.compile(r"(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+brent\Z", re.ASCII)
MAX_ROUTES = 1000
SYNC_INTERVAL_SECONDS = 6 * 60 * 60


class RouteError(ValueError):
    """Invalid BrentNET route manifest."""


def parse_routes(value: bytes | str | dict[str, Any]) -> dict[str, str]:
    if isinstance(value, bytes):
        if len(value) > MAX_MANIFEST_BYTES:
            raise RouteError("Manifest exceeds size limit")
        value = value.decode("utf-8")
    if isinstance(value, str):
        if len(value.encode("utf-8")) > MAX_MANIFEST_BYTES:
            raise RouteError("Manifest exceeds size limit")
        value = json.loads(value)
    if not isinstance(value, dict):
        raise RouteError("Manifest root must be a JSON object")
    entries = value.get("routes", value)
    if not isinstance(entries, dict) or not entries:
        raise RouteError("No routes found")
    if len(entries) > MAX_ROUTES:
        raise RouteError("Too many routes")
    parsed: dict[str, str] = {}
    for raw_host, spec in entries.items():
        if not isinstance(raw_host, str):
            raise RouteError("Route host is not a string")
        host = raw_host.strip().lower()
        if not HOST_RE.fullmatch(host):
            raise RouteError(f"Invalid .brent hostname: {raw_host!r}")
        if isinstance(spec, dict):
            if spec.get("type") != "redirect":
                raise RouteError(f"Unsupported route type for {host}")
            target = spec.get("target")
        else:
            target = spec  # Read-only compatibility with legacy manifest.
        if not isinstance(target, str) or any(ord(c) < 32 for c in target):
            raise RouteError(f"Invalid destination for {host}")
        u = urlsplit(target)
        # The current published manifest uses HTTPS. Restrict to HTTP(S)
        # destinations and reject credentials to reduce manifest abuse.
        if u.scheme not in ("https", "http") or not u.hostname or u.username or u.password:
            raise RouteError(f"Unsafe destination for {host}")
        if len(target) > 2048:
            raise RouteError(f"Destination too long for {host}")
        parsed[host] = target
    return parsed


def read_routes(path: Path) -> dict[str, str]:
    data = path.read_bytes()
    return parse_routes(data)


def load_best_routes(local: Path = LOCAL_ROUTES, cache: Path = CACHE_ROUTES) -> tuple[dict[str, str], str]:
    if cache.is_file():
        try:
            return read_routes(cache), "last-known-good remote cache"
        except (OSError, ValueError, UnicodeError, json.JSONDecodeError):
            pass
    return read_routes(local), "bundled routes.json"


def sync_routes(url: str = MANIFEST_URL, cache: Path = CACHE_ROUTES) -> dict[str, str]:
    if url != MANIFEST_URL:
        raise RouteError("Remote source must be the configured BrentNET HTTPS manifest")
    req = Request(url, headers={"User-Agent": f"BrentNET-Linux/{VERSION}", "Accept": "application/json"})
    with urlopen(req, timeout=10) as response:
        if response.geturl() != MANIFEST_URL:
            raise RouteError("Remote manifest redirected to an unexpected URL")
        raw = response.read(MAX_MANIFEST_BYTES + 1)
    routes = parse_routes(raw)
    cache.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    # Atomic cache update; leave last-good cache untouched if validation fails.
    data = json.dumps({"version": 1, "routes": {
        host: {"type": "redirect", "target": target} for host, target in sorted(routes.items())
    }}, indent=2).encode("utf-8") + b"\n"
    fd, filename = tempfile.mkstemp(prefix=".routes-", suffix=".json", dir=cache.parent)
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        os.chmod(filename, 0o600)
        os.replace(filename, cache)
    finally:
        if os.path.exists(filename):
            os.unlink(filename)
    return routes


class RouteTable:
    def __init__(self, routes: dict[str, str]):
        self._routes = dict(routes)
        self._lock = Lock()

    def get(self, host: str) -> str | None:
        with self._lock:
            return self._routes.get(host)

    def replace(self, routes: dict[str, str]) -> None:
        with self._lock:
            self._routes = dict(routes)

    def count(self) -> int:
        with self._lock:
            return len(self._routes)


def handler_for(table: RouteTable) -> type[BaseHTTPRequestHandler]:
    class Handler(BaseHTTPRequestHandler):
        server_version = "BrentNET-Linux-Preview"
        sys_version = ""

        def do_GET(self) -> None:
            self._respond()

        def do_HEAD(self) -> None:
            self._respond()

        def _respond(self) -> None:
            host_header = self.headers.get("Host", "").strip().lower()
            host = host_header.split(":", 1)[0]
            if host in ("127.0.0.1", "localhost") and self.path == "/_brentnet/health":
                body = b"BrentNET Linux preview OK\n"
                self.send_response(200)
                self.send_header("Content-Type", "text/plain; charset=utf-8")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                if self.command != "HEAD":
                    self.wfile.write(body)
                return
            if not HOST_RE.fullmatch(host):
                self.send_error(400, "Invalid BrentNET hostname")
                return
            destination = table.get(host)
            if destination is None:
                self.send_error(404, "Unknown BrentNET route")
                return
            self.send_response(302)
            self.send_header("Location", destination)
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", "0")
            self.end_headers()

        def log_message(self, fmt: str, *args: object) -> None:
            # Prevent untrusted Host header text from entering system journal.
            sys.stderr.write("BrentNET request: " + fmt % args + "\n")

    return Handler


def run_preview(port: int, no_sync: bool = False) -> None:
    routes, source = load_best_routes()
    if not no_sync:
        try:
            routes = sync_routes()
            source = "live BrentNET.net/routes.json"
        except (OSError, URLError, RouteError, ValueError, json.JSONDecodeError) as e:
            print(f"Remote route sync unavailable ({type(e).__name__}); using {source}.", file=sys.stderr)
    table = RouteTable(routes)
    stop = Event()

    def refresher() -> None:
        while not stop.wait(SYNC_INTERVAL_SECONDS):
            try:
                table.replace(sync_routes())
                print(f"BrentNET route refresh OK: {table.count()} routes", file=sys.stderr)
            except (OSError, URLError, RouteError, ValueError, json.JSONDecodeError) as e:
                print(f"BrentNET route refresh failed: {type(e).__name__}", file=sys.stderr)

    # Never bind to 0.0.0.0; this preview is accessible only on Ari-Core itself.
    with ThreadingHTTPServer(("127.0.0.1", port), handler_for(table)) as httpd:
        httpd.daemon_threads = True
        if not no_sync:
            Thread(target=refresher, name="route-refresh", daemon=True).start()
        print(f"BrentNET Linux {VERSION}, {table.count()} routes ({source})", flush=True)
        print(f"Loopback preview listening on 127.0.0.1:{httpd.server_port}", flush=True)
        try:
            httpd.serve_forever(poll_interval=0.2)
        finally:
            stop.set()


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="BrentNET Linux loopback-only redirect preview")
    p.add_argument("--version", action="version", version=VERSION)
    cmds = p.add_subparsers(dest="command", required=True)
    cmds.add_parser("status", help="Show the currently cached routes")
    cmds.add_parser("sync", help="Fetch and validate the published BrentNET route manifest")
    serve = cmds.add_parser("serve", help="Run a local-only HTTP preview (no hosts-file changes)")
    serve.add_argument("--port", type=int, default=8765)
    serve.add_argument("--no-sync", action="store_true", help="Use cached/local manifest only")
    args = p.parse_args(argv)
    try:
        if args.command == "status":
            routes, source = load_best_routes()
            print(f"BrentNET Linux {VERSION}\nRoute source: {source}\nRoutes: {len(routes)}")
            for host, target in sorted(routes.items()):
                print(f"  {host} -> {target}")
        elif args.command == "sync":
            routes = sync_routes()
            print(f"Synced {len(routes)} routes from {MANIFEST_URL}")
        elif args.command == "serve":
            if not 1024 <= args.port <= 65535:
                p.error("Preview port must be from 1024 through 65535")
            run_preview(args.port, args.no_sync)
    except (OSError, ValueError, UnicodeError, json.JSONDecodeError, URLError) as e:
        print(f"BrentNET error: {e}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("\nBrentNET preview stopped")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
