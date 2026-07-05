# TM 다이얼러 × milestone-crm 연동 설계서

- 날짜: 2026-07-05
- 상태: 승인 대기
- 구현 분담: **서버(milestone-crm) = Codex**, **클라이언트(파이썬 다이얼러) = Claude Code**
- 이 문서의 "API 계약" 절이 두 구현 사이의 계약이다. 계약 변경이 필요하면 이 문서를 먼저 수정하고 상대측에 알린다.

## 1. 배경과 목표

현재 TM 직원들은 엑셀 파일로 고객 DB를 받아 `auto_call.py`(customtkinter GUI + ADB 발신)로 전화를 걸고 결과를 로컬 엑셀에 기록한다. 관리자는 직원별 실적과 DB 소진 상태를 파악할 수 없다.

**목표:**

1. 다이얼러를 **단일 exe**로 만들어 TM 직원에게 배포한다 (adb 동봉, 별도 설치물 없음).
2. 엑셀 워크플로우를 **완전히 폐지**하고 milestone-crm을 단일 데이터 소스로 전환한다.
3. 직원이 **본인 CRM 계정으로 다이얼러에 로그인**하면 본인에게 배정된 DB(리드)가 표시된다.
4. 다이얼러에서 기록하는 **모든 것(콜 결과, 메모, 콜백 예약)이 CRM에 즉시 반영**되어 웹 칸반·실적 화면에 나타난다.
5. 관리자용 CRM 화면 신설: 직원별 콜 실적 집계, DB(리드) 상태 현황, 실시간 현황판, 엑셀 다운로드.
6. 다이얼러 UI 재디자인 (재디자인 범위는 다이얼러만).

**확정된 전제 (사용자 확인 완료):**

- CRM은 EC2에서 운영 중이며 직원 PC에서 접속 가능하다.
- 업무 환경은 Windows PC + 안드로이드 폰 USB 연결(ADB 발신) 유지.
- 아키텍처는 "파이썬 데스크톱 앱 + CRM REST API 신설" 방식(A안).

## 2. 아키텍처 개요

```
[TM 직원 PC]                              [EC2 서버]
┌─────────────────────────┐               ┌──────────────────────────┐
│  다이얼러.exe (파이썬)     │── HTTPS ────▶│  milestone-crm (Next.js)  │
│  · CRM 계정으로 로그인     │   REST API   │  · /api/v1/* 신설         │
│  · 배정 리드 큐 표시       │              │  · 기존 서비스 로직 재사용   │
│  · ADB로 폰 발신          │              │  · RDS Postgres           │
│  · 결과/메모 기록 → CRM    │              │                           │
└──────────┬──────────────┘               │  [관리자 브라우저]          │
           │ USB                          │  · 콜 실적/DB 현황/실시간    │
     [안드로이드 폰]                        │  · 엑셀 다운로드            │
                                          └──────────────────────────┘
```

- 다이얼러는 서버가 주는 콜 큐만 표시한다. 로컬에 고객 DB를 저장하지 않는다.
- 전화번호 원문은 발신 직전 reveal API로 1건씩만 받는다(감사로그 강제). 목록에는 마스킹 번호만 내려간다.
- 결과 저장 = 서버의 `logCall()` 재사용 → 리드 상태 자동 전이 → 웹 칸반 즉시 반영.

## 3. API 계약 (`/api/v1/*`)

### 3.1 공통 규약

- Base URL: `https://<crm-host>/api/v1`
- 인증: `Authorization: Bearer <token>` 헤더 (login, version 제외)
- 토큰 = 기존 `sessions` 테이블의 세션 id를 재사용한다. 유휴 30분 / 절대 8시간 / 즉시 강제 무효화 / 매 요청 계정상태(ACTIVE) 재검증 등 웹과 동일한 수명 규칙이 적용된다.
- Content-Type: `application/json; charset=utf-8`
- 에러 포맷 (모든 4xx/5xx 공통):

```json
{ "error": { "code": "NIGHT_BLOCKED", "message": "야간(21~08시)에는 발신할 수 없습니다." } }
```

| HTTP | code | 의미 |
|---|---|---|
| 400 | `VALIDATION` | 요청 형식 오류 |
| 401 | `UNAUTHENTICATED` | 토큰 없음/만료/무효 → 클라는 재로그인 유도 |
| 401 | `INVALID_CREDENTIALS` | 로그인 실패 (계정 존재 여부 은닉, 웹과 동일 메시지) |
| 401 | `MFA_REQUIRED` / `MFA_INVALID` | MFA 코드 필요 / 코드 불일치 |
| 401 | `ACCOUNT_LOCKED` | 5회 실패 잠금 (웹과 동일 정책·감사로그) |
| 403 | `FORBIDDEN` | 권한 없음 (감사로그 DENIED 기록, 기존 `requirePermission` 동일) |
| 404 | `NOT_FOUND` | 리드 없음 **또는 본인 배정이 아님** (존재 여부 노출 금지) |
| 423 | `NIGHT_BLOCKED` | 야간 발신 차단 (`assertCallAllowed`) |
| 500 | `INTERNAL` | 서버 오류 |

### 3.2 엔드포인트

#### `POST /auth/login`

요청: `{ "loginId": "hong", "password": "...", "code": "123456"? }`

- 검증은 웹 `loginAction`과 동일해야 한다: Argon2 검증, 실패 카운트/5회 잠금, 상태별 거부, MFA(TOTP), 성공·실패 감사로그, `lastLoginAt` 갱신.
- MFA 활성 계정이 code 없이 요청 → `401 MFA_REQUIRED` (클라가 코드 입력란 표시 후 재요청).

응답 200:

```json
{
  "token": "<sessions.id>",
  "expiresAt": "2026-07-05T18:00:00+09:00",
  "user": {
    "id": "uuid", "loginId": "hong", "name": "홍길동",
    "orgName": "강남1팀", "roles": ["TM"], "mustChangePassword": false
  }
}
```

- `mustChangePassword: true`이면 클라는 "웹에서 비밀번호를 변경한 뒤 다시 로그인하세요" 안내 후 로그인을 중단한다 (다이얼러에 비밀번호 변경 UI를 만들지 않는다).

#### `POST /auth/logout` → 204. 세션 행 삭제.

#### `GET /me` → 200 `{ "user": {...login 응답과 동일...} }` — 토큰 유효성 확인 겸용.

#### `GET /leads/queue?limit=50`

본인 배정 + 콜 가능 상태(`NEW, ASSIGNED, NOANSWER, CALLBACK, INTERESTED, CONSULT`)의 리드. 기존 `callQueue()` 정렬(`nextCallAt asc, createdAt asc`) 유지, 응답 필드 확장.

```json
{
  "serverTime": "2026-07-05T10:00:00+09:00",
  "items": [
    { "id": "uuid", "name": "김철수", "phoneMasked": "010-****-1234",
      "status": "INTERESTED", "nextCallAt": null, "memo": "5시 이후 선호",
      "updatedAt": "..." }
  ]
}
```

- `phoneMasked`: 서버 내부에서 복호화 후 가운데 자리를 마스킹해 반환. **평문 전체는 이 응답에 절대 포함하지 않는다.** 마스킹 표시는 웹 UI와 동일한 노출 수준이므로 reveal 감사로그 대상이 아니다. (reveal 게이트의 module-private decrypt와 별개로 마스킹 전용 내부 함수를 두는 등 구현은 Codex 재량 — 단 "평문을 export하지 않는다"는 기존 원칙 유지)
- 권한: `lead:read`.

#### `POST /leads/{id}/reveal`

요청: `{ "reason": "TM 발신" }` (2자 이상 필수 — 기존 `revealPII` 규칙)

응답 200: `{ "phone": "01012345678" }` (정규화된 숫자, `normalizePhone` 기준)

- 기존 `revealPII()` 경유: `pii:reveal` 권한 + 사유 + 감사로그 동기 기록.
- 본인 배정 리드가 아니면 `404 NOT_FOUND`.

#### `POST /leads/{id}/call` — 콜 결과 기록

헤더: `Idempotency-Key: <uuid4>` (클라이언트가 기록 1건마다 생성, 필수)

요청:

```json
{ "resultCode": "CALLBACK", "talkSeconds": 154,
  "memo": "다음주 재상담 원함", "callbackAt": "2026-07-06T14:30:00+09:00" }
```

- `resultCode` ∈ `NOANSWER | CALLBACK | INTERESTED | CONSULT | WON | REJECT | DNC` (기존 `call_result` enum)
- `callbackAt`은 `CALLBACK`일 때 필수, 그 외 null.
- 서버는 기존 `logCall()`을 재사용한다: call_logs 삽입 + 리드 상태 전이(`RESULT_TO_STATUS`) + CALLBACK 시 `next_call_at` 설정 + DNC 시 전사 DNC 자동등록 + 야간 차단 + `call:log` 감사로그. 본인 배정 검증(`getLeadForCall`) 포함.
- **멱등성**: 같은 사용자의 같은 `Idempotency-Key` 재전송 시 중복 삽입 없이 최초 결과를 200으로 반환한다(보존 기간 최소 24시간). 구현 방식(예: `call_logs`에 nullable `idempotency_key` 컬럼 + `(user_id, idempotency_key)` unique 인덱스)은 Codex 재량.

응답 200: `{ "ok": true, "lead": { "id": "uuid", "status": "CALLBACK", "nextCallAt": "..." } }`

#### `PATCH /leads/{id}/memo`

요청: `{ "memo": "지난주 상담, 5시 이후 선호" }` (≤2000자) → 200 `{ "ok": true }`

- `leads.memo`(리드 단위 메모, 칸반 노출) 갱신. 본인 배정만. 권한 `lead:update`.
- **2026-07-05 변경**: 리드 메모는 관리자만 관리하기로 결정 — 다이얼러는 이 엔드포인트를 더 이상 호출하지 않고 리드 메모를 읽기 전용으로 표시만 한다. 서버는 엔드포인트를 유지해도 무방하나, 웹 칸반의 메모 수정 권한을 관리자급으로 좁히는 작업은 서버(Codex) 몫.

#### `GET /version` (무인증, 선택 구현)

응답 200: `{ "minVersion": "2.0.0", "latestVersion": "2.0.0", "downloadUrl": null }`

- 다이얼러가 시작 시 확인해 구버전 안내. 서버가 아직 미구현이면 클라는 404를 무시한다.

### 3.3 서버 구현 노트 (Codex 참고)

- 라우트 핸들러는 `src/app/api/v1/**/route.ts`, `runtime = "nodejs"`, `force-dynamic` (기존 `api/health` 관례).
- `getApiSession()`: `getCurrentSession()`(src/features/auth/session.ts)의 쿠키 조회 부분만 `Authorization` 헤더로 대체한 변형. 유휴 슬라이딩 갱신·상태 재검증 로직 동일 유지.
- `proxy.ts` matcher에 `api/v1` 제외 추가 (현재 `api/health`만 제외되어 있음).
- 재사용 대상: `verifyPassword`(auth/password.ts), `verifyLoginTotp`(auth/mfa.ts), `createSession`, `callQueue`/`getLeadForCall`/`logCall`(leads/call.service.ts), `revealPII`(pii/reveal.ts), `requirePermission` 로직(rbac/guard.ts — redirect 없는 API 변형 필요), `writeAudit`, `assertCallAllowed`, `normalizePhone`(lib/phone.ts).
- CORS 불필요 (클라이언트는 브라우저가 아님).
- 다이얼러 로그인에 역할 제한을 두지 않는다 — 엔드포인트별 권한(`lead:read`, `call:create`, `pii:reveal`, `lead:update`)으로 통제 (TM/SALES는 모두 보유).

## 4. 관리자 화면 (Codex 구현 범위)

모든 화면은 기존 권한·스코프 체계를 따른다: `requirePage("dashboard:read")` + `scopeWhere` 재사용 → 지사관리자는 자기 팀, 본사는 전사.

1. **`/tm/stats` — 직원별 콜 실적**: 기간 필터(오늘/이번 주/이번 달/직접 지정). 직원별 행: 발신 수, 총 통화시간(`talk_seconds` 합), 결과 7종 분포, 가입(WON) 수, 전환율(가입/발신). 원천은 `call_logs`.
2. **같은 페이지 탭 — DB(리드) 현황**: 상태별 리드 분포(10개 상태), 배정 vs 미배정, 팀·직원별 필터, 소진율(콜 가능 상태를 벗어난 리드 / 전체).
3. **`/tm/live` — 실시간 현황판**: 오늘 직원별 콜 수 + 마지막 콜 시각, 최근 콜 피드(시간·직원·결과), 30초 자동 갱신(폴링). 관리자 근무 중 모니터링용.
4. **엑셀 다운로드**: 1·2의 집계를 exceljs로 내보내기. 다운로드 시 감사로그 기록.

## 5. 클라이언트 설계 (Claude Code 구현 범위)

### 5.1 저장소 구조 (`/home/mirage/office/auto_call`)

```
auto_call/
  main.py              # 진입점
  api.py               # CRM API 클라이언트 (이 문서 3장 계약 구현)
  adb.py               # ADB 발신·종료·연결감지 (번들 adb.exe)
  state.py             # 로컬 설정(%APPDATA%\TMDialer) + 재전송 큐
  version.py           # 클라이언트 버전 상수
  ui/
    theme.py           # 디자인 토큰: 색·폰트·간격 (CRM 상태색과 동일 매핑)
    widgets.py         # 공용 컴포넌트 (카드, 상태 배지, 결과 버튼, 토스트)
    login.py           # 로그인 화면
    workspace.py       # 콜 워크스페이스 (메인 화면)
  tests/               # pytest (api/state/adb 단위 테스트)
  build/
    dialer.spec        # PyInstaller onefile 설정 (adb.exe + DLL 동봉)
    build.bat          # Windows 원클릭 빌드
```

- 의존성: `customtkinter`, `requests`뿐. **pandas/openpyxl 제거** (엑셀 폐지) → exe 크기 대폭 감소.
- 기존 `auto_call.py`는 마이그레이션 완료 후 삭제. `my_env`는 리눅스 개발용 venv로 유지하되 의존성 갱신.

### 5.2 UI (customtkinter, 라이트 테마)

**로그인**: 중앙 카드 — 아이디/비밀번호, MFA 계정이면 `MFA_REQUIRED` 응답을 받은 뒤 인증코드 필드 노출. 마지막 로그인 아이디 기억. 서버 주소는 기본값 내장, 고급 설정에서만 변경. `mustChangePassword`면 웹 안내 후 중단.

**메인 워크스페이스** (2컬럼):

```
┌────────────────────────────────────────────────────────────┐
│ 🏢 강남1팀 · 홍길동   오늘: 발신 47 · 가입 3    ● ADB ● CRM │
├──────────────┬─────────────────────────────────────────────┤
│ 오늘의 콜 큐   │  김철수                        [가망]        │
│ (콜백 도래분   │  📱 010-****-1234                           │
│  상단 고정)   │  리드 메모: 지난주 상담, 5시 이후 선호  ✎      │
│              │ ────────────────────────────────────────── │
│              │   [📞 발신 F1]  [⏹ 종료 F2]   ⏱ 02:34       │
│              │ ────────────────────────────────────────── │
│              │  결과(1~7): 부재 콜백 가망 상담 가입 거절 거부  │
│              │  메모 [________________]  콜백 [14:30 ▾]     │
│              │              [💾 저장하고 다음 F3]            │
└──────────────┴─────────────────────────────────────────────┘
```

- 결과 7종 버튼은 CRM 칸반 상태색과 동일한 색상 사용(구현 시 CRM 코드에서 hex 추출), 숫자키 1~7 즉시 선택.
- 단축키: F1 발신 / F2 종료 / F3 저장 후 다음 / 1~7 결과 선택.
- 콜백 시간 도래 리드: 큐 상단 승격 + 하이라이트 + 토스트 알림 (기존 재통화 알림 계승, 서버 `nextCallAt` 기준).
- 상단 카운터는 서버 기록 기준(큐 새로고침 시 오늘 실적도 재계산 — `GET /leads/queue` 응답만으로는 부족하므로 클라가 자체 세션 카운터로 유지, 재로그인 시 0부터. 서버 집계는 관리자 화면 소관).

### 5.3 동작 흐름·에러 처리

1. 로그인 → 토큰은 **메모리에만 보관** (디스크 저장 안 함, 아침 1회 로그인).
2. 큐 폴링 60초 + 수동 새로고침. 웹에서 재배정·상태변경되면 다음 폴링에 반영.
3. **발신(F1)**: reveal API → 평문 번호 취득 → 즉시 `adb shell am start -a android.intent.action.CALL -d tel:<번호>` → 통화 타이머 시작. 평문은 발신 후 즉시 폐기(UI에는 마스킹 번호만 표시).
4. **종료(F2)**: `adb shell input keyevent 6` → 타이머 정지 → `talkSeconds` 확정.
5. **저장(F3)**: `POST /leads/{id}/call` (Idempotency-Key 부여) → 성공 시 다음 고객 자동 전환.
6. **저장 실패(네트워크)**: 요청 전문을 `%APPDATA%\TMDialer\pending_calls.json`에 적재, 상단 배너 표시("기록 1건 전송 대기"), 30초 간격 자동 재시도(같은 멱등키 → 서버가 중복 방지). 401이면 재로그인 다이얼로그 후 재시도.
7. **423 NIGHT_BLOCKED**: "야간에는 발신/기록이 제한됩니다" 안내.
8. CRM 접속 불가: 상태바 ● CRM 빨강 + 발신 비활성 (번호가 서버에만 있으므로 의도된 동작). ADB 미연결: 발신 버튼 비활성 + 안내, 5초 주기 재감지.

### 5.4 빌드·배포

- PyInstaller **onefile**: 파이썬 런타임 + `adb.exe` + `AdbWinApi.dll` + `AdbWinUsbApi.dll` 동봉. 실행 시 `sys._MEIPASS`에서 adb 호출.
- **빌드는 Windows에서** `build/build.bat` 실행 (WSL에서 Windows exe 크로스 빌드 불가). platform-tools 바이너리는 빌드 시 로컬 경로에서 주입(저장소에 커밋하지 않음).
- 배포: exe 파일 1개 전달. 서버 주소 기본값 내장.
- 버전 체크: 시작 시 `GET /version` (미구현/실패 시 무시).

## 6. 테스트 계획

**서버(Codex, vitest — 기존 관례)**: 로그인 5회 실패 잠금·MFA 분기, 토큰 만료/비활성 계정 401, 본인 배정 외 리드 404, reveal 사유 누락 거부 + 감사로그, 콜 기록 상태전이·DNC 자동등록, 멱등키 중복 전송 시 1건만 기록, 야간 차단 423.

**클라이언트(Claude Code, pytest)**: `api.py` — mock HTTP로 계약 전체(에러 코드 분기 포함), `state.py` — 재전송 큐 적재/재시도/성공 제거, `adb.py` — fake adb 스크립트로 발신/종료/연결감지. UI는 수동 테스트 체크리스트 문서(`docs/manual-test.md`)로 관리. Codex API가 로컬 CRM(dev, PGlite)에 올라오면 통합 확인.

## 7. 전환 계획 (엑셀 → CRM)

1. CRM에 TM/영업 직원 계정 생성 + TM·SALES 역할 부여 (기존 기능).
2. 기존 엑셀 고객 DB를 CRM 리드 임포트(기존 기능, 중복·DNC 자동 필터) 후 배정(라운드로빈 or 수동).
3. 서버 API 배포(기존 docker-compose) → 다이얼러 exe 배포.
4. 즉시 전환, 병행 기간 없음(엑셀 완전 폐지 결정). 전환 첫날 관리자가 `/tm/live`로 모니터링.

## 8. 보안·컴플라이언스 요약

- 전화번호 평문은 ① 발신 직전 reveal(권한+사유+감사로그) ② 응답 1건 ③ 메모리에서 즉시 폐기 — 로컬 디스크에 평문 저장 없음.
- 다이얼러의 모든 기록·열람은 기존 감사로그 체계(`writeAudit`)를 그대로 통과.
- 야간 발신 차단·DNC 자동 등록은 서버가 강제 (클라이언트 우회 불가).
- 토큰은 디스크에 저장하지 않음. 세션 정책(유휴 30분/절대 8시간/즉시 무효화)은 웹과 동일.

## 9. 범위 밖 (이번에 하지 않음)

- CRM 기존 웹 화면 재디자인 (신규 관리자 화면만 신설)
- 다이얼러 자동 업데이트(버전 안내까지만), 녹취, 통계의 다이얼러 내 표시
- iOS/비-ADB 발신 방식
