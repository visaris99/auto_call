@echo off
REM TM 다이얼러 Windows 빌드. 사전 준비:
REM   1) Python 3.12+ 설치 (py 런처 포함)
REM   2) build\adb\ 에 adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll 복사
cd /d %~dp0..
if not exist build\adb\adb.exe (
  echo [오류] build\adb\ 에 adb.exe와 DLL 2종을 먼저 복사하세요.
  pause & exit /b 1
)
if not exist .venv (py -3 -m venv .venv)
call .venv\Scripts\activate.bat
python -m pip install --upgrade pip
python -m pip install -r requirements.txt pyinstaller
pyinstaller build\dialer.spec --noconfirm --distpath dist --workpath build\out
echo.
echo [완료] dist\TM다이얼러.exe
pause
