@echo off
chcp 65001 >nul
REM TM 다이얼러 Windows 빌드. 사전 준비:
REM   1) Python 3.12+ 설치 (설치 시 "Add python.exe to PATH" + py 런처 포함)
REM   2) build\adb\ 에 adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll 복사

REM ── \\wsl$ 같은 네트워크 경로에서는 빌드 불가 → 로컬 디스크로 클론 안내
echo %~dp0 | findstr /b /c:"\\\\" >nul
if not errorlevel 1 (
  echo [오류] 네트워크 경로(\\wsl$ 등)에서는 빌드할 수 없습니다.
  echo        C: 드라이브 등 로컬 폴더에 git clone 한 뒤 다시 실행하세요.
  echo        예) git clone https://github.com/visaris99/auto_call C:\work\auto_call
  pause
  exit /b 1
)

cd /d %~dp0..

REM ── Python 확인
where py >nul 2>nul
if errorlevel 1 (
  echo [오류] Python이 설치되어 있지 않습니다 ('py' 런처를 찾을 수 없음^).
  echo        https://www.python.org/downloads/ 에서 설치 후 다시 실행하세요.
  pause
  exit /b 1
)

REM ── adb 동봉 파일 확인
if not exist build\adb\adb.exe (
  echo [오류] build\adb\ 에 adb.exe와 DLL 2종을 먼저 복사하세요.
  echo        https://developer.android.com/tools/releases/platform-tools 에서 받아
  echo        adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll 3개를 build\adb\ 에 넣으면 됩니다.
  pause
  exit /b 1
)

echo [1/3] 가상환경 준비...
if not exist .venv (py -3 -m venv .venv)
call .venv\Scripts\activate.bat
if errorlevel 1 (
  echo [오류] 가상환경 활성화 실패. .venv 폴더를 지우고 다시 실행해보세요.
  pause
  exit /b 1
)

echo [2/3] 패키지 설치...
python -m pip install --upgrade pip
python -m pip install -r requirements.txt pyinstaller
if errorlevel 1 (
  echo [오류] 패키지 설치 실패. 인터넷 연결을 확인하세요.
  pause
  exit /b 1
)

echo [3/3] exe 빌드...
pyinstaller build\dialer.spec --noconfirm --distpath dist --workpath build\out
if errorlevel 1 (
  echo [오류] 빌드 실패. 위 오류 메시지를 확인하세요.
  pause
  exit /b 1
)

echo.
echo [완료] dist\TM다이얼러.exe 가 생성되었습니다.
pause
