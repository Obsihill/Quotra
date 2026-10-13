using System.Runtime.InteropServices;

namespace Quotra.WinUI.Interop;

internal enum TrayCommand { ToggleWidget = 1, ProviderSettings = 2, Exit = 3, ToggleRunAtStartup = 4 }

/// <summary>전역 단축키, Windows 설정 변경, 알림 영역 메뉴를 받는 숨은 창.</summary>
internal sealed class SystemMessageSink : IDisposable
{
    private const int TrayIconId = 1;
    private const int TrayCallbackMessage = Native.WM_APP + 1;

    private readonly Native.WndProcDelegate _callback;
    private IntPtr _hwnd;
    private IntPtr _previous;
    private IntPtr _trayIcon;
    private int _taskbarCreated;
    private bool _trayIconAdded;
    private string _iconPath = string.Empty;

    public bool RunAtStartupEnabled { get; set; }

    public event EventHandler? ToggleRequested;
    public event EventHandler? SystemThemeChanged;
    public event EventHandler? DisplayChanged;
    public event EventHandler<TrayCommand>? TrayCommandRequested;

    public SystemMessageSink() => _callback = WndProc;

    public void Start(string iconPath)
    {
        _iconPath = iconPath;
        // Shell_NotifyIcon expects a normal window handle for callbacks; keep this
        // tool window hidden instead of using HWND_MESSAGE.
        uint exStyle = unchecked((uint)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        _hwnd = Native.CreateWindowExW(exStyle, "STATIC", "Quotra.SystemMessages", 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("시스템 메시지 창을 만들지 못했습니다.");

        _previous = Native.SetWindowLongPtrW(_hwnd, -4, Marshal.GetFunctionPointerForDelegate(_callback));
        bool registered = Native.RegisterHotKey(_hwnd, 1, Native.MOD_CONTROL | Native.MOD_ALT, 0x78);
        Log.Write($"hotkey: Ctrl+Alt+F9 등록={registered}");

        _taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
        AddTrayIcon(iconPath);
    }

    private void AddTrayIcon(string iconPath)
    {
        if (_hwnd == IntPtr.Zero || _trayIconAdded) return;

        if (_trayIcon == IntPtr.Zero)
        {
            _trayIcon = Native.LoadImageW(IntPtr.Zero, iconPath, Native.IMAGE_ICON, 0, 0, Native.LR_LOADFROMFILE);
            if (_trayIcon == IntPtr.Zero)
            {
                Log.Write($"tray icon: 아이콘 파일을 읽지 못했습니다 (win32={Marshal.GetLastWin32Error()})");
                return;
            }
        }

        var data = new Native.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _trayIcon,
            szTip = "Quotra · 사용량 위젯",
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        _trayIconAdded = Native.Shell_NotifyIconW(Native.NIM_ADD, ref data);
        Log.Write($"tray icon: 등록={_trayIconAdded} (win32={Marshal.GetLastWin32Error()})");
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == Native.WM_HOTKEY)
        {
            ToggleRequested?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }
        if (message == Native.WM_SETTINGCHANGE)
        {
            SystemThemeChanged?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }
        if (message == Native.WM_DISPLAYCHANGE)
        {
            DisplayChanged?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }
        if (message == unchecked((uint)_taskbarCreated) && _taskbarCreated != 0)
        {
            _trayIconAdded = false;
            AddTrayIcon(_iconPath);
            return IntPtr.Zero;
        }
        if (message == TrayCallbackMessage)
        {
            int notification = unchecked((int)lParam.ToInt64());
            if (notification == Native.WM_LBUTTONUP)
            {
                TrayCommandRequested?.Invoke(this, TrayCommand.ToggleWidget);
            }
            else if (notification == Native.WM_RBUTTONUP)
            {
                ShowTrayMenu();
            }
            return IntPtr.Zero;
        }

        return Native.CallWindowProcW(_previous, hwnd, message, wParam, lParam);
    }

    private void ShowTrayMenu()
    {
        IntPtr menu = Native.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)(int)TrayCommand.ToggleWidget, "위젯 표시 / 숨기기");
            uint startupFlags = Native.MF_STRING | (RunAtStartupEnabled ? Native.MF_CHECKED : 0);
            Native.AppendMenuW(menu, startupFlags, (IntPtr)(int)TrayCommand.ToggleRunAtStartup, "Windows 로그인 시 실행");
            Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)(int)TrayCommand.ProviderSettings, "프로바이더 설정…");
            Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);
            Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)(int)TrayCommand.Exit, "종료");

            Native.GetCursorPos(out Native.POINT point);
            Native.SetForegroundWindow(_hwnd);
            int command = Native.TrackPopupMenuEx(menu,
                Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON,
                point.X, point.Y, _hwnd, IntPtr.Zero);
            Native.PostMessageW(_hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (Enum.IsDefined(typeof(TrayCommand), command))
            {
                TrayCommandRequested?.Invoke(this, (TrayCommand)command);
            }
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;

        Native.UnregisterHotKey(_hwnd, 1);
        if (_trayIconAdded)
        {
            var data = new Native.NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = TrayIconId,
                szTip = string.Empty,
                szInfo = string.Empty,
                szInfoTitle = string.Empty,
            };
            Native.Shell_NotifyIconW(Native.NIM_DELETE, ref data);
            _trayIconAdded = false;
        }

        if (_trayIcon != IntPtr.Zero)
        {
            Native.DestroyIcon(_trayIcon);
            _trayIcon = IntPtr.Zero;
        }
        Native.SetWindowLongPtrW(_hwnd, -4, _previous);
        Native.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }
}
