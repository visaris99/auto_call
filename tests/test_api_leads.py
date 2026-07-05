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


def test_check_version_returns_none_when_missing(crm, client):
    assert client.check_version() is None  # 라우트 없음 → 404 → None


def test_check_version_returns_payload(crm, client):
    crm.routes[("GET", "/api/v1/version")] = (
        200, {"minVersion": "2.0.0", "latestVersion": "2.1.0", "downloadUrl": None})
    assert client.check_version()["latestVersion"] == "2.1.0"
