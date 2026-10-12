using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quotra.Forecast;

/// <summary>
/// 프로바이더·한도창별 사용률 표본 저장소(10분 간격).
///
/// 목적은 "지금 얼마 남았나"가 아니라 "얼마나 빨리 줄고 있나"다. 그래서 값 자체보다
/// 시각이 붙은 표본이 필요하고, 앱을 껐다 켜도 이력이 이어져야 하므로 파일로 남긴다.
/// 저장/로드 실패는 치명적이지 않다 — 이력이 없으면 예측이 "측정 중"으로만 남는다.
/// </summary>
public sealed class UsageHistory
{
    /// <summary>10분 간격 7일치 = 1008개. 시리즈당 상한.</summary>
    private const int MaxSamplesPerSeries = 1008;

    private readonly Dictionary<string, List<UsagePoint>> _series = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>이 기간보다 오래된 표본은 버린다.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>전체 표본 수(화면 표시·검증용).</summary>
    public int TotalSamples
    {
        get
        {
            lock (_gate)
            {
                int total = 0;
                foreach (List<UsagePoint> list in _series.Values)
                {
                    total += list.Count;
                }

                return total;
            }
        }
    }

    public static string Key(string providerId, UsageWindow window) => $"{providerId}|{window}";

    /// <summary>
    /// 표본 하나를 기록한다. 같은 시각이거나 과거면 무시한다(재시작 직후 중복 방지).
    /// </summary>
    public void Record(string providerId, UsageWindow window, double used, DateTimeOffset at)
    {
        string key = Key(providerId, window);

        lock (_gate)
        {
            if (!_series.TryGetValue(key, out List<UsagePoint>? list))
            {
                list = [];
                _series[key] = list;
            }

            if (list.Count > 0 && at <= list[^1].At)
            {
                return;
            }

            list.Add(new UsagePoint(at, Math.Clamp(used, 0, 1)));

            if (list.Count > MaxSamplesPerSeries)
            {
                list.RemoveRange(0, list.Count - MaxSamplesPerSeries);
            }
        }
    }

    /// <summary>한 시리즈의 표본(시간순 복사본).</summary>
    public IReadOnlyList<UsagePoint> Points(string providerId, UsageWindow window)
    {
        lock (_gate)
        {
            return _series.TryGetValue(Key(providerId, window), out List<UsagePoint>? list)
                ? list.ToArray()
                : [];
        }
    }

    /// <summary>보관 기간을 넘긴 표본을 버린다.</summary>
    public void Prune(DateTimeOffset now)
    {
        DateTimeOffset floor = now - Retention;

        lock (_gate)
        {
            foreach (List<UsagePoint> list in _series.Values)
            {
                list.RemoveAll(p => p.At < floor);
            }
        }
    }

    // ------------------------------------------------------------ 파일 저장

    /// <summary>파일에서 읽는다. 없거나 깨져도 예외를 던지지 않는다(이력은 있으면 좋은 것).</summary>
    public static UsageHistory Load(string path, out string? warning)
    {
        warning = null;
        var history = new UsageHistory();

        try
        {
            if (!File.Exists(path))
            {
                return history;
            }

            var dto = JsonSerializer.Deserialize<Dictionary<string, List<PointDto>>>(File.ReadAllText(path));
            if (dto is null)
            {
                return history;
            }

            foreach ((string key, List<PointDto> points) in dto)
            {
                var list = new List<UsagePoint>(points.Count);
                foreach (PointDto p in points)
                {
                    list.Add(new UsagePoint(p.At, Math.Clamp(p.Used, 0, 1)));
                }

                list.Sort((a, b) => a.At.CompareTo(b.At));
                history._series[key] = list;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            warning = $"사용 이력을 읽지 못했습니다 ({ex.GetType().Name}) — 빈 이력으로 시작합니다.";
        }

        return history;
    }

    /// <summary>파일에 쓴다. 실패해도 예외를 던지지 않고 이유를 돌려준다.</summary>
    public string? Save(string path)
    {
        try
        {
            var dto = new Dictionary<string, List<PointDto>>(StringComparer.Ordinal);

            lock (_gate)
            {
                foreach ((string key, List<UsagePoint> list) in _series)
                {
                    var points = new List<PointDto>(list.Count);
                    foreach (UsagePoint p in list)
                    {
                        points.Add(new PointDto { At = p.At, Used = Math.Round(p.Used, 4) });
                    }

                    dto[key] = points;
                }
            }

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 임시 파일에 쓰고 교체한다 — 저장 중 종료돼도 이력이 깨지지 않게.
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(dto, SerializerOptions));
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private sealed class PointDto
    {
        [JsonPropertyName("at")]
        public DateTimeOffset At { get; set; }

        [JsonPropertyName("used")]
        public double Used { get; set; }
    }
}
