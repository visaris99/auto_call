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

    def check_version(self) -> dict | None:
        """서버 미구현(404 포함) 등 어떤 오류든 None — 버전 안내는 선택 기능."""
        try:
            return self._request("GET", "/version", auth=False)
        except ApiError:
            return None
