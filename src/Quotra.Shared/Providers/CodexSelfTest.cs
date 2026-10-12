using System.Globalization;

namespace Quotra.Providers;

public readonly record struct CodexTestCase(string Name, string Expected, string Observed, bool Passed);

/// <summary>
/// Codex 파서 자체 점검 — 네트워크 없이 합성 응답만 쓴다(<c>--codex-test</c>).
///
/// 실제 응답에서 확인한 구조를 그대로 넣었다(2026-10-12 실측):
/// <c>rate_limit.primary_window{used_percent, limit_window_seconds, reset_after_seconds, reset_at}</c>, <c>plan_type</c>.
/// </summary>
public static class CodexSelfTest
{
    /// <summary>에포크 초 변환이 흔들리지 않도록 기준 시각을 고정한다.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    /// <summary>실제 응답에서 확인한 형태(주간 창만 있고 세션 창은 null).</summary>
    private const string RealShape = """
    {
      "account_id": "acct-1",
      "plan_type": "prolite",
      "rate_limit": {
        "allowed": true,
        "limit_reached": false,
        "primary_window": {
          "used_percent": 10,
          "limit_window_seconds": 604800,
          "reset_after_seconds": 572677,
          "reset_at": 1792310612
        },
        "secondary_window": null
      },
      "credits": { "has_credits": false, "balance": "0" }
    }
    """;

    /// <summary>주간 + 세션(5시간) 창이 함께 오는 형태. 순서를 뒤집어도 같아야 한다.</summary>
    private const string TwoWindows = """
    {
      "plan_type": "plus",
      "rate_limit": {
        "secondary_window": { "used_percent": 12.5, "limit_window_seconds": 18000, "reset_after_seconds": 3600 },
        "primary_window": { "used_percent": 63, "limit_window_seconds": 604800, "reset_after_seconds": 500000 },
        "limit_reached": false
      }
    }
    """;

    public static IReadOnlyList<CodexTestCase> Run()
    {
        List<CodexTestCase> results = [];

        // 1) 실측 응답: 주간 10% / 세션 없음 / 플랜 prolite / 리셋 572677초.
        CodexUsage real = Parse(RealShape, results, "실측 응답 형태");
        Near(results, "실측 응답 형태 · 주간", real.WeeklyUsed, 0.10);
        Same(results, "실측 응답 형태 · 세션 없음", real.SessionUsed is null, "null");
        Text(results, "실측 응답 형태 · 플랜", real.Plan, "prolite");
        Near(results, "실측 응답 형태 · 리셋(분)", real.WeeklyResetsIn?.TotalMinutes, 572677 / 60.0, 0.5);
        Same(results, "실측 응답 형태 · 한도 도달", real.LimitReached is false, "False");

        // 2) 창 두 개: 길이로 가른다(응답에서 secondary 가 먼저 와도 주간/세션이 뒤집히지 않아야 한다).
        CodexUsage two = Parse(TwoWindows, results, "창 두 개");
        Near(results, "창 두 개 · 주간", two.WeeklyUsed, 0.63);
        Near(results, "창 두 개 · 세션", two.SessionUsed, 0.125);
        Near(results, "창 두 개 · 세션 리셋(분)", two.SessionResetsIn?.TotalMinutes, 60, 0.5);

        // 3) 0~1 스케일로 오는 응답도 같은 값이 된다.
        CodexUsage unit = Parse("""{"rate_limit":{"used_percent":0.42,"limit_window_seconds":604800}}""", results, "0~1 스케일");
        Near(results, "0~1 스케일 · 주간", unit.WeeklyUsed, 0.42);

        // 4) 다른 이름 조합(utilization + window_minutes)도 읽는다.
        CodexUsage alias = Parse("""{"limits":[{"utilization":37,"window_minutes":10080}]}""", results, "다른 필드명");
        Near(results, "다른 필드명 · 주간", alias.WeeklyUsed, 0.37);

        // 5) 에포크 초로 온 리셋 시각: 기준 시각(2026-10-12) + 2일.
        long epoch = Now.AddDays(2).ToUnixTimeSeconds();
        CodexUsage epochUsage = Parse(
            "{\"rate_limit\":{\"used_percent\":5,\"limit_window_seconds\":604800,\"resets_at\":" +
            epoch + "}}",
            results,
            "에포크 리셋");
        Near(results, "에포크 리셋 · 남은 시간(시간)", epochUsage.WeeklyResetsIn?.TotalHours, 48, 0.05);

        // 6) 세션 창만 있고 주간 창이 없으면 주간은 null 이다(값을 지어내지 않는다).
        CodexUsage sessionOnly = Parse("""{"rate_limit":{"used_percent":20,"limit_window_seconds":18000}}""", results, "세션만");
        Same(results, "세션만 · 주간 없음", sessionOnly.WeeklyUsed is null, "null");
        Near(results, "세션만 · 세션", sessionOnly.SessionUsed, 0.20);

        // 7) 100 을 넘는 값은 1.0 으로 자른다.
        CodexUsage over = Parse("""{"rate_limit":{"used_percent":137,"limit_window_seconds":604800}}""", results, "초과 값");
        Near(results, "초과 값 · 상한", over.WeeklyUsed, 1.0);

        // 8) 창 길이가 없으면 해석 실패다(사용률만으로는 주간/세션을 가를 수 없다).
        bool lengthless = CodexUsageParser.TryParse("""{"used_percent":42}""", out _, out string? lengthError, Now);
        Same(results, "창 길이 없음 · 실패", !lengthless && lengthError is not null, "실패+사유");

        // 9) JSON 이 아니면 실패한다.
        bool garbage = CodexUsageParser.TryParse("not json", out _, out string? jsonError, Now);
        Same(results, "JSON 아님 · 실패", !garbage && jsonError is not null, "실패+사유");

        // 10) 필드가 통째로 바뀌어도 이름을 하드코딩하지 않으므로 살아남는다(로그 요약은 남는다).
        CodexUsage renamed = Parse(
            """{"quota":{"weekly":{"used":88,"window_hours":168},"session":{"used":12,"window_hours":5}}}""",
            results,
            "이름이 바뀐 응답");
        Near(results, "이름이 바뀐 응답 · 주간", renamed.WeeklyUsed, 0.88);
        Near(results, "이름이 바뀐 응답 · 세션", renamed.SessionUsed, 0.12);

        return results;
    }

    /// <summary>검증 표를 표준 출력으로 내보낸다.</summary>
    public static bool Print(IReadOnlyList<CodexTestCase> results, Action<string> write)
    {
        int passed = results.Count(r => r.Passed);
        write($"Codex 파서 자체 점검: {passed}/{results.Count} 통과 " + (passed == results.Count ? "(전부 통과)" : "(실패 있음)"));
        write(string.Format(CultureInfo.InvariantCulture, "  {0,-30} {1,-16} {2,-18} {3}", "케이스", "기대", "실측", "판정"));

        foreach (CodexTestCase r in results)
        {
            write(string.Format(
                CultureInfo.InvariantCulture,
                "  {0,-30} {1,-16} {2,-18} {3}",
                r.Name,
                r.Expected,
                r.Observed,
                r.Passed ? "OK" : "FAIL"));
        }

        return passed == results.Count;
    }

    private static CodexUsage Parse(string json, List<CodexTestCase> results, string name)
    {
        bool ok = CodexUsageParser.TryParse(json, out CodexUsage usage, out string? error, Now);
        results.Add(new CodexTestCase($"{name} · 해석", "성공", ok ? "성공" : $"실패({error})", ok));
        return usage;
    }

    private static void Near(List<CodexTestCase> results, string name, double? actual, double expected, double tolerance = 0.005) =>
        results.Add(new CodexTestCase(
            name,
            expected.ToString("0.###", CultureInfo.InvariantCulture),
            actual is null ? "null" : actual.Value.ToString("0.###", CultureInfo.InvariantCulture),
            actual is not null && Math.Abs(actual.Value - expected) <= tolerance));

    private static void Text(List<CodexTestCase> results, string name, string actual, string expected) =>
        results.Add(new CodexTestCase(name, expected, actual, string.Equals(actual, expected, StringComparison.Ordinal)));

    private static void Same(List<CodexTestCase> results, string name, bool ok, string expected) =>
        results.Add(new CodexTestCase(name, expected, ok ? expected : "다름", ok));
}
