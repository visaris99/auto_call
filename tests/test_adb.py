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
    monkeypatch.setenv("TM_ADB_DEVICES", "R3CN123\tdevice\n")
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
