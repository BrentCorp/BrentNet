import http.client
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import threading
import unittest
from http.server import ThreadingHTTPServer

from brentnet_linux import RouteError, RouteTable, handler_for, load_best_routes, parse_routes


class BrentNetLinuxTests(unittest.TestCase):
    GOOD = {"version": 1, "routes": {"net.brent": {"type": "redirect", "target": "https://brentnet.net"}}}

    def test_parses_current_manifest(self):
        self.assertEqual(parse_routes(self.GOOD), {"net.brent": "https://brentnet.net"})

    def test_legacy_routes(self):
        self.assertEqual(parse_routes({"net.brent": "https://brentnet.net"}), {"net.brent": "https://brentnet.net"})

    def test_rejects_bad_hostname(self):
        for host in ("bad_domain", "net.brent.evil", "a..brent", "localhost", "evil\n.brent"):
            with self.subTest(host=host), self.assertRaises(RouteError):
                parse_routes({"routes": {host: {"type": "redirect", "target": "https://example.org"}}})

    def test_rejects_unsafe_url(self):
        for target in ("javascript:alert(1)", "file:///etc/passwd", "https://foo:bar@example.org", "https://example.org\r\nX: bad"):
            with self.subTest(target=target), self.assertRaises(RouteError):
                parse_routes({"routes": {"net.brent": {"type": "redirect", "target": target}}})

    def test_rejects_unsupported_type(self):
        with self.assertRaises(RouteError):
            parse_routes({"routes": {"net.brent": {"type": "p2p", "target": "https://example.org"}}})

    def test_cache_fallback(self):
        with TemporaryDirectory() as d:
            local = Path(d) / "local.json"
            cache = Path(d) / "cache.json"
            local.write_text(json.dumps(self.GOOD))
            cache.write_text("{invalid")
            routes, source = load_best_routes(local, cache)
            self.assertIn("bundled", source)
            self.assertEqual(routes["net.brent"], "https://brentnet.net")

    def test_http_redirect_health_and_bad_hosts(self):
        table = RouteTable(parse_routes(self.GOOD))
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(table))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            cases = [
                ("net.brent", "/", 302, "https://brentnet.net"),
                ("missing.brent", "/", 404, None),
                ("localhost", "/_brentnet/health", 200, None),
                ("net.brent.evil", "/", 400, None),
            ]
            for host, path, status, redirect in cases:
                with self.subTest(host=host):
                    conn = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=3)
                    conn.request("GET", path, headers={"Host": host})
                    response = conn.getresponse()
                    self.assertEqual(response.status, status)
                    if redirect:
                        self.assertEqual(response.getheader("Location"), redirect)
                    response.read()
                    conn.close()
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=3)

    def test_bundle_routes(self):
        routes = parse_routes((Path(__file__).parent / "routes.json").read_bytes())
        self.assertIn("net.brent", routes)
        self.assertIn("corp.brent", routes)
        self.assertIn("home.brent", routes)


if __name__ == "__main__":
    unittest.main(verbosity=2)
