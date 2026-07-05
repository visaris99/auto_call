"""번들 리소스 경로 해석 + (Windows) 동봉 폰트 로드."""
from __future__ import annotations

import os
import sys


def resource_path(rel: str) -> str:
    """개발 시 저장소 루트, exe 실행 시 PyInstaller 임시폴더 기준 경로."""
    base = getattr(sys, "_MEIPASS", os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(base, rel)


def load_private_fonts() -> None:
    """assets/fonts/*.ttf 를 이 프로세스 전용 폰트로 등록 (Windows 전용).

    나눔고딕 등 TTF를 assets/fonts/에 넣고 빌드하면 직원 PC에 폰트 설치 없이 적용된다.
    실패해도 앱 동작에는 지장 없으므로 조용히 무시한다.
    """
    if sys.platform != "win32":
        return
    import ctypes
    import glob

    FR_PRIVATE = 0x10
    for path in glob.glob(resource_path(os.path.join("assets", "fonts", "*.ttf"))):
        try:
            ctypes.windll.gdi32.AddFontResourceExW(path, FR_PRIVATE, 0)
        except OSError:
            pass
