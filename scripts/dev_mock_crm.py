"""GUI 수동 테스트용 가짜 CRM. 사용: my_env/bin/python scripts/dev_mock_crm.py
로그인: hong / 1234. 앱 실행 시 TM_SERVER_URL=http://127.0.0.1:3002 로 지정할 것."""
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
    for lid in LEADS:
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
