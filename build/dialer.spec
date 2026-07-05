# -*- mode: python ; coding: utf-8 -*-
# PyInstaller onefile 스펙 — Windows에서 build.bat로 실행.
# 사전 준비: Android platform-tools에서 build/adb/ 에 아래 3개 파일 복사
#   adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll  (저장소에는 커밋하지 않음)
import os

HERE = os.path.dirname(os.path.abspath(SPEC))
ROOT = os.path.dirname(HERE)
ADB_DIR = os.path.join(HERE, "adb")

datas = [
    (os.path.join(ADB_DIR, "adb.exe"), "adb"),
    (os.path.join(ADB_DIR, "AdbWinApi.dll"), "adb"),
    (os.path.join(ADB_DIR, "AdbWinUsbApi.dll"), "adb"),
]

a = Analysis(
    [os.path.join(ROOT, "main.py")],
    pathex=[ROOT],
    binaries=[],
    datas=datas,
    hiddenimports=[],
    excludes=["pandas", "numpy", "openpyxl"],
)
pyz = PYZ(a.pure)
exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    name="TM다이얼러",
    console=False,
    upx=False,
)
