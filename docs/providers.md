# 프로바이더 붙이기 (확장 지점)

OpenCode Go는 위젯 우클릭 → **프로바이더 설정…**에서 API 키를 입력합니다.
**연결 확인 및 저장**은 Go 사용량 조회 성공 후 Windows 자격 증명 보관함에 키를 저장합니다.
저장된 키로 `https://opencode.ai/zen/go/v1/usage`를 조회해 월간 잔여량과 리셋 시각만 배터리에 표시합니다.
키를 입력하지 않은 프로바이더는 위젯에 나타나지 않습니다. **연결 해제**는 키와 표시 값을 제거합니다.
키는 설정 JSON 및 로그에 기록하지 않으며 다른 앱의 키를 자동으로 가져오지 않습니다.
`--provider-test=opencode-go`로 사용률·리셋 시각 변환을 자체 검증할 수 있습니다.

Command Code는 기존 Command Code 로그인을 자동으로 사용합니다. Command Code CLI 또는 Desktop에서 로그인하면
`%USERPROFILE%\.commandcode\auth.json` 을 읽고, `COMMAND_CODE_API_KEY` 환경 변수가 있으면 그 값을 우선합니다.
별도 키 복사나 Quotra 설정창에서의 저장은 필요하지 않습니다.

Quotra 위젯은 **"무엇을 보여줄지"** 와 **"얼마나 썼는지"** 를 분리합니다.

| 역할 | 담당 | 파일 |
| --- | --- | --- |
| 무엇을 보여줄지 | 카탈로그(JSON) | `src/Quotra.WinUI/Data/providers.json` |
| 얼마나 썼는지 | `IUsageSource` 구현체 | `src/Quotra.WinUI/Services/IUsageSource.cs` |
| 둘을 잇는 곳 | `[UsageSource(id)]` 표시 → `UsageService.Register` 자동 호출 | `src/Quotra.WinUI/App.xaml.cs` 의 `RegisterAnnotatedSources` |

목업 난수는 **없습니다.** 소스가 등록되지 않은 프로바이더는 값이 `null` 로 남고 위젯은
`미연동` 이라고 표시합니다 — 0% 로 그리면 "한도가 가득 찼다"로 잘못 읽히기 때문입니다.

**Codex(ChatGPT 플랜), Claude, Command Code가 기본 연동됩니다.** 기본 카탈로그에 들어 있어 앱을 켜면
각 서비스의 기존 로그인 정보를 찾아 사용량을 자동으로 읽습니다. OpenCode Go는 API 키를 설정한 경우 표시됩니다.
Codex는 4절, Claude는 5절, Command Code는 6절을 참고하세요.

---

## 1단계 — 카탈로그에 정의 추가

`Data/providers.json` (또는 `--catalog=` 로 지정한 파일)에 한 항목을 넣습니다.
형식 예시는 [providers.example.json](../src/Quotra.WinUI/Data/providers.example.json).

```jsonc
[
  {
    "id": "codex",           // 소스를 연결하는 키(App.xaml.cs 의 Register 와 같아야 한다)
    "name": "ChatGPT",       // 카드 제목
    "vendor": "OpenAI",
    "plan": "ChatGPT 플랜",   // 소스가 알려주면 그 값이 덮어쓴다(응답의 plan_type)
    "accent": "#5FD3A8",     // 배터리 테두리·눈금 색
    "primaryLabel": "주간",   // 장기 한도 이름
    "secondaryLabel": "세션", // 단기 한도 이름
    "note": "Codex 클라이언트 엔드포인트로 조회(공식 API 아님)",
    "enabled": true
  }
]
```

**값(사용률·리셋)은 여기에 쓰지 않습니다.** 카탈로그는 정적 정의만 담당합니다.
JSON 에 `//` 주석과 끝 쉼표를 쓸 수 있습니다(로더가 허용).

## 2단계 — `IUsageSource` 구현

실제 구현은 [CodexUsageSource.cs](../src/Quotra.WinUI/Services/CodexUsageSource.cs) 입니다.

```csharp
[UsageSource("codex")]                       // 등록 표시 — App 이 리플렉션으로 자동 등록한다
public sealed class CodexUsageSource : IUsageSource
{
    public string DisplayName => "Codex (ChatGPT 플랜 한도)";
    public string ShortName => "Codex";          // 로그에 쓰는 짧은 이름

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        CodexFetchResult result = await CodexUsageClient.FetchAsync(cancellationToken);

        if (!result.Ok)
        {
            throw new InvalidOperationException(Explain(result));   // 이 프로바이더만 "조회 오류"
        }

        CodexUsage usage = result.Usage!;
        DateTimeOffset now = DateTimeOffset.Now;

        return new ProviderSnapshot(
            Primary: usage.WeeklyUsed is { } weekly
                ? new WindowUsage(weekly, usage.WeeklyResetsIn is { } w ? now + w : null)
                : null,
            Secondary: usage.SessionUsed is { } session
                ? new WindowUsage(session, usage.SessionResetsIn is { } s ? now + s : null)
                : null,
            Plan: PlanLabel(usage.Plan));
    }
}
```

### 계약 규칙

| 규칙 | 이유 |
| --- | --- |
| 값이 확실하지 않으면 `null` | `0` 은 "0% 사용"이라는 **주장**입니다. 모르는 것과 다릅니다 |
| 실패는 예외로 | `UsageService` 가 그 프로바이더만 `조회 오류` 로 표시하고 나머지는 계속 돌립니다 |
| 실패 문구에 다음 행동을 담기 | 토큰 만료면 "Codex 앱을 한 번 실행하면 갱신됩니다"처럼 사용자가 할 일을 남깁니다 |
| 블로킹 IO 금지 | `Task.Run` 으로 워커에서 시작하지만, UI 스레드를 붙잡는 동기 대기는 창을 멈춥니다 |
| 리셋은 **절대 시각**(`DateTimeOffset`) | 응답은 "몇 초 뒤"로 오므로 조회 시점에 한 번 변환합니다 |
| 토큰/자격증명을 로그에 남기지 않기 | 실패 사유에 경로·상태 코드만 남깁니다 |
| API 키 입력형은 `IKeyedUsageSource` | 설정 창이 프로바이더 목록을 자동으로 만들고, 키 저장·검증·삭제를 소스가 담당합니다 |
| 예측 이력 분리는 `HistoryKey` | 창 구성이 바뀌어 예전 표본과 섞이면 안 될 때 스냅샷에 지정합니다(예: OpenCode Go 월간) |

## 3단계 — 등록

`src/Quotra.WinUI/App.xaml.cs` 의 `OnLaunched` 에서 카탈로그를 읽은 직후:

```csharp
// 실연동: Codex(ChatGPT 플랜) 사용량. 카탈로그에 id=codex 가 있을 때만 실제로 조회한다.
if (_usage.Providers.Any(p => p.Id == "codex"))
{
    _usage.Register("codex", new CodexUsageSource(_codexHost, _codexAuthPath));
}
```

`Register` 한 순간부터 그 프로바이더는 `미연동` → 실제 값으로 바뀝니다(배터리 채움 + 예측 감소량).
갱신 주기는 `--refresh=ms`(기본 60초)입니다.

---

## 4. Codex 실연동

설치된 Codex 데스크톱·CLI의 **공식 [App Server](https://learn.chatgpt.com/docs/app-server)** 를 먼저 실행해
`account/read`, `account/rateLimits/read`로 사용량을 읽습니다. 클라이언트가 자신의 파일·Windows 보안 저장소를 처리하며,
Quotra는 RPC에서 로그인 토큰을 받지 않습니다. 별도 CLI 설치 없이 데스크톱에 포함된 `codex.exe`도 탐색합니다.
이 클라이언트 조회를 사용할 수 없으면 기존 `auth.json`·사용량 HTTP 경로로 대체합니다.
`CODEX_HOME`이 설정되면 그 폴더를 사용하고, 기본값은 `%USERPROFILE%\.codex`입니다.
환경 변수는 프로세스·Windows 사용자·시스템 순서로 확인합니다.
로그인 저장 방식은 [Codex 인증 문서](https://learn.chatgpt.com/docs/auth)에 설명되어 있습니다.

| 항목 | 값 |
| --- | --- |
| 호스트/경로 | `https://chatgpt.com` + `/api/codex/usage` → 실패 시 `/backend-api/wham/usage` |
| 실측(2026-10-12) | `/api/codex/usage` → **403**, `/backend-api/wham/usage` → **200** (그래서 후보가 둘이다) |
| 자격증명 | 클라이언트가 로그인 저장소를 처리. HTTP 대체 경로는 `CODEX_HOME/auth.json` → `tokens.access_token`, `tokens.account_id` |
| 헤더 | `Authorization: Bearer …`, `chatgpt-account-id: …`, `originator: codex_cli_rs`, `Accept: application/json` |
| 주간/세션 구분 | 필드명이 버전마다 바뀌므로 **창 길이**로 가릅니다 — 3일(4320분) 이상 = 주간, 미만 = 세션 |
| 사용률 필드 후보 | `used_percent` · `usedPercent` · `percent_used`는 백분율. `used`는 0~1 스케일도 허용 |
| 창 길이 후보 | `windowDurationMins` · `window_minutes` · `window_seconds` · `limit_window_seconds` · `window_hours` |
| 리셋 후보 | `resetsAt` · `resets_in_seconds` · `reset_after_seconds` · `reset_at` · `resets_at` · `resets_at_seconds` |
| 주간 창을 `Primary` 로 | 위젯의 배터리·예측은 "오래 사는 한도"를 기준으로 돕니다. 세션 창은 `Secondary` |
| 파일 | 파서·클라이언트 `src/Quotra.Shared/Providers/`, 어댑터 `src/Quotra.WinUI/Services/CodexUsageSource.cs` |

**왜 이름이 아니라 창 길이인가** — 실측 응답(`plan_type=prolite`)은 `primary_window` 가 **7일** 창이었습니다.
Codex 응답은 5시간 창이 `primary`, 7일 창이 `secondary` 로 오는 경우도 있어 이름 기반 매핑은 순서가 뒤집힙니다.
그래서 트리 전체에서 "사용률 + 창 길이"를 가진 객체를 모아 **길이로** 가릅니다.

**주간 창이 없으면 세션 창을 주 배터리에 표시합니다.** 라벨을 `세션`으로 바꾸고,
예측 이력을 주간과 분리합니다. 없는 한도를 0으로 채우지 않습니다.

### 실패는 이렇게 나옵니다

| 상태 | 화면 | 로그의 사유 |
| --- | --- | --- |
| `NoCredential` | `조회 오류` | `auth.json` 에 ChatGPT 토큰이 없음(API 키 로그인 상태일 수 있음) |
| `Expired` | `조회 오류` | 401/403 — Codex 앱에서 로그인을 갱신하세요 |
| `RateLimited` | `조회 오류` | 429 |
| `NetworkError` | `조회 오류` | 연결 거부/시간 초과(10초) |
| `SchemaChanged` | `조회 오류` | 200 인데 해석 불가 — 원문을 보관해 파서를 고칠 수 있게 합니다 |

어느 경우든 **그 프로바이더만** `조회 오류` 이고 나머지는 계속 돕니다. 직전 값은 지우지 않습니다.

---

## 5. Claude 실연동

Claude는 **Quotra에서 직접 연결한 로그인 → Claude 데스크톱 로그인 → Claude Code OAuth** 순서로 찾습니다.
이 경로는 Anthropic이 공개 안정 API로 문서화한 경로가 아니므로, 응답 형식이나 접근 정책이 바뀌면
위젯에 `조회 오류`가 표시될 수 있습니다. Claude Pro/Max의 Claude와 Claude Code 사용량은 같은
한도를 공유하며 5시간 및 주간 한도를 지원합니다([Anthropic 도움말](https://support.claude.com/en/articles/11145838-use-claude-code-with-your-pro-or-max-plan)).
현재 사용하는 OAuth 사용량 경로의 형식은 [Anthropic Claude Code 저장소의 이슈](https://github.com/anthropics/claude-code/issues/84995)에 보고되어 있습니다.

| 항목 | 값 |
| --- | --- |
| 엔드포인트 | 웹 로그인: `https://claude.ai/api/organizations` 및 `/api/organizations/{id}/usage`. Claude Code: `https://api.anthropic.com/api/oauth/usage` |
| 자격증명 | 데스크톱: Claude 전용 프로필의 `sessionKey` 쿠키. Claude Code: `CLAUDE_CONFIG_DIR/.credentials.json`의 `claudeAiOauth.accessToken` (기본 `%USERPROFILE%\.claude`) |
| 헤더 | `Authorization: Bearer …`, `anthropic-beta: oauth-2025-04-20`, `Accept: application/json` |
| 주간/5시간 매핑 | `seven_day`를 배터리에, `five_hour`를 보조 한도에 표시. 주간 값이 없으면 주 배터리를 5시간으로 표시하고 이력 분리 |
| 사용률 | 응답의 `utilization` 백분율(0~100)을 내부 0~1 값으로 변환 |
| 리셋 | `resets_at` 절대 시각을 그대로 사용 |
| 플랜 | 로그인 파일의 `subscriptionType` 이 Pro/Max/Free 중 하나면 표시 |
| 파일 | 요청·응답 파서 `src/Quotra.WinUI/Services/ClaudeUsageSource.cs` |

Claude 데스크톱은 `%APPDATA%\Claude` 및 Microsoft Store 패키지의 Claude 전용 프로필에서
로그인 쿠키만 읽습니다. 일반 브라우저의 쿠키나 대화 내역은 탐색하지 않습니다.
Windows DPAPI 및 Chromium AES-GCM 저장 형식을 지원하며, 앱 전용 암호화(`v20`), 접근 거부,
저장소 잠금 등으로 읽을 수 없으면 이유를 설정에 표시합니다. 암호화를 우회하지 않습니다.
웹 조회 흐름은 [CodexBar의 공개 구현](https://github.com/steipete/CodexBar/blob/main/docs/claude.md)을 참고했습니다.

자동 연결이 안 되면 **프로바이더 설정 → Claude → Claude 로그인**에서 로그인한 뒤
**로그인 확인 및 연결**을 누릅니다. 별도 CLI 설치나 다른 PC 사용자의 폴더 확인이 필요하지 않습니다.
조회에 성공한 로그인만 Windows 자격 증명 보관함에 저장합니다. 로그인 창은 Quotra 전용 WebView2
프로필을 사용하며 닫을 때 쿠키를 지웁니다. 웹 접근 정책 때문에 403이 반환되면 차단 사유를 안내하며
연결 성공으로 저장하지 않습니다. 조직이 여러 개면 로그인 창에서 사용하려는 조직으로 전환하세요.
**Quotra 로그인 삭제**는 Quotra가 저장한 로그인만 삭제합니다. 새로고침하면 기존 앱 로그인을 다시 찾습니다.

자격증명 파일은 **매 조회마다 읽기만** 합니다. 토큰을 갱신하거나 파일을 수정하지 않으며,
토큰과 원문 응답도 로그나 오류 문구에 남기지 않습니다. Claude Code가 로그인 정보를 갱신하면
다음 자동 조회부터 새 토큰을 읽습니다. 기본 갱신 간격은 60초입니다.

로그인이 없거나 만료되면 **설정의 Claude 항목**에 실패 원인과 로그인 안내가 나옵니다.
값을 받은 적이 없는 서비스도 설정 목록에 표시됩니다. HTTP 429는 다음 주기 때 다시 시도하고, 401·403·기타 오류는
상태 코드만 표시합니다. 구독 로그인을 사용하며 Anthropic Console API 키나
API 크레딧은 사용하지 않습니다.

---

## 6. Command Code 실연동

Command Code 공식 사이트의 Studio **Usage** 탭은 요청별 비용·토큰 사용 내역을 보여주고, CLI의 `/usage` 는 크레딧과 주간·5시간 한도를 보여줍니다([Usage 한도 안내](https://commandcode.ai/docs/resources/pricing-limits)).
공식 [설정 문서](https://commandcode.ai/docs/settings)는 CLI 로그인 정보를 `%USERPROFILE%\.commandcode\auth.json` 에 저장한다고 설명하며,
`COMMAND_CODE_API_KEY` 환경 변수도 지원합니다. Quotra는 이 로그인 정보를 읽기만 해
[`CommandCodeUsageSource`](../src/Quotra.WinUI/Services/CommandCodeUsageSource.cs)에서 `api.commandcode.ai` 의 계정 사용량 경로로 주간·5시간 창을 가져옵니다.

| 항목 | Command Code | OpenCode Go(Quotra 기존 연동) |
| --- | --- | --- |
| 사이트/앱의 기준 화면 | [Command Code Studio Usage](https://commandcode.ai/settings/usage), CLI `/usage` | [OpenCode Go 사용량 문서](https://docs.opencode.ai/docs/go/)와 사용량 API |
| 인증 | 기존 Command Code 로그인 파일 또는 `COMMAND_CODE_API_KEY` | 설정창에 OpenCode Go API 키를 직접 입력 |
| Quotra 조회 경로 | `GET /alpha/whoami?limits=1` 후 `GET /alpha/billing/credits` | `GET https://opencode.ai/zen/go/v1/usage` |
| 서비스 한도 | 주간·5시간 롤링 한도와 월간 크레딧 | 5시간·주간·월간 사용 한도 |
| 위젯 창 | 주간(Primary) + 5시간(Secondary) 표시 | 현재 연결은 월간(Primary)만 표시 |
| 인증/조회 성격 | CLI 로그인으로 CLI 계정 API 조회 | 설정창에 API 키를 저장하고 Go 사용량 API 조회 |

Command Code [Provider API 문서](https://commandcode.ai/docs/provider)의 공개 `/provider/v1` 은 **모델 추론 요청**용이며 사용량 조회 API가 아닙니다.
Quotra의 `/alpha/...` 사용량 경로는 CLI `/usage` 와 같은 계정 한도 데이터를 제공하는 현재 계정 API 경로입니다.
`alpha` 경로는 별도 공개 API 계약으로 문서화되지 않았으므로 Command Code 측 변경에 따라 고장 날 수 있습니다.
로그인 파일과 키는 읽기만 하며, Quotra는 토큰을 갱신하거나 로그에 쓰지 않습니다.

`--provider-test=commandcode` 는 아직 네트워크 없는 자체 파서를 제공하지 않습니다. 연결 상태는 아래 명령으로 확인합니다.

```powershell
$out = 'src\Quotra.WinUI\bin\x64\Release\net10.0-windows10.0.22000.0\win-x64'
$exe = Join-Path $out 'QuotraWinUI.exe'
Start-Process $exe -WorkingDirectory $out -ArgumentList '--provider-once=commandcode' -Wait -RedirectStandardOutput commandcode.log
```

## 값을 확인하는 법

```powershell
$out = 'src\Quotra.WinUI\bin\x64\Release\net10.0-windows10.0.22000.0\win-x64'
$exe = "$out\QuotraWinUI.exe"
$repo = (Get-Location).Path        # 저장소 루트에서 실행

# 1) Codex 파서만 점검(네트워크 없음 · 합성 응답 26케이스) — 프로바이더 공통: --provider-test=<id>
Start-Process $exe -WorkingDirectory $out -ArgumentList '--provider-test=codex' -Wait -RedirectStandardOutput ct.log

# 2) 실연동 1회 조회(창 없음) — 값·엔드포인트·실패 사유가 로그에 남는다 — 공통: --provider-once=<id>
Start-Process $exe -WorkingDirectory $out -ArgumentList '--provider-once=codex' -Wait -RedirectStandardOutput co.log

# 3) 저장해 둔 응답으로 파서만 검증(tools\fixtures\codex-usage-sample.json)
Start-Process $exe -WorkingDirectory $out -ArgumentList "--codex-parse=$repo\tools\fixtures\codex-usage-sample.json" -Wait

# 4) 실패 경로 검증(도달 불가 호스트 / 자격증명 경로 없음) — 공통: --provider-url=<id>=<주소>, --provider-file=<id>=<경로>
Start-Process $exe -WorkingDirectory $out -ArgumentList '--provider-once=codex','--provider-url=codex=https://127.0.0.1:9' -Wait
Start-Process $exe -WorkingDirectory $out -ArgumentList '--provider-once=codex','--provider-file=codex=no-such-auth.json' -Wait

# 5) 예측 엔진만 점검(합성 시계열 19케이스)
Start-Process $exe -WorkingDirectory $out -ArgumentList '--forecast-test' -Wait -RedirectStandardOutput ft.log
```

### 값을 만들지 않고 화면만 보는 법

소스를 붙이기 전에도 **화면과 예측이 도는지**는 확인할 수 있습니다.

```powershell
# 미연동 상태: 배터리 빈 트랙 + "프로바이더 없음"(빈 카탈로그)
Start-Process $exe -WorkingDirectory $out -ArgumentList "--catalog=$repo\tools\fixtures\empty-providers.json",'--exit-after=6000' -Wait

# 개발용 소스로 값을 흘려보내 화면·예측까지 확인(시간당 120% = 20%/10분)
Start-Process $exe -WorkingDirectory $out -ArgumentList "--catalog=$repo\tools\fixtures\test-providers.json",
  '--simulate=120','--refresh=1000','--sample-interval=1000','--sample-span=0.2',
  "--history=$repo\tools\fixtures\history-sim.json",'--exit-after=36000'
```

`SimulatedUsageSource` 는 **`IUsageSource` 구현 예시**입니다 — 필요한 메서드가 둘뿐이라는 것을
보여주는 가장 짧은 예제이므로, 새 소스를 쓸 때 이 파일을 복사해 시작하면 됩니다.
`--simulate` 를 주지 않으면 앱에는 어떤 가짜 값도 없습니다.

---

## 다음 프로바이더를 붙이는 순서

1. 카탈로그에 항목을 추가한다(`id`·`name`·`accent`·`primaryLabel`).
2. `src/Quotra.Shared/Providers/` 에 파서를 만든다 — **UI 타입을 쓰지 않으면 두 앱이 함께 쓸 수 있습니다.**
3. `src/Quotra.WinUI/Services/` 에 `IUsageSource` 어댑터를 만든다(절대 시각 변환·실패 문구).
   클래스에 `[UsageSource("<카탈로그 id>")]` 를 붙이면 등록은 끝이다 — `App.xaml.cs` 는 고치지 않는다.
4. 선택 사항:
   · 자체 점검을 붙이려면 `IUsageSelfTest` 를 구현한다(`--provider-test=<id>` 가 실행한다).
   · API 키 입력형은 `IKeyedUsageSource` 를 구현한다 — 우클릭 → **프로바이더 설정…** 목록에 자동으로 나타난다.
   · 실패 경로 검증용 재정의는 `IUsageDiagnostics` 로 받는다(`--provider-url=<id>=<주소>` · `--provider-file=<id>=<경로>`).
   · 창 구성이 바뀌어 예측 이력을 분리해야 하면 `ProviderSnapshot.HistoryKey` 를 지정한다.
5. 문서·캡처를 갱신한다([winui3.md](winui3.md) 의 검증 표가 기준입니다).

진단 스위치는 id 로만 동작한다: `--provider-test=<id>` · `--provider-once=<id>`.
Codex 의 `--codex-test`/`--codex-once`/`--codex-host`/`--codex-auth` 와 `--opencode-test`/`--opencode-once` 는 별칭으로 계속 동작한다.

## 유명한 미연동 프로바이더 후보

2026-10-13 기준 공식 문서를 확인했습니다. 아래에서 **잔여량 화면**은 제품 UI가 보여주는 값이고, **공식 API**는 Quotra가 공개된 계정 경로로 직접 읽을 수 있는 값입니다. 둘은 다를 수 있습니다. 사용량 통계만 있고 한도 총량·리셋 시각이 없으면 배터리 퍼센트로 단정하지 않습니다.

| 프로바이더 | 공식 사용량·한도 확인 방법 | Quotra 연동성 |
| --- | --- | --- |
| **GitHub Copilot** | 개인 플랜은 월별 AI Credits를 제공하며, [사용량 REST API](https://docs.github.com/en/rest/billing/usage)는 개인 계정의 월별 AI credit 사용 보고서를 돌려줍니다. 사용자 토큰에 `Plan: read` 권한이 필요합니다. | **가장 먼저 검토.** 공개 개인 API가 있습니다. 응답은 사용량 보고서이므로, 잔여 퍼센트와 리셋 시각은 플랜별 포함량과 결합해야 합니다. 조직이 결제하는 Copilot은 사용자 경로에 포함되지 않습니다. |
| **Cursor** | [Spending 대시보드](https://prod.cursor.com/help/models-and-usage/usage-limits)는 두 월간 사용 풀의 실시간 사용량·잔여량·리셋일을 표시합니다. [Analytics API](https://prod.cursor.com/docs/account/teams/analytics-api)는 Enterprise 팀용 분석 API입니다. | **개인용 공개 잔여량 API는 확인되지 않음.** Enterprise 분석은 팀 활동 통계이며 개인 플랜 잔여량 API와는 용도가 다릅니다. |
| **Windsurf** | 공식 [제품 변경 기록](https://docs.windsurf.com/plugins/changelog)은 Windsurf 위젯의 Plan Info 탭에서 크레딧 사용량을 확인하는 기능을 안내합니다. | **화면 확인은 가능, 자동 연동 경로는 미확인.** 검토한 공식 자료에서 개인 잔여량을 읽는 공개 API를 찾지 못했습니다. |
| **JetBrains AI / Junie** | [AI Assistant 사용량 안내](https://www.jetbrains.com/help/ai-assistant/licensing-and-subscriptions.html)는 IDE 위젯에 월간 잔여 AI Credits와 갱신일을 표시한다고 설명합니다. 조직은 [Central Console](https://www.jetbrains.com/help/jetbrains-console/ai-credits-consumption.html)에서 소비 보고서를 볼 수 있습니다. | **개인 잔여량 API는 확인되지 않음.** IDE에서 잔여량은 명확하지만, 조직 콘솔 보고서는 개인 위젯용 실시간 API를 대신하지 않습니다. |
| **Gemini Code Assist / Gemini CLI** | [할당량 문서](https://docs.cloud.google.com/gemini/docs/quotas)는 Standard·Enterprise의 사용자별 일일 요청 한도를 안내합니다. [Cloud Monitoring](https://docs.cloud.google.com/gemini/docs/codeassist/monitor-gemini-code-assist)은 조직의 IDE 사용량 지표를 제공합니다. | **조직 모니터링 중심.** 요청 한도는 있으나 개인별 현재 잔여량 API는 확인되지 않았습니다. Cloud Monitoring 권한과 프로젝트 설정이 필요합니다. |
| **Amazon Q Developer** | [서비스 할당량 문서](https://docs.aws.amazon.com/general/latest/gr/amazonqdev.html)는 기능별 한도를 정의합니다. 사용 대시보드는 [Amazon Q 관리자 전용](https://docs.aws.amazon.com/amazonq/latest/qdeveloper-ug/dashboard.html)이며 Pro 구독자의 활동을 집계합니다. | **조직 관리자용에 가까움.** 기능별 제한은 문서화되어 있지만 개인 구독의 현재 잔여량을 읽는 공개 API는 확인되지 않았습니다. |

### 제안하는 추가 순서

1. **GitHub Copilot** — 개인 사용량 API부터 연결하고, 인증은 `Plan: read` 최소 권한의 GitHub 토큰 흐름으로 처리합니다. 플랜별 월간 포함량과 리셋일을 확인하기 전에는 사용량을 임의의 잔여 퍼센트로 바꾸지 않습니다.
2. **JetBrains AI, Cursor, Windsurf** — 제품 화면의 잔여량은 유용하지만 개인용 공개 조회 API가 확인되지 않았습니다. 공식 연동 경로가 생기거나 로컬에서 안정적으로 읽을 수 있는 공개 계약이 확인될 때 구현합니다.
3. **Gemini Code Assist, Amazon Q Developer** — 개인 배터리보다 조직 대시보드·클라우드 모니터링에 무게가 있어, 조직 계정 지원이 필요해질 때 검토합니다.

### 예상 소모량 예측 가능성

Quotra는 사용률(0~100%)과 리셋 시각을 받아 10분마다 표본을 남기고, 두 표본이 10분 이상 벌어지면 현재 소모 속도를 계산합니다. 그러므로 예측 계산은 여섯 후보 모두에 적용할 수 있지만, 자동 예측에는 사용량을 반복해서 읽을 수 있는 경로가 필요합니다.

| 프로바이더 | 예측 기능 평가 |
| --- | --- |
| **GitHub Copilot** | **조건부 가능.** 월별 AI Credits 사용 보고서 API와 IDE의 한도 진행률·리셋 표시가 있습니다. Quotra 연동 시 API 값의 갱신 빈도와 플랜별 총량을 확인해야 합니다. 보고가 늦거나 변화가 드문 구간에는 예측도 늦게 반응합니다. |
| **Cursor** | **화면에서는 가능, 자동 연동은 미확인.** Spending 화면은 두 풀의 실시간 사용량과 잔여량을 보여줍니다. 개인 계정 공개 API가 확인되지 않아 Quotra가 같은 값을 공식 경로로 주기 수집할 수 있는지는 별도 문제입니다. |
| **Windsurf** | **자동 예측 경로 미확인.** 제품 위젯의 Plan Info에서 크레딧을 확인할 수 있다는 안내는 있으나, 이를 반복 조회할 공개 개인 API는 확인되지 않았습니다. |
| **JetBrains AI / Junie** | **화면에서는 가능, 자동 연동은 미확인.** IDE 위젯에 잔여 크레딧과 갱신일이 있어 예측에 필요한 값은 화면에 있습니다. 공개 개인 조회 API가 확인되지 않아 공식 자동 수집 경로는 미확인입니다. |
| **Gemini Code Assist / Gemini CLI** | **조직 범위에서 조건부 가능.** 일일 요청 한도와 Cloud Monitoring의 요청·토큰 지표가 있습니다. 지표를 개인별로 분리하고 한도와 같은 요청 단위로 맞출 수 있는지 확인해야 하며, 현재 개인 잔여량 API는 확인되지 않았습니다. |
| **Amazon Q Developer** | **조직 범위에서 지연 예측 가능.** 관리자 대시보드는 대부분 시간 단위로 갱신되고, 개인별 사용 보고서는 하루 단위로 생성됩니다. 추세는 계산할 수 있지만 실시간 개인 잔여량처럼 표시할 수는 없고, 기능별 한도를 따로 매핑해야 합니다. |

**정리:** 예측 수식 자체를 여섯 곳 모두에 재사용할 수 있습니다. 하지만 Quotra에서 자동으로 정확한 예측을 제공할 준비가 된 후보는 현재 없습니다. Copilot은 공개 API가 있어 가장 먼저 실측을 검증할 수 있고, Cursor·Windsurf·JetBrains는 화면 표시와 공개 자동 조회 경로 사이에 간극이 있습니다. Gemini·Amazon Q는 조직 데이터 연결과 사용자별 구분이 추가로 필요합니다.

공통 파서와 응답 모델은 `src/Quotra.Shared/Providers/`에 두고 WinUI 앱의 `IUsageSource` 어댑터에서 연결합니다. 새 후보의 API가 사용량만 제공하고 한도 잔여량을 제공하지 않으면 숫자를 추정해 채우지 않고, 확인 가능한 사용량 지표만 별도 표현할 수 있는지 먼저 검토합니다.
