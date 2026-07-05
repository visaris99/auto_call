# TM 다이얼러 클라이언트 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** milestone-crm의 `/api/v1` API를 사용하는 파이썬 TM 다이얼러(로그인 → 배정 리드 큐 → ADB 발신 → 결과 기록)를 만들고 PyInstaller 단일 exe로 배포 가능하게 한다.

**Architecture:** 설계서(`docs/superpowers/specs/2026-07-05-tm-dialer-crm-integration-design.md`) 5장의 구조를 따른다. 네트워크/상태/ADB는 GUI와 분리된 모듈(`api.py`, `state.py`, `adb.py`, `logic.py`)로 TDD 구현하고, GUI(`ui/`)는 그 위에 얹어 수동 테스트한다. 서버는 Codex가 별도 구현 중이므로 개발·테스트는 로컬 mock CRM으로 진행한다.

**Tech Stack:** Python 3.12, customtkinter 5.2.2, requests, pytest(dev), PyInstaller(빌드 시). 저장소: `/home/mirage/office/auto_call` (이하 모든 경로는 저장소 루트 기준).

## Global Constraints

- 런타임 의존성은 **customtkinter, requests 둘뿐** (CTkMessagebox·pandas·openpyxl 제거). 개발 의존성은 pytest만 추가.
- API 계약은 설계서 3장을 따른다. 계약을 바꾸고 싶으면 **코드가 아니라 설계서를 먼저 수정**하고 사용자에게 알린다.
- milestone-crm 저장소의 코드는 절대 수정하지 않는다 (서버는 Codex 담당).
- **전화번호 평문은 어떤 경우에도 디스크에 저장하지 않는다** (재전송 큐에도 결과 페이로드만 저장 — 평문 번호 없음).
- UI 문구는 전부 한국어. 상태/결과 라벨과 색은 CRM과 동일 매핑(계획 Task 7의 theme.py 값이 기준).
- 테스트 실행: `my_env/bin/python -m pytest tests/ -v` (WSL). GUI 수동 확인은 WSLg(`python main.py`) 또는 Windows에서.
- 커밋 메시지는 conventional commit(`feat:`, `test:`, `build:`, `docs:`, `chore:`) + 마지막 줄에 `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- Windows 전용 API(`subprocess.STARTUPINFO` 등)는 반드시 `sys.platform == "win32"` 분기 안에서만 사용 (개발·테스트는 리눅스에서 돌기 때문).

## 파일 구조 (최종 상태)

```
auto_call/
  main.py                # 진입점: App(로그인↔워크스페이스 전환, 오류 로그)
  version.py             # VERSION 상수
  api.py                 # ApiClient + 오류 계층 (설계서 3장 계약)
  logic.py               # 순수 헬퍼: ISO 파싱, 큐 정렬, 콜백 도래, 시간 포맷
  state.py               # config_dir, Config, PendingCallQueue
  adb.py                 # adb_path/call/hangup/is_connected
  ui/
    __init__.py
    theme.py             # CRM globals.css에서 이식한 색 토큰 + 상태/결과 매핑
    widgets.py           # Badge, StatusDot, ResultSelector, Toast, run_bg
    login.py             # LoginFrame
    workspace.py         # WorkspaceFrame (메인 화면)
  scripts/
    dev_mock_crm.py      # 수동 테스트용 가짜 CRM 서버 (stateful)
  tests/
    conftest.py          # MockCRM fixture
    test_api_auth.py
    test_api_leads.py
    test_logic.py
    test_state_config.py
    test_state_pending.py
    test_adb.py
    test_theme_widgets.py
  build/
    dialer.spec          # PyInstaller onefile 스펙
    build.bat            # Windows 원클릭 빌드
    adb/                 # (gitignore) adb.exe + DLL 2종을 여기 복사
  docs/manual-test.md    # UI 수동 테스트 체크리스트
  requirements.txt / requirements-dev.txt
```

---

### Task 1: 스캐폴딩 + API 클라이언트 — 인증 (login/logout/me)

**Files:**
- Create: `requirements.txt`, `requirements-dev.txt`, `.gitignore`, `version.py`, `api.py`, `tests/conftest.py`, `tests/test_api_auth.py`

**Interfaces:**
- Produces: `api.ApiClient(base_url, timeout=10)` — `.login(login_id, password, code=None) -> dict(user)`, `.logout()`, `.me() -> dict`, `.is_authenticated: bool`, `.user: dict|None`, `.base_url: str`
- Produces: 오류 계층 `ApiError(code, message, http_status)` / `NetworkError` / `AuthError` / `MfaRequired` / `NightBlocked` — 이후 모든 태스크가 이 타입으로 분기
- Produces: pytest fixture `crm` (`MockCRM`: `.routes[(method, path)] = (status, body) | callable`, `.requests: list`, `.url`)

- [ ] **Step 1: 스캐폴딩 파일 작성**

`requirements.txt`:

```
customtkinter==5.2.2
requests>=2.32
```

`requirements-dev.txt`:

```
-r requirements.txt
pytest>=8
```

`.gitignore`:

```
__pycache__/
*.pyc
.pytest_cache/
my_env/
.venv/
dist/
build/out/
build/adb/
error_log.txt
```

`version.py`:

```python
APP_NAME = "TM 다이얼러"
VERSION = "2.0.0"
```

- [ ] **Step 2: 의존성 설치**

Run: `my_env/bin/python -m pip install -r requirements-dev.txt`
Expected: requests, pytest 설치 성공 (customtkinter는 이미 있음)

- [ ] **Step 3: mock CRM fixture 작성**

`tests/conftest.py`:

```python
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
```

- [ ] **Step 4: 인증 실패 테스트 작성**

`tests/test_api_auth.py`:

```python
import pytest

from api import ApiClient, ApiError, AuthError, MfaRequired, NetworkError

USER = {"id": "u1", "loginId": "hong", "name": "홍길동", "orgName": "강남1팀",
        "roles": ["TM"], "mustChangePassword": False}
LOGIN_OK = (200, {"token": "tok1", "expiresAt": "2026-07-05T18:00:00+09:00", "user": USER})


def test_login_success_sets_token_and_user(crm):
    crm.routes[("POST", "/api/v1/auth/login")] = LOGIN_OK
    c = ApiClient(crm.url)
    user = c.login("hong", "pw")
    assert c.is_authenticated
    assert user["name"] == "홍길동" and c.user["orgName"] == "강남1팀"
    method, path, headers, body = crm.requests[0]
    assert (method, path) == ("POST", "/api/v1/auth/login")
    assert body == {"loginId": "hong", "password": "pw"}  # code 없으면 미포함


def test_login_with_mfa_code_included(crm):
    crm.routes[("POST", "/api/v1/auth/login")] = LOGIN_OK
    ApiClient(crm.url).login("hong", "pw", code="123456")
    assert crm.requests[0][3]["code"] == "123456"


def test_login_mfa_required_raises(crm):
    crm.routes[("POST", "/api/v1/auth/login")] = (
        401, {"error": {"code": "MFA_REQUIRED", "message": "인증 코드가 필요합니다."}})
    with pytest.raises(MfaRequired):
        ApiClient(crm.url).login("hong", "pw")


def test_login_invalid_credentials_is_plain_apierror(crm):
    """INVALID_CREDENTIALS는 재로그인 유도(AuthError)가 아니라 일반 오류여야 한다."""
    crm.routes[("POST", "/api/v1/auth/login")] = (
        401, {"error": {"code": "INVALID_CREDENTIALS", "message": "아이디 또는 비밀번호가 올바르지 않습니다."}})
    with pytest.raises(ApiError) as e:
        ApiClient(crm.url).login("hong", "bad")
    assert not isinstance(e.value, AuthError)
    assert e.value.code == "INVALID_CREDENTIALS"


def test_authed_request_sends_bearer(crm):
    crm.routes[("POST", "/api/v1/auth/login")] = LOGIN_OK
    crm.routes[("GET", "/api/v1/me")] = (200, {"user": USER})
    c = ApiClient(crm.url)
    c.login("hong", "pw")
    assert c.me()["loginId"] == "hong"
    headers = crm.requests[1][2]
    assert headers.get("Authorization") == "Bearer tok1"


def test_expired_token_raises_autherror(crm):
    crm.routes[("POST", "/api/v1/auth/login")] = LOGIN_OK
    crm.routes[("GET", "/api/v1/me")] = (
        401, {"error": {"code": "UNAUTHENTICATED", "message": "세션이 만료되었습니다."}})
    c = ApiClient(crm.url)
    c.login("hong", "pw")
    with pytest.raises(AuthError):
        c.me()


def test_request_without_login_raises_autherror(crm):
    with pytest.raises(AuthError):
        ApiClient(crm.url).me()


def test_logout_clears_token(crm):
    crm.routes[("POST", "/api/v1/auth/login")] = LOGIN_OK
    crm.routes[("POST", "/api/v1/auth/logout")] = (204, None)
    c = ApiClient(crm.url)
    c.login("hong", "pw")
    c.logout()
    assert not c.is_authenticated and c.user is None


def test_connection_refused_raises_networkerror():
    with pytest.raises(NetworkError):
        ApiClient("http://127.0.0.1:1").login("hong", "pw")
```

- [ ] **Step 5: 테스트 실패 확인**

Run: `my_env/bin/python -m pytest tests/test_api_auth.py -v`
Expected: 전부 FAIL (`ModuleNotFoundError: No module named 'api'`)

- [ ] **Step 6: api.py 구현 (인증 부분)**

`api.py`:

```python
"""milestone-crm /api/v1 클라이언트 — 설계서 3장(API 계약) 구현."""
from __future__ import annotations

import requests

DEFAULT_TIMEOUT = 10


class ApiError(Exception):
    """API 오류 공통. code는 설계서 3.1 에러 표의 error.code 값."""

    def __init__(self, code: str, message: str, http_status: int = 0):
        super().__init__(message)
        self.code = code
        self.message = message
        self.http_status = http_status


class NetworkError(ApiError):
    """연결 실패/타임아웃 — 재시도 대상."""

    def __init__(self, message: str = "서버에 연결할 수 없습니다."):
        super().__init__("NETWORK", message, 0)


class AuthError(ApiError):
    """토큰 없음/만료(UNAUTHENTICATED) — 재로그인 필요."""


class MfaRequired(ApiError):
    """MFA 코드 입력 필요."""


class NightBlocked(ApiError):
    """야간(21~08 KST) 발신 차단."""


def _error_class(http_status: int, code: str) -> type[ApiError]:
    if code == "MFA_REQUIRED":
        return MfaRequired
    if code == "NIGHT_BLOCKED":
        return NightBlocked
    if http_status == 401 and code == "UNAUTHENTICATED":
        return AuthError
    return ApiError


class ApiClient:
    def __init__(self, base_url: str, timeout: float = DEFAULT_TIMEOUT):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout
        self._session = requests.Session()
        self._token: str | None = None
        self.user: dict | None = None

    @property
    def is_authenticated(self) -> bool:
        return self._token is not None

    def _request(self, method: str, path: str, json_body: dict | None = None,
                 headers: dict | None = None, auth: bool = True):
        hdrs = dict(headers or {})
        if auth:
            if not self._token:
                raise AuthError("UNAUTHENTICATED", "로그인이 필요합니다.", 401)
            hdrs["Authorization"] = f"Bearer {self._token}"
        try:
            res = self._session.request(method, f"{self.base_url}/api/v1{path}",
                                        json=json_body, headers=hdrs, timeout=self.timeout)
        except requests.RequestException as exc:
            raise NetworkError() from exc
        if res.status_code == 204:
            return None
        try:
            data = res.json()
        except ValueError as exc:
            raise ApiError("INTERNAL", f"서버 응답 오류(HTTP {res.status_code})",
                           res.status_code) from exc
        if res.ok:
            return data
        err = (data or {}).get("error") or {}
        code = err.get("code", "INTERNAL")
        message = err.get("message", "알 수 없는 오류가 발생했습니다.")
        raise _error_class(res.status_code, code)(code, message, res.status_code)

    # ---- 인증 ----

    def login(self, login_id: str, password: str, code: str | None = None) -> dict:
        body = {"loginId": login_id, "password": password}
        if code:
            body["code"] = code
        data = self._request("POST", "/auth/login", body, auth=False)
        self._token = data["token"]
        self.user = data["user"]
        return data["user"]

    def logout(self) -> None:
        if self._token:
            try:
                self._request("POST", "/auth/logout")
            except ApiError:
                pass  # 로그아웃 실패는 무시 — 로컬 토큰만 버리면 됨
        self._token = None
        self.user = None

    def me(self) -> dict:
        return self._request("GET", "/me")["user"]
```

- [ ] **Step 7: 테스트 통과 확인**

Run: `my_env/bin/python -m pytest tests/test_api_auth.py -v`
Expected: 9 passed

- [ ] **Step 8: 커밋**

```bash
git add requirements.txt requirements-dev.txt .gitignore version.py api.py tests/
git commit -m "feat: API 클라이언트 인증(login/logout/me) + mock CRM 테스트 기반

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: API 클라이언트 — 리드·콜 (queue/reveal/log_call/save_memo/check_version)

**Files:**
- Modify: `api.py` (ApiClient에 메서드 추가)
- Create: `tests/test_api_leads.py`

**Interfaces:**
- Consumes: Task 1의 `ApiClient._request`, `crm` fixture
- Produces: `.queue(limit=50) -> list[dict]`, `.reveal(lead_id, reason="TM 발신") -> str`, `.log_call(lead_id, *, result_code, talk_seconds, memo, callback_at, idempotency_key) -> dict`, `.save_memo(lead_id, memo) -> None`, `.check_version() -> dict|None`

- [ ] **Step 1: 테스트 작성**

`tests/test_api_leads.py`:

```python
import pytest

from api import ApiClient, ApiError, NightBlocked

USER = {"id": "u1", "loginId": "hong", "name": "홍길동", "orgName": "강남1팀",
        "roles": ["TM"], "mustChangePassword": False}
LOGIN_OK = (200, {"token": "tok1", "expiresAt": "2026-07-05T18:00:00+09:00", "user": USER})
LEAD = {"id": "L1", "name": "김철수", "phoneMasked": "010-****-1234",
        "status": "INTERESTED", "nextCallAt": None, "memo": "5시 이후 선호",
        "updatedAt": "2026-07-04T10:00:00+09:00"}


@pytest.fixture
def client(crm):
    crm.routes[("POST", "/api/v1/auth/login")] = LOGIN_OK
    c = ApiClient(crm.url)
    c.login("hong", "pw")
    return c


def test_queue_returns_items_and_sends_limit(crm, client):
    crm.routes[("GET", "/api/v1/leads/queue")] = (
        200, {"serverTime": "2026-07-05T10:00:00+09:00", "items": [LEAD]})
    items = client.queue(limit=20)
    assert items == [LEAD]
    method, path, headers, body = crm.requests[-1]
    assert path == "/api/v1/leads/queue?limit=20"
    assert headers.get("Authorization") == "Bearer tok1"


def test_reveal_sends_reason_and_returns_phone(crm, client):
    crm.routes[("POST", "/api/v1/leads/L1/reveal")] = (200, {"phone": "01012341234"})
    assert client.reveal("L1") == "01012341234"
    assert crm.requests[-1][3] == {"reason": "TM 발신"}


def test_log_call_sends_idempotency_key_and_camelcase_body(crm, client):
    crm.routes[("POST", "/api/v1/leads/L1/call")] = (
        200, {"ok": True, "lead": {"id": "L1", "status": "CALLBACK",
                                   "nextCallAt": "2026-07-06T14:30:00+09:00"}})
    res = client.log_call("L1", result_code="CALLBACK", talk_seconds=154,
                          memo="재상담 원함", callback_at="2026-07-06T14:30:00+09:00",
                          idempotency_key="key-1")
    assert res["lead"]["status"] == "CALLBACK"
    method, path, headers, body = crm.requests[-1]
    assert headers.get("Idempotency-Key") == "key-1"
    assert body == {"resultCode": "CALLBACK", "talkSeconds": 154,
                    "memo": "재상담 원함", "callbackAt": "2026-07-06T14:30:00+09:00"}


def test_log_call_night_blocked(crm, client):
    crm.routes[("POST", "/api/v1/leads/L1/call")] = (
        423, {"error": {"code": "NIGHT_BLOCKED", "message": "야간에는 발신할 수 없습니다."}})
    with pytest.raises(NightBlocked):
        client.log_call("L1", result_code="NOANSWER", talk_seconds=0, memo=None,
                        callback_at=None, idempotency_key="key-2")


def test_save_memo_patches(crm, client):
    crm.routes[("PATCH", "/api/v1/leads/L1/memo")] = (200, {"ok": True})
    client.save_memo("L1", "메모 수정")
    method, path, headers, body = crm.requests[-1]
    assert method == "PATCH" and body == {"memo": "메모 수정"}


def test_check_version_returns_none_when_missing(crm, client):
    assert client.check_version() is None  # 라우트 없음 → 404 → None


def test_check_version_returns_payload(crm, client):
    crm.routes[("GET", "/api/v1/version")] = (
        200, {"minVersion": "2.0.0", "latestVersion": "2.1.0", "downloadUrl": None})
    assert client.check_version()["latestVersion"] == "2.1.0"
```

- [ ] **Step 2: 실패 확인**

Run: `my_env/bin/python -m pytest tests/test_api_leads.py -v`
Expected: FAIL (`AttributeError: 'ApiClient' object has no attribute 'queue'` 등)

- [ ] **Step 3: api.py에 메서드 추가**

`api.py`의 `ApiClient` 클래스 끝에 추가:

```python
    # ---- 리드/콜 ----

    def queue(self, limit: int = 50) -> list[dict]:
        return self._request("GET", f"/leads/queue?limit={limit}")["items"]

    def reveal(self, lead_id: str, reason: str = "TM 발신") -> str:
        """발신 직전 1건 복호화. 평문은 반환값으로만 다루고 저장하지 않는다."""
        return self._request("POST", f"/leads/{lead_id}/reveal", {"reason": reason})["phone"]

    def log_call(self, lead_id: str, *, result_code: str, talk_seconds: int,
                 memo: str | None, callback_at: str | None, idempotency_key: str) -> dict:
        body = {"resultCode": result_code, "talkSeconds": talk_seconds,
                "memo": memo, "callbackAt": callback_at}
        return self._request("POST", f"/leads/{lead_id}/call", body,
                             headers={"Idempotency-Key": idempotency_key})

    def save_memo(self, lead_id: str, memo: str) -> None:
        self._request("PATCH", f"/leads/{lead_id}/memo", {"memo": memo})

    def check_version(self) -> dict | None:
        """서버 미구현(404 포함) 등 어떤 오류든 None — 버전 안내는 선택 기능."""
        try:
            return self._request("GET", "/version", auth=False)
        except ApiError:
            return None
```

- [ ] **Step 4: 통과 확인**

Run: `my_env/bin/python -m pytest tests/ -v`
Expected: 16 passed

- [ ] **Step 5: 커밋**

```bash
git add api.py tests/test_api_leads.py
git commit -m "feat: API 클라이언트 리드 큐·reveal·콜 기록(멱등키)·메모·버전 체크

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: logic.py — 순수 헬퍼

**Files:**
- Create: `logic.py`, `tests/test_logic.py`

**Interfaces:**
- Produces: `parse_iso(value: str|None) -> datetime|None`(aware), `is_callback_due(item: dict, now: datetime) -> bool`, `sort_queue(items: list[dict], now: datetime) -> list[dict]`, `format_seconds(total: int) -> str`, `callback_iso(hhmm: str, now: datetime) -> str|None`
- Consumes: 큐 아이템 dict 형태(Task 2의 `LEAD` 참고 — `nextCallAt` ISO 문자열 또는 None)

- [ ] **Step 1: 테스트 작성**

`tests/test_logic.py`:

```python
from datetime import datetime, timezone, timedelta

from logic import parse_iso, is_callback_due, sort_queue, format_seconds, callback_iso

KST = timezone(timedelta(hours=9))
NOW = datetime(2026, 7, 5, 10, 0, tzinfo=KST)


def lead(id_, next_call_at=None):
    return {"id": id_, "nextCallAt": next_call_at}


def test_parse_iso():
    assert parse_iso(None) is None
    assert parse_iso("") is None
    dt = parse_iso("2026-07-05T14:30:00+09:00")
    assert dt.tzinfo is not None and dt.hour == 14


def test_is_callback_due():
    assert is_callback_due(lead("a", "2026-07-05T09:59:00+09:00"), NOW) is True
    assert is_callback_due(lead("b", "2026-07-05T10:01:00+09:00"), NOW) is False
    assert is_callback_due(lead("c", None), NOW) is False


def test_sort_queue_due_callbacks_first_oldest_first():
    items = [lead("a"), lead("b", "2026-07-05T09:30:00+09:00"),
             lead("c", "2026-07-05T14:00:00+09:00"), lead("d", "2026-07-05T09:00:00+09:00")]
    assert [x["id"] for x in sort_queue(items, NOW)] == ["d", "b", "a", "c"]


def test_sort_queue_keeps_server_order_for_rest():
    items = [lead("a"), lead("b"), lead("c")]
    assert [x["id"] for x in sort_queue(items, NOW)] == ["a", "b", "c"]


def test_format_seconds():
    assert format_seconds(0) == "00:00"
    assert format_seconds(75) == "01:15"
    assert format_seconds(3700) == "1:01:40"


def test_callback_iso_today_and_tomorrow():
    assert callback_iso("14:30", NOW) == "2026-07-05T14:30:00+09:00"
    assert callback_iso("09:00", NOW) == "2026-07-06T09:00:00+09:00"  # 지난 시각 → 내일


def test_callback_iso_invalid():
    assert callback_iso("25:00", NOW) is None
    assert callback_iso("abc", NOW) is None
    assert callback_iso("", NOW) is None
```

- [ ] **Step 2: 실패 확인**

Run: `my_env/bin/python -m pytest tests/test_logic.py -v`
Expected: FAIL (`No module named 'logic'`)

- [ ] **Step 3: 구현**

`logic.py`:

```python
"""GUI와 무관한 순수 헬퍼 — 시간 파싱/큐 정렬/포맷."""
from __future__ import annotations

from datetime import datetime, timedelta


def parse_iso(value: str | None) -> datetime | None:
    if not value:
        return None
    try:
        return datetime.fromisoformat(value)
    except ValueError:
        return None


def is_callback_due(item: dict, now: datetime) -> bool:
    dt = parse_iso(item.get("nextCallAt"))
    return dt is not None and dt <= now


def sort_queue(items: list[dict], now: datetime) -> list[dict]:
    """콜백 도래분을 오래된 순으로 맨 위에, 나머지는 서버 순서 유지."""
    due = [x for x in items if is_callback_due(x, now)]
    due.sort(key=lambda x: parse_iso(x["nextCallAt"]))
    rest = [x for x in items if not is_callback_due(x, now)]
    return due + rest


def format_seconds(total: int) -> str:
    h, rem = divmod(max(0, int(total)), 3600)
    m, s = divmod(rem, 60)
    return f"{h}:{m:02d}:{s:02d}" if h else f"{m:02d}:{s:02d}"


def callback_iso(hhmm: str, now: datetime) -> str | None:
    """'14:30' → 오늘(지났으면 내일)의 aware ISO 문자열. 형식 오류는 None."""
    try:
        hour_s, minute_s = hhmm.strip().split(":")
        hour, minute = int(hour_s), int(minute_s)
    except ValueError:
        return None
    if not (0 <= hour <= 23 and 0 <= minute <= 59):
        return None
    target = now.replace(hour=hour, minute=minute, second=0, microsecond=0)
    if target <= now:
        target += timedelta(days=1)
    return target.isoformat()
```

- [ ] **Step 4: 통과 확인**

Run: `my_env/bin/python -m pytest tests/test_logic.py -v`
Expected: 7 passed

- [ ] **Step 5: 커밋**

```bash
git add logic.py tests/test_logic.py
git commit -m "feat: 큐 정렬·콜백 도래·시간 포맷 순수 헬퍼

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: state.py — 설정 저장 (config_dir, Config)

**Files:**
- Create: `state.py`, `tests/test_state_config.py`

**Interfaces:**
- Produces: `config_dir() -> Path` (Windows `%APPDATA%\TMDialer`, 그 외 `$XDG_CONFIG_HOME/TMDialer`), `DEFAULT_SERVER_URL`, `Config(server_url, last_login_id)` — `.load(path=None)`, `.save(path=None)`; `TM_SERVER_URL` 환경변수가 있으면 load 시 server_url을 덮어씀

- [ ] **Step 1: 테스트 작성**

`tests/test_state_config.py`:

```python
from pathlib import Path

import state
from state import Config, config_dir


def test_config_dir_uses_xdg(monkeypatch, tmp_path):
    monkeypatch.setenv("XDG_CONFIG_HOME", str(tmp_path))
    d = config_dir()
    assert d == tmp_path / "TMDialer" and d.is_dir()


def test_config_roundtrip(tmp_path):
    p = tmp_path / "config.json"
    Config(server_url="http://crm:3002", last_login_id="hong").save(p)
    loaded = Config.load(p)
    assert loaded.server_url == "http://crm:3002"
    assert loaded.last_login_id == "hong"


def test_config_load_missing_returns_defaults(tmp_path, monkeypatch):
    monkeypatch.delenv("TM_SERVER_URL", raising=False)
    loaded = Config.load(tmp_path / "none.json")
    assert loaded.server_url == state.DEFAULT_SERVER_URL
    assert loaded.last_login_id == ""


def test_env_overrides_server_url(tmp_path, monkeypatch):
    p = tmp_path / "config.json"
    Config(server_url="http://saved:1", last_login_id="").save(p)
    monkeypatch.setenv("TM_SERVER_URL", "http://env:2")
    assert Config.load(p).server_url == "http://env:2"


def test_config_load_corrupt_file_returns_defaults(tmp_path, monkeypatch):
    monkeypatch.delenv("TM_SERVER_URL", raising=False)
    p = tmp_path / "config.json"
    p.write_text("{broken json", encoding="utf-8")
    assert Config.load(p).server_url == state.DEFAULT_SERVER_URL
```

- [ ] **Step 2: 실패 확인**

Run: `my_env/bin/python -m pytest tests/test_state_config.py -v`
Expected: FAIL (`No module named 'state'`)

- [ ] **Step 3: 구현**

`state.py`:

```python
"""로컬 설정과 재전송 큐 — %APPDATA%\\TMDialer (리눅스 개발: ~/.config/TMDialer)."""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path

DEFAULT_SERVER_URL = "http://localhost:3002"  # 배포 전 실제 CRM 주소로 교체


def config_dir() -> Path:
    if sys.platform == "win32":
        base = Path(os.environ.get("APPDATA", str(Path.home())))
    else:
        base = Path(os.environ.get("XDG_CONFIG_HOME", str(Path.home() / ".config")))
    d = base / "TMDialer"
    d.mkdir(parents=True, exist_ok=True)
    return d


def _atomic_write_json(path: Path, data) -> None:
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    tmp.replace(path)


def _read_json(path: Path, default):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return default


class Config:
    def __init__(self, server_url: str = DEFAULT_SERVER_URL, last_login_id: str = ""):
        self.server_url = server_url
        self.last_login_id = last_login_id

    @classmethod
    def load(cls, path: Path | None = None) -> "Config":
        path = path or (config_dir() / "config.json")
        raw = _read_json(path, {})
        if not isinstance(raw, dict):
            raw = {}
        cfg = cls(server_url=raw.get("server_url", DEFAULT_SERVER_URL),
                  last_login_id=raw.get("last_login_id", ""))
        env_url = os.environ.get("TM_SERVER_URL")
        if env_url:
            cfg.server_url = env_url
        return cfg

    def save(self, path: Path | None = None) -> None:
        path = path or (config_dir() / "config.json")
        _atomic_write_json(path, {"server_url": self.server_url,
                                  "last_login_id": self.last_login_id})
```

- [ ] **Step 4: 통과 확인**

Run: `my_env/bin/python -m pytest tests/test_state_config.py -v`
Expected: 5 passed

- [ ] **Step 5: 커밋**

```bash
git add state.py tests/test_state_config.py
git commit -m "feat: 로컬 설정 저장(Config, config_dir)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: state.py — 재전송 큐 (PendingCallQueue)

**Files:**
- Modify: `state.py` (클래스 추가)
- Create: `tests/test_state_pending.py`

**Interfaces:**
- Consumes: `api`의 오류 타입(NetworkError/AuthError/NightBlocked/ApiError), `ApiClient.log_call` 시그니처 (덕 타이핑 — 테스트는 FakeClient 사용)
- Produces: `PendingCallQueue(path=None)` — `.add(*, idempotency_key, lead_id, payload)`, `.items() -> list[dict]`, `.remove(idempotency_key)`, `.flush(client) -> tuple[int, int]` (성공 수, 잔여 수). payload 키는 `result_code/talk_seconds/memo/callback_at` (log_call 키워드와 동일)

- [ ] **Step 1: 테스트 작성**

`tests/test_state_pending.py`:

```python
import pytest

from api import ApiError, AuthError, NetworkError
from state import PendingCallQueue

PAYLOAD = {"result_code": "NOANSWER", "talk_seconds": 0, "memo": None, "callback_at": None}


class FakeClient:
    """log_call 결과를 순서대로 돌려주는 가짜. 예외 인스턴스면 raise."""

    def __init__(self, results):
        self.results = list(results)
        self.calls = []

    def log_call(self, lead_id, **kwargs):
        self.calls.append((lead_id, kwargs))
        r = self.results.pop(0)
        if isinstance(r, Exception):
            raise r
        return r


def make_queue(tmp_path, n=0):
    q = PendingCallQueue(tmp_path / "pending.json")
    for i in range(n):
        q.add(idempotency_key=f"k{i}", lead_id=f"L{i}", payload=dict(PAYLOAD))
    return q


def test_add_persists_to_disk(tmp_path):
    make_queue(tmp_path, 1)
    reloaded = PendingCallQueue(tmp_path / "pending.json")
    assert len(reloaded.items()) == 1
    assert reloaded.items()[0]["idempotency_key"] == "k0"


def test_flush_success_removes_and_counts(tmp_path):
    q = make_queue(tmp_path, 2)
    client = FakeClient([{"ok": True}, {"ok": True}])
    assert q.flush(client) == (2, 0)
    assert q.items() == []
    lead_id, kwargs = client.calls[0]
    assert lead_id == "L0" and kwargs["idempotency_key"] == "k0"
    assert kwargs["result_code"] == "NOANSWER"


def test_flush_network_error_stops_and_keeps(tmp_path):
    q = make_queue(tmp_path, 3)
    client = FakeClient([{"ok": True}, NetworkError()])
    assert q.flush(client) == (1, 2)
    assert len(client.calls) == 2  # 3번째는 시도 안 함


def test_flush_auth_error_raises(tmp_path):
    q = make_queue(tmp_path, 1)
    with pytest.raises(AuthError):
        q.flush(FakeClient([AuthError("UNAUTHENTICATED", "만료", 401)]))
    assert len(q.items()) == 1  # 재로그인 후 재시도되어야 하므로 유지


def test_flush_validation_error_drops_poison_entry(tmp_path):
    q = make_queue(tmp_path, 2)
    client = FakeClient([ApiError("VALIDATION", "잘못된 요청", 400), {"ok": True}])
    assert q.flush(client) == (1, 0)  # 1건 버리고 1건 성공
```

- [ ] **Step 2: 실패 확인**

Run: `my_env/bin/python -m pytest tests/test_state_pending.py -v`
Expected: FAIL (`cannot import name 'PendingCallQueue'`)

- [ ] **Step 3: 구현**

`state.py` 끝에 추가:

```python
from api import ApiError, AuthError, NetworkError, NightBlocked  # noqa: E402


class PendingCallQueue:
    """전송 실패한 콜 기록의 재전송 큐. ★평문 전화번호는 절대 저장하지 않는다."""

    def __init__(self, path: Path | None = None):
        self.path = path or (config_dir() / "pending_calls.json")
        loaded = _read_json(self.path, [])
        self._items: list[dict] = loaded if isinstance(loaded, list) else []

    def _save(self) -> None:
        _atomic_write_json(self.path, self._items)

    def add(self, *, idempotency_key: str, lead_id: str, payload: dict) -> None:
        self._items.append({"idempotency_key": idempotency_key,
                            "lead_id": lead_id, "payload": payload})
        self._save()

    def items(self) -> list[dict]:
        return list(self._items)

    def remove(self, idempotency_key: str) -> None:
        self._items = [x for x in self._items if x["idempotency_key"] != idempotency_key]
        self._save()

    def flush(self, client) -> tuple[int, int]:
        """(성공 수, 잔여 수). NetworkError/NightBlocked→중단(다음에 재시도),
        AuthError→re-raise(재로그인 필요), 그 외 ApiError→해당 건 폐기(재시도 무의미)."""
        sent = 0
        for entry in self.items():
            try:
                client.log_call(entry["lead_id"],
                                idempotency_key=entry["idempotency_key"],
                                **entry["payload"])
            except (NetworkError, NightBlocked):
                break
            except AuthError:
                raise
            except ApiError:
                self.remove(entry["idempotency_key"])
                continue
            self.remove(entry["idempotency_key"])
            sent += 1
        return sent, len(self._items)
```

- [ ] **Step 4: 통과 확인**

Run: `my_env/bin/python -m pytest tests/ -v`
Expected: 33 passed

- [ ] **Step 5: 커밋**

```bash
git add state.py tests/test_state_pending.py
git commit -m "feat: 콜 기록 재전송 큐(멱등키 기반, 평문 번호 미저장)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: adb.py — ADB 제어

**Files:**
- Create: `adb.py`, `tests/test_adb.py`

**Interfaces:**
- Produces: `adb_path() -> str` (우선순위: `TM_ADB` 환경변수 → frozen이면 `sys._MEIPASS/adb/adb.exe` → `"adb"`), `call(phone: str) -> bool`, `hangup() -> bool`, `is_connected() -> bool`
- Consumes: 없음 (독립 모듈)

- [ ] **Step 1: 테스트 작성**

`tests/test_adb.py`:

```python
import os
import stat

import pytest

import adb

FAKE_ADB = """#!/bin/sh
echo "$@" >> "$TM_ADB_LOG"
if [ "$1" = "devices" ]; then
  printf 'List of devices attached\\n%s' "$TM_ADB_DEVICES"
fi
exit ${TM_ADB_EXIT:-0}
"""


@pytest.fixture
def fake_adb(tmp_path, monkeypatch):
    script = tmp_path / "fakeadb.sh"
    script.write_text(FAKE_ADB)
    script.chmod(script.stat().st_mode | stat.S_IEXEC)
    log = tmp_path / "calls.log"
    monkeypatch.setenv("TM_ADB", str(script))
    monkeypatch.setenv("TM_ADB_LOG", str(log))
    monkeypatch.setenv("TM_ADB_DEVICES", "R3CN123\\tdevice\\n")
    monkeypatch.delenv("TM_ADB_EXIT", raising=False)
    return log


def test_call_invokes_call_intent(fake_adb):
    assert adb.call("01012341234") is True
    assert "shell am start -a android.intent.action.CALL -d tel:01012341234" in fake_adb.read_text()


def test_call_failure_returns_false(fake_adb, monkeypatch):
    monkeypatch.setenv("TM_ADB_EXIT", "1")
    assert adb.call("01012341234") is False


def test_call_missing_binary_returns_false(monkeypatch):
    monkeypatch.setenv("TM_ADB", "/nonexistent/adb")
    assert adb.call("01012341234") is False


def test_hangup_sends_keyevent(fake_adb):
    assert adb.hangup() is True
    assert "shell input keyevent 6" in fake_adb.read_text()


def test_is_connected_true(fake_adb):
    assert adb.is_connected() is True


def test_is_connected_false_when_no_devices(fake_adb, monkeypatch):
    monkeypatch.setenv("TM_ADB_DEVICES", "")
    assert adb.is_connected() is False
```

- [ ] **Step 2: 실패 확인**

Run: `my_env/bin/python -m pytest tests/test_adb.py -v`
Expected: FAIL (`No module named 'adb'`)

- [ ] **Step 3: 구현**

`adb.py`:

```python
"""ADB 발신/종료/연결감지. 배포 시 adb.exe는 exe 안에 동봉된다(sys._MEIPASS)."""
from __future__ import annotations

import os
import subprocess
import sys


def adb_path() -> str:
    override = os.environ.get("TM_ADB")
    if override:
        return override
    if getattr(sys, "frozen", False):
        return os.path.join(sys._MEIPASS, "adb", "adb.exe")  # type: ignore[attr-defined]
    return "adb"


def _startupinfo():
    if sys.platform == "win32":
        si = subprocess.STARTUPINFO()
        si.dwFlags |= subprocess.STARTF_USESHOWWINDOW  # 콘솔창 깜빡임 방지
        return si
    return None


def _run(args: list[str], timeout: float = 10) -> subprocess.CompletedProcess | None:
    try:
        return subprocess.run([adb_path(), *args], capture_output=True, text=True,
                              timeout=timeout, startupinfo=_startupinfo())
    except (OSError, subprocess.SubprocessError):
        return None


def call(phone: str) -> bool:
    r = _run(["shell", "am", "start", "-a", "android.intent.action.CALL",
              "-d", f"tel:{phone}"])
    return r is not None and r.returncode == 0


def hangup() -> bool:
    r = _run(["shell", "input", "keyevent", "6"])
    return r is not None and r.returncode == 0


def is_connected() -> bool:
    r = _run(["devices"], timeout=5)
    if r is None or r.returncode != 0:
        return False
    lines = r.stdout.strip().splitlines()[1:]
    return any(line.strip().endswith("device") for line in lines)
```

- [ ] **Step 4: 통과 확인**

Run: `my_env/bin/python -m pytest tests/test_adb.py -v`
Expected: 6 passed

- [ ] **Step 5: 커밋**

```bash
git add adb.py tests/test_adb.py
git commit -m "feat: ADB 발신·종료·연결감지 (번들 경로 해석 포함)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: ui/theme.py + ui/widgets.py — 디자인 토큰과 공용 컴포넌트

**Files:**
- Create: `ui/__init__.py`(빈 파일), `ui/theme.py`, `ui/widgets.py`, `tests/test_theme_widgets.py`

**Interfaces:**
- Produces(theme): `COLORS: dict`, `STATUS_LABEL: dict`, `RESULTS: list[(code, label, key)]` 7종, `status_colors(status) -> (bg, fg)`, `font(size, weight="normal") -> CTkFont`, `FONT_FAMILY`
- Produces(widgets): `Badge(master, status)`, `StatusDot(master, text)` — `.set_ok(bool)`, `ResultSelector(master, on_change=None)` — `.selected: str|None`, `.set(code)`, `.reset()`, `Toast(master, message)`, `run_bg(widget, work, on_success=None, on_error=None)`
- 색·라벨의 근거: milestone-crm `src/app/globals.css`(웜 크림+골드 토큰), `src/lib/labels.ts`(한글 라벨), `src/components/ui/badge.tsx`(statusVariant 매핑) — **CRM과 동일 값 유지**

- [ ] **Step 1: 테스트 작성** (매핑 무결성 + run_bg — GUI 생성 없이 검증 가능한 부분만)

`tests/test_theme_widgets.py`:

```python
import threading

from ui.theme import COLORS, STATUS_LABEL, RESULTS, status_colors
from ui.widgets import run_bg

ALL_STATUSES = ["NEW", "ASSIGNED", "NOANSWER", "CALLBACK", "INTERESTED",
                "CONSULT", "WON", "REJECT", "DNC", "RECYCLE"]


def test_every_status_has_label_and_colors():
    for s in ALL_STATUSES:
        assert s in STATUS_LABEL
        bg, fg = status_colors(s)
        assert bg.startswith("#") and fg.startswith("#")


def test_results_are_seven_with_keys_1_to_7():
    assert [r[2] for r in RESULTS] == [str(i) for i in range(1, 8)]
    codes = [r[0] for r in RESULTS]
    assert codes == ["NOANSWER", "CALLBACK", "INTERESTED", "CONSULT", "WON", "REJECT", "DNC"]


def test_crm_variant_mapping():
    """badge.tsx statusVariant와 동일해야 한다: WON/INTERESTED/CONSULT=brand,
    DNC/REJECT=danger, ASSIGNED/CALLBACK=soft."""
    assert status_colors("WON") == (COLORS["brand"], COLORS["ink"])
    assert status_colors("REJECT") == (COLORS["danger_soft"], COLORS["danger"])
    assert status_colors("CALLBACK") == (COLORS["brand_soft"], COLORS["ink"])
    assert status_colors("NEW") == (COLORS["surface2"], COLORS["foreground"])


class DummyWidget:
    """tk 없이 after만 흉내 — 콜백을 즉시 실행."""

    def after(self, _delay, fn):
        fn()


def test_run_bg_success_and_error():
    done = threading.Event()
    results = {}

    def ok_work():
        return 42

    run_bg(DummyWidget(), ok_work,
           on_success=lambda v: (results.__setitem__("v", v), done.set()))
    assert done.wait(2) and results["v"] == 42

    err_done = threading.Event()
    run_bg(DummyWidget(), lambda: 1 / 0,
           on_error=lambda e: (results.__setitem__("e", type(e).__name__), err_done.set()))
    assert err_done.wait(2) and results["e"] == "ZeroDivisionError"
```

- [ ] **Step 2: 실패 확인**

Run: `my_env/bin/python -m pytest tests/test_theme_widgets.py -v`
Expected: FAIL (`No module named 'ui'`)

- [ ] **Step 3: theme.py 구현**

`ui/theme.py`:

```python
"""CRM(globals.css/labels.ts/badge.tsx)에서 이식한 디자인 토큰 — 값 임의 변경 금지."""
from __future__ import annotations

COLORS = {
    "background": "#f3f1ea",   # 웜 크림
    "surface": "#ffffff",
    "surface2": "#faf8f2",
    "foreground": "#211f1a",
    "ink": "#161410",          # 제목·기본 버튼(near-black)
    "line": "#e8e3d8",
    "track": "#ebe6db",
    "hover": "#f0ebe0",
    "brand": "#ecdf4a",        # 로고 옐로
    "brand_soft": "#f6efbe",
    "gold": "#a98a1f",
    "danger": "#b3372c",
    "danger_soft": "#f7e2df",
    "success": "#1a7f4b",
    "success_soft": "#e2f0e8",
    "muted": "#8a857a",
}

# labels.ts LEAD_STATUS_LABEL과 동일
STATUS_LABEL = {
    "NEW": "신규", "ASSIGNED": "배정됨", "NOANSWER": "부재", "CALLBACK": "콜백예약",
    "INTERESTED": "가망", "CONSULT": "상담중", "WON": "가입", "REJECT": "거절",
    "DNC": "수신거부", "RECYCLE": "재활용",
}

# CALL_RESULT_LABEL 7종 + 단축키 1~7
RESULTS = [
    ("NOANSWER", "부재", "1"), ("CALLBACK", "콜백예약", "2"), ("INTERESTED", "가망", "3"),
    ("CONSULT", "상담중", "4"), ("WON", "가입", "5"), ("REJECT", "거절", "6"),
    ("DNC", "수신거부", "7"),
]

# badge.tsx statusVariant와 동일한 매핑
_VARIANT = {
    "WON": "brand", "INTERESTED": "brand", "CONSULT": "brand",
    "DNC": "danger", "REJECT": "danger",
    "ASSIGNED": "soft", "CALLBACK": "soft",
}
_VARIANT_COLORS = {
    "brand": (COLORS["brand"], COLORS["ink"]),
    "soft": (COLORS["brand_soft"], COLORS["ink"]),
    "danger": (COLORS["danger_soft"], COLORS["danger"]),
    "neutral": (COLORS["surface2"], COLORS["foreground"]),
}

FONT_FAMILY = "맑은 고딕"


def status_colors(status: str) -> tuple[str, str]:
    """상태코드 → (배경색, 글자색)."""
    return _VARIANT_COLORS[_VARIANT.get(status, "neutral")]


def font(size: int, weight: str = "normal"):
    """CTkFont 팩토리 — tk 초기화 이후에만 호출할 것."""
    import customtkinter as ctk

    return ctk.CTkFont(family=FONT_FAMILY, size=size, weight=weight)
```

- [ ] **Step 4: widgets.py 구현**

`ui/widgets.py`:

```python
"""공용 위젯 + 백그라운드 실행 헬퍼."""
from __future__ import annotations

import threading

import customtkinter as ctk

from ui.theme import COLORS, RESULTS, STATUS_LABEL, font, status_colors


def run_bg(widget, work, on_success=None, on_error=None):
    """work()를 데몬 스레드에서 실행하고 결과를 widget.after로 UI 스레드에 전달."""

    def runner():
        try:
            result = work()
        except Exception as exc:  # noqa: BLE001 — UI 콜백으로 전달
            if on_error:
                widget.after(0, lambda: on_error(exc))
        else:
            if on_success:
                widget.after(0, lambda: on_success(result))

    threading.Thread(target=runner, daemon=True).start()


class Badge(ctk.CTkLabel):
    def __init__(self, master, status: str):
        bg, fg = status_colors(status)
        super().__init__(master, text=f" {STATUS_LABEL.get(status, status)} ",
                         fg_color=bg, text_color=fg, corner_radius=6,
                         font=font(11, "bold"), height=22)


class StatusDot(ctk.CTkLabel):
    """상단 상태바의 ● ADB / ● CRM 표시."""

    def __init__(self, master, text: str):
        super().__init__(master, text=f"● {text}", font=font(12),
                         text_color=COLORS["muted"])

    def set_ok(self, ok: bool):
        self.configure(text_color=COLORS["success"] if ok else COLORS["danger"])


class ResultSelector(ctk.CTkFrame):
    """콜 결과 7종 버튼 — CRM 결과코드와 동일, 숫자키 1~7."""

    def __init__(self, master, on_change=None):
        super().__init__(master, fg_color="transparent")
        self.selected: str | None = None
        self.on_change = on_change
        self._buttons: dict[str, ctk.CTkButton] = {}
        for i, (code, label, key) in enumerate(RESULTS):
            btn = ctk.CTkButton(self, text=f"{label}\n({key})", width=76, height=48,
                                font=font(12), corner_radius=8,
                                command=lambda c=code: self.set(c))
            btn.grid(row=0, column=i, padx=3, pady=2)
            self._buttons[code] = btn
        self._restyle()

    def _restyle(self):
        for code, btn in self._buttons.items():
            if code == self.selected:
                btn.configure(fg_color=COLORS["ink"], text_color="white",
                              hover_color=COLORS["ink"])
            else:
                bg, fg = status_colors(code)
                btn.configure(fg_color=bg, text_color=fg, hover_color=COLORS["hover"])

    def set(self, code: str):
        self.selected = code
        self._restyle()
        if self.on_change:
            self.on_change(code)

    def reset(self):
        self.selected = None
        self._restyle()


class Toast(ctk.CTkToplevel):
    """우상단에 잠깐 떴다 사라지는 알림."""

    def __init__(self, master, message: str, duration_ms: int = 4000):
        super().__init__(master)
        self.overrideredirect(True)
        self.attributes("-topmost", True)
        frame = ctk.CTkFrame(self, fg_color=COLORS["ink"], corner_radius=10)
        frame.pack()
        ctk.CTkLabel(frame, text=message, text_color="white",
                     font=font(13)).pack(padx=16, pady=10)
        self.update_idletasks()
        x = master.winfo_rootx() + master.winfo_width() - self.winfo_width() - 24
        y = master.winfo_rooty() + 70
        self.geometry(f"+{x}+{y}")
        self.after(duration_ms, self.destroy)
```

- [ ] **Step 5: 통과 확인**

Run: `my_env/bin/python -m pytest tests/ -v`
Expected: 37 passed

- [ ] **Step 6: 커밋**

```bash
git add ui/ tests/test_theme_widgets.py
git commit -m "feat: CRM 디자인 토큰 이식 + 공용 위젯(배지·결과선택·토스트·run_bg)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: scripts/dev_mock_crm.py — 수동 테스트용 가짜 CRM

서버(Codex)가 준비되기 전에 GUI를 끝까지 수동 테스트하기 위한 로컬 서버. conftest의 MockCRM을 재사용해 상태를 가진 라우트를 얹는다.

**Files:**
- Create: `scripts/dev_mock_crm.py`

**Interfaces:**
- Consumes: `tests/conftest.py`의 `MockCRM`
- Produces: `my_env/bin/python scripts/dev_mock_crm.py` → 127.0.0.1:3002에서 로그인(`hong`/`1234`, MFA 없음)·큐 5건·reveal·call·memo가 동작하는 가짜 CRM. 콜 기록 시 해당 리드가 큐에서 상태 전이됨

- [ ] **Step 1: 구현**

`scripts/dev_mock_crm.py`:

```python
"""GUI 수동 테스트용 가짜 CRM. 사용: my_env/bin/python scripts/dev_mock_crm.py
로그인: hong / 1234. 서버 주소: http://127.0.0.1:3002 (TM_SERVER_URL 기본값과 동일)."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tests"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from conftest import MockCRM  # noqa: E402

USER = {"id": "u1", "loginId": "hong", "name": "홍길동", "orgName": "강남1팀",
        "roles": ["TM"], "mustChangePassword": False}

LEADS = {
    f"L{i}": {"id": f"L{i}", "name": name, "phoneMasked": f"010-****-{1000 + i}",
              "status": "ASSIGNED", "nextCallAt": None, "memo": "",
              "updatedAt": "2026-07-05T09:00:00+09:00"}
    for i, name in enumerate(["김철수", "이영희", "박민수", "정수진", "최지훈"])
}
CALLABLE = {"NEW", "ASSIGNED", "NOANSWER", "CALLBACK", "INTERESTED", "CONSULT"}
RESULT_TO_STATUS = {"NOANSWER": "NOANSWER", "CALLBACK": "CALLBACK",
                    "INTERESTED": "INTERESTED", "CONSULT": "CONSULT",
                    "WON": "WON", "REJECT": "REJECT", "DNC": "DNC"}
seen_keys = set()


def login(method, path, headers, body):
    if body and body.get("loginId") == "hong" and body.get("password") == "1234":
        return 200, {"token": "devtoken", "expiresAt": "2099-01-01T00:00:00+09:00",
                     "user": USER}
    return 401, {"error": {"code": "INVALID_CREDENTIALS",
                           "message": "아이디 또는 비밀번호가 올바르지 않습니다."}}


def queue(method, path, headers, body):
    items = [x for x in LEADS.values() if x["status"] in CALLABLE]
    return 200, {"serverTime": "2026-07-05T10:00:00+09:00", "items": items}


def make_routes(server):
    server.routes[("POST", "/api/v1/auth/login")] = login
    server.routes[("POST", "/api/v1/auth/logout")] = (204, None)
    server.routes[("GET", "/api/v1/me")] = (200, {"user": USER})
    server.routes[("GET", "/api/v1/leads/queue")] = queue
    for lid, lead in LEADS.items():
        def reveal(method, path, headers, body, _lid=lid):
            return 200, {"phone": f"0101234{LEADS[_lid]['phoneMasked'][-4:]}"}

        def call(method, path, headers, body, _lid=lid):
            key = headers.get("Idempotency-Key", "")
            if key not in seen_keys:
                seen_keys.add(key)
                LEADS[_lid]["status"] = RESULT_TO_STATUS[body["resultCode"]]
                LEADS[_lid]["nextCallAt"] = body.get("callbackAt")
            return 200, {"ok": True, "lead": {"id": _lid, "status": LEADS[_lid]["status"],
                                              "nextCallAt": LEADS[_lid]["nextCallAt"]}}

        def memo(method, path, headers, body, _lid=lid):
            LEADS[_lid]["memo"] = body["memo"]
            return 200, {"ok": True}

        server.routes[("POST", f"/api/v1/leads/{lid}/reveal")] = reveal
        server.routes[("POST", f"/api/v1/leads/{lid}/call")] = call
        server.routes[("PATCH", f"/api/v1/leads/{lid}/memo")] = memo


if __name__ == "__main__":
    import time

    server = MockCRM(port=3002)
    make_routes(server)
    server.start()
    print("가짜 CRM 실행 중: http://127.0.0.1:3002  (로그인 hong / 1234, Ctrl+C로 종료)")
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        server.stop()
```

- [ ] **Step 2: 동작 확인**

Run: `my_env/bin/python scripts/dev_mock_crm.py &` 후
`curl -s -X POST http://127.0.0.1:3002/api/v1/auth/login -d '{"loginId":"hong","password":"1234"}' -H 'Content-Type: application/json'`
Expected: `{"token": "devtoken", ...}` JSON. 확인 후 `kill %1`

- [ ] **Step 3: 커밋**

```bash
git add scripts/dev_mock_crm.py tests/conftest.py
git commit -m "chore: GUI 수동 테스트용 가짜 CRM 서버

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 9: ui/login.py — 로그인 화면

**Files:**
- Create: `ui/login.py`

**Interfaces:**
- Consumes: `ApiClient.login`(MfaRequired/ApiError/NetworkError), `Config`(last_login_id, server_url, save), `run_bg`, theme
- Produces: `LoginFrame(master, client, config, on_success)` — 로그인 성공 시 `config.last_login_id` 저장 후 `on_success()` 호출. `client.base_url`은 고급설정에서 변경 가능

- [ ] **Step 1: 구현**

`ui/login.py`:

```python
"""로그인 화면 — CRM 계정으로 로그인, MFA 계정은 코드 입력란이 나타난다."""
from __future__ import annotations

from tkinter import messagebox

import customtkinter as ctk

from api import ApiError, MfaRequired, NetworkError
from ui.theme import COLORS, font
from ui.widgets import run_bg
from version import APP_NAME


class LoginFrame(ctk.CTkFrame):
    def __init__(self, master, client, config, on_success):
        super().__init__(master, fg_color=COLORS["background"])
        self.client = client
        self.config_data = config
        self.on_success = on_success
        self._mfa_visible = False
        self._build()

    def _build(self):
        card = ctk.CTkFrame(self, fg_color=COLORS["surface"], corner_radius=16,
                            border_width=1, border_color=COLORS["line"])
        card.place(relx=0.5, rely=0.45, anchor="center")

        ctk.CTkLabel(card, text=f"📞 {APP_NAME}", font=font(22, "bold"),
                     text_color=COLORS["ink"]).pack(padx=48, pady=(36, 4))
        ctk.CTkLabel(card, text="CRM 계정으로 로그인하세요", font=font(12),
                     text_color=COLORS["muted"]).pack(pady=(0, 20))

        self.id_entry = ctk.CTkEntry(card, width=260, height=40, font=font(13),
                                     placeholder_text="아이디")
        self.id_entry.pack(padx=48, pady=6)
        if self.config_data.last_login_id:
            self.id_entry.insert(0, self.config_data.last_login_id)

        self.pw_entry = ctk.CTkEntry(card, width=260, height=40, font=font(13),
                                     placeholder_text="비밀번호", show="•")
        self.pw_entry.pack(padx=48, pady=6)

        self.mfa_entry = ctk.CTkEntry(card, width=260, height=40, font=font(13),
                                      placeholder_text="인증앱 6자리 코드")
        # MFA 필요해질 때만 pack

        self.error_label = ctk.CTkLabel(card, text="", font=font(12),
                                        text_color=COLORS["danger"], wraplength=260)
        self.error_label.pack(pady=(4, 0))

        self.submit_btn = ctk.CTkButton(card, text="로그인", width=260, height=42,
                                        font=font(14, "bold"), corner_radius=8,
                                        fg_color=COLORS["ink"], hover_color="#2d2a24",
                                        command=self.submit)
        self.submit_btn.pack(padx=48, pady=(12, 8))

        ctk.CTkButton(card, text="서버 주소 설정", width=260, height=28, font=font(11),
                      fg_color="transparent", text_color=COLORS["muted"],
                      hover_color=COLORS["hover"],
                      command=self._server_settings).pack(pady=(0, 28))

        for entry in (self.id_entry, self.pw_entry, self.mfa_entry):
            entry.bind("<Return>", lambda e: self.submit())

    def _server_settings(self):
        dialog = ctk.CTkInputDialog(title="서버 주소",
                                    text=f"CRM 서버 주소:\n(현재: {self.client.base_url})")
        value = dialog.get_input()
        if value and value.strip():
            self.client.base_url = value.strip().rstrip("/")
            self.config_data.server_url = self.client.base_url
            self.config_data.save()

    def submit(self):
        login_id = self.id_entry.get().strip()
        password = self.pw_entry.get()
        code = self.mfa_entry.get().strip() or None if self._mfa_visible else None
        if not login_id or not password:
            self.error_label.configure(text="아이디와 비밀번호를 입력하세요.")
            return
        self.submit_btn.configure(state="disabled", text="확인 중…")
        self.error_label.configure(text="")
        run_bg(self, lambda: self.client.login(login_id, password, code),
               on_success=self._on_login, on_error=self._on_error)

    def _on_login(self, user: dict):
        self.submit_btn.configure(state="normal", text="로그인")
        if user.get("mustChangePassword"):
            messagebox.showwarning(
                "비밀번호 변경 필요",
                "초기 비밀번호 상태입니다.\n웹 CRM에서 비밀번호를 변경한 뒤 다시 로그인하세요.")
            self.client.logout()
            return
        self.config_data.last_login_id = user["loginId"]
        self.config_data.save()
        self.on_success()

    def _on_error(self, exc: Exception):
        self.submit_btn.configure(state="normal", text="로그인")
        if isinstance(exc, MfaRequired):
            if not self._mfa_visible:
                self._mfa_visible = True
                self.mfa_entry.pack(padx=48, pady=6, after=self.pw_entry)
                self.error_label.configure(text="인증앱의 6자리 코드를 입력하세요.")
            else:
                self.error_label.configure(text=exc.message)
            self.mfa_entry.focus()
        elif isinstance(exc, (ApiError, NetworkError)):
            self.error_label.configure(text=exc.message)
        else:
            self.error_label.configure(text=f"오류: {exc}")
```

- [ ] **Step 2: 수동 확인 (가짜 CRM 대상)**

터미널 1: `my_env/bin/python scripts/dev_mock_crm.py`
터미널 2 (임시 실행 스크립트 — Task 11에서 main.py가 대체):

```bash
my_env/bin/python - <<'EOF'
import customtkinter as ctk
from api import ApiClient
from state import Config
from ui.login import LoginFrame

ctk.set_appearance_mode("light")
root = ctk.CTk(); root.geometry("900x640"); root.title("로그인 테스트")
cfg = Config.load()
client = ApiClient(cfg.server_url)
frame = LoginFrame(root, client, cfg, on_success=lambda: print("LOGIN OK:", client.user))
frame.pack(fill="both", expand=True)
root.mainloop()
EOF
```

Expected: 로그인 카드 표시 → 틀린 비번 시 빨간 에러 → `hong`/`1234` 성공 시 콘솔에 `LOGIN OK` 출력. (WSLg 없으면 이 단계는 Windows에서 확인하고 넘어간다)

- [ ] **Step 3: 커밋**

```bash
git add ui/login.py
git commit -m "feat: 로그인 화면(MFA 분기·서버주소 설정·초기비번 안내)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 10: ui/workspace.py — 콜 워크스페이스

**Files:**
- Create: `ui/workspace.py`

**Interfaces:**
- Consumes: `ApiClient`(queue/reveal/log_call/save_memo + 오류 타입), `adb`(call/hangup/is_connected), `logic`(sort_queue/is_callback_due/format_seconds/callback_iso/parse_iso), `PendingCallQueue`, widgets/theme 전부
- Produces: `WorkspaceFrame(master, client, config, pending, on_auth_lost)` — `on_auth_lost()`는 세션 만료 시 App이 로그인 화면으로 돌려보내는 콜백

- [ ] **Step 1: 구현**

`ui/workspace.py`:

```python
"""콜 워크스페이스 — 좌측 큐, 우측 고객 카드·통화 컨트롤·결과 기록."""
from __future__ import annotations

import time
import uuid
from datetime import datetime
from tkinter import messagebox

import customtkinter as ctk

import adb
from api import ApiError, AuthError, NetworkError, NightBlocked
from logic import callback_iso, format_seconds, is_callback_due, parse_iso, sort_queue
from ui.theme import COLORS, RESULTS, font
from ui.widgets import Badge, ResultSelector, StatusDot, Toast, run_bg

QUEUE_POLL_MS = 60_000
ADB_POLL_MS = 5_000
FLUSH_POLL_MS = 30_000
TICK_MS = 1_000


def now_local() -> datetime:
    return datetime.now().astimezone()


class WorkspaceFrame(ctk.CTkFrame):
    def __init__(self, master, client, config, pending, on_auth_lost):
        super().__init__(master, fg_color=COLORS["background"])
        self.client = client
        self.config_data = config
        self.pending = pending
        self.on_auth_lost = on_auth_lost

        self.leads: list[dict] = []
        self.current: dict | None = None
        self.call_started: float | None = None  # time.monotonic()
        self.talk_seconds = 0
        self.today_dials = 0
        self.today_won = 0
        self._notified_callbacks: set[str] = set()
        self._destroyed = False

        self._build()
        self._bind_keys()
        self.refresh_queue()
        self.after(TICK_MS, self._tick)
        self.after(ADB_POLL_MS, self._poll_adb)
        self.after(FLUSH_POLL_MS, self._poll_flush)
        self.after(QUEUE_POLL_MS, self._poll_queue)

    # ---------- 레이아웃 ----------

    def _build(self):
        self.grid_columnconfigure(1, weight=1)
        self.grid_rowconfigure(1, weight=1)

        # 상단 상태바
        bar = ctk.CTkFrame(self, fg_color=COLORS["surface"], corner_radius=0,
                           border_width=1, border_color=COLORS["line"], height=52)
        bar.grid(row=0, column=0, columnspan=2, sticky="ew")
        user = self.client.user or {}
        ctk.CTkLabel(bar, text=f"🏢 {user.get('orgName', '')} · {user.get('name', '')}",
                     font=font(13, "bold"), text_color=COLORS["ink"]).pack(side="left", padx=16)
        self.today_label = ctk.CTkLabel(bar, text="오늘: 발신 0 · 가입 0",
                                        font=font(12), text_color=COLORS["gold"])
        self.today_label.pack(side="left", padx=12)
        self.crm_dot = StatusDot(bar, "CRM")
        self.crm_dot.pack(side="right", padx=(4, 16))
        self.adb_dot = StatusDot(bar, "ADB")
        self.adb_dot.pack(side="right", padx=4)
        self.banner = ctk.CTkLabel(bar, text="", font=font(11), text_color=COLORS["danger"])
        self.banner.pack(side="right", padx=12)

        # 좌측 큐
        left = ctk.CTkFrame(self, fg_color=COLORS["surface"], corner_radius=12,
                            border_width=1, border_color=COLORS["line"], width=280)
        left.grid(row=1, column=0, sticky="nsw", padx=(12, 6), pady=12)
        left.grid_propagate(False)
        head = ctk.CTkFrame(left, fg_color="transparent")
        head.pack(fill="x", padx=12, pady=(12, 4))
        ctk.CTkLabel(head, text="오늘의 콜 큐", font=font(13, "bold"),
                     text_color=COLORS["ink"]).pack(side="left")
        ctk.CTkButton(head, text="↻", width=28, height=24, font=font(12),
                      fg_color=COLORS["surface2"], text_color=COLORS["ink"],
                      hover_color=COLORS["hover"],
                      command=self.refresh_queue).pack(side="right")
        self.queue_box = ctk.CTkScrollableFrame(left, fg_color="transparent")
        self.queue_box.pack(fill="both", expand=True, padx=6, pady=(0, 8))

        # 우측 상세
        right = ctk.CTkFrame(self, fg_color="transparent")
        right.grid(row=1, column=1, sticky="nsew", padx=(6, 12), pady=12)
        right.grid_columnconfigure(0, weight=1)

        card = ctk.CTkFrame(right, fg_color=COLORS["surface"], corner_radius=12,
                            border_width=1, border_color=COLORS["line"])
        card.grid(row=0, column=0, sticky="ew")
        top = ctk.CTkFrame(card, fg_color="transparent")
        top.pack(fill="x", padx=20, pady=(18, 0))
        self.name_label = ctk.CTkLabel(top, text="-", font=font(24, "bold"),
                                       text_color=COLORS["ink"])
        self.name_label.pack(side="left")
        self.badge_slot = ctk.CTkFrame(top, fg_color="transparent")
        self.badge_slot.pack(side="left", padx=10)
        self.phone_label = ctk.CTkLabel(card, text="📱 -", font=font(17),
                                        text_color=COLORS["gold"])
        self.phone_label.pack(anchor="w", padx=20, pady=(6, 0))
        memo_row = ctk.CTkFrame(card, fg_color="transparent")
        memo_row.pack(fill="x", padx=20, pady=(8, 16))
        ctk.CTkLabel(memo_row, text="리드 메모", font=font(11),
                     text_color=COLORS["muted"]).pack(side="left")
        self.lead_memo_entry = ctk.CTkEntry(memo_row, font=font(12), height=30,
                                            placeholder_text="영업 메모 (칸반에 표시)")
        self.lead_memo_entry.pack(side="left", fill="x", expand=True, padx=8)
        ctk.CTkButton(memo_row, text="메모 저장", width=76, height=30, font=font(11),
                      fg_color=COLORS["surface2"], text_color=COLORS["ink"],
                      hover_color=COLORS["hover"],
                      command=self.save_lead_memo).pack(side="left")

        # 통화 컨트롤
        controls = ctk.CTkFrame(right, fg_color=COLORS["surface"], corner_radius=12,
                                border_width=1, border_color=COLORS["line"])
        controls.grid(row=1, column=0, sticky="ew", pady=(10, 0))
        inner = ctk.CTkFrame(controls, fg_color="transparent")
        inner.pack(pady=14)
        self.dial_btn = ctk.CTkButton(inner, text="📞 발신 (F1)", width=170, height=52,
                                      font=font(15, "bold"), corner_radius=10,
                                      fg_color=COLORS["success"], hover_color="#14653c",
                                      command=self.dial)
        self.dial_btn.grid(row=0, column=0, padx=8)
        self.hangup_btn = ctk.CTkButton(inner, text="⏹ 종료 (F2)", width=170, height=52,
                                        font=font(15, "bold"), corner_radius=10,
                                        fg_color=COLORS["danger"], hover_color="#8f2c23",
                                        state="disabled", command=self.hangup)
        self.hangup_btn.grid(row=0, column=1, padx=8)
        self.timer_label = ctk.CTkLabel(inner, text="00:00", font=font(20, "bold"),
                                        text_color=COLORS["ink"], width=90)
        self.timer_label.grid(row=0, column=2, padx=12)

        # 결과 기록
        result_card = ctk.CTkFrame(right, fg_color=COLORS["surface"], corner_radius=12,
                                   border_width=1, border_color=COLORS["line"])
        result_card.grid(row=2, column=0, sticky="ew", pady=(10, 0))
        ctk.CTkLabel(result_card, text="상담 결과 (숫자키 1~7)", font=font(12, "bold"),
                     text_color=COLORS["ink"]).pack(anchor="w", padx=20, pady=(14, 4))
        self.result_selector = ResultSelector(result_card, on_change=self._on_result_change)
        self.result_selector.pack(padx=14, pady=2)
        form = ctk.CTkFrame(result_card, fg_color="transparent")
        form.pack(fill="x", padx=20, pady=(6, 16))
        ctk.CTkLabel(form, text="메모", font=font(11),
                     text_color=COLORS["muted"]).pack(side="left")
        self.memo_entry = ctk.CTkEntry(form, font=font(12), height=32,
                                       placeholder_text="상담 메모…")
        self.memo_entry.pack(side="left", fill="x", expand=True, padx=8)
        self.callback_entry = ctk.CTkEntry(form, font=font(12), height=32, width=76,
                                           placeholder_text="14:30")
        # CALLBACK 선택 시에만 pack
        self.save_btn = ctk.CTkButton(form, text="💾 저장하고 다음 (F3)", width=170,
                                      height=36, font=font(13, "bold"), corner_radius=8,
                                      fg_color=COLORS["ink"], hover_color="#2d2a24",
                                      command=self.save_result)
        self.save_btn.pack(side="left", padx=(8, 0))

    def _bind_keys(self):
        root = self.winfo_toplevel()
        root.bind("<F1>", lambda e: self.dial())
        root.bind("<F2>", lambda e: self.hangup())
        root.bind("<F3>", lambda e: self.save_result())
        for code, _label, key in RESULTS:
            root.bind(key, lambda e, c=code: self._key_result(c))

    def _key_result(self, code: str):
        # 입력창에 타이핑 중일 때 숫자키를 가로채지 않는다
        focus = self.focus_get()
        if isinstance(focus, (ctk.CTkEntry,)) or "entry" in str(focus).lower():
            return
        self.result_selector.set(code)

    # ---------- 큐 ----------

    def refresh_queue(self):
        run_bg(self, lambda: self.client.queue(),
               on_success=self._on_queue, on_error=self._on_queue_error)

    def _poll_queue(self):
        if self._destroyed:
            return
        self.refresh_queue()
        self.after(QUEUE_POLL_MS, self._poll_queue)

    def _on_queue(self, items: list[dict]):
        self.crm_dot.set_ok(True)
        self.leads = items
        self._render_queue()
        ids = {x["id"] for x in items}
        if self.current is None or self.current["id"] not in ids:
            self._select(items[0] if items else None)

    def _on_queue_error(self, exc: Exception):
        if isinstance(exc, AuthError):
            self.on_auth_lost()
            return
        self.crm_dot.set_ok(False)

    def _render_queue(self):
        for child in self.queue_box.winfo_children():
            child.destroy()
        now = now_local()
        for item in sort_queue(self.leads, now):
            due = is_callback_due(item, now)
            selected = self.current is not None and item["id"] == self.current["id"]
            bg = COLORS["brand_soft"] if due else (
                COLORS["hover"] if selected else COLORS["surface"])
            row = ctk.CTkFrame(self.queue_box, fg_color=bg, corner_radius=8)
            row.pack(fill="x", pady=2, padx=2)
            name = item.get("name") or "(이름없음)"
            prefix = "🔔 " if due else ""
            ctk.CTkLabel(row, text=f"{prefix}{name}", font=font(13),
                         text_color=COLORS["ink"], anchor="w").pack(
                side="left", padx=(10, 4), pady=6)
            Badge(row, item["status"]).pack(side="right", padx=8)
            dt = parse_iso(item.get("nextCallAt"))
            if dt is not None:
                ctk.CTkLabel(row, text=dt.strftime("%H:%M"), font=font(11),
                             text_color=COLORS["muted"]).pack(side="right")
            for widget in (row, *row.winfo_children()):
                widget.bind("<Button-1>", lambda e, it=item: self._select(it))
            if due and item["id"] not in self._notified_callbacks:
                self._notified_callbacks.add(item["id"])
                Toast(self.winfo_toplevel(),
                      f"🔔 재통화 시간: {name} ({dt.strftime('%H:%M') if dt else ''})")

    def _select(self, item: dict | None):
        if self.call_started:
            return  # 통화 중에는 리드 전환 금지
        self.current = item
        for child in self.badge_slot.winfo_children():
            child.destroy()
        if item is None:
            self.name_label.configure(text="✅ 대기 중인 콜이 없습니다")
            self.phone_label.configure(text="큐가 비어 있습니다 — 새 배정을 기다리세요")
            self.lead_memo_entry.delete(0, "end")
        else:
            self.name_label.configure(text=item.get("name") or "(이름없음)")
            self.phone_label.configure(text=f"📱 {item['phoneMasked']}")
            Badge(self.badge_slot, item["status"]).pack()
            self.lead_memo_entry.delete(0, "end")
            if item.get("memo"):
                self.lead_memo_entry.insert(0, item["memo"])
        self._render_queue()

    # ---------- 통화 ----------

    def dial(self):
        if self.current is None or self.call_started is not None:
            return
        if not adb.is_connected():
            messagebox.showwarning("ADB", "휴대폰이 연결되지 않았습니다.\nUSB 연결과 디버깅 허용을 확인하세요.")
            return
        lead = self.current
        self.dial_btn.configure(state="disabled", text="발신 중…")

        def work():
            phone = self.client.reveal(lead["id"])
            if not adb.call(phone):
                raise RuntimeError("ADB 발신에 실패했습니다.")

        run_bg(self, work, on_success=lambda _: self._on_dialed(),
               on_error=self._on_dial_error)

    def _on_dialed(self):
        self.call_started = time.monotonic()
        self.talk_seconds = 0
        self.today_dials += 1
        self._update_today()
        self.dial_btn.configure(text="📞 발신 (F1)")
        self.hangup_btn.configure(state="normal")

    def _on_dial_error(self, exc: Exception):
        self.dial_btn.configure(state="normal", text="📞 발신 (F1)")
        if isinstance(exc, AuthError):
            self.on_auth_lost()
        elif isinstance(exc, NetworkError):
            self.crm_dot.set_ok(False)
            messagebox.showerror("연결 오류", exc.message)
        elif isinstance(exc, ApiError):
            messagebox.showerror("발신 불가", exc.message)
        else:
            messagebox.showerror("오류", str(exc))

    def hangup(self):
        if self.call_started is None:
            return
        run_bg(self, adb.hangup)
        self._end_call()

    def _end_call(self):
        if self.call_started is not None:
            self.talk_seconds = int(time.monotonic() - self.call_started)
            self.call_started = None
        self.hangup_btn.configure(state="disabled")
        self.dial_btn.configure(state="normal")

    # ---------- 결과 기록 ----------

    def _on_result_change(self, code: str):
        if code == "CALLBACK":
            self.callback_entry.pack(side="left", padx=(8, 0), before=self.save_btn)
        else:
            self.callback_entry.pack_forget()

    def save_lead_memo(self):
        if self.current is None:
            return
        lead = self.current
        memo = self.lead_memo_entry.get().strip()
        run_bg(self, lambda: self.client.save_memo(lead["id"], memo),
               on_success=lambda _: Toast(self.winfo_toplevel(), "메모 저장됨"),
               on_error=self._on_dial_error)

    def save_result(self):
        if self.current is None:
            return
        code = self.result_selector.selected
        if code is None:
            messagebox.showwarning("결과 선택", "상담 결과를 먼저 선택하세요 (1~7).")
            return
        callback_at = None
        if code == "CALLBACK":
            callback_at = callback_iso(self.callback_entry.get(), now_local())
            if callback_at is None:
                messagebox.showwarning("시간 형식", "콜백 시간을 HH:MM 형식으로 입력하세요 (예: 14:30).")
                return
        if self.call_started is not None:
            self._end_call()
        lead = self.current
        payload = {"result_code": code, "talk_seconds": self.talk_seconds,
                   "memo": self.memo_entry.get().strip() or None,
                   "callback_at": callback_at}
        key = str(uuid.uuid4())
        self.save_btn.configure(state="disabled")

        def ok(_res):
            self.save_btn.configure(state="normal")
            if code == "WON":
                self.today_won += 1
            self._update_today()
            self._reset_form()
            self.refresh_queue()

        def err(exc: Exception):
            self.save_btn.configure(state="normal")
            if isinstance(exc, NetworkError):
                self.pending.add(idempotency_key=key, lead_id=lead["id"], payload=payload)
                self._update_banner()
                self.crm_dot.set_ok(False)
                Toast(self.winfo_toplevel(), "연결 실패 — 기록을 대기열에 보관했습니다")
                self._reset_form()
                self.leads = [x for x in self.leads if x["id"] != lead["id"]]
                self._select(self.leads[0] if self.leads else None)
            elif isinstance(exc, AuthError):
                self.pending.add(idempotency_key=key, lead_id=lead["id"], payload=payload)
                self.on_auth_lost()
            elif isinstance(exc, NightBlocked):
                messagebox.showwarning("야간 제한", exc.message)
            elif isinstance(exc, ApiError):
                messagebox.showerror("저장 실패", exc.message)
            else:
                messagebox.showerror("오류", str(exc))

        run_bg(self, lambda: self.client.log_call(lead["id"], idempotency_key=key,
                                                  **payload),
               on_success=ok, on_error=err)

    def _reset_form(self):
        self.result_selector.reset()
        self.memo_entry.delete(0, "end")
        self.callback_entry.delete(0, "end")
        self.callback_entry.pack_forget()
        self.talk_seconds = 0
        self.timer_label.configure(text="00:00")

    # ---------- 주기 작업 ----------

    def _update_today(self):
        self.today_label.configure(text=f"오늘: 발신 {self.today_dials} · 가입 {self.today_won}")

    def _update_banner(self):
        n = len(self.pending.items())
        self.banner.configure(text=f"📤 전송 대기 {n}건" if n else "")

    def _tick(self):
        if self._destroyed:
            return
        if self.call_started is not None:
            self.timer_label.configure(
                text=format_seconds(int(time.monotonic() - self.call_started)))
        self.after(TICK_MS, self._tick)

    def _poll_adb(self):
        if self._destroyed:
            return
        run_bg(self, adb.is_connected, on_success=self.adb_dot.set_ok)
        self.after(ADB_POLL_MS, self._poll_adb)

    def _poll_flush(self):
        if self._destroyed:
            return
        if self.pending.items():
            def done(_res):
                self._update_banner()

            def fail(exc):
                if isinstance(exc, AuthError):
                    self.on_auth_lost()

            run_bg(self, lambda: self.pending.flush(self.client),
                   on_success=done, on_error=fail)
        self.after(FLUSH_POLL_MS, self._poll_flush)

    def destroy(self):
        self._destroyed = True
        super().destroy()
```

- [ ] **Step 2: 기존 테스트 회귀 확인**

Run: `my_env/bin/python -m pytest tests/ -v`
Expected: 전체 통과 (workspace는 import 대상 아님 — GUI 모듈은 수동 검증)

- [ ] **Step 3: 수동 확인 (가짜 CRM 대상)**

터미널 1: `my_env/bin/python scripts/dev_mock_crm.py`
터미널 2: Task 11의 main.py가 아직 없으므로 임시 실행:

```bash
TM_ADB=/bin/true my_env/bin/python - <<'EOF'
import customtkinter as ctk
from api import ApiClient
from state import Config, PendingCallQueue
from ui.workspace import WorkspaceFrame

ctk.set_appearance_mode("light")
root = ctk.CTk(); root.geometry("1000x680"); root.title("워크스페이스 테스트")
cfg = Config.load()
client = ApiClient(cfg.server_url)
client.login("hong", "1234")
frame = WorkspaceFrame(root, client, cfg, PendingCallQueue(),
                       on_auth_lost=lambda: print("AUTH LOST"))
frame.pack(fill="both", expand=True)
root.mainloop()
EOF
```

Expected 체크: 큐 5명 표시 / 첫 리드 자동 선택 / 발신(F1) → 타이머 시작 / 종료(F2) / 결과 3(가망) + 저장(F3) → 큐에서 상태 변경 반영 / 리드 메모 저장 토스트.

- [ ] **Step 4: 커밋**

```bash
git add ui/workspace.py
git commit -m "feat: 콜 워크스페이스(큐·발신·타이머·결과기록·재전송 배너)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 11: main.py — 앱 부트스트랩

**Files:**
- Create: `main.py`

**Interfaces:**
- Consumes: 전 모듈. `LoginFrame(master, client, config, on_success)`, `WorkspaceFrame(master, client, config, pending, on_auth_lost)`
- Produces: `python main.py`로 실행되는 완성 앱. 치명적 오류는 `config_dir()/error_log.txt`에 기록

- [ ] **Step 1: 구현**

`main.py`:

```python
"""TM 다이얼러 진입점."""
from __future__ import annotations

import datetime
import traceback
from tkinter import messagebox

import customtkinter as ctk

from api import ApiClient
from state import Config, PendingCallQueue, config_dir
from ui.login import LoginFrame
from ui.theme import COLORS
from ui.workspace import WorkspaceFrame
from version import APP_NAME, VERSION


def log_error(message: str) -> None:
    try:
        stamp = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        with open(config_dir() / "error_log.txt", "a", encoding="utf-8") as f:
            f.write(f"[{stamp}] {message}\n")
    except OSError:
        pass


class App(ctk.CTk):
    def __init__(self):
        super().__init__(fg_color=COLORS["background"])
        ctk.set_appearance_mode("light")
        self.title(f"{APP_NAME} v{VERSION}")
        self.geometry("1000x680")
        self.minsize(920, 620)

        self.config_data = Config.load()
        self.client = ApiClient(self.config_data.server_url)
        self.pending = PendingCallQueue()
        self._frame: ctk.CTkFrame | None = None
        self.show_login()
        self.after(500, self._check_version)

    def _swap(self, frame: ctk.CTkFrame):
        if self._frame is not None:
            self._frame.destroy()
        self._frame = frame
        frame.pack(fill="both", expand=True)

    def show_login(self):
        self._swap(LoginFrame(self, self.client, self.config_data,
                              on_success=self.show_workspace))

    def show_workspace(self):
        self._swap(WorkspaceFrame(self, self.client, self.config_data, self.pending,
                                  on_auth_lost=self.on_auth_lost))

    def on_auth_lost(self):
        messagebox.showinfo("세션 만료", "세션이 만료되었습니다. 다시 로그인해주세요.")
        self.show_login()

    def _check_version(self):
        def check():
            return self.client.check_version()

        def done(info):
            if not info:
                return
            if tuple(VERSION.split(".")) < tuple(str(info.get("minVersion", "0")).split(".")):
                messagebox.showwarning(
                    "업데이트 필요",
                    f"이 버전({VERSION})은 더 이상 지원되지 않습니다.\n"
                    f"관리자에게 새 버전을 요청하세요. (최신: {info.get('latestVersion')})")

        from ui.widgets import run_bg
        run_bg(self, check, on_success=done)


def main():
    try:
        App().mainloop()
    except Exception as exc:  # noqa: BLE001 — 마지막 안전망
        log_error(f"Fatal: {exc}\n{traceback.format_exc()}")
        try:
            messagebox.showerror("치명적 오류", f"프로그램을 시작할 수 없습니다.\n{exc}")
        except Exception:  # noqa: BLE001
            pass
        raise


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: 수동 확인 — 전체 흐름**

터미널 1: `my_env/bin/python scripts/dev_mock_crm.py`
터미널 2: `TM_ADB=/bin/true my_env/bin/python main.py`

Expected: 로그인(hong/1234) → 워크스페이스 → 발신/결과 저장 전체 사이클 동작. 가짜 CRM을 끈 상태에서 저장 → "전송 대기 1건" 배너 → 서버 재기동 후 30초 내 자동 전송 확인.

- [ ] **Step 3: 전체 테스트 회귀**

Run: `my_env/bin/python -m pytest tests/ -v`
Expected: 전체 통과

- [ ] **Step 4: 커밋**

```bash
git add main.py
git commit -m "feat: 앱 부트스트랩(화면 전환·세션만료 처리·버전 안내·오류 로그)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 12: 빌드 파이프라인 + 문서 + 레거시 정리

**Files:**
- Create: `build/dialer.spec`, `build/build.bat`, `docs/manual-test.md`, `README.md`
- Delete: `auto_call.py` (레거시 — git 이력에 보존됨)

**Interfaces:**
- Consumes: `main.py` 진입점, `adb.adb_path()`의 `sys._MEIPASS/adb/adb.exe` 규약
- Produces: Windows에서 `build\build.bat` 실행 → `dist\TM다이얼러.exe` 단일 파일

- [ ] **Step 1: PyInstaller 스펙 작성**

`build/dialer.spec`:

```python
# -*- mode: python ; coding: utf-8 -*-
# PyInstaller onefile 스펙 — Windows에서 build.bat로 실행.
# 사전 준비: Android platform-tools에서 build/adb/ 에 아래 3개 파일 복사
#   adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll  (저장소에는 커밋하지 않음)
import os

HERE = os.path.dirname(os.path.abspath(SPEC))
ROOT = os.path.dirname(HERE)
ADB_DIR = os.path.join(HERE, "adb")

datas = [
    (os.path.join(ADB_DIR, "adb.exe"), "adb"),
    (os.path.join(ADB_DIR, "AdbWinApi.dll"), "adb"),
    (os.path.join(ADB_DIR, "AdbWinUsbApi.dll"), "adb"),
]

a = Analysis(
    [os.path.join(ROOT, "main.py")],
    pathex=[ROOT],
    binaries=[],
    datas=datas,
    hiddenimports=[],
    excludes=["pandas", "numpy", "openpyxl"],
)
pyz = PYZ(a.pure)
exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    name="TM다이얼러",
    console=False,
    upx=False,
)
```

- [ ] **Step 2: 빌드 배치 작성**

`build/build.bat`:

```bat
@echo off
REM TM 다이얼러 Windows 빌드. 사전 준비:
REM   1) Python 3.12 설치 (py 런처 포함)
REM   2) build\adb\ 에 adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll 복사
cd /d %~dp0..
if not exist build\adb\adb.exe (
  echo [오류] build\adb\ 에 adb.exe와 DLL 2종을 먼저 복사하세요.
  pause & exit /b 1
)
if not exist .venv (py -3.12 -m venv .venv)
call .venv\Scripts\activate.bat
python -m pip install --upgrade pip
python -m pip install -r requirements.txt pyinstaller
pyinstaller build\dialer.spec --noconfirm --distpath dist --workpath build\out
echo.
echo [완료] dist\TM다이얼러.exe
pause
```

- [ ] **Step 3: 수동 테스트 체크리스트 작성**

`docs/manual-test.md`:

```markdown
# TM 다이얼러 수동 테스트 체크리스트

사전: `my_env/bin/python scripts/dev_mock_crm.py` (또는 실제 CRM dev 서버) 실행.

## 로그인
- [ ] 빈 입력 제출 → "아이디와 비밀번호를 입력하세요"
- [ ] 틀린 비밀번호 → 빨간 에러 문구, 프로그램 유지
- [ ] 정상 로그인 → 워크스페이스 전환, 상단에 소속·이름 표시
- [ ] (실서버) MFA 계정 → 코드 입력란 등장 → 올바른 코드로 통과
- [ ] (실서버) 초기비밀번호 계정 → "웹에서 변경" 안내 후 로그인 중단
- [ ] 서버 주소 설정에서 잘못된 주소 입력 → 로그인 시 연결 오류 표시

## 큐/선택
- [ ] 배정 리드가 큐에 표시, 첫 리드 자동 선택
- [ ] 리드 클릭 시 우측 카드 갱신(이름·마스킹 번호·상태 배지·리드 메모)
- [ ] ↻ 새로고침 동작, 60초 자동 폴링 동작
- [ ] 콜백 시간이 지난 리드: 🔔 + 큐 상단 + 토스트 1회

## 통화
- [ ] 폰 미연결 시 발신 → ADB 경고, ● ADB 빨강
- [ ] 발신(F1) → 폰이 걸림 → 타이머 시작, 종료(F2) → 타이머 정지
- [ ] 통화 중 다른 리드 클릭 → 전환되지 않음

## 결과 기록
- [ ] 결과 미선택 저장 → 경고
- [ ] 콜백예약(2) 선택 → 시간 입력란 등장, "25:99" → 형식 경고
- [ ] 가망(3) + 메모 저장(F3) → 다음 리드 자동 선택, 웹 칸반에서 상태·메모 확인
- [ ] 수신거부(7) 저장 → (실서버) 웹에서 DNC 등록 확인
- [ ] 숫자키가 메모 입력 중에는 결과를 바꾸지 않음

## 장애/복구
- [ ] CRM 중단 후 저장 → "전송 대기 1건" 배너 + 대기열 보관
- [ ] CRM 재기동 → 30초 내 자동 전송, 배너 사라짐, 중복 기록 없음(멱등키)
- [ ] 세션 만료(서버에서 세션 삭제) → "세션 만료" 안내 → 재로그인 → 대기분 전송
- [ ] (실서버) 야간(21시 이후) 저장 → "야간 제한" 안내

## 빌드(Windows)
- [ ] build\build.bat → dist\TM다이얼러.exe 생성
- [ ] exe 단독 실행(파이썬 미설치 PC) → 로그인·발신·기록 전체 동작
- [ ] %APPDATA%\TMDialer\ 에 config.json 생성, error_log.txt 위치 확인
```

- [ ] **Step 4: README 작성 + 레거시 삭제**

`README.md`:

```markdown
# TM 다이얼러

milestone-crm과 연동되는 TM 상담원용 전화 프로그램.
CRM 계정으로 로그인 → 배정된 리드 큐 → USB 연결 안드로이드 폰으로 발신(ADB) → 결과는 CRM에 즉시 기록.

- 설계서: `docs/superpowers/specs/2026-07-05-tm-dialer-crm-integration-design.md` (API 계약 포함)
- 서버(API·관리자 화면)는 milestone-crm 저장소에서 별도 구현.

## 개발 (WSL/리눅스)
    my_env/bin/python -m pip install -r requirements-dev.txt
    my_env/bin/python -m pytest tests/ -v          # 단위 테스트
    my_env/bin/python scripts/dev_mock_crm.py      # 가짜 CRM (hong/1234)
    TM_ADB=/bin/true my_env/bin/python main.py     # 앱 실행(발신은 no-op)

## Windows 빌드
1. Python 3.12 설치, Android platform-tools에서 `build/adb/`에 adb.exe + DLL 2종 복사
2. `build\build.bat` 실행 → `dist\TM다이얼러.exe`

## 배포 전 확인
- `state.py`의 `DEFAULT_SERVER_URL`을 실제 CRM 주소로 변경
- `docs/manual-test.md` 체크리스트 통과
```

```bash
git rm auto_call.py
```

- [ ] **Step 5: 최종 회귀 + 커밋**

Run: `my_env/bin/python -m pytest tests/ -v`
Expected: 전체 통과

```bash
git add build/ docs/manual-test.md README.md
git commit -m "build: PyInstaller 단일 exe 파이프라인 + 수동 테스트 문서 + 레거시 제거

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## 통합 확인 (Codex 서버 완성 후 — 계획 외 후속 작업)

Codex의 `/api/v1` 구현이 milestone-crm dev 서버(PGlite)에 올라오면:

1. `TM_SERVER_URL=http://localhost:3000 my_env/bin/python main.py`으로 실서버 로그인부터 전체 사이클 확인 (`docs/manual-test.md`의 "(실서버)" 항목 포함).
2. 계약 불일치 발견 시: 설계서 3장을 기준으로 어느 쪽이 어긋났는지 판단 → 클라이언트 잘못이면 수정, 서버 잘못이면 사용자에게 보고.
3. 통과 후 Windows에서 exe 빌드 → 실제 폰으로 발신 스모크 테스트.
```
