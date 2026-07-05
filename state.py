"""로컬 설정과 재전송 큐 — %APPDATA%\\TMDialer (리눅스 개발: ~/.config/TMDialer)."""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path

DEFAULT_SERVER_URL = "https://crm.milestone-sales.xyz"  # 운영 CRM (개발 시 TM_SERVER_URL로 덮어씀)


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
