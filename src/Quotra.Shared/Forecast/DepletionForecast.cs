namespace Quotra.Forecast;

/// <summary>예측 상태.</summary>
public enum ForecastStatus
{
    /// <summary>표본이 아직 부족(첫 10분 안 — 표본 1개뿐).</summary>
    Collecting,

    /// <summary>소모가 사실상 없다.</summary>
    Idle,

    /// <summary>지금 속도면 리셋 전에 바닥난다 — 고갈 시각을 알려줄 수 있다.</summary>
    Depleting,

    /// <summary>지금 속도로는 한도 리셋이 먼저 온다(고갈 걱정 없음).</summary>
    ResetsFirst,

    /// <summary>고갈이 너무 멀다(기본 7일 초과) — 시각을 말해도 의미가 없다.</summary>
    Far,

    /// <summary>이미 바닥이다.</summary>
    Depleted,
}

/// <summary>예측 계산 설정.</summary>
public sealed record ForecastOptions
{
    /// <summary>이 기간의 표본만 본다. 짧으면 반응이 빠르고, 길면 잡음에 강하다.</summary>
    public TimeSpan RecentWindow { get; init; } = TimeSpan.FromHours(3);

    /// <summary>기울기를 믿으려면 필요한 최소 표본 수(2 = 두 점을 잇는 선).</summary>
    public int MinSamples { get; init; } = 2;

    /// <summary>
    /// 최소 관측 폭(기본 10분 = 표본 간격).
    ///
    /// 표본 2개 + 이 폭이면 첫 예측이 나온다 — 10분 간격 운용에서 **두 번째 표본 시점**이다.
    /// (3개/20분을 요구하던 때는 앱을 켜고 30분 동안 "측정 중"만 보였다.)
    /// 폭 하한이 없으면 1~2분 사이의 우연한 차이를 속도로 오인한다.
    /// </summary>
    public TimeSpan MinSpan { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>시간당 이보다 느리면 "소모 없음"으로 본다(0~1 스케일, 0.005 = 시간당 0.5%).</summary>
    public double IdleRatePerHour { get; init; } = 0.005;

    /// <summary>이보다 멀면 시각을 제시하지 않는다.</summary>
    public TimeSpan FarHorizon { get; init; } = TimeSpan.FromDays(7);

    /// <summary>직전 표본보다 이만큼(0~1) 떨어지면 한도 창이 리셋된 것으로 본다.</summary>
    public double ResetDropThreshold { get; init; } = 0.05;

    /// <summary>
    /// 표본 사이가 이 시리즈의 **최소 간격**의 몇 배를 넘으면 앱이 꺼져 있던 구간으로 보고 끊는다(기본 4배).
    ///
    /// 끊지 않으면 비어 있는 시간이 시간축에 남아 기울기의 분모만 커진다 — 실제로는 시간당 20% 를
    /// 쓰는데 6시간 공백이 섞이면 3% 로 읽혔다(자체 점검 "긴 공백 뒤 재시작").
    /// </summary>
    public double MaxGapRatio { get; init; } = 4;
}

/// <summary>예측 결과. 비율은 0~1 스케일(0.05 = 시간당 5%)이다.</summary>
public readonly record struct ForecastResult(
    ForecastStatus Status,
    double RatePerHour,
    DateTimeOffset? DepletesAt,
    TimeSpan? TimeLeft,
    int SampleCount,
    TimeSpan SampleSpan)
{
    /// <summary>10분당 소모량(0~1). 사용자에게 보여줄 기본 단위다.</summary>
    public double RatePer10Minutes => RatePerHour / 6.0;

    internal static ForecastResult Collecting(int samples, TimeSpan span) =>
        new(ForecastStatus.Collecting, 0, null, null, samples, span);
}

/// <summary>
/// 소모 속도와 고갈 시각을 계산한다.
///
/// 핵심 판단 두 가지:
///   1) 한도 창이 리셋되면 사용률이 뚝 떨어진다. 그 지점 이전 표본을 섞으면 속도가 왜곡되므로 잘라낸다.
///   2) 최소제곱 기울기를 쓴다. 처음-끝 두 점만 빼면 목업/실측 잡음에 그대로 흔들린다.
/// </summary>
public static class DepletionForecaster
{
    public static ForecastResult Compute(
        IReadOnlyList<UsagePoint> points,
        double usedNow,
        DateTimeOffset now,
        DateTimeOffset? resetAt = null,
        ForecastOptions? options = null)
    {
        ForecastOptions opt = options ?? new ForecastOptions();
        usedNow = Math.Clamp(usedNow, 0, 1);

        List<UsagePoint> recent = [.. points
            .Where(p => p.At <= now && p.At >= now - opt.RecentWindow)
            .OrderBy(p => p.At)];

        // 표본 간격의 기준값은 설정이 아니라 데이터에서 얻는다 — 최소값이 아니라 **중간값**이다.
        //
        // 최소값을 쓰면 앱을 다시 켤 때마다 즉시 붙는 표본 하나 때문에 기준이 무너진다.
        // 실측: 10분 간격 이력에 재시작 표본 2개(30초 간격)가 섞이자 기준이 30초가 되고,
        // 공백 한계가 2분으로 내려가 10분짜리 표본이 전부 잘려 화면이 "측정 중"에서 멈췄다.
        double gapLimitSeconds = MedianGapSeconds(recent) * opt.MaxGapRatio;

        // 마지막 리셋 이후만 남긴다. 앱이 꺼져 있던 구간(표본이 띄엄띄엄한 곳)도 같이 끊는다 —
        // 비어 있는 시간을 시간축에 남기면 기울기가 실제보다 낮게 나온다.
        int start = 0;
        for (int i = 1; i < recent.Count; i++)
        {
            bool resetDrop = recent[i].Used < recent[i - 1].Used - opt.ResetDropThreshold;
            bool offPeriod = gapLimitSeconds > 0 &&
                (recent[i].At - recent[i - 1].At).TotalSeconds > gapLimitSeconds;

            if (resetDrop || offPeriod)
            {
                start = i;
            }
        }

        if (start > 0)
        {
            recent.RemoveRange(0, start);
        }

        TimeSpan span = recent.Count >= 2 ? recent[^1].At - recent[0].At : TimeSpan.Zero;
        if (recent.Count < opt.MinSamples || span < opt.MinSpan)
        {
            return ForecastResult.Collecting(recent.Count, span);
        }

        // 잡음으로 살짝 내려간 기울기는 0 으로 본다 — 사용률은 리셋 말고는 줄지 않으므로
        // 음수 기울기는 측정 잡음이다. 그대로 두면 화면과 로그에 "-0%/10분"이 뜬다.
        double rate = Math.Max(0, Slope(recent));

        if (usedNow >= 1.0)
        {
            return new ForecastResult(ForecastStatus.Depleted, rate, null, null, recent.Count, span);
        }

        if (rate < opt.IdleRatePerHour)
        {
            // rate 가 음수인 경우(한도가 다시 채워짐)도 여기로 온다.
            return new ForecastResult(ForecastStatus.Idle, rate, null, null, recent.Count, span);
        }

        var timeLeft = TimeSpan.FromHours((1.0 - usedNow) / rate);
        DateTimeOffset depletesAt = now + timeLeft;

        if (resetAt is { } reset && reset <= depletesAt)
        {
            return new ForecastResult(ForecastStatus.ResetsFirst, rate, depletesAt, timeLeft, recent.Count, span);
        }

        ForecastStatus status = timeLeft > opt.FarHorizon ? ForecastStatus.Far : ForecastStatus.Depleting;
        return new ForecastResult(status, rate, depletesAt, timeLeft, recent.Count, span);
    }

    /// <summary>
    /// 표본 사이 간격의 중간값(초). 표본이 2개 미만이면 0 = "기준 없음"(공백 절단 안 함).
    ///
    /// 최소값 대신 중간값을 쓰는 이유: 앱을 다시 켤 때마다 표본이 하나씩 즉시 붙어
    /// 30초짜리 간격이 몇 개 섞이는데, 최소값은 그 하나에 끌려 내려가 정상 간격 표본을
    /// 전부 "앱이 꺼져 있던 구간"으로 오인한다(실측으로 겪은 결함).
    /// </summary>
    private static double MedianGapSeconds(List<UsagePoint> points)
    {
        if (points.Count < 2)
        {
            return 0;
        }

        List<double> gaps = [];
        for (int i = 1; i < points.Count; i++)
        {
            double gap = (points[i].At - points[i - 1].At).TotalSeconds;
            if (gap > 0)
            {
                gaps.Add(gap);
            }
        }

        if (gaps.Count == 0)
        {
            return 0;
        }

        gaps.Sort();
        int middle = gaps.Count / 2;

        return gaps.Count % 2 == 1
            ? gaps[middle]
            : (gaps[middle - 1] + gaps[middle]) / 2.0;
    }

    /// <summary>최소제곱 기울기(시간당, 0~1 스케일).</summary>
    private static double Slope(List<UsagePoint> points)
    {
        DateTimeOffset t0 = points[0].At;
        double meanX = 0;
        double meanY = 0;

        foreach (UsagePoint p in points)
        {
            meanX += (p.At - t0).TotalHours;
            meanY += p.Used;
        }

        meanX /= points.Count;
        meanY /= points.Count;

        double numerator = 0;
        double denominator = 0;

        foreach (UsagePoint p in points)
        {
            double x = (p.At - t0).TotalHours - meanX;
            numerator += x * (p.Used - meanY);
            denominator += x * x;
        }

        return denominator <= 0 ? 0 : numerator / denominator;
    }
}

/// <summary>예측 결과를 사람이 읽는 문구로 바꾼다. 두 앱이 같은 문구를 쓰도록 여기에 둔다.</summary>
public static class ForecastText
{
    /// <summary>
    /// 예: "0.4%/10분".
    ///
    /// 단위는 <c>%</c> 다 — 한도 창은 언제나 100% 를 기준으로 하므로 "한도의 몇 %를 10분에 쓰는가"가
    /// 그대로 읽히고, 퍼센트 포인트를 구분하는 <c>%p</c> 는 화면에서 군더더기였다(사용자 지적).
    ///
    /// 자릿수는 **0 으로 뭉개지지 않게** 고른다 — 0.004%/10분을 "0%/10분"으로 쓰면
    /// 화면은 "줄고 있다"고 말하면서 숫자는 0 을 보여주는 모순이 된다.
    /// </summary>
    public static string Rate(ForecastResult result)
    {
        double per10 = result.RatePer10Minutes * 100;

        if (per10 <= 0)
        {
            return "0%/10분";
        }

        return per10 < 0.1 ? $"{per10:0.###}%/10분"
            : per10 < 1 ? $"{per10:0.##}%/10분"
            : $"{per10:0.#}%/10분";
    }

    /// <summary>오른쪽 칩에 들어가는 짧은 문구.</summary>
    public static string Short(ForecastResult result, DateTimeOffset now) => result.Status switch
    {
        ForecastStatus.Depleting => $"{Rate(result)} → {Remaining(result.DepletesAt, now)}",
        ForecastStatus.ResetsFirst => "리셋 먼저",
        ForecastStatus.Idle => "소모 없음",
        ForecastStatus.Far => "여유",
        ForecastStatus.Depleted => "소진됨",
        _ => "측정 중",
    };

    /// <summary>로그·상세용 긴 문구.</summary>
    public static string Detail(ForecastResult result, DateTimeOffset now) => result.Status switch
    {
        ForecastStatus.Depleting =>
            $"{Rate(result)} 소모 · 소진까지 {Remaining(result.DepletesAt, now)}",
        ForecastStatus.ResetsFirst =>
            $"{Rate(result)} 소모 · 현재 속도의 소진 예상은 {Remaining(result.DepletesAt, now)} 이지만 리셋이 먼저",
        ForecastStatus.Idle => $"소모 없음 (표본 {result.SampleCount}개, {Duration(result.SampleSpan)})",
        ForecastStatus.Far => $"{Rate(result)} 소모 · 고갈까지 {Duration(result.TimeLeft ?? TimeSpan.Zero)} 이상",
        ForecastStatus.Depleted => "이미 소진됨",
        _ => $"측정 중 (표본 {result.SampleCount}개, {Duration(result.SampleSpan)})",
    };

    /// <summary>예상 시각을 달력 날짜 대신 남은 기간으로 표시한다.</summary>
    private static string Remaining(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } value)
        {
            return "—";
        }

        TimeSpan left = value - now;
        string duration = Duration(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        if (duration.EndsWith(" 0분", StringComparison.Ordinal))
        {
            duration = duration[..^4];
        }

        return $"{duration} 남음";
    }

    /// <summary>예: "4시간 12분".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span >= TimeSpan.FromDays(1))
        {
            return $"{(int)span.TotalDays}일 {span.Hours}시간";
        }

        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}시간 {span.Minutes}분" : $"{Math.Max(1, span.Minutes)}분";
    }
}
