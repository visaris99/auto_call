# Milestone Dialer C#(.NET 8 WPF) 포팅 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> (이 프로젝트는 사용자 지시로 서브에이전트 없이 인라인 실행한다)

**Goal:** customtkinter 렉 문제를 해결하기 위해 파이썬 다이얼러를 .NET 8 WPF 네이티브 앱으로 포팅한다 (기능 동일, 독립 프로그램 형태 유지).

**Architecture:** 3-프로젝트 솔루션 — `Core`(net8.0, GUI 무관 로직: API 클라이언트/재전송 큐/설정/ADB/큐 정렬), `App`(net8.0-windows WPF, 코드비하인드 방식), `Tests`(xunit, Linux에서 실행 가능). 행위 스펙 = 저장소의 파이썬 구현(`api.py`, `state.py`, `adb.py`, `logic.py`, `ui/*`)과 설계서 3장 API 계약. WPF는 Linux에서 `EnableWindowsTargeting`으로 **빌드만** 검증하고 GUI 실행 확인은 Windows에서 사용자가 한다.

**Tech Stack:** .NET 8 LTS, WPF(코드비하인드), System.Text.Json, xunit. 외부 NuGet 의존성 없음(테스트 제외).

## Global Constraints

- 서버 계약은 설계서 3장 그대로: Bearer 토큰, 에러 포맷 `{error:{code,message}}`, `Idempotency-Key` uuid4 필수, 결과코드 7종.
- 기본 서버 주소 `https://crm.milestone-sales.xyz`, 환경변수 `TM_SERVER_URL`로 덮어씀.
- 설정·재전송 큐 저장 위치: `%APPDATA%\MilestoneDialer\` (Linux 테스트 시 `XDG_CONFIG_HOME` 또는 홈 하위). **평문 전화번호는 디스크에 저장 금지.**
- ADB 경로 우선순위: env `TM_ADB` → `AppContext.BaseDirectory/adb/adb.exe` → `"adb"`.
- UI 문구 한국어, 이모지 금지, 폰트 `나눔고딕 → Malgun Gothic` 폴백, 색상은 `ui/theme.py`의 CRM 토큰과 동일 hex.
- 비밀번호·MFA 입력은 ASCII만(WPF PasswordBox는 IME를 원천 차단하므로 자연 해결), 아이디는 한글 허용.
- exe 이름 `milestone_dialer`, 아이콘 `assets/icon.ico`, 로그인 화면에 `assets/milestone_logo.png`.
- 빌드/테스트 명령: `~/.dotnet/dotnet` (Linux). 커밋 규칙: conventional commit + `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- 파이썬 구현은 C# 검증 완료 전까지 삭제하지 않는다.

## 파일 구조 (최종)

```
dotnet/
  MilestoneDialer.sln
  Core/
    Core.csproj                # net8.0
    ApiException.cs            # ApiException + Network/Auth/MfaRequired/NightBlocked
    ApiModels.cs               # UserInfo, LeadItem, QueueResponse, CallResponse, VersionInfo ...
    ApiClient.cs               # HttpClient 기반, 설계서 3장 구현
    QueueLogic.cs              # ParseIso/IsCallbackDue/SortQueue/FormatSeconds/CallbackIso/AsciiOnly
    AppConfig.cs               # config.json 로드/저장 (+TM_SERVER_URL)
    PendingCallQueue.cs        # pending_calls.json, Flush 의미론은 파이썬과 동일
    AdbController.cs           # Process 실행 (CreateNoWindow)
  App/
    App.csproj                 # net8.0-windows, UseWPF, AssemblyName=milestone_dialer
    App.xaml / App.xaml.cs     # 리소스 로드, 전역 예외 → error_log.txt
    Theme.xaml                 # CRM 색 토큰 브러시 + 공용 스타일
    LoginWindow.xaml(.cs)      # 로고, 아이디/비밀번호/MFA, 서버주소 설정
    MainWindow.xaml(.cs)       # 상태바/큐 ListBox/고객카드/통화/결과기록
  Tests/
    Tests.csproj               # xunit, net8.0 (Linux 실행)
    MockCrm.cs                 # HttpListener 가짜 CRM (파이썬 conftest.MockCRM 대응)
    ApiClientTests.cs
    QueueLogicTests.cs
    AppConfigTests.cs
    PendingCallQueueTests.cs
    AdbControllerTests.cs
  publish.bat                  # Windows: self-contained 배포 폴더 생성 + adb 복사
```

---

### Task 1: 솔루션 스캐폴딩 + 예외/모델 + ApiClient 인증 (login/logout/me)

**Files:**
- Create: `dotnet/MilestoneDialer.sln`, `dotnet/Core/Core.csproj`, `dotnet/Tests/Tests.csproj`, `dotnet/Core/ApiException.cs`, `dotnet/Core/ApiModels.cs`, `dotnet/Core/ApiClient.cs`, `dotnet/Tests/MockCrm.cs`, `dotnet/Tests/ApiClientTests.cs`

**Interfaces (Produces):**
- 예외: `ApiException(string code, string message, int httpStatus)` / `NetworkException` / `AuthException` / `MfaRequiredException` / `NightBlockedException`
- `ApiClient(string baseUrl, TimeSpan? timeout = null)` — `BaseUrl {get;set;}`, `User {get;}`, `IsAuthenticated {get;}`, `Task<UserInfo> LoginAsync(loginId, password, code=null)`, `Task LogoutAsync()`, `Task<UserInfo> MeAsync()`
- `MockCrm : IDisposable` — `Url`, `Routes[(method, path)] = (status, jsonBody) | Func<...>`, `Requests` 기록
- 모델 레코드(camelCase JSON): `UserInfo(Id, LoginId, Name, OrgName, Roles, MustChangePassword)`, `LoginResponse(Token, ExpiresAt, User)`

- [ ] **Step 1: 스캐폴딩** — `dotnet new sln/classlib/xunit`, 프로젝트 참조(Tests→Core), .gitignore에 `dotnet/**/bin/ obj/ dist_dotnet/` 추가
- [ ] **Step 2: MockCrm.cs 작성** — HttpListener(포트 0 → 실제 포트 조회), 요청 기록, 라우트 매칭(쿼리스트링 제거), 고정/동적 응답, 204 지원 (파이썬 conftest.MockCRM과 동일 의미론)
- [ ] **Step 3: 인증 테스트 작성** (파이썬 `test_api_auth.py`의 9케이스 이식):
  로그인 성공(토큰·유저·요청바디 code 미포함), MFA 코드 포함, `MFA_REQUIRED`→MfaRequiredException, `INVALID_CREDENTIALS`→ApiException(단 AuthException 아님), Bearer 헤더 전송, `UNAUTHENTICATED`→AuthException, 미로그인 요청→AuthException, 로그아웃 후 토큰 제거, 연결거부→NetworkException
- [ ] **Step 4: 실패 확인** — `~/.dotnet/dotnet test dotnet` → 컴파일 에러(ApiClient 미구현)
- [ ] **Step 5: ApiException/ApiModels/ApiClient 구현** — HttpClient, JsonSerializerOptions(camelCase), `_RequestAsync(method, path, body, headers, auth)` 공통부: 204→null, 에러 JSON 파싱→코드별 예외 매핑(파이썬 `_error_class`와 동일), HttpRequestException/Timeout→NetworkException
- [ ] **Step 6: 통과 확인** — `dotnet test` 9 passed
- [ ] **Step 7: 커밋** `feat(dotnet): Core ApiClient 인증 + MockCrm 테스트 기반`

### Task 2: ApiClient 리드·콜

**Files:** Modify `dotnet/Core/ApiClient.cs`, `dotnet/Core/ApiModels.cs`; Create `dotnet/Tests/ApiClientLeadsTests.cs`

**Interfaces (Produces):** `Task<List<LeadItem>> QueueAsync(int limit=50)`, `Task<string> RevealAsync(leadId, reason="TM 발신")`, `Task<CallResponse> LogCallAsync(leadId, resultCode, talkSeconds, memo, callbackAt, idempotencyKey)`, `Task<VersionInfo?> CheckVersionAsync()`
모델: `LeadItem(Id, Name, PhoneMasked, Status, NextCallAt, Memo, UpdatedAt)`, `CallResponse(Ok, Lead: CallLead(Id, Status, NextCallAt))`, `VersionInfo(MinVersion, LatestVersion, DownloadUrl)`

- [ ] 파이썬 `test_api_leads.py` 6케이스 이식(메모 제외): queue limit·Bearer, reveal reason·전화번호, LogCall의 Idempotency-Key 헤더+camelCase 바디+응답, NIGHT_BLOCKED→NightBlockedException, version 404→null, version 정상
- [ ] 실패 확인 → 구현 → `dotnet test` 통과 → 커밋 `feat(dotnet): ApiClient 리드 큐·reveal·콜 기록(멱등키)·버전`

### Task 3: QueueLogic

**Files:** Create `dotnet/Core/QueueLogic.cs`, `dotnet/Tests/QueueLogicTests.cs`

**Interfaces (Produces):** `static class QueueLogic` — `DateTimeOffset? ParseIso(string?)`, `bool IsCallbackDue(LeadItem, DateTimeOffset now)`, `List<LeadItem> SortQueue(IEnumerable<LeadItem>, DateTimeOffset now)`(도래 콜백 오래된순 우선, 나머지 원순서), `string FormatSeconds(int)`("MM:SS"/"H:MM:SS"), `string? CallbackIso(string hhmm, DateTimeOffset now)`(지난 시각→내일, 형식오류 null), `string AsciiOnly(string)`(0x20~0x7E만)

- [ ] 파이썬 `test_logic.py` 8케이스 이식 → 실패 → 구현 → 통과 → 커밋 `feat(dotnet): 큐 정렬·콜백·시간 포맷·ASCII 필터`

### Task 4: AppConfig

**Files:** Create `dotnet/Core/AppConfig.cs`, `dotnet/Tests/AppConfigTests.cs`

**Interfaces (Produces):** `AppConfig` — `string ServerUrl`(기본 `https://crm.milestone-sales.xyz`), `string LastLoginId`, `static AppConfig Load(string? path=null)`(TM_SERVER_URL 덮어씀, 손상 파일→기본값), `void Save(string? path=null)`, `static string ConfigDir()`(win: APPDATA, 그 외 XDG_CONFIG_HOME/홈, 하위 `MilestoneDialer` 생성)

- [ ] 파이썬 `test_state_config.py` 5케이스 이식(경로 파라미터로 tmp 사용) → TDD 사이클 → 커밋 `feat(dotnet): 로컬 설정 저장`

### Task 5: PendingCallQueue

**Files:** Create `dotnet/Core/PendingCallQueue.cs`, `dotnet/Tests/PendingCallQueueTests.cs`

**Interfaces (Produces):** `record PendingCall(string IdempotencyKey, string LeadId, string ResultCode, int TalkSeconds, string? Memo, string? CallbackAt)`; `PendingCallQueue(string? path=null)` — `Add(PendingCall)`, `IReadOnlyList<PendingCall> Items`, `Remove(string key)`, `Task<(int Sent,int Remaining)> FlushAsync(ApiClient)`. Flush 의미론(파이썬 동일): Network/NightBlocked→중단, Auth→throw, 기타 ApiException→해당 건 폐기, 성공→제거. 원자적 저장(tmp→rename).
- FlushAsync가 ApiClient 대신 `Func<PendingCall, Task>` 전송자를 받도록 해 테스트에서 가짜 주입 (`FlushAsync(Func<PendingCall,Task> sender)` + ApiClient 편의 오버로드).

- [ ] 파이썬 `test_state_pending.py` 5케이스 이식 → TDD → 커밋 `feat(dotnet): 재전송 큐(멱등키, 평문 미저장)`

### Task 6: AdbController

**Files:** Create `dotnet/Core/AdbController.cs`, `dotnet/Tests/AdbControllerTests.cs`

**Interfaces (Produces):** `static class AdbController` — `string AdbPath()`, `bool Call(string phone)`, `bool Hangup()`, `bool IsConnected()`. Process 실행: `CreateNoWindow=true, UseShellExecute=false`, 타임아웃 10초(devices 5초), 실패/미존재→false.

- [ ] 파이썬 `test_adb.py` 6케이스 이식 (fake sh 스크립트 + TM_ADB/TM_ADB_LOG/TM_ADB_DEVICES/TM_ADB_EXIT env) → TDD → 커밋 `feat(dotnet): ADB 발신·종료·연결감지`

### Task 7: App 프로젝트 + Theme.xaml (빌드 검증)

**Files:** Create `dotnet/App/App.csproj`, `dotnet/App/App.xaml(.cs)`, `dotnet/App/Theme.xaml`

- App.csproj: `<TargetFramework>net8.0-windows</TargetFramework> <UseWPF>true</UseWPF> <EnableWindowsTargeting>true</EnableWindowsTargeting> <AssemblyName>milestone_dialer</AssemblyName> <ApplicationIcon>../../assets/icon.ico</ApplicationIcon>`, assets(logo png) Content 복사, Core 참조.
- Theme.xaml: `ui/theme.py` COLORS와 동일 hex의 SolidColorBrush 리소스(Background/Surface/Surface2/Ink/Foreground/Line/Hover/Brand/BrandSoft/Gold/Danger/DangerSoft/Success/SuccessSoft/Muted) + `FontFamily "나눔고딕, Malgun Gothic"` + 스타일: `Card`(Border 12px 라운드), `PrimaryBtn/SuccessBtn/DangerBtn/GhostBtn`, `FieldLabel`, 상태→브러시 매핑은 코드(`StatusStyle` 헬퍼, badge.tsx statusVariant와 동일).
- App.xaml.cs: DispatcherUnhandledException → `%APPDATA%\MilestoneDialer\error_log.txt` 기록 후 메시지박스.
- [ ] `~/.dotnet/dotnet build dotnet/App` 성공(Linux, 실행은 안 함) → 커밋 `feat(dotnet): WPF 앱 스캐폴딩 + CRM 테마`

### Task 8: LoginWindow

**Files:** Create `dotnet/App/LoginWindow.xaml(.cs)`

- 레이아웃: 중앙 카드(로고 Image 160px, "CRM 계정으로 로그인하세요", 아이디 TextBox(한글 허용), 비밀번호 PasswordBox(IME 원천 차단 + `QueueLogic.AsciiOnly` 방어), MFA TextBox(숨김, MfaRequiredException 시 표시), 에러 TextBlock, 로그인 Button, 서버 주소 설정 링크버튼).
- 동작: LoginAsync await → MustChangePassword→경고 후 중단 / 성공→LastLoginId 저장, MainWindow 열고 닫기. Enter 제출. 버튼 비활성 "확인 중…".
- [ ] 빌드 성공 → 커밋 `feat(dotnet): 로그인 화면`

### Task 9: MainWindow

**Files:** Create `dotnet/App/MainWindow.xaml(.cs)`

- 레이아웃: 상단바(소속·이름/오늘: 발신·가입/전송대기 배너/ADB·CRM 상태점), 좌측 큐 `ListBox`(가상화 기본, ItemTemplate: 이름+상태배지+콜백시간, 도래분 BrandSoft 배경), 우측 고객카드(이름/상태배지/마스킹번호/리드메모 읽기전용) + 통화 컨트롤(발신 F1·종료 F2·타이머) + 결과 기록(7종 ToggleButton 1~7키, 메모 TextBox, 콜백 HH:MM TextBox는 CALLBACK 선택 시 표시, 저장 F3).
- 동작(파이썬 workspace.py와 동일 의미론): 큐 60초 폴링·수동 새로고침, ADB 5초, 재전송 flush 30초, 타이머 1초 — 전부 DispatcherTimer + async/await. 발신=Reveal→Adb.Call(백그라운드 Task.Run), 통화중 리드전환 금지, 저장 성공→다음 리드, NetworkException→PendingCallQueue.Add+배너, AuthException→재로그인(LoginWindow 다시 열기), NightBlocked→경고. 콜백 도래 토스트는 상태바 텍스트 플래시로 대체(간소화).
- [ ] 빌드 성공 → 커밋 `feat(dotnet): 메인 워크스페이스`

### Task 10: publish.bat + 문서

**Files:** Create `dotnet/publish.bat`; Modify `README.md`, `docs/manual-test.md`

- publish.bat(로컬 드라이브 가드 포함): `dotnet publish App -c Release -r win-x64 --self-contained true -o ..\dist_dotnet\milestone_dialer` 후 `build\adb\*`를 `dist_dotnet\milestone_dialer\adb\`로 복사. .NET SDK 미설치 시 안내(ASCII 메시지).
- README: C# 버전이 기본, 빌드·배포 절차, 파이썬 버전은 폴백으로 유지 중임을 명시. manual-test.md에 "(C#)" 관점 갱신은 체크리스트 동일하므로 경로만 수정.
- [ ] Linux에서 `dotnet build` 전체 성공 + `dotnet test` 전체 통과 → 커밋 `build(dotnet): self-contained 배포 스크립트 + 문서`

### 통합 확인 (계획 외 후속)

1. Linux: Core 테스트를 실서버 dev CRM(PGlite, :3005)에 붙여 파이썬 때와 동일한 10항목 통합 스크립트(C# 콘솔 or 기존 파이썬 스크립트로 서버만 검증) 확인.
2. Windows(사용자): `dotnet\publish.bat` → `dist_dotnet\milestone_dialer\milestone_dialer.exe` 실행 → `docs/manual-test.md` 체크리스트 + **렉 체감 확인이 핵심**.
3. 렉 해소 확인 후 파이썬 구현 제거 여부 결정.

## Self-Review 메모

- 스펙 커버리지: 파이썬 기능 전부 매핑됨. 제외: 리드 메모 편집(사용자 결정으로 이미 제거), Toast 위젯(상태바 플래시로 간소화 — 명시함), save_memo API(사용 안 함).
- 타입 일관성: LogCallAsync 시그니처가 Task 2 정의와 Task 5 FlushAsync 사용처, Task 9 사용처에서 동일해야 함 — PendingCall 필드명과 LogCallAsync 파라미터 매핑 주의.
- WPF 코드는 빌드 검증만 가능(Linux) — 런타임 버그 리스크는 Windows 수동 테스트로 흡수, 로직은 최대한 Core로 내려 테스트 커버.
