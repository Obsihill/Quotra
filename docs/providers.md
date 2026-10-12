# 프로바이더 붙이기 (확장 지점)

OpenCode Go는 위젯 우클릭 → **프로바이더 설정…**에서 API 키를 입력합니다.
**연결 확인 및 저장**은 Go 사용량 조회 성공 후 Windows 자격 증명 보관함에 키를 저장합니다.
저장된 키로 `https://opencode.ai/zen/go/v1/usage`를 조회해 월간 잔여량과 리셋 시각만 배터리에 표시합니다.
키를 입력하지 않은 프로바이더는 위젯에 나타나지 않습니다. **연결 해제**는 키와 표시 값을 제거합니다.
키는 설정 JSON 및 로그에 기록하지 않으며 다른 앱의 키를 자동으로 가져오지 않습니다.
`--provider-test=opencode-go`로 사용률·리셋 시각 변환을 자체 검증할 수 있습니다.

Quotra 위젯은 **"무엇을 보여줄지"** 와 **"얼마나 썼는지"** 를 분리합니다.

| 역할 | 담당 | 파일 |
| --- | --- | --- |
| 무엇을 보여줄지 | 카탈로그(JSON) | `src/Quotra.WinUI/Data/providers.json` |
| 얼마나 썼는지 | `IUsageSource` 구현체 | `src/Quotra.WinUI/Services/IUsageSource.cs` |
| 둘을 잇는 곳 | `[UsageSource(id)]` 표시 → `UsageService.Register` 자동 호출 | `src/Quotra.WinUI/App.xaml.cs` 의 `RegisterAnnotatedSources` |

목업 난수는 **없습니다.** 소스가 등록되지 않은 프로바이더는 값이 `null` 로 남고 위젯은
`미연동` 이라고 표시합니다 — 0% 로 그리면 "한도가 가득 찼다"로 잘못 읽히기 때문입니다.

**Codex(ChatGPT 플랜)와 Claude가 기본 연동됩니다.** 기본 카탈로그에 들어 있어 앱을 켜면
각 서비스의 기존 로그인 정보를 찾아 사용량을 자동으로 읽습니다. Codex는 4절, Claude는 5절을 참고하세요.

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

공식 API 가 없어 **Codex 클라이언트가 쓰는 엔드포인트를 그대로** 씁니다.
토큰은 `%USERPROFILE%\.codex\auth.json` 에서 **읽기만** 하고 갱신하지 않습니다(갱신은 Codex 앱 담당).

| 항목 | 값 |
| --- | --- |
| 호스트/경로 | `https://chatgpt.com` + `/api/codex/usage` → 실패 시 `/backend-api/wham/usage` |
| 실측(2026-10-12) | `/api/codex/usage` → **403**, `/backend-api/wham/usage` → **200** (그래서 후보가 둘이다) |
| 자격증명 | `auth.json` → `tokens.access_token`, `tokens.account_id`, `auth_mode` |
| 헤더 | `Authorization: Bearer …`, `chatgpt-account-id: …`, `originator: codex_cli_rs`, `Accept: application/json` |
| 주간/세션 구분 | 필드명이 버전마다 바뀌므로 **창 길이**로 가릅니다 — 3일(4320분) 이상 = 주간, 미만 = 세션 |
| 사용률 필드 후보 | `used_percent` · `usedPercent` · `utilization` · `used` · `percent_used` (0~1 과 0~100 둘 다 허용) |
| 창 길이 후보 | `window_minutes` · `window_seconds` · `limit_window_seconds` · `window_hours` |
| 리셋 후보 | `resets_in_seconds` · `reset_after_seconds` · `reset_at` · `resets_at` · `resets_at_seconds` |
| 주간 창을 `Primary` 로 | 위젯의 배터리·예측은 "오래 사는 한도"를 기준으로 돕니다. 세션 창은 `Secondary` |
| 파일 | 파서·클라이언트 `src/Quotra.Shared/Providers/`, 어댑터 `src/Quotra.WinUI/Services/CodexUsageSource.cs` |

**왜 이름이 아니라 창 길이인가** — 실측 응답(`plan_type=prolite`)은 `primary_window` 가 **7일** 창이었습니다.
Codex 응답은 5시간 창이 `primary`, 7일 창이 `secondary` 로 오는 경우도 있어 이름 기반 매핑은 순서가 뒤집힙니다.
그래서 트리 전체에서 "사용률 + 창 길이"를 가진 객체를 모아 **길이로** 가릅니다.

**주간 창이 없으면 `null` 입니다** — 세션 창만 주는 플랜이라면 배터리는 빈 트랙 + `값 없음` 으로 남습니다.
없는 값을 0 으로 채우지 않습니다.

### 실패는 이렇게 나옵니다

| 상태 | 화면 | 로그의 사유 |
| --- | --- | --- |
| `NoCredential` | `조회 오류` | `auth.json` 에 ChatGPT 토큰이 없음(API 키 로그인 상태일 수 있음) |
| `Expired` | `조회 오류` | 401/403 + `auth_mode` — Codex 앱을 한 번 실행하면 갱신됩니다 |
| `RateLimited` | `조회 오류` | 429 |
| `NetworkError` | `조회 오류` | 연결 거부/시간 초과(10초) |
| `SchemaChanged` | `조회 오류` | 200 인데 해석 불가 — 원문을 보관해 파서를 고칠 수 있게 합니다 |

어느 경우든 **그 프로바이더만** `조회 오류` 이고 나머지는 계속 돕니다. 직전 값은 지우지 않습니다.

---

## 5. Claude 실연동

Claude 사용량은 Claude Code의 OAuth 구독 로그인을 읽어 Anthropic 사용량 응답에서 가져옵니다.
이 경로는 Anthropic이 공개 안정 API로 문서화한 경로가 아니므로, 응답 형식이나 접근 정책이 바뀌면
위젯에 `조회 오류`가 표시될 수 있습니다. Claude Pro/Max의 Claude와 Claude Code 사용량은 같은
한도를 공유하며 5시간 및 주간 한도를 지원합니다([Anthropic 도움말](https://support.claude.com/en/articles/11145838-use-claude-code-with-your-pro-or-max-plan)).
현재 사용하는 OAuth 사용량 경로의 형식은 [Anthropic Claude Code 저장소의 이슈](https://github.com/anthropics/claude-code/issues/84995)에 보고되어 있습니다.

| 항목 | 값 |
| --- | --- |
| 엔드포인트 | `https://api.anthropic.com/api/oauth/usage` |
| 자격증명 | `%USERPROFILE%\.claude\.credentials.json` 의 `claudeAiOauth.accessToken` |
| 헤더 | `Authorization: Bearer …`, `anthropic-beta: oauth-2025-04-20`, `Accept: application/json` |
| 주간/5시간 매핑 | `seven_day` 를 배터리(주간)에, `five_hour` 를 보조 한도(5시간)에 표시 |
| 사용률 | 응답의 `utilization` 백분율(0~100)을 내부 0~1 값으로 변환 |
| 리셋 | `resets_at` 절대 시각을 그대로 사용 |
| 플랜 | 로그인 파일의 `subscriptionType` 이 Pro/Max/Free 중 하나면 표시 |
| 파일 | 요청·응답 파서 `src/Quotra.WinUI/Services/ClaudeUsageSource.cs` |

자격증명 파일은 **매 조회마다 읽기만** 합니다. 토큰을 갱신하거나 파일을 수정하지 않으며,
토큰과 원문 응답도 로그나 오류 문구에 남기지 않습니다. Claude Code가 로그인 정보를 갱신하면
다음 자동 조회부터 새 토큰을 읽습니다. 기본 갱신 간격은 60초입니다.

로그인이 없거나 만료되면 해당 배터리에 `조회 오류`가 표시되고 Claude Code로 로그인 정보를
갱신하라는 안내가 나옵니다. HTTP 429는 다음 주기 때 다시 시도하고, 401·403·기타 오류는
상태 코드만 표시합니다. Claude Code 로그인(구독 OAuth)을 사용하며 Anthropic Console API 키나
API 크레딧은 사용하지 않습니다.

---

## 값을 확인하는 법

```powershell
$out = 'src\Quotra.WinUI\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64'
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

아직 붙지 않은 후보: Gemini, Copilot. 공통 파서와 응답 모델은 `src/Quotra.Shared/Providers/`에 두고, WinUI 앱의 `IUsageSource` 어댑터에서 연결합니다.



