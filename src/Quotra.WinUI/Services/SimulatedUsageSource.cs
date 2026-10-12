namespace Quotra.WinUI.Services;

/// <summary>
/// 개발용 소스 — <see cref="IUsageSource"/> 구현 예시이자 예측 검증 하네스.
///
/// 주입된 비율만큼 시간에 비례해 값을 올린다(난수 없음 → 같은 입력이면 같은 예측).
/// <c>--simulate=&lt;시간당 %&gt;</c> 로만 등록되며, 등록하지 않으면 앱에는 어떤 가짜 값도 없다.
/// 실제 연동(예: Codex)도 이 인터페이스를 구현해 <see cref="UsageService.Register"/> 로 붙이면 된다 —
/// 이 클래스가 그 최소 예시다.
/// </summary>
public sealed class SimulatedUsageSource : IUsageSource
{
    private readonly double _primaryPerHour;
    private readonly double _secondaryPerHour;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly DateTimeOffset? _primaryResetsAt;
    private readonly DateTimeOffset? _secondaryResetsAt;

    public SimulatedUsageSource(
        string providerName,
        double startUsed = 0.10,
        double primaryPerHour = 0.02,
        double secondaryPerHour = 0.05)
    {
        DisplayName = $"시뮬레이션({providerName}, 시간당 {primaryPerHour * 100:0.##}%)";
        ShortName = "시뮬레이션";
        StartUsed = Math.Clamp(startUsed, 0, 1);
        _primaryPerHour = primaryPerHour;
        _secondaryPerHour = secondaryPerHour;

        // 실제 서비스처럼 리셋 시각을 함께 준다(주간 7일 · 세션 5시간).
        _primaryResetsAt = _startedAt.AddDays(7);
        _secondaryResetsAt = _startedAt.AddHours(5);
    }

    public string DisplayName { get; }

    public string ShortName { get; }

    public double StartUsed { get; }

    public Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        double hours = (DateTimeOffset.Now - _startedAt).TotalHours;

        return Task.FromResult(new ProviderSnapshot(
            new WindowUsage(Math.Clamp(StartUsed + (_primaryPerHour * hours), 0, 1), _primaryResetsAt),
            new WindowUsage(Math.Clamp((StartUsed / 2) + (_secondaryPerHour * hours), 0, 1), _secondaryResetsAt),
            Note: "시뮬레이션 값(개발용)"));
    }
}
