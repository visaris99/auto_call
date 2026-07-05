# Milestone Dialer

milestone-crm과 연동되는 TM 상담원용 전화 프로그램.
CRM 계정으로 로그인 → 배정된 리드 큐 → USB 연결 안드로이드 폰으로 발신(ADB) → 결과는 CRM에 즉시 기록.

- **현재 버전: C# (.NET 8 WPF)** — `dotnet/` (customtkinter 렉 문제로 파이썬에서 포팅)
- 설계서: `docs/superpowers/specs/2026-07-05-tm-dialer-crm-integration-design.md` (API 계약 포함)
- 포팅 계획: `docs/superpowers/plans/2026-07-05-dotnet-port.md`
- 서버(API·관리자 화면)는 milestone-crm 저장소에서 별도 구현.

## Windows 빌드·배포 (C#)

1. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 설치
2. [platform-tools](https://developer.android.com/tools/releases/platform-tools)에서 `adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll`을 `build\adb\`에 복사
3. (권장) [Inno Setup 6](https://jrsoftware.org/isinfo.php) 설치 — setup.exe 인스톨러 자동 생성용.
   한국어 설치 화면을 원하면 [Korean.isl](https://jrsoftware.org/files/istrans/)을 Inno Setup의 `Languages\`에 복사
4. `dotnet\publish.bat` 실행
   - Inno Setup 있음 → `dist_dotnet\milestone_dialer_setup_<버전>.exe` 생성. **이 파일 하나만 직원에게 배포**
     (더블클릭 설치 → 바탕화면 "마일스톤 다이얼러" 바로가기 생성. 관리자 권한 불필요, 새 버전은 같은 setup 재실행으로 덮어씀)
   - Inno Setup 없음 → 기존처럼 `dist_dotnet\milestone_dialer\` 폴더 통째로 배포
   - 인스톨러 버전은 `App\Ui.cs`의 `Version` 상수에서 자동으로 가져옴

처음 실행 시 SmartScreen 경고("PC 보호")가 뜨면 **더 알아보기 → 실행**을 안내할 것 (미서명 내부 배포 프로그램이라 정상).

폰트: PC에 나눔고딕이 설치돼 있으면 자동 사용, 없으면 맑은 고딕.

## 개발 (WSL/리눅스)

    export PATH=$HOME/.dotnet:$PATH
    dotnet test dotnet/Tests                    # Core 단위 테스트 40개
    dotnet build dotnet/App                     # WPF는 빌드 검증만 가능(실행은 Windows)

    # 실서버 통합 테스트 (dev CRM 필요)
    TM_ITEST_URL=http://127.0.0.1:3005 dotnet test dotnet/Tests --filter RealCrm

환경변수: `TM_SERVER_URL`(서버 주소 강제), `TM_ADB`(adb 바이너리 경로 강제).
설정·재전송 큐: `%APPDATA%\MilestoneDialer\`

## 파이썬 버전 (레거시 폴백)

C# 버전 검증 완료 전까지 유지. 실행·빌드 방법:

    my_env/bin/python -m pytest tests/ -v
    my_env/bin/python scripts/dev_mock_crm.py      # 가짜 CRM (hong/1234, :3002)
    TM_SERVER_URL=http://127.0.0.1:3002 TM_ADB=/bin/true my_env/bin/python main.py
    # Windows 단일 exe: build\build.bat

## 수동 테스트

`docs/manual-test.md` 체크리스트 (C#/파이썬 공통 — UI 동작 기준 동일).
