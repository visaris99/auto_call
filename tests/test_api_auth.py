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
