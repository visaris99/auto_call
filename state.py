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
