using System.Runtime.InteropServices;

namespace Quotra.WinUI.Interop;

/// <summary>알림 영역 아이콘 없이 전역 핫키와 Windows 설정 변경을 수신한다.</summary>
internal sealed class SystemMessageSink : IDisposable
{
    private readonly Native.WndProcDelegate _callback;
    private IntPtr _hwnd;
    private IntPtr _previous;
    public event EventHandler? ToggleRequested;
    public event EventHandler? SystemThemeChanged;
    public event EventHandler? DisplayChanged;
    public SystemMessageSink() => _callback = WndProc;
    public void Start()
    {
        _hwnd = Native.CreateWindowExW(0, "STATIC", "Quotra.SystemMessages", 0,
            0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("시스템 메시지 창을 만들지 못했습니다.");
        _previous = Native.SetWindowLongPtrW(_hwnd, -4, Marshal.GetFunctionPointerForDelegate(_callback));
        bool registered = Native.RegisterHotKey(_hwnd, 1, Native.MOD_CONTROL | Native.MOD_ALT, 0x78);
        Log.Write($"hotkey: Ctrl+Alt+F9 등록={registered}");
    }
    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == Native.WM_HOTKEY) { ToggleRequested?.Invoke(this, EventArgs.Empty); return IntPtr.Zero; }
        if (message == Native.WM_SETTINGCHANGE) { SystemThemeChanged?.Invoke(this, EventArgs.Empty); return IntPtr.Zero; }
        if (message == Native.WM_DISPLAYCHANGE) { DisplayChanged?.Invoke(this, EventArgs.Empty); return IntPtr.Zero; }
        return Native.CallWindowProcW(_previous, hwnd, message, wParam, lParam);
    }
    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.UnregisterHotKey(_hwnd, 1);
        Native.SetWindowLongPtrW(_hwnd, -4, _previous);
        Native.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }
}
