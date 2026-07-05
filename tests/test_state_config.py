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
