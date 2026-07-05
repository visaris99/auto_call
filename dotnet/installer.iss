; ============================================================
;  Milestone Dialer - Inno Setup 스크립트 (setup.exe 생성)
;  publish.bat이 dist_dotnet\milestone_dialer\ 를 만든 뒤 자동 호출한다.
;  버전은 publish.bat이 App\Ui.cs의 Version 상수를 읽어 /DMyAppVersion= 으로 주입
;  (직접 컴파일 시에는 아래 기본값 사용).
; ============================================================

#ifndef MyAppVersion
#define MyAppVersion "2.1.0"
#endif

[Setup]
; AppId는 업그레이드 설치 인식용 고정 GUID — 절대 바꾸지 말 것
AppId={{B5117FF1-22C3-4F91-BA1E-78F55C78CCD7}}
AppName=Milestone Dialer
AppVersion={#MyAppVersion}
AppPublisher=Milestone Invest
; 관리자 권한 없이 직원 계정으로 설치 가능하도록 사용자 폴더에 설치
DefaultDirName={localappdata}\MilestoneDialer\app
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=yes
CloseApplications=yes
OutputDir=..\dist_dotnet
OutputBaseFilename=milestone_dialer_setup_{#MyAppVersion}
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\milestone_dialer.exe
Compression=lzma2
SolidCompression=yes

; Inno Setup 기본 설치에는 한국어(비공식 번역)가 없다.
; 한국어 설치 화면을 원하면 Korean.isl을 받아 Inno Setup의 Languages 폴더에 넣으면 자동 적용.
; https://jrsoftware.org/files/istrans/
#if FileExists(CompilerPath + "\Languages\Korean.isl")
[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
#endif

[Files]
Source: "..\dist_dotnet\milestone_dialer\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{userdesktop}\마일스톤 다이얼러"; Filename: "{app}\milestone_dialer.exe"
Name: "{userprograms}\마일스톤 다이얼러"; Filename: "{app}\milestone_dialer.exe"

[Run]
Filename: "{app}\milestone_dialer.exe"; Description: "설치 후 바로 실행"; Flags: nowait postinstall skipifsilent

; 참고: 로그인 설정(%APPDATA%\MilestoneDialer\config.json)과 재전송 큐는
; 프로그램 제거 시에도 남긴다(재설치 시 서버주소·아이디 유지).
