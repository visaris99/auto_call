# 검증형 자동 업데이트 구현 및 운영 절차

작성일: 2026-07-11  
대상: `auto_call` WPF 2.4.0 이후  
상태: 코드 구현 완료, 운영 배포 및 버전 공개 미실행

## 1. 결정 사항

- 자동 다운로드와 무음 설치는 유지한다.
- 설치파일을 실행하기 전에 RSA manifest 서명, HTTPS 호스트, 크기, SHA-256, PE 제품명과 버전을 검증한다.
- 유료 Authenticode 인증서는 현재 도입하지 않는다.
- 최초 2.4.0은 각 PC에 수동 설치한다.
- 모든 PC가 2.4.0으로 전환된 뒤 2.4.1부터 검증형 자동 업데이트를 사용한다.
- 운영 CRM의 `latestVersion=2.3.4`는 최초 전환이 끝날 때까지 유지한다.

기존 2.3.4 updater는 서명과 해시를 확인하지 않는다. 따라서 2.4.0을 API에서 먼저 공개하면 아직 2.3.4인 PC가 검증 없이 설치파일을 실행한다. 이 한 번의 부트스트랩만 수동 배포해야 한다.

## 2. 구현 구조

| 구성 | 책임 |
| --- | --- |
| `Core/UpdateManifest.cs` | API 응답을 고정 형식 manifest로 변환 |
| `Core/UpdateTrust.cs` | 앱에 내장한 공개키와 key ID 보관 |
| `Core/UpdateManifestVerifier.cs` | RSA-SHA256 서명과 URL 정책 검증 |
| `Core/UpdateDownloader.cs` | 리다이렉트 제한, 크기 제한, 스트리밍 SHA-256 검증 |
| `Core/UpdateCoordinator.cs` | 프로세스 내부·프로세스 간 단일 실행 잠금, PE 검사, 설치 실행 순서 제어 |
| `App/App.xaml.cs` | 진행 UI와 검증 성공 후 무음 설치 시작 |

manifest canonical payload는 다음 순서를 고정한다.

```text
milestone-dialer-update-v1
version={x.y.z}
downloadUrl={absolute HTTPS URL}
sha256={lowercase hex}
size={decimal bytes}
publishedAtUnixSeconds={UTC unix seconds}
keyId={lowercase key id}
```

서명 알고리즘은 `RSA-3072 + SHA-256 + PKCS#1 v1.5`다. 현재 key ID는 `58ee66c991445856`이다.

## 3. 개인키 보관

개인키는 저장소와 CRM 서버에 두지 않는다.

```text
/home/mirage/.config/milestone/dialer-update-signing/private-key.pem
```

- 현재 파일 권한: `600`
- 공개키만 소스에 포함됨
- 개인키 내용을 채팅, 이슈, CI 로그에 출력하지 않음
- 운영 공개 전 암호화된 오프라인 백업 1개를 만들고 접근자를 기록함
- 키가 유출되면 해당 키로 새 릴리스를 발행하지 않고 키 교체 절차를 수행함

## 4. Claude Code 담당 범위

Claude Code에는 Windows Setup 생성만 요청한다.

```text
작업 저장소: /home/mirage/office/auto_call

Windows 환경에서 Milestone Dialer 2.4.0 Setup 파일만 빌드해줘.

필수 조건:
- 기존 변경사항을 되돌리거나 새 기능을 수정하지 말 것
- dotnet/App/Ui.cs의 Version이 2.4.0인지 확인
- build/adb에 adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll이 모두 있는지 확인
- .NET 8 SDK와 Inno Setup 6 사용
- dotnet 테스트를 먼저 실행
- dotnet/publish.bat으로 Release win-x64 self-contained Setup 생성
- 결과물은 dist_dotnet/milestone_dialer_setup_2.4.0.exe
- CRM 배포, latestVersion 변경, manifest 생성, 자동 업데이트 활성화는 하지 말 것
- 코드서명은 하지 말 것

완료 보고:
- 테스트 결과
- Setup 절대 경로
- 파일 크기
- Get-FileHash -Algorithm SHA256 결과
```

`publish.bat`은 ADB 런타임이 없으면 이제 실패 처리한다. 경고만 내고 발신 불가능한 설치파일을 만들지 않는다.
또한 `Ui.Version`을 앱 PE의 ProductVersion/FileVersion과 Inno Setup 버전에 함께 주입하므로 세 버전이 달라지면 릴리스하지 않는다.

## 5. 최초 2.4.0 배포

1. Claude Code가 생성한 Setup을 별도 테스트 PC에서 설치한다.
2. 로그인, 단말 선택, 테스트 리드 발신, 통화 종료, 결과 저장을 확인한다.
3. 운영 `latestVersion`이 여전히 `2.3.4`인지 확인한다.
4. 직원 PC별로 2.4.0을 수동 설치한다.
5. PC별 설치 버전을 자산 목록에 기록한다.
6. 모든 PC가 2.4.0임을 확인한 뒤 사용자 환경변수 `TM_NO_AUTOUPDATE`를 제거한다.
7. 앱을 재시작해 자동 업데이트가 활성 상태인지 확인한다.

환경변수 제거 PowerShell:

```powershell
[Environment]::SetEnvironmentVariable("TM_NO_AUTOUPDATE", $null, "User")
```

## 6. 2.4.1 이후 자동 릴리스

Setup 파일을 이 Linux 작업환경으로 전달받은 뒤 CRM 저장소에서 실행한다.

```bash
cd /home/mirage/office/milestone-crm
npm run dialer:sign-update -- \
  --setup /absolute/path/milestone_dialer_setup_2.4.1.exe \
  --version 2.4.1 \
  --private-key /home/mirage/.config/milestone/dialer-update-signing/private-key.pem \
  --output /absolute/path/milestone_dialer_update_2.4.1.json
```

그다음 순서를 지킨다.

1. 테스트 PC에서 Setup을 수동 검증한다.
2. 운영 서버의 `downloads/milestone_dialer_setup.exe`를 원자적으로 교체한다.
3. 다운로드 URL의 `Content-Length`와 `X-Checksum-SHA256`을 확인한다.
4. CRM `다이얼러 업데이트` 화면에 생성된 manifest JSON을 붙여넣는다.
5. `검증 후 활성화`를 누른다.
6. `/api/v1/version`의 version, hash, size, keyId, signature를 확인한다.
7. 카나리 PC 1대에서 자동 다운로드, 설치, 재실행을 확인한다.
8. 30분 모니터링 후 나머지 PC에 적용한다.

CRM은 서버에 실제로 존재하는 Setup의 크기와 SHA-256이 manifest와 다르면 활성화를 거부한다.

## 7. 실패 및 롤백

- manifest가 없거나 서명이 틀리면 설치하지 않고 현재 앱을 계속 실행한다.
- HTTP URL, 다른 호스트, 과도한 파일, 크기·해시 불일치는 모두 실행 전에 차단한다.
- 설치 프로세스를 시작하지 못하면 다운로드 파일을 삭제한다.
- 앱과 Inno Setup 모두 단일 인스턴스 잠금을 사용해 같은 PC의 중복 설치를 차단한다.
- 긴급 중단은 모든 PC에 `TM_NO_AUTOUPDATE=1`을 설정하고 앱을 재시작한다.
- 잘못된 Setup 파일이 서버에 올라가도 서명 manifest를 활성화하지 않으면 자동 설치되지 않는다.
- 이전 버전으로 롤백할 때도 별도의 새 버전 번호와 새 서명 manifest를 사용한다. 버전 번호를 낮추는 방식은 updater가 거부한다.

## 8. 남은 위험

- 유료 Authenticode 서명이 없으므로 최초 수동 설치에서는 Windows SmartScreen 경고가 보일 수 있다.
- private key를 가진 사람은 유효한 update manifest를 만들 수 있다. 개인키 접근 통제가 핵심이다.
- 2.3.4에서 2.4.0으로 넘어가는 최초 설치는 새 검증 로직의 보호를 받지 않으므로 반드시 수동 배포한다.
- 실제 Inno Setup 결과물의 ProductName과 ProductVersion 검증은 Windows 카나리에서 최종 확인해야 한다.
