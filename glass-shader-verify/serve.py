"""Static server for glass-shader-verify, plus a POST endpoint so the page can
save its own canvas as a PNG. Needed because this environment's browser has no
visible viewport, so the usual screenshot path is unavailable."""
import base64
import http.server
import os
import re
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8788


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=ROOT, **kw)

    def do_POST(self):
        if not self.path.startswith("/save/"):
            self.send_error(404)
            return
        # Accept either raw image bytes or a data: URL payload.
        raw = self.rfile.read(int(self.headers.get("content-length", 0)))
        if raw[:7] == b"data:im":
            raw = base64.b64decode(raw.split(b",", 1)[1])
        name = re.sub(r"[^A-Za-z0-9._-]", "_", self.path[len("/save/"):]) or "shot.png"
        path = os.path.join(ROOT, name)
        with open(path, "wb") as fh:
            fh.write(raw)
        self.send_response(200)
        self.send_header("content-type", "text/plain")
        self.end_headers()
        self.wfile.write(f"saved {name} {len(raw)} bytes".encode())

    def log_message(self, *a):
        pass


http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
