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
