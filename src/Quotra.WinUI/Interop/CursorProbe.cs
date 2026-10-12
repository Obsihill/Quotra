using System.Diagnostics;

namespace Quotra.WinUI.Interop;

/// <summary>
/// 커서 좌표 공급 지점. <c>--simpath</c> 로 가짜 커서를 주입하면 결정적 검증이 된다.
/// </summary>
internal static class CursorProbe
{
    private static readonly List<Step> Script = [];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly record struct Step(TimeSpan At, Native.POINT Point);

    public static bool IsSimulated => Script.Count > 0;

    /// <summary>"1280,600@0;1280,0@1200;1280,600@4000" 형태(@ms 생략 시 이전 시점).</summary>
    public static void LoadPath(string spec)
    {
        var parsed = new List<Step>();
        var offset = TimeSpan.Zero;

        foreach (var raw in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segments = raw.Split('@', StringSplitOptions.TrimEntries);
            var coords = segments[0].Split(',', StringSplitOptions.TrimEntries);
            if (coords.Length != 2 || !int.TryParse(coords[0], out var x) || !int.TryParse(coords[1], out var y))
            {
                continue;
            }

            if (segments.Length > 1 && int.TryParse(segments[1], out var ms))
            {
                offset = TimeSpan.FromMilliseconds(ms);
            }

            parsed.Add(new Step(offset, new Native.POINT { X = x, Y = y }));
        }

        if (parsed.Count == 0)
        {
            return;
        }

        parsed.Sort((a, b) => a.At.CompareTo(b.At));
        Script.Clear();
        Script.AddRange(parsed);
        Clock.Restart();
    }

    public static bool TryGet(out Native.POINT point)
    {
        if (Script.Count == 0)
        {
            return Native.GetCursorPos(out point);
        }

        var now = Clock.Elapsed;
        var current = Script[0].Point;
        foreach (var step in Script)
        {
            if (step.At <= now)
            {
                current = step.Point;
            }
            else
            {
                break;
            }
        }

        point = current;
        return true;
    }
}
