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
