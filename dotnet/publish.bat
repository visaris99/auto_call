@echo off
setlocal
REM ============================================================
REM  Milestone Dialer (C#/WPF) - Windows publish script
REM  Prerequisites:
REM    1) .NET 8 SDK installed  (https://dotnet.microsoft.com/download/dotnet/8.0)
REM    2) Copy adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll into ..\build\adb\
REM    3) (optional) Inno Setup 6 for setup.exe (https://jrsoftware.org/isinfo.php)
REM  Output: ..\dist_dotnet\milestone_dialer\milestone_dialer.exe
REM          ..\dist_dotnet\milestone_dialer_setup_<version>.exe  (Inno Setup 있을 때)
REM ============================================================

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~0,2%"=="\\" goto err_unc
cd /d "%SCRIPT_DIR%"

REM dotnet을 PATH에서 찾고, 없으면 기본 설치 경로에서 직접 사용
REM (SDK 설치 직후에는 기존 창의 PATH가 갱신되지 않아 못 찾는 경우가 흔함)
set "DOTNET=dotnet"
where dotnet >nul 2>nul
if errorlevel 1 (
  if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
  ) else if exist "%LocalAppData%\Microsoft\dotnet\dotnet.exe" (
    set "DOTNET=%LocalAppData%\Microsoft\dotnet\dotnet.exe"
  ) else (
    goto err_dotnet
  )
)

echo [1/3] Publishing (self-contained, win-x64)...
"%DOTNET%" publish App -c Release -r win-x64 --self-contained true -o ..\dist_dotnet\milestone_dialer
if errorlevel 1 goto err_build

echo [2/3] Copying adb...
if exist ..\build\adb\adb.exe (
  if not exist ..\dist_dotnet\milestone_dialer\adb mkdir ..\dist_dotnet\milestone_dialer\adb
  copy /Y ..\build\adb\*.* ..\dist_dotnet\milestone_dialer\adb\ >nul
) else (
  echo [WARN] ..\build\adb\ has no adb.exe.
  echo        Copy adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll into
  echo        dist_dotnet\milestone_dialer\adb\ before distributing.
)

echo [3/3] Building installer (Inno Setup)...
REM App\Ui.cs의 Version 상수를 읽어 인스톨러 버전으로 사용
set "APPVER=0.0.0"
for /f tokens^=2^ delims^=^" %%v in ('findstr /C:"public const string Version" App\Ui.cs') do set "APPVER=%%v"

set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" (
  "%ISCC%" /Qp "/DMyAppVersion=%APPVER%" installer.iss
  if errorlevel 1 goto err_installer
  echo.
  echo [OK] dist_dotnet\milestone_dialer_setup_%APPVER%.exe
  echo      Distribute this single setup.exe to employees.
  echo      (installs to user AppData, adds desktop shortcut, no admin needed)
) else (
  echo [SKIP] Inno Setup 6 not found - https://jrsoftware.org/isinfo.php
  echo.
  echo [OK] dist_dotnet\milestone_dialer\milestone_dialer.exe
  echo      Distribute the whole "milestone_dialer" folder to employees.
)
pause
exit /b 0

:err_unc
echo.
echo [ERROR] Cannot run from a network path (\\wsl$ etc). Clone to C:\ first.
pause
exit /b 1

:err_dotnet
echo.
echo [ERROR] .NET SDK not found. Install .NET 8 SDK:
echo         https://dotnet.microsoft.com/download/dotnet/8.0
pause
exit /b 1

:err_build
echo.
echo [ERROR] Publish failed. See messages above.
pause
exit /b 1

:err_installer
echo.
echo [ERROR] Installer build failed. See messages above.
echo         You can still distribute the dist_dotnet\milestone_dialer folder.
pause
exit /b 1
