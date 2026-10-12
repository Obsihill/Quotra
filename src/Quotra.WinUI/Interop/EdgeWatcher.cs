using Microsoft.UI.Dispatching;

namespace Quotra.WinUI.Interop;

/// <summary>
/// 상단 엣지 감지기. DispatcherQueueTimer 로 커서 위치를 확인하고 흔들기 상태를 판정한다.
/// </summary>
internal sealed class EdgeWatcher : IDisposable
{
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly Native.LowLevelMouseProc _proc;
    private IntPtr _hook;
    private bool _armed = true;
    private bool _started;
    private bool _disposed;

    private int _direction;
    private double _anchorX;
    private int _reversals;
    private DateTimeOffset _strokeAt = DateTimeOffset.MinValue;

    public EdgeWatcher(int pollIntervalMs = 45)
    {
        _proc = HookCallback;
        var queue = DispatcherQueue.GetForCurrentThread();
        _pollTimer = queue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(pollIntervalMs);
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += (_, _) => PollOnce();
    }

    public double EdgeBand { get; set; } = 6;
    public double RearmDistance { get; set; } = 48;
    public double ShakeDistance { get; set; } = 40;
    public int ShakeWindowMs { get; set; } = 1000;
    public double ShakeSlack { get; set; } = 22;
    public int ShakeCount { get; set; } = 2;
    public bool UseLowLevelHook { get; set; } = true;
    public string? StartWarning { get; private set; }

    /// <summary>트리거 성립(UI 스레드).</summary>
    public event EventHandler<(int X, int Y, MonitorInfo Monitor)>? Triggered;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _pollTimer.Start();
        Log.Write($"edge watcher: 폴링 시작 interval={_pollTimer.Interval.TotalMilliseconds}ms");

        if (!UseLowLevelHook)
        {
            return;
        }

        try
        {
            _hook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandleW(null), 0);
            Log.Write(_hook == IntPtr.Zero
                ? $"edge watcher: 저수준 훅 실패(Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}) — 폴링만 사용"
                : "edge watcher: 저수준 마우스 훅 등록됨");
        }
        catch (Exception ex)
        {
            StartWarning = ex.Message;
            Log.Write($"edge watcher: 훅 예외 {ex.GetType().Name} — 폴링만 사용");
        }
    }

    private void PollOnce()
    {
        if (CursorProbe.TryGet(out var pt))
        {
            Consider(pt.X, pt.Y);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == Native.WM_MOUSEMOVE)
        {
            var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            Consider(data.pt.X, data.pt.Y);
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Consider(int x, int y)
    {
        var monitor = ScreenInfo.FromPoint(x, y);
        if (monitor is null)
        {
            return;
        }

        var offsetY = y - monitor.Monitor.Top;
        var inZone = offsetY >= -1 && offsetY <= EdgeBand + ShakeSlack;

        if (!inZone)
        {
            ResetGesture();
            if (offsetY > EdgeBand + RearmDistance)
            {
                _armed = true;
            }

            return;
        }

        if (!_armed)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (now - _strokeAt > TimeSpan.FromMilliseconds(ShakeWindowMs))
        {
            ResetGesture();
            _anchorX = x;
            _strokeAt = now;
            return;
        }

        var dx = x - _anchorX;
        if (_direction == 0)
        {
            if (Math.Abs(dx) >= ShakeDistance)
            {
                _direction = Math.Sign(dx);
                _anchorX = x;
                _strokeAt = now;
            }

            return;
        }

        if (Math.Sign(dx) == _direction)
        {
            _anchorX = x;
            _strokeAt = now;
            return;
        }

        if (Math.Abs(dx) < ShakeDistance)
        {
            return;
        }

        _direction = -_direction;
        _anchorX = x;
        _strokeAt = now;
        _reversals++;

        if (_reversals >= ShakeCount)
        {
            ResetGesture();
            _armed = false;
            Log.Write($"edge-trigger shake x2 ({x},{y}) — {ScreenInfo.Describe(monitor)}");
            Triggered?.Invoke(this, (x, y, monitor));
        }
    }

    private void ResetGesture()
    {
        _direction = 0;
        _reversals = 0;
        _strokeAt = DateTimeOffset.MinValue;
    }

    public void Rearm()
    {
        _armed = true;
        ResetGesture();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollTimer.Stop();
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
