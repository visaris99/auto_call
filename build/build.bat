@echo off
setlocal
REM ============================================================
REM  TM Dialer - Windows build script
REM  Prerequisites:
REM    1) Python 3.12+ installed (python.org, check "Add to PATH")
REM    2) Copy adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll
REM       into the build\adb\ folder
REM  NOTE: Run this from a LOCAL drive (C:\...), not from \\wsl$
REM ============================================================

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~0,2%"=="\\" goto err_unc

cd /d "%SCRIPT_DIR%.."
if errorlevel 1 goto err_cd

where py >nul 2>nul
if errorlevel 1 goto err_python

if not exist "build\adb\adb.exe" goto err_adb
if not exist "build\adb\AdbWinApi.dll" goto err_adb
if not exist "build\adb\AdbWinUsbApi.dll" goto err_adb

echo [1/3] Preparing virtualenv...
if not exist ".venv" py -3 -m venv .venv
if not exist ".venv\Scripts\activate.bat" goto err_venv
call ".venv\Scripts\activate.bat"

echo [2/3] Installing packages...
python -m pip install --upgrade pip
python -m pip install -r requirements.txt pyinstaller
if errorlevel 1 goto err_pip

echo [3/3] Building exe...
pyinstaller build\dialer.spec --noconfirm --distpath dist --workpath build\out
if errorlevel 1 goto err_build

echo.
echo [OK] Done! The exe file is in the "dist" folder.
pause
exit /b 0

:err_unc
echo.
echo [ERROR] This script cannot run from a network path (\\wsl$ / \\wsl.localhost).
echo Copy the project to a local drive first. Two easy options:
echo   A) git clone https://github.com/visaris99/auto_call C:\work\auto_call
echo   B) GitHub - green "Code" button - "Download ZIP" - extract to C:\work
echo Then run build\build.bat from there.
pause
exit /b 1

:err_cd
echo.
echo [ERROR] Could not change to the project folder.
pause
exit /b 1

:err_python
echo.
echo [ERROR] Python not found (the "py" launcher is missing).
echo Install Python from https://www.python.org/downloads/
echo (check "Add python.exe to PATH" during install), then run again.
pause
exit /b 1

:err_adb
echo.
echo [ERROR] Missing adb files in build\adb\
echo Download platform-tools:
echo   https://developer.android.com/tools/releases/platform-tools
echo Extract and copy these 3 files into build\adb\ :
echo   adb.exe  AdbWinApi.dll  AdbWinUsbApi.dll
pause
exit /b 1

:err_venv
echo.
echo [ERROR] Failed to create the virtualenv (.venv).
echo Delete the .venv folder and run again.
pause
exit /b 1

:err_pip
echo.
echo [ERROR] Package install failed. Check your internet connection.
pause
exit /b 1

:err_build
echo.
echo [ERROR] PyInstaller build failed. See the messages above.
pause
exit /b 1
