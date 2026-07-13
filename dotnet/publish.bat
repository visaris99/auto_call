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

REM Ui.Version을 앱 PE와 인스톨러의 단일 릴리스 버전으로 사용
set "APPVER=0.0.0"
for /f tokens^=2^ delims^=^" %%v in ('findstr /C:"public const string Version" App\Ui.cs') do set "APPVER=%%v"
if "%APPVER%"=="0.0.0" goto err_version

echo [1/3] Publishing (self-contained, win-x64)...
"%DOTNET%" publish App -c Release -r win-x64 --self-contained true -o ..\dist_dotnet\milestone_dialer -p:Version=%APPVER% -p:FileVersion=%APPVER%.0 -p:AssemblyVersion=%APPVER%.0 -p:InformationalVersion=%APPVER%
if errorlevel 1 goto err_build

echo [2/3] Copying adb...
if not exist ..\build\adb\adb.exe goto err_adb
if not exist ..\build\adb\AdbWinApi.dll goto err_adb
if not exist ..\build\adb\AdbWinUsbApi.dll goto err_adb
if not exist ..\dist_dotnet\milestone_dialer\adb mkdir ..\dist_dotnet\milestone_dialer\adb
copy /Y ..\build\adb\*.* ..\dist_dotnet\milestone_dialer\adb\ >nul

echo [3/3] Building installer (Inno Setup)...
if not defined ISCC set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" for /f "delims=" %%i in ('where ISCC.exe 2^>nul') do set "ISCC=%%i"
if exist "%ISCC%" (
  "%ISCC%" /Qp "/DMyAppVersion=%APPVER%" installer.iss
  if errorlevel 1 goto err_installer
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%normalize-setup-metadata.ps1" -SetupPath "%SCRIPT_DIR%..\dist_dotnet\milestone_dialer_setup_%APPVER%.exe" -Version "%APPVER%"
  if errorlevel 1 goto err_metadata
  echo.
  echo [OK] dist_dotnet\milestone_dialer_setup_%APPVER%.exe
  echo      Distribute this single setup.exe to employees.
  echo      Installs to user AppData, adds desktop shortcut, no admin needed.
) else (
  echo [SKIP] Inno Setup 6 not found - https://jrsoftware.org/isinfo.php
  echo.
  echo [OK] dist_dotnet\milestone_dialer\milestone_dialer.exe
  echo      Distribute the whole "milestone_dialer" folder to employees.
)
exit /b 0

:err_unc
echo.
echo [ERROR] Cannot run from a network path (\\wsl$ etc). Clone to C:\ first.
exit /b 1

:err_dotnet
echo.
echo [ERROR] .NET SDK not found. Install .NET 8 SDK:
echo         https://dotnet.microsoft.com/download/dotnet/8.0
exit /b 1

:err_version
echo.
echo [ERROR] App\Ui.cs Version could not be parsed.
exit /b 1

:err_build
echo.
echo [ERROR] Publish failed. See messages above.
exit /b 1

:err_adb
echo.
echo [ERROR] Required ADB runtime is missing.
echo         Copy adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll into ..\build\adb\
echo         and run publish.bat again. No installer was produced.
exit /b 1

:err_installer
echo.
echo [ERROR] Installer build failed. See messages above.
echo         You can still distribute the dist_dotnet\milestone_dialer folder.
exit /b 1

:err_metadata
echo.
echo [ERROR] Setup version metadata normalization failed.
echo         Do not distribute this installer.
exit /b 1
