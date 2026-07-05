"""테스트용 가짜 CRM 서버. 스레드에서 http.server로 돈다."""
import json
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer

import pytest


class MockCRM:
    """routes[("POST", "/api/v1/auth/login")] = (200, {...})       # 고정 응답
    routes[...] = lambda method, path, headers, body: (200, {...})  # 동적 응답
    수신 요청은 requests에 (method, full_path, headers, body)로 기록된다.
    라우트 매칭은 쿼리스트링을 뗀 경로 기준. port=0이면 임의 포트."""

    def __init__(self, port: int = 0):
        self.routes = {}
        self.requests = []
        mock = self

        class Handler(BaseHTTPRequestHandler):
            def _serve(self):
                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length) if length else b""
                body = json.loads(raw) if raw else None
                mock.requests.append((self.command, self.path, dict(self.headers), body))
                route = mock.routes.get((self.command, self.path.split("?")[0]))
                if route is None:
                    status, payload = 404, {"error": {"code": "NOT_FOUND", "message": "no route"}}
                elif callable(route):
                    status, payload = route(self.command, self.path, dict(self.headers), body)
                else:
                    status, payload = route
                data = json.dumps(payload).encode() if payload is not None else b""
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                if data:
                    self.wfile.write(data)

            do_GET = _serve
            do_POST = _serve
            do_PATCH = _serve

            def log_message(self, *args):
                pass

        self._server = HTTPServer(("127.0.0.1", port), Handler)
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)

    @property
    def url(self) -> str:
        return f"http://127.0.0.1:{self._server.server_port}"

    def start(self):
        self._thread.start()

    def stop(self):
        self._server.shutdown()
        self._server.server_close()


@pytest.fixture
def crm():
    server = MockCRM()
    server.start()
    yield server
    server.stop()
