# Windows 릴리스

공개판은 Windows 11 x64용 vMAJOR.MINOR.PATCH 태그로 만듭니다. 첫 정식 버전은 v1.0.0입니다.

## 일회성 설정

1. 공개 GitHub 저장소를 만들고 이 저장소의 origin으로 연결합니다.
2. Windows 코드 서명 인증서를 준비합니다. PFX는 저장소에 넣지 말고 GitHub 저장소의 Actions secrets에 다음 이름으로 등록합니다.
   - QUOTRA_SIGNING_PFX_BASE64: PFX 파일의 Base64 텍스트
   - QUOTRA_SIGNING_PASSWORD: PFX 암호
   - 서명 준비가 끝나면 Actions variable `QUOTRA_SIGNED_RELEASES`를 `true`로 설정합니다. 이 값이 없으면 태그의 서명 릴리스 작업을 건너뛰며, 현재 서명 없는 배포 파일은 직접 첨부합니다.
3. 첫 서명 앱은 SmartScreen 경고가 남을 수 있습니다. 게시자 이름이 표시되는지와 릴리스 체크섬을 확인하도록 사용자에게 안내합니다.

Inno Setup 6.7.3 설치 파일은 릴리스 러너에서 Authenticode 서명을 확인한 뒤 설치합니다. 상업적으로 배포한다면 [Inno Setup 상업 라이선스 안내](https://jrsoftware.org/isorder.php)도 확인하세요.

PFX를 Base64로 변환할 때는 로컬에서 다음 PowerShell 명령을 사용하고 출력 문자열만 secret에 등록합니다. PFX나 암호를 저장소에 커밋하지 않습니다.

    [Convert]::ToBase64String([IO.File]::ReadAllBytes('C:\path\to\codesign.pfx'))

## 릴리스 순서

1. CHANGELOG.md에 공개할 변경 사항을 기록하고 src/Quotra.WinUI/Quotra.WinUI.csproj의 기본 버전을 다음 버전으로 올립니다.
2. 변경을 병합한 뒤 SemVer 태그를 만들고 푸시합니다.

    git tag v1.0.0
    git push origin v1.0.0

3. release.yml이 안정 .NET 10 SDK로 자체 포함 폴더를 빌드하고 오프라인 예측·Codex 검사와 일반 사용자 권한 트레이 시작 검사를 실행합니다.
4. 앱과 설치 파일에 Authenticode 서명을 적용·검증하고, 격리된 Windows 러너에서 최초 설치·덮어쓰기 업그레이드·제거·사용자 데이터 보존을 확인합니다.
5. 검증을 모두 통과하면 사용자 설치 파일, 포터블 ZIP, SHA-256 체크섬, 해당 버전 변경 내역을 GitHub Release에 게시합니다.

설치 프로그램은 사용자 프로필에 설치되며 관리자 권한을 요구하지 않습니다. 같은 설치 프로그램을 새 버전으로 다시 실행하면 기존 설치를 갱신합니다. 제거 프로그램은 %LOCALAPPDATA%\Quotra의 설정·사용량 이력을 삭제하지 않습니다.
첫 공개 태그 전에는 별도의 Windows 11 x64 사용자 환경에서 프로바이더 로그인/오류 상태와 트레이 메뉴 조작도 확인합니다. GitHub의 `windows-2025` 러너는 Windows Server 2025이므로 이 검사는 실제 Windows 11 클라이언트 점검을 대신하지 않습니다.

## 로컬 패키징

안정 .NET 10 SDK와 Inno Setup이 설치된 Windows에서 다음 명령을 실행합니다.

    ./tools/publish-release.ps1 -Version 1.0.0
    ./tools/verify-release.ps1 -Executable ./artifacts/release/publish/win-x64/QuotraWinUI.exe
    ./tools/package-release.ps1 -Version 1.0.0 -SkipPublish

출력은 artifacts/release에 생성됩니다. -SkipPublish를 생략하면 패키징 스크립트가 게시 빌드도 실행합니다.

Inno Setup은 현재 사용자 설치 경로(`%USERPROFILE%\AppData\Local\Programs\Inno Setup 6`)도 자동으로 찾습니다. 다른 위치에 설치했다면 `-CompilerPath 'C:\path\to\ISCC.exe'`를 지정합니다. 앱 파일 버전과 요청한 버전이 같을 때만 패키징하며, ZIP과 설치 파일의 `.sha256` 및 `SHA256SUMS.txt`를 함께 생성합니다. 로컬 패키징은 코드 서명을 적용하지 않으므로 공개 배포에는 위 서명 절차가 필요합니다.
