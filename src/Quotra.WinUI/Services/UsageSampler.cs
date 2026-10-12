using Microsoft.UI.Dispatching;
using Quotra.Forecast;
using Quotra.WinUI.Models;

namespace Quotra.WinUI.Services;

/// <summary>
/// 10분마다 프로바이더 사용률을 이력에 남긴다.
///
/// 앱이 꺼져 있던 구간은 기록하지 않는다 — 없던 시간을 지어내면 예측이 조용히 틀어진다.
/// 대신 표본 사이에 구멍이 생겨도 기울기 계산은 관측된 시각만 보므로 왜곡되지 않는다.
/// </summary>
public sealed class UsageSampler
{
    /// <summary>기본 표본 간격.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(10);

    private readonly UsageHistory _history;
    private DispatcherQueueTimer? _timer;
    private DispatcherQueueTimer? _warmup;
    private int _warmupTries;

    /// <summary>첫 값이 들어오기를 기다릴 때의 재시도 간격.</summary>
    private static readonly TimeSpan WarmupInterval = TimeSpan.FromSeconds(5);

    /// <summary>재시도 상한(5초 × 24 = 2분). 그 뒤에는 정상 주기로 내려간다.</summary>
    private const int WarmupTries = 24;

    public UsageSampler(UsageHistory history, string path, TimeSpan? interval = null)
    {
        _history = history;
        Path = path;
        Interval = interval ?? DefaultInterval;
    }

    /// <summary>이력 파일 경로.</summary>
    public string Path { get; }

    /// <summary>표본 간격.</summary>
    public TimeSpan Interval { get; }

    public UsageHistory History => _history;

    /// <summary>마지막 표본 시각.</summary>
    public DateTimeOffset? LastSampleAt { get; private set; }

    /// <summary>마지막 저장 실패 사유(정상이면 null). 이력은 없어도 앱은 돈다.</summary>
    public string? SaveWarning { get; private set; }

    /// <summary>표본을 남긴 직후 발생(화면 갱신용).</summary>
    public event EventHandler? Sampled;

    /// <summary>즉시 1개 기록하고 주기 반복을 시작한다. 시작 직후 1개를 남겨야 재시작 사이가 메워진다.</summary>
    public void Start(DispatcherQueue queue, IReadOnlyList<ProviderUsage> providers)
    {
        // 시작 직후의 첫 표본은 첫 조회가 끝나기 전에 찍힌다 — 값이 없어 건너뛰면
        // 10분을 통째로 잃고, 화면은 그동안 "측정 중"에서 벗어나지 못한다.
        // 값이 없는 동안만 짧게 다시 찍어서, 첫 값이 들어오는 즉시 표본을 남긴다.
        if (Sample(providers))
        {
            StartLoop(queue, providers);
            return;
        }

        Log.Write($"샘플러: 첫 조회를 기다리는 중(값 없음) · {WarmupInterval.TotalSeconds:0}초 간격으로 재시도");

        _warmup = queue.CreateTimer();
        _warmup.Interval = WarmupInterval;
        _warmup.IsRepeating = true;
        _warmup.Tick += (_, _) =>
        {
            _warmupTries++;

            if (Sample(providers))
            {
                _warmup.Stop();
                _warmup = null;
                StartLoop(queue, providers);
            }
            else if (_warmupTries >= WarmupTries)
            {
                // 계속 값이 없으면(미연동·조회 실패) 정상 주기로 내려간다 — 5초마다 파일을 쓰지 않는다.
                _warmup.Stop();
                _warmup = null;
                Log.Write("샘플러: 첫 조회가 끝내 오지 않아 정상 주기로 전환");
                StartLoop(queue, providers);
            }
        };
        _warmup.Start();
    }

    private void StartLoop(DispatcherQueue queue, IReadOnlyList<ProviderUsage> providers)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = Interval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Sample(providers);
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _warmup?.Stop();
        _warmup = null;
    }

    /// <summary>표본 하나를 남기고 파일에 저장한다. 저장 실패는 앱을 멈추지 않는다.</summary>
    /// <returns>값이 있는 프로바이더를 하나라도 기록했으면 true.</returns>
    public bool Sample(IReadOnlyList<ProviderUsage> providers)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        bool recorded = false;

        foreach (ProviderUsage provider in providers)
        {
            // 값이 없는 프로바이더는 표본을 남기지 않는다 — 0 으로 기록하면 예측이 거짓말을 한다.
            if (provider.Primary is { } primary)
            {
                _history.Record(provider.PrimaryHistoryKey, UsageWindow.Primary, primary, now);
                recorded = true;
            }

            if (provider.Secondary is { } secondary)
            {
                _history.Record(provider.PrimaryHistoryKey, UsageWindow.Secondary, secondary, now);
                recorded = true;
            }
        }

        if (!recorded)
        {
            return false;
        }

        _history.Prune(now);
        LastSampleAt = now;
        SaveWarning = _history.Save(Path);
        Sampled?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
