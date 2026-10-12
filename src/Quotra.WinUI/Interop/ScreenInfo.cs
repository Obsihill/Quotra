namespace Quotra.WinUI.Interop;

/// <summary>모니터 한 대의 정보(물리 픽셀 + DPI).</summary>
internal sealed record MonitorInfo(IntPtr Handle, Native.RECT Monitor, Native.RECT Work, uint Dpi, bool IsPrimary)
{
    public double Scale => Dpi / 96.0;
    public int CenterX => Monitor.Left + Monitor.Width / 2;
    public int Bottom => Monitor.Bottom;
}

/// <summary>GetMonitorInfo / GetDpiForMonitor 기반 모니터 정보 질의.</summary>
internal static class ScreenInfo
{
    public static MonitorInfo? FromPoint(int x, int y)
    {
        var pt = new Native.POINT { X = x, Y = y };
        return Describe(Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST));
    }

    public static MonitorInfo? FromCursor()
        => CursorProbe.TryGet(out var pt) ? FromPoint(pt.X, pt.Y) : null;

    public static MonitorInfo? Primary() => Describe(Native.MonitorFromPoint(new Native.POINT(), Native.MONITOR_DEFAULTTONEAREST));

    public static MonitorInfo? Describe(IntPtr hMonitor)
    {
        if (hMonitor == IntPtr.Zero)
        {
            return null;
        }

        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfoW(hMonitor, ref info))
        {
            return null;
        }

        uint dpi = 96;
        try
        {
            if (Native.GetDpiForMonitor(hMonitor, Native.MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0)
            {
                dpi = dx;
            }
        }
        catch (DllNotFoundException)
        {
            // shcore.dll 이 없는 환경은 96dpi 로 간주.
        }

        return new MonitorInfo(hMonitor, info.rcMonitor, info.rcWork, dpi, (info.dwFlags & 1) == 1);
    }

    public static string Describe(MonitorInfo? monitor)
        => monitor is null
            ? "(모니터 없음)"
            : $"monitor=({monitor.Monitor.Left},{monitor.Monitor.Top})-({monitor.Monitor.Right},{monitor.Monitor.Bottom}) dpi={monitor.Dpi} scale={monitor.Scale:0.##} primary={monitor.IsPrimary}";
}
