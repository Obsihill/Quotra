using Quotra.Providers;

namespace Quotra.WinUI.Services;

/// <summary>
/// Codex(ChatGPT 플랜) 실연동 — <see cref="IUsageSource"/> 구현.
///
/// <c>%USERPROFILE%\.codex\auth.json</c> 의 토큰을 **읽기만** 해서
/// Codex 클라이언트가 쓰는 사용량 엔드포인트를 조회한다(공식 API 가 없다).
/// 토큰은 로그·오류 메시지 어디에도 넣지 않는다.
///
/// 응답의 주간 창을 <see cref="ProviderSnapshot.Primary"/> 로 쓴다 —
/// 위젯의 배터리와 예측은 "오래 사는 한도"(주간)를 기준으로 돌기 때문이다.
/// 세션 창이 없는 플랜도 있으므로 <see cref="ProviderSnapshot.Secondary"/> 는 null 일 수 있다.
/// </summary>
[UsageSource("codex")]
public sealed class CodexUsageSource : IUsageSource, IUsageDiagnostics, IUsageSelfTest
{
    private string _host;
    private string? _authPath;

    public CodexUsageSource(string host = "https://chatgpt.com", string? authPath = null)
    {
        _host = host;
        _authPath = authPath;
    }

    public string DisplayName => "Codex (ChatGPT 플랜 한도)";

    public string ShortName => "Codex";

    public string SelfTestName => "Codex 파서";

    public void ApplyDiagnostics(string? url, string? file)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            _host = url.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(file))
        {
            _authPath = Path.GetFullPath(file, AppContext.BaseDirectory);
        }
    }

    public bool RunSelfTest()
    {
        bool allPassed = CodexSelfTest.Print(CodexSelfTest.Run(), Log.Write);
        Log.Write($"Codex 파서 자체 점검: {(allPassed ? "전부 통과" : "실패 있음")}");
        return allPassed;
    }

    /// <summary>마지막 조회의 사람이 읽을 요약(로그·검증용).</summary>
    public string LastDetail { get; private set; } = string.Empty;

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        CodexFetchResult result = await CodexUsageClient.FetchAsync(_host, _authPath, cancellationToken).ConfigureAwait(false);

        LastDetail = result.Ok ? $"windows=[{result.Usage!.WindowsSummary}]" : $"{result.Status}: {result.Detail}";

        if (!result.Ok)
        {
            // 예외로 알린다 — UsageService 가 이 프로바이더만 "조회 오류"로 표시하고 나머지는 계속 돈다.
            throw new InvalidOperationException(Explain(result));
        }

        CodexUsage usage = result.Usage!;
        DateTimeOffset now = DateTimeOffset.Now;

        return new ProviderSnapshot(
            Primary: usage.WeeklyUsed is { } weekly
                ? new WindowUsage(weekly, ResolveReset(now, usage.WeeklyResetsIn))
                : null,
            Secondary: usage.SessionUsed is { } session
                ? new WindowUsage(session, ResolveReset(now, usage.SessionResetsIn))
                : null,
            Plan: PlanLabel(usage.Plan),
            Note: usage.LimitReached == true ? "한도 도달" : null);
    }

    private static DateTimeOffset? ResolveReset(DateTimeOffset now, TimeSpan? resetsIn) =>
        resetsIn is { } span ? now + span : null;

    /// <summary>응답의 플랜 이름을 사람이 읽는 라벨로 바꾼다(모르면 원문 그대로).</summary>
    private static string PlanLabel(string plan) => plan.ToLowerInvariant() switch
    {
        "free" => "Free",
        "plus" => "Plus",
        "pro" => "Pro",
        "prolite" => "Pro Lite",
        "team" => "Team",
        "enterprise" => "Enterprise",
        _ => plan,
    };

    /// <summary>실패를 다음에 할 일이 보이는 한 줄로 바꾼다.</summary>
    private string Explain(CodexFetchResult result) => result.Status switch
    {
        CodexFetchStatus.NoCredential =>
            $"Codex 자격증명 없음 — {_authPath ?? CodexUsageClient.AuthPath} 에 ChatGPT 토큰이 필요합니다(API 키 로그인 상태일 수 있습니다).",
        CodexFetchStatus.Expired =>
            $"Codex 토큰이 거부됐습니다({result.Detail}) — Codex 앱을 한 번 실행하면 갱신됩니다.",
        CodexFetchStatus.RateLimited =>
            $"Codex 조회가 일시적으로 제한됐습니다({result.Detail}).",
        CodexFetchStatus.NetworkError =>
            $"Codex 조회 실패({result.Detail}).",
        CodexFetchStatus.SchemaChanged =>
            $"Codex 응답 해석 실패({result.Detail}).",
        _ => result.Detail,
    };
}
