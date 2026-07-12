# 다이얼러 1차 안전화 구현 결과

작성일: 2026-07-10  
대상: `/home/mirage/office/auto_call` .NET WPF 클라이언트

## 목표

Android Companion 전환 전에 현행 ADB 버전에서 발생할 수 있는 중복 발신, 잘못된 리드 기록, 종료되지 않은 통화 위 저장, 다중 단말 오발신을 우선 차단한다. CRM API 재설계와 Companion 앱은 이번 단계에 포함하지 않는다.

## 구현 결과

### 1. 명시적 통화 상태 머신

`CallSessionCoordinator`를 Core에 추가하고 다음 전이를 강제했다.

`Idle -> Authorizing -> Dialing/Active -> Ending -> Ended -> Saving -> Idle`

- F1, F2, F3의 중복 입력은 상태 전이에서 거부된다.
- 발신 시작 시 `leadId`, ADB `deviceSerial`, UUIDv4 `operationId`를 한 번만 생성해 고정한다.
- API 저장 실패 후 재시도해도 같은 `operationId`를 멱등키로 사용한다.
- 발신 시작부터 저장 완료까지 큐 선택과 단말 선택을 잠근다.
- 저장 payload는 화면의 현재 선택이 아니라 잠긴 세션의 리드 ID를 사용한다.

### 2. 실제 통화 종료 확인

- F2와 통화 중 F3은 선택 단말에 종료 명령을 전송한다.
- `dumpsys telephony.registry`가 `idle`을 반환한 경우에만 `Ended`로 전환한다.
- 종료 확인이 실패하면 저장을 막고 휴대폰에서 종료한 뒤 다시 확인하도록 안내한다.
- 앱 종료 및 세션 만료 때도 진행 중인 통화를 먼저 종료한다.
- 세션 만료 중인 통화 결과는 로컬 대기열에 확정한 뒤 로그인 화면으로 이동한다.
- 미저장 통화 결과가 있으면 창 종료 전에 폐기 여부를 확인한다.

### 3. ADB 다중 단말 안전화

- ADB 실행을 비동기 프로세스 방식으로 바꾸고 stdout/stderr를 동시에 소비한다.
- 명령 시간 제한 시 프로세스 트리를 종료한다.
- `adb devices`에서 `device`, `unauthorized`, `offline` 상태를 구분한다.
- 정상 단말이 여러 개면 UI에서 명시적으로 선택한다.
- 발신, 종료, 상태 확인의 모든 명령에 `-s <serial>`을 적용한다.
- 선택 serial은 `config.json`의 `adb_serial`에 보존된다.

### 4. CRM 큐 및 결과 정합성

- 상태별 5회 큐 요청을 단일 `/leads/queue?limit=500` 요청으로 변경했다.
- 저장 완료 리드와 로컬 전송 대기 리드는 현재 앱 세션의 후보에서 제외한다.
- 저장 직후 서버 큐에 리드가 남아 있어도 같은 리드를 즉시 다시 발신하지 않는다.
- CRM이 지원하지 않는 `INVALID_NUMBER` 결과를 제거해 결과 코드를 10종으로 일치시켰다.

### 5. 정책 우회 기능 중단

별도 서버 정책 검사 API가 없는 다음 기능은 UI와 코드 경로에서 비활성화했다.

- 평문 전화번호 복사
- CRM 기록 없는 수동 발신
- 결과 저장 후 연속 자동 발신

`POST /call-attempts` 계약은 2.4.0에서 구현됐지만, 이 기능들은 실단말 안정화와 별도 운영 승인이 끝날 때까지 비활성 상태를 유지한다. 수동 번호 발신은 리드 기반 정책 검사가 불가능하므로 별도 manual-call 계약 없이는 복구하지 않는다.

## 주요 변경 파일

| 파일 | 변경 내용 |
|---|---|
| `dotnet/Core/CallSessionCoordinator.cs` | 통화 상태와 세션 불변값 관리 |
| `dotnet/Core/AdbController.cs` | 비동기 실행, 장치 열거, serial 고정, idle 확인 |
| `dotnet/Core/AppConfig.cs` | 선택 ADB serial 저장 |
| `dotnet/App/MainWindow.xaml(.cs)` | 상태 기반 UI, 다중 장치 선택, 안전한 종료와 저장 |
| `dotnet/App/Ui.cs` | CRM 결과 코드 10종 정합성 |
| `dotnet/Tests/*` | 상태 전이, 동시 입력, ADB serial 회귀 테스트 |

## 검증

- .NET 단위/Mock API 테스트: 77 passed, 0 failed, 0 skipped
- Python 레거시 회귀 테스트: 43 passed
- WPF Release 빌드: 0 warnings, 0 errors
- 변경 파일 `dotnet format --verify-no-changes`: 통과
- `git diff --check`: 통과
- Linux 환경이므로 WPF 화면 실행과 실제 Android 통화는 수행하지 않았다.
- 실 CRM 통합 테스트는 운영 데이터 변경 위험 때문에 실행하지 않았다.

Windows 실단말 검증 절차는 [manual-test.md](manual-test.md)에 정리했다.

## 남은 한계

- `talkSeconds`는 아직 실제 연결 시점이 아니라 ADB 발신 intent 이후 시간을 측정한다.
- 큐는 최대 500건 단일 페이지이며 cursor 페이지네이션 계약이 없다.
- ADB의 통화 상태는 Android 제조사와 버전에 따라 정확도가 달라질 수 있다.
- 로컬 outbox의 메모는 아직 OS 암호화 저장소로 보호되지 않는다.
- 서버 429/5xx 재시도 분류, 지수 백오프, 실패 복구 UI는 별도 단계다.
- 자동 업데이트 서명과 manifest 검증은 이번 단계에 포함하지 않았다.

## 다음 권장 순서

1. 완료: CRM `POST /call-attempts`에서 DNC, 야간, 권한, 배정, 장치를 발신 전에 검사한다.
2. 완료: 결과 저장을 `attemptId` 기준의 영구 멱등 계약으로 변경하고 OpenAPI 계약을 추가한다.
3. 로컬 outbox를 암호화하고 408, 429, 5xx 재시도와 관리자 복구 화면을 구현한다.
4. `ICallDevice` 경계를 도입하고 ADB 구현과 Android Companion 구현을 병행한다.
5. Companion MVP에서 등록, 상호 인증, 발신, 종료, 실제 연결/종료 이벤트를 구현한다.
6. 기능 플래그로 ADB와 Companion을 병행 운영한 뒤 성공률과 상태 정확도를 기준으로 ADB를 제거한다.
7. 코드 서명과 서명된 update manifest 검증을 적용한 뒤 자동 업데이트를 다시 활성화한다.
