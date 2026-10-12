using System.Globalization;

namespace Quotra.Forecast;

public readonly record struct ForecastTestCase(string Name, string Expected, string Observed, bool Passed);

/// <summary>
/// 예측 엔진 자체 점검. 앱 실행 없이 합성 시계열로만 계산하므로 결과가 결정적이다.
/// (개발용 — `--forecast-test` 로 실행한다.)
/// </summary>
public static class ForecastSelfTest
{
    /// <summary>기준 시각을 고정한다(로컬 시간대와 무관한 검증을 위해).</summary>
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<ForecastTestCase> Run()
    {
        List<ForecastTestCase> results = [];

        // 1) 꾸준한 소모: 3시간 동안 10% → 40% = 시간당 10%. 남은 60% → 6시간.
        ForecastResult steady = DepletionForecaster.Compute(
            Series(h => 0.10 + 0.10 * h, -3, 0), usedNow: 0.40, Now);
        Rate(results, "꾸준한 소모", steady, 0.10);
        State(results, "꾸준한 소모", steady, ForecastStatus.Depleting);
        Near(results, "꾸준한 소모 · 남은 시간", steady.TimeLeft?.TotalHours, 6.0, 0.1);

        // 2) 변화 없음 → 소모 없음.
        ForecastResult idle = DepletionForecaster.Compute(
            Series(_ => 0.30, -3, 0), usedNow: 0.30, Now);
        State(results, "변화 없음", idle, ForecastStatus.Idle);

        // 3) 리셋 섞임: 리셋 전 1시간에 80%를 태웠지만, 리셋 뒤에는 시간당 6%로만 준다.
        //    리셋 이전 표본을 잘라내지 않으면 속도가 크게 왜곡된다.
        List<UsagePoint> mixed = [.. Series(h => 0.10 + 0.80 * (h + 3), -3, -2.1667)];
        mixed.Add(new UsagePoint(Now.AddHours(-2), 0.10));
        mixed.AddRange(Series(h => 0.10 + 0.06 * (h + 2), -110.0 / 60.0, 0));
        mixed.Sort((a, b) => a.At.CompareTo(b.At));
        ForecastResult reset = DepletionForecaster.Compute(mixed, usedNow: 0.22, Now);
        Rate(results, "리셋 이후만 반영", reset, 0.06);
        State(results, "리셋 이후만 반영", reset, ForecastStatus.Depleting);
        Near(results, "리셋 이후만 반영 · 남은 시간", reset.TimeLeft?.TotalHours, 13.0, 0.3);

        // 4) 표본 1개 → 아직 측정 중(기울기를 만들 점이 없다).
        ForecastResult one = DepletionForecaster.Compute(
            [new UsagePoint(Now, 0.21)], 0.21, Now);
        State(results, "표본 1개", one, ForecastStatus.Collecting);

        // 4-1) 표본 2개 + 10분 폭 → 첫 예측. 10분 간격 운용에서 두 번째 표본 시점에 숫자가 나온다.
        ForecastResult twice = DepletionForecaster.Compute(
            [new UsagePoint(Now.AddMinutes(-10), 0.20), new UsagePoint(Now, 0.21)], 0.21, Now);
        State(results, "표본 2개면 예측 시작", twice, ForecastStatus.Depleting);
        Rate(results, "표본 2개면 예측 시작", twice, 0.06);

        // 4-2) 표본 2개라도 폭이 1분이면 우연한 차이를 속도로 오인하지 않는다.
        ForecastResult blink = DepletionForecaster.Compute(
            [new UsagePoint(Now.AddMinutes(-1), 0.20), new UsagePoint(Now, 0.21)], 0.21, Now);
        State(results, "1분 폭은 측정 중", blink, ForecastStatus.Collecting);

        // 5) 관측 폭이 기본 하한(10분)보다 짧으면 표본 수가 충분해도 측정 중.
        ForecastResult narrow = DepletionForecaster.Compute(
            Series(h => 0.20 + 0.02 * h, -4.0 / 60.0, 0, stepMinutes: 2), 0.202, Now);
        State(results, "관측 폭 부족", narrow, ForecastStatus.Collecting);

        // 5-1) 딱 10분이면 첫 예측이 나온다 — 10분 간격 운용에서 두 번째 표본 시점.
        ForecastResult first = DepletionForecaster.Compute(
            Series(h => 0.20 + 0.06 * h, -10.0 / 60.0, 0, stepMinutes: 5), 0.21, Now);
        State(results, "10분 관측이면 예측 시작", first, ForecastStatus.Depleting);

        // 5-2) 아주 느린 소모도 "0%/10분"으로 뭉개지 않는다(화면이 0 을 보여주면 모순이다).
        ForecastResult slow = DepletionForecaster.Compute(
            Series(h => 0.10 + 0.002 * h, -3, 0), 0.106, Now);
        Text(results, "느린 소모 문구", ForecastText.Rate(slow), "0.033%/10분");

        Text(results, "속도 문구 단위", ForecastText.Rate(slow).Contains("p") ? "p 붙음" : "%", "%");

        // 5-3) "소모 없음"과 속도 숫자가 한 줄에 같이 나오지 않는다.
        ForecastResult flat = DepletionForecaster.Compute(
            Series(h => 0.10, -3, 0), 0.10, Now);
        Text(results, "소모 없음 문구", ForecastText.Detail(flat, Now), "소모 없음 (표본 19개, 3시간 0분)");

        // 6) 이미 바닥.
        ForecastResult depleted = DepletionForecaster.Compute(
            Series(h => 0.10 + 0.30 * h, -3, 0), usedNow: 1.0, Now);
        State(results, "이미 소진", depleted, ForecastStatus.Depleted);

        // 7) 리셋이 먼저: 시간당 2%면 25시간 뒤 고갈인데 리셋은 3시간 뒤.
        ForecastResult sooner = DepletionForecaster.Compute(
            Series(h => 0.10 + 0.02 * h, -3, 0), usedNow: 0.50, Now, Now.AddHours(3));
        State(results, "리셋이 먼저", sooner, ForecastStatus.ResetsFirst);

        // 8) 너무 먼 고갈: 시간당 0.15%, 남은 90% → 600시간(7일 초과).
        ForecastResult far = DepletionForecaster.Compute(
            Series(h => 0.10 + 0.0015 * h, -3, 0), usedNow: 0.10, Now, null,
            new ForecastOptions { IdleRatePerHour = 0.0005 });
        State(results, "고갈이 너무 멀다", far, ForecastStatus.Far);

        // 9) 한도가 다시 채워지는 중(사용률이 시간에 따라 줄어듦) → 소모 없음.
        ForecastResult refilled = DepletionForecaster.Compute(
            Series(h => 0.60 - 0.033 * h, -3, 0), usedNow: 0.60, Now);
        State(results, "한도 회복 중", refilled, ForecastStatus.Idle);

        // 9-2) 잡음으로 살짝 내려간 기울기는 0 으로 본다 — "-0%/10분"이 화면·로그에 뜨지 않도록.
        ForecastResult noiseDown = DepletionForecaster.Compute(
            Series(h => 0.50 - 0.0004 * h, -3, 0), usedNow: 0.50, Now);
        State(results, "잡음으로 내려간 기울기", noiseDown, ForecastStatus.Idle);
        Rate(results, "잡음으로 내려간 기울기", noiseDown, 0);

        // 10) 잡음이 섞여도 기울기가 흔들리지 않아야 한다(±0.5% 교대 진동 + 시간당 3% 상승).
        List<UsagePoint> noisySeries = [];
        for (int i = 0; i < 19; i++)
        {
            double h = -3 + (i / 6.0);
            double noise = i % 2 == 0 ? 0.005 : -0.005;
            noisySeries.Add(new UsagePoint(Now.AddHours(h), 0.20 + (0.03 * (h + 3)) + noise));
        }

        ForecastResult noisy = DepletionForecaster.Compute(noisySeries, usedNow: 0.30, Now);
        Rate(results, "잡음 속 기울기", noisy, 0.03);

        // 11) 긴 공백 뒤 재시작: 앞뒤로 1시간씩 시간당 20%로 썼지만 사이에 6시간 앱이 꺼져 있었다.
        //     공백을 시간축에서 빼지 않으면 기울기가 실제의 1/4 로 읽힌다(실측으로 겪은 결함).
        List<UsagePoint> withGap = [.. Series(h => 0.10 + 0.20 * (h + 7), -7, -6)];
        withGap.AddRange(Series(h => 0.30 + 0.20 * (h + 1), -1, 0));
        withGap.Sort((a, b) => a.At.CompareTo(b.At));
        ForecastResult afterGap = DepletionForecaster.Compute(withGap, usedNow: 0.50, Now);
        Rate(results, "긴 공백 뒤 재시작", afterGap, 0.20);
        State(results, "긴 공백 뒤 재시작", afterGap, ForecastStatus.Depleting);

        // 12) 잦은 재시작: 10분 간격 이력에 재시작마다 붙는 30초 간격 표본이 섞여도
        //     정상 간격 표본을 버리지 않는다(최소 간격을 기준으로 쓰면 전부 잘려 "측정 중"이 됐다).
        List<UsagePoint> restarted = [.. Series(h => 0.20 + 0.10 * (h + 3), -3, 0)];
        restarted.Add(new UsagePoint(Now.AddSeconds(-40), 0.50));
        restarted.Add(new UsagePoint(Now.AddSeconds(-10), 0.50));
        restarted.Sort((a, b) => a.At.CompareTo(b.At));
        ForecastResult clustering = DepletionForecaster.Compute(restarted, usedNow: 0.50, Now);
        State(results, "재시작 표본이 섞여도", clustering, ForecastStatus.Depleting);
        Rate(results, "재시작 표본이 섞여도", clustering, 0.10);

        return results;
    }

    /// <summary>기준 시각 기준 상대 시간(시간)으로 표본을 만든다.</summary>
    private static List<UsagePoint> Series(Func<double, double> usedAtHour, double fromHours, double toHours, double stepMinutes = 10)
    {
        List<UsagePoint> points = [];
        double stepHours = stepMinutes / 60.0;

        for (double h = fromHours; h <= toHours + 1e-9; h += stepHours)
        {
            points.Add(new UsagePoint(Now.AddHours(h), usedAtHour(h)));
        }

        return points;
    }

    private static void State(List<ForecastTestCase> results, string name, ForecastResult actual, ForecastStatus expected) =>
        results.Add(new ForecastTestCase(
            name,
            expected.ToString(),
            $"{actual.Status} (표본 {actual.SampleCount}개)",
            actual.Status == expected));

    private static void Rate(List<ForecastTestCase> results, string name, ForecastResult actual, double expectedPerHour) =>
        results.Add(new ForecastTestCase(
            $"{name} · 속도",
            $"{expectedPerHour * 100:0.##}%/시간",
            $"{actual.RatePerHour * 100:0.##}%/시간",
            Math.Abs(actual.RatePerHour - expectedPerHour) < 0.002));

    private static void Near(List<ForecastTestCase> results, string name, double? actual, double expected, double tolerance) =>
        results.Add(new ForecastTestCase(
            name,
            $"{expected:0.##}시간",
            actual is null ? "없음" : $"{actual.Value:0.##}시간",
            actual is not null && Math.Abs(actual.Value - expected) <= tolerance));

    /// <summary>문구를 글자 그대로 비교한다(반올림·문구가 화면에 나오는 그대로인지 보려고).</summary>
    private static void Text(List<ForecastTestCase> results, string name, string actual, string expected) =>
        results.Add(new ForecastTestCase(name, $"\"{expected}\"", $"\"{actual}\"", actual == expected));

    /// <summary>검증 표를 표준 출력으로 내보낸다(앱 로그로도 확인 가능).</summary>
    public static bool Print(IReadOnlyList<ForecastTestCase> results, Action<string> write)
    {
        int passed = results.Count(r => r.Passed);
        write($"예측 엔진 자체 점검: {passed}/{results.Count} 통과 " + (passed == results.Count ? "(전부 통과)" : "(실패 있음)"));
        write(string.Format(CultureInfo.InvariantCulture, "  {0,-26} {1,-16} {2,-24} {3}", "케이스", "기대", "실측", "판정"));

        foreach (ForecastTestCase r in results)
        {
            write(string.Format(
                CultureInfo.InvariantCulture,
                "  {0,-26} {1,-16} {2,-24} {3}",
                r.Name,
                r.Expected,
                r.Observed,
                r.Passed ? "OK" : "FAIL"));
        }

        return passed == results.Count;
    }
}
