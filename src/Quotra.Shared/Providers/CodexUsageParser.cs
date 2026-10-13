using System.Globalization;
using System.Text.Json;

namespace Quotra.Providers;

/// <summary>Codex 응답에서 뽑아낸 한도 사용량. 모르는 값은 <c>null</c> 로 둔다(0 과 구분).</summary>
/// <param name="WeeklyUsed">주간 창 사용률 0~1.</param>
/// <param name="SessionUsed">세션 창 사용률 0~1(플랜에 따라 없을 수 있다).</param>
/// <param name="WeeklyResetsIn">주간 창이 리셋되기까지 남은 시간.</param>
/// <param name="SessionResetsIn">세션 창이 리셋되기까지 남은 시간.</param>
/// <param name="Plan">응답의 플랜 이름(예: "prolite").</param>
/// <param name="LimitReached">응답이 한도 도달을 알렸는지(없으면 null).</param>
/// <param name="WindowsSummary">로그용 요약(예: "10080m:10%, 300m:42%").</param>
public sealed record CodexUsage(
    double? WeeklyUsed,
    double? SessionUsed,
    TimeSpan? WeeklyResetsIn,
    TimeSpan? SessionResetsIn,
    string Plan,
    bool? LimitReached,
    string WindowsSummary);

/// <summary>
/// Codex( ChatGPT 플랜) 사용량 응답 파서 — 네트워크 없이 문자열만 다룬다.
///
/// 응답의 필드명은 버전에 따라 바뀌므로 이름을 하드코딩하지 않는다.
/// 트리 전체에서 "사용률 + 창 길이"를 가진 객체를 모으고, **창 길이로** 주간/세션을 가른다.
/// (Codex 응답은 5시간 창이 primary, 7일 창이 secondary 로 오는 경우가 많아
///  이름 기반 매핑은 순서가 뒤집히기 쉽다.)
/// </summary>
public static class CodexUsageParser
{
    /// <summary>창 길이가 이 값(분) 미만이면 세션 창으로 본다 — 3일.</summary>
    public const double SessionWindowMaxMinutes = 3 * 1440;

    private static readonly string[] UsedNames = ["used_percent", "usedPercent", "utilization", "used", "percent_used"];
    private static readonly string[] ResetSecondsNames = ["resets_in_seconds", "reset_after_seconds"];
    private static readonly string[] ResetEpochNames = ["reset_at", "resets_at", "resets_at_seconds", "resetsAt"];

    /// <summary>원시 응답을 해석한다. 창을 하나도 못 찾으면 false 와 사유를 돌려준다.</summary>
    /// <param name="now">에포크 초를 상대 시간으로 바꿀 기준(검증에서 고정값을 넣는다).</param>
    public static bool TryParse(string json, out CodexUsage usage, out string? error, DateTimeOffset? now = null)
    {
        usage = new CodexUsage(null, null, null, null, string.Empty, null, string.Empty);
        error = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"JSON 이 아닙니다: {ex.Message}";
            return false;
        }

        using (document)
        {
            DateTimeOffset reference = now ?? DateTimeOffset.UtcNow;
            List<RateWindow> windows = [];
            Collect(document.RootElement, windows, null, reference);

            if (windows.Count == 0)
            {
                error = "사용률과 창 길이를 함께 가진 객체를 찾지 못했습니다.";
                return false;
            }

            // 창 길이로 가른다: 3일 이상 = 주간, 그 미만 = 세션(5시간 등).
            List<RateWindow> weekly = [.. windows.Where(w => w.WindowMinutes >= SessionWindowMaxMinutes)];
            List<RateWindow> session = [.. windows.Where(w => w.WindowMinutes < SessionWindowMaxMinutes)];

            // 같은 종류가 여러 개면 가장 긴 창을 쓴다(주간이면 7일, 세션이면 5시간).
            RateWindow? weeklyPick = weekly.Count > 0 ? weekly.MaxBy(w => w.WindowMinutes) : null;
            RateWindow? sessionPick = session.Count > 0 ? session.MaxBy(w => w.WindowMinutes) : null;

            string summary = string.Join(", ", windows
                .OrderByDescending(w => w.WindowMinutes)
                .Select(w => $"{w.WindowMinutes:0}m:{w.Used * 100:0.#}%"));

            usage = new CodexUsage(
                weeklyPick?.Used,
                sessionPick?.Used,
                weeklyPick?.ResetsIn,
                sessionPick?.ResetsIn,
                FindString(document.RootElement, "plan_type") ?? FindString(document.RootElement, "planType") ?? FindString(document.RootElement, "plan") ?? string.Empty,
                weeklyPick?.LimitReached,
                summary);

            return true;
        }
    }

    private readonly record struct RateWindow(double Used, double WindowMinutes, TimeSpan? ResetsIn, bool? LimitReached);

    /// <summary>트리 전체에서 "사용률 + 창 길이"를 가진 객체를 모은다.</summary>
    private static void Collect(JsonElement element, List<RateWindow> found, bool? limitReached, DateTimeOffset now)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // limit_reached 는 창 객체가 아니라 그 부모(rate_limit)에 있으므로 문맥으로 내려보낸다.
                bool? context = TryGetBool(element, "limit_reached") ?? limitReached;

                if (TryReadWindow(element, context, now) is { } window)
                {
                    found.Add(window);
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Collect(property.Value, found, context, now);
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Collect(item, found, limitReached, now);
                }

                break;
        }
    }

    private static RateWindow? TryReadWindow(JsonElement element, bool? limitReached, DateTimeOffset now)
    {
        double? used = null;
        bool isPercent = false;
        foreach (string name in UsedNames)
        {
            if (TryGetNumber(element, name, out double value))
            {
                used = value;
                isPercent = name is "used_percent" or "usedPercent" or "percent_used";
                break;
            }
        }

        if (used is null || !double.IsFinite(used.Value) || used.Value < 0)
        {
            return null;
        }

        // 0~1 로 오든 0~100 으로 오든 받아들인다.
        double normalized = Math.Clamp(isPercent || used.Value > 1.5 ? used.Value / 100.0 : used.Value, 0, 1);

        double? minutes = null;
        if (TryGetNumber(element, "windowDurationMins", out double rpcMinutes))
        {
            minutes = rpcMinutes;
        }
        else if (TryGetNumber(element, "window_minutes", out double m))
        {
            minutes = m;
        }
        else if (TryGetNumber(element, "window_seconds", out double s))
        {
            minutes = s / 60.0;
        }
        else if (TryGetNumber(element, "limit_window_seconds", out double ls))
        {
            minutes = ls / 60.0;
        }
        else if (TryGetNumber(element, "window_hours", out double h))
        {
            minutes = h * 60.0;
        }

        if (minutes is null or <= 0 || !double.IsFinite(minutes.Value))
        {
            return null;
        }

        TimeSpan? resetsIn = null;

        foreach (string name in ResetSecondsNames)
        {
            if (TryGetNumber(element, name, out double seconds))
            {
                resetsIn = TimeSpan.FromSeconds(Math.Max(0, seconds));
                break;
            }
        }

        if (resetsIn is null)
        {
            foreach (string name in ResetEpochNames)
            {
                if (TryGetNumber(element, name, out double epochSeconds))
                {
                    double delta = epochSeconds - now.ToUnixTimeSeconds();
                    resetsIn = TimeSpan.FromSeconds(Math.Max(0, delta));
                    break;
                }
            }
        }

        return new RateWindow(normalized, minutes.Value, resetsIn, limitReached);
    }

    private static bool? TryGetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static bool TryGetNumber(JsonElement element, string name, out double value)
    {
        value = 0;

        if (!element.TryGetProperty(name, out JsonElement property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.Number:
                return property.TryGetDouble(out value);

            case JsonValueKind.String:
                return double.TryParse(
                    property.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);

            default:
                return false;
        }
    }

    private static string? FindString(JsonElement element, string name)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }

                    if (FindString(property.Value, name) is { } nested)
                    {
                        return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (FindString(item, name) is { } nested)
                    {
                        return nested;
                    }
                }

                break;
        }

        return null;
    }
}
