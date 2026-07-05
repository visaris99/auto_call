# TM 다이얼러

milestone-crm과 연동되는 TM 상담원용 전화 프로그램.
CRM 계정으로 로그인 → 배정된 리드 큐 → USB 연결 안드로이드 폰으로 발신(ADB) → 결과는 CRM에 즉시 기록.

- 설계서: `docs/superpowers/specs/2026-07-05-tm-dialer-crm-integration-design.md` (API 계약 포함)
- 구현 계획: `docs/superpowers/plans/2026-07-05-tm-dialer-client.md`
- 서버(API·관리자 화면)는 milestone-crm 저장소에서 별도 구현.

## 개발 (WSL/리눅스)

    my_env/bin/python -m pip install -r requirements-dev.txt
    my_env/bin/python -m pytest tests/ -v          # 단위 테스트
    my_env/bin/python scripts/dev_mock_crm.py      # 가짜 CRM (hong/1234, :3002)
    TM_ADB=/bin/true my_env/bin/python main.py     # 앱 실행(발신은 no-op)

환경변수: `TM_SERVER_URL`(서버 주소 강제), `TM_ADB`(adb 바이너리 경로 강제).

## Windows 빌드

1. Python 3.12+ 설치, Android platform-tools에서 `build/adb/`에 adb.exe + DLL 2종 복사
2. `build\build.bat` 실행 → `dist\TM다이얼러.exe`

## 배포 전 확인

- `state.py`의 `DEFAULT_SERVER_URL`을 실제 CRM 주소로 변경
- `docs/manual-test.md` 체크리스트 통과
