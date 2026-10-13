# Quotra — LLM 사용량 위젯 (WinUI 3)

실제 사용량이 확인된 프로바이더만 표시합니다. 미로그인·미연동 서비스는 숨기고, 성공한 서비스의 일시적인 조회 오류는 마지막 값을 유지합니다.
위젯을 우클릭하면 고정, 항상 위, Windows 로그인 시 실행, 반투명, 테마, 새로고침, 숨기기 메뉴가 열립니다.
알림 영역 아이콘 우클릭 메뉴에서도 Windows 로그인 시 실행을 켜고 끌 수 있습니다. 위젯 표시·숨기기와 프로바이더 설정, 종료도 같은 메뉴에 있으며 `Ctrl+Alt+F9`로도 위젯을 표시할 수 있습니다.

여러 LLM 구독의 **남은 한도**를 화면 위쪽에 떠 있는 가로 배터리 바로 보여줍니다.
프로바이더마다 배터리 하나가 나란히 놓이고, 평소에는 **배터리 몸통 안에 예측 감소량**만 보입니다.
퍼센테이지와 초기화까지 남은 시간은 **마우스를 올린 프로바이더에만** 나옵니다(창은 커지지 않습니다).
Windows 11 스타일의 **Desktop Acrylic 반투명 표면 · 차분한 배터리 · 다크/라이트 테마**를 사용합니다.
서비스 이름과 한도 종류를 정렬하고, 호버한 셀만 은은하게 강조합니다.

**Codex·Claude·Command Code를 기본 연동합니다** — 앱을 켜면 기존 로그인 정보를 사용해 값을 자동으로 읽습니다.

```
평소 — 프로바이더마다 배터리 하나(숫자는 몸통 안)
┌────────────────────────────────────────────────────────────┐
│  ChatGPT · 주간     Claude · 주간      Grok · 주간         │
│  [▓20%/10분→2시간 25분 남음]▮ [▓20%/10분→2시간 18분 남음]▮ [▓20%/10분→2시간 11분 남음]▮ │
└────────────────────────────────────────────────────────────┘

Claude 에 마우스를 올리면 그 셀의 문구만 바뀐다(창 크기는 그대로)
│  [▓20%/10분→2시간 25분 남음]▮ [▓남음 77%·리셋 6일 23시간]▮ [▓20%/10분→2시간 11분 남음]▮ │
```

- **주력 앱은 WinUI 3 위젯입니다** — `src/Quotra.WinUI`

---

## 1. 지금 되는 것

| 기능 | 상태 |
| --- | --- |
| **Codex·Claude·Command Code 실연동** | Codex·Claude 데스크톱/CLI와 Command Code CLI 로그인을 탐색해 한도를 조회. Claude 자동 연결 실패 시 설정에서 직접 로그인 ([docs/providers.md](docs/providers.md)) |
| **가로 배터리 바** | 서비스 배지·이름·한도 종류를 정렬. 24dip 높이의 배터리가 남은 한도를 왼쪽부터 채우고, 그 안에 숫자가 들어간다. 색은 등급(정상/주의/위험) |
| **반투명 배경** | 창 뒤에 Desktop Acrylic 블러를 적용하고 루트·패널은 투명하게 유지합니다. 지원하지 않는 환경에서는 다크/라이트 불투명 색으로 대체 |
| **바깥에는 아무 줄도 없음** | 평소 노출은 배터리 안 `20%/10분 → 2시간 25분 남음`(예측 감소량)뿐. 배터리 밖에 남는 줄이 없다 |
| **호버로 값 확인** | 커서가 올라간 셀의 문구만 `남음 84% · 리셋 6일 13시간` 으로 바뀜(창 크기·배터리 높이 고정, 드롭다운 없음) |
| **값 없음** | 기존 동작대로 값이 없거나 예측을 수집 중이면 문구를 비워 둔다. 없는 사용량을 만들지 않는다 |
| 상단에서 내려오는 팝업 | 상단 엣지에서 **좌우로 2회 흔들면** 표시/숨김 |
| 위젯 고정 | 우클릭 메뉴에서 고정/팝업 모드를 선택 |
| **버튼 없는 헤더** | 기존처럼 위젯에는 버튼을 두지 않는다. 제어는 우클릭 메뉴, 고정 해제 시 `Ctrl+Alt+F9`와 상단 흔들기 |
| 다크/라이트 테마 | Windows 설정을 따르거나 우클릭 메뉴에서 강제. 런타임 전환 |
| 우클릭 설정 | 고정 · 항상 위 · 반투명 · 테마 · 즉시 새로고침 · 숨기기 · 종료. 전역 핫키 `Ctrl+Alt+F9` |
| 포커스를 훔치지 않음 | `WS_EX_NOACTIVATE` + `SW_SHOWNOACTIVATE`. 작업 중인 창의 포커스가 그대로 유지됨 |
| **소모량 추적 · 고갈 예측** | 값이 들어오는 즉시 첫 표본을 남기고(첫 10분을 버리지 않는다) 10분마다 이어 찍는다. 두 번째 표본부터 속도와 고갈까지 남은 시간이 나온다 |
| **확장 지점** | `IUsageSource` 하나만 구현해 `Register` 하면 그 프로바이더가 실제 값을 받는다 ([docs/providers.md](docs/providers.md)) |

![다크 테마](docs/fluent-dark.png)
![라이트 테마 · Claude 호버](docs/fluent-light.png)

위 이미지는 **검증용 데이터로 실제 WinUI XAML을 렌더링한 화면**입니다. DWM 창 그림자는 포함하지 않습니다.
아래는 Claude 호버 상태로, 해당 셀의 문구만 남은 비율·리셋 시간으로 바뀝니다.

프로바이더가 0곳일 때는 [빈 상태 안내](docs/fluent-empty.png)를 보여줍니다.
[한 서비스](docs/fluent-single.png) / [미연동](docs/fluent-unlinked.png)도 같은 레이아웃을 사용합니다.

**값은 `IUsageSource` 구현체만 공급합니다.** 목업 난수는 없습니다 — 등록된 소스가 없으면
위젯은 값을 지어내지 않고 빈 배터리로 표시합니다. 새 프로바이더를 붙이는 방법은
[docs/providers.md](docs/providers.md) 한 장에 정리해 두었습니다.

## 2. 실행

```powershell
dotnet build src\Quotra.WinUI\Quotra.WinUI.csproj -c Release
src\Quotra.WinUI\bin\x64\Release\net10.0-windows10.0.22000.0\win-x64\QuotraWinUI.exe
```

| 요구 | 값 |
| --- | --- |
| OS | Windows 11 x64 (빌드 10.0.22000 이상) |
| SDK | 안정 버전 .NET 10 SDK (`net10.0-windows10.0.22000.0`) |
| 플랫폼 | x64 |
| Windows App SDK | 2.5.1 — **자체 포함**(`WindowsAppSDKSelfContained=true`)이라 Windows App Runtime 설치가 필요 없습니다 |

종료는 우클릭 메뉴의 **종료**로 합니다. 기본 카탈로그에는 ChatGPT, Claude, Command Code, OpenCode Go가 있습니다. OpenCode Go는 API 키 연결이 필요합니다.
다른 서비스를 추가하려면 `Data\providers.json` 정의와 소스를 함께 연결합니다([docs/providers.md](docs/providers.md)).

UI를 검증할 때는 `tools\verify-fluent.ps1`을 실행합니다. 기존 Release 빌드를 재사용하고 검증용 데이터와 로그는 임시 폴더에 저장합니다.

## 배포와 업데이트

서명 준비와 태그 배포 절차는 [docs/releasing.md](docs/releasing.md)를 참고하세요.

- 버전은 `v주.부.수`로 올립니다. 현재 버전은 `1.0.1`이며, 버그 수정은 패치, 기능 추가는 부 버전, 호환성을 깨는 변경은 주 버전을 올립니다.
- GitHub Releases에서 Windows 11 x64용 `Quotra-Setup-버전-win-x64.exe`를 받아 설치합니다. 설치 없이 실행하려면 포터블 ZIP을 폴더째 압축 해제한 뒤 `QuotraWinUI.exe`를 실행합니다.
- 설치 프로그램은 관리자 권한 없이 설치하고 시작 메뉴 바로가기를 만듭니다. 업데이트는 새 설치 파일을 기존 설치 위에 실행합니다. 설정과 사용량 이력은 제거 후에도 기본 보존됩니다.
- 현재 `1.0.1` 설치 파일과 앱에는 코드 서명이 적용되지 않았습니다. Windows에서 게시자 확인 또는 SmartScreen 경고가 표시될 수 있습니다. 다운로드한 파일은 릴리스의 `SHA256SUMS.txt`와 비교할 수 있습니다. 서명 인증서를 준비한 뒤 자동 릴리스에서 서명을 적용합니다.
- 각 프로바이더의 연결 상태와 마지막 성공 조회는 설정 창에서 확인할 수 있습니다. 예측은 사용량 표본을 10분 이상 수집한 뒤 나타납니다.

배포 스크립트와 설치 프로그램 정의는 `tools\publish-release.ps1`, `tools\package-release.ps1`, `packaging\windows\Quotra.iss`에 있습니다. 릴리스 자동화 전에 GitHub 저장소와 `QUOTRA_SIGNING_PFX_BASE64`, `QUOTRA_SIGNING_PASSWORD` Actions 비밀 값을 설정해야 합니다.
서명 준비 후 Actions variable `QUOTRA_SIGNED_RELEASES`를 `true`로 설정하면 태그를 통한 자동 릴리스가 활성화됩니다.
서명 인증서가 없는 현재 버전은 로컬에서 만든 설치 파일·ZIP·체크섬을 릴리스에 직접 첨부합니다. 서명과 새 Windows 11 PC에서의 설치·제거 확인은 아직 완료되지 않았습니다.

### 개발용 스위치

`--` 로 시작하는 인자는 자동 검증용입니다(사람이 쓰는 옵션이 아닙니다).

| 스위치 | 뜻 |
| --- | --- |
| `--simpath=x,y@ms;...` | 커서를 스크립트대로 움직인다(실제 마우스를 건드리지 않음) — 호버 검증용 |
| `--theme=auto\|light\|dark` | 테마 강제 |
| `--ontop=true\|false` | 항상 위 고정을 강제로 켜고 끔 |
| `--pinned=true\|false` | 저장된 설정을 바꾸지 않고 고정/팝업 시작 상태 지정 |
| `--catalog=path` | 다른 프로바이더 정의 파일을 쓴다(기본 `Data\providers.json`) |
| `--simulate=percentPerHour` | 개발용 소스를 **모든** 프로바이더에 붙인다(시간당 %, 난수 없음) |
| `--refresh=ms` | 소스 갱신 주기(기본 60000) |
| `--sample-interval=ms` | 소모 표본 간격(기본 10분) |
| `--sample-span=minutes` | 예측에 쓰는 관측 최소 폭(기본 10분) |
| `--history=path` | 이력 파일 위치(기본 `%LOCALAPPDATA%\Quotra\usage-history.json`) |
| `--forecast-test` | 예측 엔진 자체 점검만 하고 종료(실패 시 종료 코드 1) |
| `--provider-test=id` | 프로바이더 소스 자체 점검만 하고 종료(실패 시 종료 코드 1) |
| `--provider-once=id` | 프로바이더를 1회 조회하고 결과만 로그로 남긴 뒤 종료 |
| `--provider-url=id=url` · `--provider-file=id=path` | 조회 주소·자격증명 경로 강제(실패 경로 검증용) |
| `--codex-parse=path` | 저장해 둔 응답 파일로 파서만 검증(네트워크 없음) |
| `--exit-after=ms` | 지정 시간 뒤 스스로 종료 |
| `--capture=path` | 3초 뒤 XAML 화면을 PNG로 저장하고 배경 투명 상태·콘텐츠 경계를 확인(Desktop Acrylic 자체는 창 캡처에만 보임) |

```powershell
$out = 'src\Quotra.WinUI\bin\x64\Release\net10.0-windows10.0.22000.0\win-x64'
$exe = "$out\QuotraWinUI.exe"
$repo = (Get-Location).Path

# Codex 실연동 1회 조회 — 값·엔드포인트·실패 사유가 로그에 남는다
Start-Process $exe -WorkingDirectory $out -ArgumentList '--provider-once=codex' -Wait -RedirectStandardOutput co.log

# 파서만 점검(창을 만들지 않음, 26케이스) · 예측 엔진 점검(27케이스)
Start-Process $exe -WorkingDirectory $out -ArgumentList '--provider-test=codex' -Wait -RedirectStandardOutput ct.log
Start-Process $exe -WorkingDirectory $out -ArgumentList '--forecast-test' -Wait -RedirectStandardOutput ft.log

# 확장 지점 확인: 테스트 카탈로그 + 개발용 소스(시간당 120%) → 20초 뒤 예측이 화면에 나온다
Start-Process $exe -WorkingDirectory $out -ArgumentList "--catalog=$repo\tools\fixtures\test-providers.json",'--simulate=120',
  '--refresh=1000','--sample-interval=1000','--sample-span=0.2',
  "--history=$repo\tools\fixtures\history-battery.json",'--exit-after=36000'

# 호버 확인: 커서를 첫 셀 가운데로 옮긴다 → 로그에 hover 두 줄(값 도착 전/후), 캡처에 "남음 84% · 리셋 …"
Start-Process $exe -WorkingDirectory $out -ArgumentList '--simpath=1900,60@4000','--exit-after=26000'
```

## 3. 구조

```
src/Quotra.WinUI/               주력 앱 (WinUI 3)
├─ App.xaml(.cs)                배선: 설정 → 카탈로그 → 소스 등록 → 창 → 표본 수집 → 시스템 메시지 → 엣지 감지
├─ Log.cs                       로그(%LOCALAPPDATA% 실패 시 %TEMP% 로 폴백) + 부팅 식별자
├─ Interop/                     Native(P/Invoke), ScreenInfo, CursorProbe, EdgeWatcher, SystemMessageSink
├─ Controls/BatteryGauge.xaml  가로 배터리 게이지(트랙 + 왼쪽부터 채움 + 단자)
├─ Controls/ProviderCell.xaml  서비스 배지·이름·한도 종류 + 네이티브 호버 상태
├─ Diagnostics/WidgetCapture.cs 검증용 실제 XAML 렌더링과 경계·배경 투명 상태 검사
├─ Views/WidgetWindow.xaml     가로 배터리 바 · 셀별 호버 · 창 크롬 · 테마
├─ Themes/Theme.xaml            ThemeDictionaries(Dark/Light/HighContrast) + 컨트롤 스타일
├─ Themes/Palette.cs            테마 무관 색(브랜드·등급) 단일 출처
├─ Themes/ColorMath.cs          Mix/Lighten/Darken/WithAlpha
├─ Models/ProviderUsage.cs      테마 의존 없는 모델(값이 없으면 null — 0 과 구분)
├─ Services/                    UsageService(카탈로그+소스) · IUsageSource(확장 지점) · UsageSampler(10분 표본)
│                               · CodexUsageSource · ClaudeUsageSource · CommandCodeUsageSource · OpenCodeUsageSource · SimulatedUsageSource(개발용 예시)
│                               · WidgetSettings · SystemTheme
└─ Data/providers.json          프로바이더 정의(기본 = ChatGPT + Claude + Command Code + OpenCode Go) + providers.example.json(형식 예시)

docs/providers.md               프로바이더를 붙이는 방법 + Codex·Claude·Command Code 연동 상세

src/Quotra.Shared/Forecast/     예측 엔진 — UI·색 타입에 의존하지 않아 두 앱이 함께 쓸 수 있다
├─ UsagePoint.cs                시각 + 사용률 표본, 한도 창(Primary/Secondary)
├─ UsageHistory.cs              시리즈별 표본(7일 보관) + JSON 영속화
├─ DepletionForecast.cs         리셋 절단 → 최소제곱 기울기 → 고갈 시각/상태 + 문구
└─ ForecastSelfTest.cs          합성 시계열 19케이스 자체 점검

src/Quotra.Shared/Providers/    Codex 파서·클라이언트 — 네트워크·파싱도 UI 타입을 쓰지 않는다
├─ CodexUsageParser.cs          이름 대신 "창 길이"로 주간/세션을 가르는 파서
├─ CodexUsageClient.cs          auth.json 읽기(읽기만) + 후보 경로 폴백 + 실패 분류
└─ CodexSelfTest.cs             합성 응답 26케이스 자체 점검

tools/                          검증 도구와 fixtures/ (verify-fluent.ps1, window-shot.ps1, image-diff.ps1)
tools/fixtures/                 검증용 응답·카탈로그·이력 JSON
docs/winui3.md                  기능별 실측 증거 · 환경 함정 · 이식성 메모
```

## 4. 소모량 추적과 고갈 예측

"지금 몇 % 남았나"만으로는 부족합니다. 같은 20%라도 1시간 뒤 바닥날 수도, 3일 뒤 바닥날 수도 있습니다.
그래서 **10분마다 시각이 붙은 표본**을 남기고 최근 3시간의 기울기를 봅니다.

| 위치 | 표시 |
| --- | --- |
| 배터리 안(평소) | `20%/10분 → 2시간 14분 남음` — 예측 감소량과 고갈까지 남은 시간. 24시간 이상이면 `1일 3시간 남음`처럼 표시합니다 |
| 배터리 안(호버) | `남음 84% · 리셋 6일 13시간` — 퍼센테이지와 초기화까지 남은 시간(그 셀 문구만 바뀌고 창 크기는 그대로) |
| 값이 없을 때 | 해당 서비스를 숨깁니다. 사용량이 확인되면 자동으로 추가합니다 |

상태: `측정 중`(표본 부족) · `소모 없음`(한도 회복 포함) · `20%/10분 → 2시간 14분 남음`(고갈 예상) · `리셋 먼저` · `여유`(7일 초과) · `소진됨`.

**설계 판단 넷** (자세한 근거와 실측은 [docs/winui3.md](docs/winui3.md))

1. **리셋 절단** — 한도가 리셋되면 사용률이 뚝 떨어지므로 마지막 리셋 이후 표본만 씁니다.
2. **최소제곱 기울기** — 처음·끝 두 점만 빼면 잡음에 그대로 흔들립니다.
3. **관측 폭 하한 = 표본 간격(10분)** — 10분 간격이면 **두 번째 표본**에 첫 예측이 나옵니다. 폭 하한이 없으면 1~2분 사이의 우연한 차이를 속도로 오인합니다.
   앱이 꺼져 있던 구간은 기록하지 않습니다 — 없던 시간을 지어내면 예측이 조용히 틀어집니다.
4. **긴 공백 절단** — 표본 사이가 시리즈 **간격 중간값**의 4배를 넘으면 리셋처럼 끊습니다. 재시작이 끼면 기울기의 분모만 커져
   속도가 낮게 나오던 결함을 고쳤습니다(주입 20%/10분 → 공백 뒤 0.8%/10분으로 읽히던 것이 20.0%/10분으로 복원).

이력은 `%LOCALAPPDATA%\Quotra\usage-history.json` 에 7일치를 남기고, 임시 파일 교체로 저장 중 종료돼도 깨지지 않게 했습니다.
설정은 `%LOCALAPPDATA%\Quotra\winui-settings.json`, 로그는 `%LOCALAPPDATA%\Quotra\quotra-winui.log`(실패 시 `%TEMP%\Quotra\`)입니다.
표본을 남길 때마다 로그에 한 줄이 남습니다 — 예측이 왜 그렇게 나왔는지는 이 줄로 확인합니다.

```
sample: 표본 240개 · 3곳 기록 · 값 있는 곳 3/3곳 · 대표 Grok: 20%/10분 소모 · 소진 예상 01:57 (약 31분) ·
        고갈 예상 3곳: ChatGPT 20%/10분 → 02:11, Claude 20%/10분 → 02:04, Grok 20%/10분 → 01:57   ← 개발용 3곳
sample: 표본 8개 · 1곳 기록 · 값 있는 곳 1/1곳 · 대표 ChatGPT: 0.61%/10분 소모 · 소진 예상 내일 02:52 (약 22시간) ·
        고갈 예상 1곳: ChatGPT 0.61%/10분 → 내일 02:52                                             ← 실연동(Codex 주간 한도)
```

값이 하나도 없으면 로그도 그렇게 말합니다 — `sample: 표본 0개 · 연동된 값 없음(미연동)`.
표본이 0개라는 사실이 곧 "예측할 근거가 없다"는 뜻입니다.

## 5. 검증

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force

# WinUI 창은 GDI 화면 캡처에 안 잡힌다(WS_EX_NOREDIRECTIONBITMAP) → PrintWindow 로 창 자신을 그린다
.\tools\window-shot.ps1 -Out docs\shot.png

# 두 PNG 의 차이 픽셀 수·최대 채널 차이·차이 영역 bbox
.\tools\image-diff.ps1 -A docs\before.png -B docs\after.png
```

| 항목 | 실측 증거 |
| --- | --- |
| 엔진 자체 점검 | `--forecast-test` **27/27 통과**, 종료 코드 0(표본 1개/2개 경계 · 1분 폭 무시 · 0% 로 뭉개지지 않는 문구 · 재시작 표본 혼입 포함) |
| Codex 파서 점검 | `--provider-test=codex` **27/27 통과**, 종료 코드 0(실측 응답 형태·창 두 개·0~1 스케일·에포크 리셋·이름 변경·실패 2종) |
| Codex 실연동 | `--provider-once=codex` → `status=Ok endpoint=…/backend-api/wham/usage` · `windows=[10080m:10%] plan=prolite` · `/api/codex/usage` 는 **403** 이라 폴백이 실제로 쓰임 |
| Codex 실패 경로 | 도달 불가 호스트 → `NetworkError`, 없는 자격증명 → `NoCredential`, 깨진 JSON → `SchemaChanged`(전부 사유가 로그에 남고 프로세스는 정상 종료) |
| 확장 지점 | 테스트 카탈로그 3곳 + 개발용 소스(시간당 120%) → 측정 **20%/10분**(=120÷6) · 셀마다 고갈 시각 산출 |
| 미연동 상태 | 빈 카탈로그(`tools\fixtures\empty-providers.json`) → `프로바이더 0곳` · `sample: 표본 0개 · 연동된 값 없음(미연동)` · `프로바이더 없음` 안내 |
| 이력 오염 없음 | 값이 `null` 인 프로바이더는 표본을 남기지 않아 예측이 0% 에서 출발하지 않음 |
| 위젯 배치(실연동 1곳) | 논리 `290×72`, 물리 `(1703,21)-(2138,129)` = 435×108 px(DPI 144, 배율 1.5) |
| 배터리 배치(3곳) | 논리 `702×72`, 물리 `(1394,21)-(2447,129)` = 1053×108 px |
| 호버 공개 | 로그 `hover: ChatGPT → 남음 84% · 리셋 6일 13시간` · **창 크기는 그대로**(290×72) |
| 호버 해제 | 커서가 밖으로 나가면 `hover: 없음` → 전 셀이 예측 감소량으로 복귀 |
| 첫 표본 | 앱을 켠 직후 값이 들어오는 즉시 남는다 — 로그 `샘플러: 첫 조회를 기다리는 중(값 없음) · 5초 간격으로 재시도` → 곧바로 `표본 1개 · 1곳 기록`(예전엔 첫 10분을 버렸다) |
| 이력 보존 | 10분 간격 이력에 재시작 표본(30초 간격)이 섞여도 버리지 않는다 — `tools\fixtures\history-clustered.json`(12표본) → `0.64%/10분`(고치기 전엔 전부 잘려 `측정 중`) |
| 배터리 안 숫자 | 채움 84% 에서 `0.67%/10분 → 내일 00:48` 이 채움 위에서 어둡게, 잔량 50% 아래 구간은 밝게 나뉘어 읽힘(빠른 소모 캡처로 대비 확인) |
| 항상 위 | Discord 를 `HWND_TOP` 으로 올린 뒤에도 Z 순서 위젯 2 < Discord 5 |
| 포커스 | 창을 띄워도 `0x70394 → 0x70394` 유지 |
| 우클릭 메뉴 | WinUI MenuFlyout 열림과 서비스 설정 창 렌더링 확인 |
| 아크릴 | `DesktopAcrylicController.IsSupported()=True 적용=True` |
| 테마 | 시스템 다크 상태에서 `--theme=light` 강제 → 흰 배경. 보조 텍스트 토큰 픽셀 검증 `#61667A` 2905px |

전체 증거와 이 환경의 함정(스토우드 예외 `0xc000027b`, GDI 캡처, `CornerRadius`×DWM)은
[docs/winui3.md](docs/winui3.md) 에 정리해 두었습니다.

## 6. 알려진 제약 / 다음 단계

**제약**

1. **로그인과 사용량 조회의 호환성은 제공자의 변경에 영향을 받습니다.** Codex는 공식 App Server의 계정 조회를 우선 사용하고, 실패하면 기존 로그인 파일과 HTTP 경로를 사용합니다. Claude 웹/OAuth와 Command Code `/alpha` 경로는 공개 API 계약이 보장되지 않습니다. 오류 원인과 해결 방법은 프로바이더 설정에서 확인하세요. 기존 로그인 파일은 읽기만 하고, Quotra의 Claude 로그인은 Windows 자격 증명 보관함에 저장합니다. 토큰은 로그에 기록하지 않습니다.
2. 예측은 **앱이 켜져 있는 동안의 표본**만 씁니다. 꺼져 있던 구간은 비어 있고, 다시 켜면 그 구멍은 예측에서 잘라냅니다(메우지는 않습니다).
3. 리셋 시각은 소스가 주는 `ResetsAt` 을 씁니다 — 소스가 안 주면 `리셋 정보 없음` 이라고 표시합니다(추측하지 않습니다).
4. 프로바이더가 6곳을 넘으면 바가 화면 폭을 넘습니다(가로 배치라 줄바꿈 없음).
5. 상단 엣지 흔들기는 **실제 커서 입력**이 필요합니다(원격/자동화 환경에서는 `--simpath=` 로 대체).

**다음 단계 후보**

1. Codex `credits`/`rate_limit_reset_credits`(리셋권 3장) 같은 부가 정보를 호버에 한 줄 더
2. 임계치 도달 시 Windows 알림
3. 업데이트 알림 또는 자동 업데이트
4. 상세 설정 화면(지금은 우클릭 메뉴 + JSON 파일)
5. 프로바이더가 많아질 때 페이지 전환/스크롤

## 7. 라이선스

["THE BEER-WARE LICENSE" (Revision 42)](LICENSE) — 이 소프트웨어는 마음대로 써도 됩니다. 언젠가 만나면 맥주 한 잔 사주세요.
