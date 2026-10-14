using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;
using Quotra.Forecast;
using Quotra.WinUI.Controls;
using Quotra.WinUI.Interop;
using Quotra.WinUI.Models;
using Quotra.WinUI.Services;
using Quotra.WinUI.Themes;
using Windows.Foundation;
using Windows.Graphics;

namespace Quotra.WinUI.Views;

/// <summary>
/// Always On Top Quota 위젯 — 프로바이더를 **가로로 나란히** 놓은 배터리 바.
///
///   · 항상 위     : OverlappedPresenter.IsAlwaysOnTop + WS_EX_TOPMOST
///   · 표면        : Windows Desktop Acrylic + DWM 둥근 모서리
///   · 배터리      : BatteryGauge — 남은 한도를 왼쪽부터 채운다
///   · 마우스 호버 : 올린 셀에만 퍼센테이지와 초기화까지 남은 시간이 나온다(창 크기는 그대로)
///   · 테마        : Windows 설정(라이트/다크)을 따르거나 사용자가 강제 지정
///
/// 화면에 늘 떠 있는 것은 **예측 감소량 한 줄**뿐이다. 나머지는 물어봤을 때(호버)만 나온다.
/// 포커스를 훔치지 않는다(WS_EX_NOACTIVATE + SW_SHOWNOACTIVATE). 작업 중인 창에서
/// 키보드 포커스가 이동하면 위젯이 아니라 도구로서 실패한 것이다.
/// </summary>
public enum WidgetCommand { TogglePinned, ToggleAlwaysOnTop, ToggleRunAtStartup, ThemeAuto, ThemeLight, ThemeDark, ToggleAcrylic, ProviderSettings, Refresh, Hide, Exit }

public sealed partial class WidgetWindow : Window
{
    private const int CellWidthDip = 232;
    private const int CellSpacingDip = 9;     // 1dip divider + 4dip margin on each side
    private const int PanelPaddingDip = 14;   // 6dip padding + 1dip border on each side
    private const int WidgetHeightDip = 80;   // 66dip cell + panel padding/border; fixed on hover
    private const int EmptyWidthDip = 340;     // 프로바이더가 없을 때의 폭
    private const int TopGapDip = 14;          // 작업 영역 위쪽 여백
    private const int HoverMarginDip = 4;      // 셀 경계에서 살짝 벗어나도 유지
    private const int HoverPollMs = 120;

    private readonly IReadOnlyList<ProviderUsage> _catalogProviders;
    private readonly List<ProviderUsage> _providers = [];
    private readonly MenuFlyout _settingsMenu = new();
    private bool _settingsMenuOpen;
    public event EventHandler<WidgetCommand>? CommandInvoked;
    private readonly WidgetSettings _settings;
    private readonly UsageHistory? _history;
    private readonly ForecastOptions _forecastOptions;
    private readonly TimeSpan _sampleInterval;
    private readonly DispatcherQueueTimer _hoverWatch;
    private readonly List<CellUi> _cells = [];
    private readonly IntPtr _hwnd;

    private MonitorInfo? _monitor;
    private bool _visible = true;
    private bool _isLight;
    private int _hoveredIndex = -1;
    private string _hoveredText = string.Empty;

    /// <summary>셀 하나의 화면 요소 + 호버 전후에 보여줄 문구.</summary>
    private sealed class CellUi
    {
        public required ProviderCell Root { get; init; }

        /// <summary>게이지 겸 숫자 자리 — 문구는 이 배터리 안에 들어간다.</summary>
        public required BatteryGauge Battery { get; init; }

        /// <summary>평소에 보이는 문구 — 예측 감소량.</summary>
        public string DefaultText { get; set; } = string.Empty;

        /// <summary>호버했을 때 보이는 문구 — 퍼센테이지 + 초기화까지 남은 시간.</summary>
        public string HoverText { get; set; } = string.Empty;
    }

    /// <summary>항상 위 상태가 바뀌었을 때.</summary>
    public event EventHandler<bool>? AlwaysOnTopChanged;

    public WidgetWindow(
        IReadOnlyList<ProviderUsage> providers,
        WidgetSettings settings,
        UsageHistory? history = null,
        ForecastOptions? forecastOptions = null,
        TimeSpan? sampleInterval = null)
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            Log.Write(ex, "WidgetWindow.xaml 초기화 실패");
            throw;
        }

        _catalogProviders = providers;
        _settings = settings;
        _history = history;
        _forecastOptions = forecastOptions ?? new ForecastOptions();
        _sampleInterval = sampleInterval ?? UsageSampler.DefaultInterval;
        _hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Quotra.ico"));

        Log.Write($"widget: hwnd=0x{_hwnd:X}");

        ConfigureChrome();
        ConfigureBackdrop();
        EmptySettingsButton.Click += (_, _) => CommandInvoked?.Invoke(this, WidgetCommand.ProviderSettings);
        BuildCells();
        ConfigureSettingsMenu();

        SetAlwaysOnTop(_settings.AlwaysOnTop, save: false);
        ApplyTheme(_settings.Theme);

        _monitor = ScreenInfo.Primary();
        PlaceOnMonitor(_monitor);
        // Layout rounding changes once the XAML island knows the monitor's DPI.
        RootHost.Loaded += (_, _) =>
        {
            if (!_popupOpen && !_popupAnimating)
            {
                PlaceOnMonitor(_monitor);
            }
        };

        // 호버: PointerEntered/Exited 는 자식 요소로 옮겨갈 때도 발생해서 믿을 수 없다.
        // 실제 커서 좌표를 창 좌표계로 옮겨 셀과 비교하는 방식이 결정적이다.
        _hoverWatch = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hoverWatch.Interval = TimeSpan.FromMilliseconds(HoverPollMs);
        _hoverWatch.IsRepeating = true;
        _hoverWatch.Tick += (_, _) => HoverTick();
        _hoverWatch.Start();

        Closed += (_, _) => _hoverWatch.Stop();
    }

    /// <summary>항상 위 여부.</summary>
    public bool IsAlwaysOnTop => _settings.AlwaysOnTop;

    /// <summary>현재 화면에 보이는가.</summary>
    public bool IsVisible => _visible;

    /// <summary>현재 메뉴가 어두운 테마인지.</summary>
    public bool PrefersDarkMenu => !_isLight;

    public void SetAcrylic(bool enabled)
    {
        _settings.Acrylic = enabled;
        ConfigureBackdrop();
        _settings.Save();
    }

    private void ConfigureSettingsMenu()
    {
        BuildSettingsMenu();
        _settingsMenu.Opened += (_, _) => { _settingsMenuOpen = true; Log.Write("settings menu: opened"); };
        _settingsMenu.Closed += (_, _) =>
        {
            _settingsMenuOpen = false;
            _popupOutsideSince = DateTimeOffset.MinValue;
            _popupStartedAt = DateTimeOffset.Now;
        };
        RootHost.ContextRequested += (_, args) =>
        {
            if (args.TryGetPosition(RootHost, out Point position))
                ShowSettingsMenu(position);
            else ShowSettingsMenu();
            args.Handled = true;
        };
    }

    internal void ShowSettingsMenu(Point? position = null)
    {
        BuildSettingsMenu();
        _settingsMenuOpen = true;
        if (position is { } point)
            _settingsMenu.ShowAt(RootHost, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
        else _settingsMenu.ShowAt(RootHost);
    }

    private void BuildSettingsMenu()
    {
        _settingsMenu.Items.Clear();
        void Toggle(string text, bool selected, WidgetCommand command)
        {
            var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = selected };
            item.Click += (_, _) => CommandInvoked?.Invoke(this, command);
            _settingsMenu.Items.Add(item);
        }
        void Action(string text, WidgetCommand command)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => CommandInvoked?.Invoke(this, command);
            _settingsMenu.Items.Add(item);
        }
        Toggle("위젯 고정", _settings.Pinned, WidgetCommand.TogglePinned);
        Toggle("항상 위에 표시", _settings.AlwaysOnTop, WidgetCommand.ToggleAlwaysOnTop);
        Toggle("Windows 로그인 시 실행", _settings.RunAtStartup, WidgetCommand.ToggleRunAtStartup);
        Toggle("반투명 배경", _settings.Acrylic, WidgetCommand.ToggleAcrylic);
        var themes = new MenuFlyoutSubItem { Text = "테마" };
        foreach (var option in new[] { ("시스템 설정", ThemeMode.Auto, WidgetCommand.ThemeAuto),
                                      ("밝게", ThemeMode.Light, WidgetCommand.ThemeLight),
                                      ("어둡게", ThemeMode.Dark, WidgetCommand.ThemeDark) })
        {
            var item = new ToggleMenuFlyoutItem { Text = option.Item1, IsChecked = _settings.Theme == option.Item2 };
            item.Click += (_, _) => CommandInvoked?.Invoke(this, option.Item3);
            themes.Items.Add(item);
        }
        _settingsMenu.Items.Add(themes);
        _settingsMenu.Items.Add(new MenuFlyoutSeparator());
        Action("지금 새로고침", WidgetCommand.Refresh);
        Action("프로바이더 설정…", WidgetCommand.ProviderSettings);
        Action("위젯 숨기기 · Ctrl+Alt+F9", WidgetCommand.Hide);
        _settingsMenu.Items.Add(new MenuFlyoutSeparator());
        Action("종료", WidgetCommand.Exit);
    }

    // ---------------------------------------------------------------- 창 구성

    private void ConfigureChrome()
    {
        ExtendsContentIntoTitleBar = true;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = _settings.AlwaysOnTop;
        }

        // 모서리: XAML 의 CornerRadius(8)와 DWM 라운드를 일치시킨다.
        int round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        // NOACTIVATE(포커스 탈취 금지) + TOOLWINDOW(Alt+Tab/작업표시줄 제외) + TOPMOST
        Native.ApplyExStyle(
            _hwnd,
            Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST,
            Native.WS_EX_TRANSPARENT);

        long ex = (long)Native.GetWindowLongPtrW(_hwnd, Native.GWL_EXSTYLE);
        Log.Write($"widget exstyle=0x{ex:X} noactivate={(ex & Native.WS_EX_NOACTIVATE) != 0} " +
                  $"toolwindow={(ex & Native.WS_EX_TOOLWINDOW) != 0} topmost={(ex & Native.WS_EX_TOPMOST) != 0}");
    }

    /// <summary>Windows Acrylic 재질을 창 뒤에 적용하고, 그 위의 XAML 표면을 투명하게 둔다.</summary>
    private void ConfigureBackdrop()
    {
        SystemBackdrop = null;
        bool light = SystemTheme.ResolveIsLight(_settings.Theme);
        var fallback = new SolidColorBrush(light ? ColorHelper.FromArgb(255, 243, 243, 243) : ColorHelper.FromArgb(255, 32, 32, 32));
        RootHost.Background = fallback;
        Panel.Background = fallback;
        if (!_settings.Acrylic || !DesktopAcrylicController.IsSupported())
        {
            Log.Write("widget backdrop: Acrylic 미사용 또는 미지원 — 불투명 표면으로 표시");
            return;
        }

        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
            var transparent = new SolidColorBrush(Colors.Transparent);
            RootHost.Background = transparent;
            Panel.Background = transparent;
            Log.Write("widget backdrop: Desktop Acrylic 적용 · XAML 표면 투명");
        }
        catch (Exception ex)
        {
            SystemBackdrop = null;
            Log.Write(ex, "widget backdrop: Acrylic 적용 실패 — 불투명 표면으로 대체");
        }
    }

    /// <summary>프로바이더마다 배터리 셀을 만든다(가로 배치).</summary>
    private void BuildCells()
    {
        ProviderRow.Children.Clear();
        _cells.Clear();
        _hoveredIndex = -1;
        _hoveredText = string.Empty;
        UpdateEmptyState();
        if (_providers.Count == 0)
        {
            // 빈 패널 대신 다음 할 일을 알려준다.
            EmptyState.Visibility = Visibility.Visible;
            Log.Write($"widget: 프로바이더 0곳 · 폭 {WindowWidthDip()}dip");
            return;
        }

        foreach (ProviderUsage provider in _providers)
        {
            if (_cells.Count > 0)
            {
                ProviderRow.Children.Add(new Border
                {
                    Style = (Style)Application.Current.Resources["ProviderDividerStyle"],
                });
            }

            var root = new ProviderCell { Width = CellWidthDip };
            root.SetIdentity(provider.Name, provider.PrimaryLabel, provider.Accent);
            root.Battery.HasValue = false;
            root.Battery.Level = 0;

            ProviderRow.Children.Add(root);
            _cells.Add(new CellUi { Root = root, Battery = root.Battery });
        }

        Log.Write($"widget: 셀 {_cells.Count}개 · 폭 {WindowWidthDip()}dip · 높이 {WidgetHeightDip}dip · 표본 간격 {SampleIntervalText()}");
    }

    /// <summary>프로바이더 수에 맞춘 창 폭(논리 픽셀).</summary>
    private int WindowWidthDip() => _providers.Count == 0
        ? EmptyWidthDip
        : PanelPaddingDip
          + (_providers.Count * CellWidthDip)
          + ((_providers.Count - 1) * CellSpacingDip);

    // ---------------------------------------------------------------- 데이터 반영

    /// <summary>사용량 값을 화면에 반영한다. 화면에는 예측 감소량만, 호버 줄에는 퍼센테이지와 초기화 시각.</summary>
    public void Refresh()
    {
        var visible = _catalogProviders.Where(p => p.HasPrimary).ToList();
        if (!_providers.SequenceEqual(visible))
        {
            _providers.Clear();
            _providers.AddRange(visible);
            BuildCells();
            if (_monitor is not null && !_popupAnimating) PlaceOnMonitor(_monitor);
        }
        UpdateEmptyState();
        DateTimeOffset now = DateTimeOffset.Now;

        for (int i = 0; i < _cells.Count && i < _providers.Count; i++)
        {
            ProviderUsage provider = _providers[i];
            CellUi cell = _cells[i];

            cell.Root.SetIdentity(provider.Name, provider.PrimaryLabel, provider.Accent);
            ToolTipService.SetToolTip(cell.Root, ProviderStatusTooltip(provider));
            cell.Battery.HasValue = provider.HasPrimary;
            cell.Battery.Level = provider.RemainingOrZero;
            cell.Battery.FillColor = provider.HasPrimary
                ? Palette.GaugeColorFor(Palette.SeverityOf(provider.Primary ?? 0), _isLight)
                : (_isLight ? Palette.LabelLight : Palette.LabelDark);

            if (!provider.HasPrimary)
            {
                // 값이 확인되지 않은 동안은 아무것도 표시하지 않는다 — 굳이 상태를 표현할 필요 없다.
                cell.DefaultText = "";
                cell.HoverText = "";
            }
            else
            {
                // 평소: 예측 감소량 (고갈이 예상되면 시각까지). 아직 예측이 준비 안 됐으면(Collecting) 아무것도 표시하지 않는다.
                var forecast = Forecast(provider, now);
                cell.DefaultText = forecast.Status == ForecastStatus.Collecting ? "측정 중" : ForecastText.Short(forecast, now);
                if (forecast.Status == ForecastStatus.Collecting)
                {
                    ToolTipService.SetToolTip(cell.Root,
                        $"{ProviderStatusTooltip(provider)}\n예측은 사용량을 10분 이상 측정한 뒤 표시됩니다. 현재 {forecast.SampleSpan.TotalMinutes:0.#}분, 표본 {forecast.SampleCount}개.");
                }

                // 호버: 남은 퍼센테이지 + 초기화까지 남은 시간
                cell.HoverText = $"남음 {provider.RemainingText} · 리셋 {provider.ResetRemainingShortAt(now)}";
            }

            // Expose the same information to assistive technology without duplicate clipped text.
            AutomationProperties.SetName(cell.Battery, provider.HasPrimary
                ? $"{provider.Name} · {cell.HoverText} · {cell.DefaultText}".TrimEnd(' ', '·')
                : $"{provider.Name} · {provider.StatusText}");
        }

        ApplyHoverVisuals();
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = _providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_catalogProviders.Count == 0)
        {
            EmptyTitle.Text = "프로바이더 목록을 찾을 수 없습니다";
            EmptyDescription.Text = "설치 파일의 서비스 목록을 확인하세요.";
            EmptySettingsButton.Visibility = Visibility.Collapsed;
            return;
        }

        EmptySettingsButton.Visibility = Visibility.Visible;
        ProviderUsage[] linked = _catalogProviders.Where(provider => provider.IsLinked).ToArray();
        ProviderUsage[] failed = linked.Where(provider => provider.Error is not null).ToArray();
        if (linked.Length == 0)
        {
            EmptyTitle.Text = "사용량 서비스를 연결하세요";
            EmptyDescription.Text = "API 키가 필요한 서비스는 연결 설정에서 등록할 수 있습니다.";
        }
        else if (failed.Length > 0)
        {
            EmptyTitle.Text = "사용량 조회에 실패했습니다";
            EmptyDescription.Text = $"{string.Join(", ", failed.Select(provider => provider.Name))} · 프로바이더 설정에서 오류 원인과 연결 안내를 확인하세요.";
        }
        else
        {
            EmptyTitle.Text = "사용량을 불러오는 중입니다";
            EmptyDescription.Text = "예측은 사용량 표본을 10분 이상 모은 뒤 표시됩니다.";
        }
    }

    private static string ProviderStatusTooltip(ProviderUsage provider)
    {
        string status = provider.Error is not null
            ? (provider.HasAnyValue ? "조회 실패 · 이전 성공 값을 유지합니다" : "연결 확인 필요")
            : provider.StatusText;
        string updated = provider.UpdatedAt is { } at
            ? $"마지막 성공 {at.LocalDateTime:yyyy-MM-dd HH:mm}"
            : "성공한 조회 없음";
        string description = string.IsNullOrWhiteSpace(provider.Note) ? string.Empty : $"\n{provider.Note}";
        string error = provider.Error is null ? string.Empty : $"\n{provider.Error}";
        return $"{provider.Name} · {status}\n{updated}{error}{description}";
    }

    /// <summary>호버된 셀만 퍼센테이지/초기화 시각을 보여준다.</summary>
    private void ApplyHoverVisuals()
    {
        for (int i = 0; i < _cells.Count; i++)
        {
            CellUi cell = _cells[i];
            bool hovered = i == _hoveredIndex;

            // 문구는 배터리 안에서 자리만 바뀐다 — 창 크기도, 배터리 높이도 변하지 않는다.
            cell.Battery.Text = hovered ? cell.HoverText : cell.DefaultText;
            cell.Root.SetHovered(hovered);
        }
    }

    /// <summary>프로바이더 한 곳의 소모 속도/고갈 예측(장기 한도 기준). 값이 없으면 "측정 중"으로 둔다.</summary>
    private ForecastResult Forecast(ProviderUsage provider, DateTimeOffset now)
    {
        if (_history is null || provider.Primary is not { } used)
        {
            return ForecastResult.Collecting(0, TimeSpan.Zero);
        }

        return DepletionForecaster.Compute(
            _history.Points(provider.PrimaryHistoryKey, UsageWindow.Primary),
            used,
            now,
            provider.PrimaryResetsAt,
            _forecastOptions);
    }

    /// <summary>로그에 쓸 표본 간격 문구("10분" / "5초" 등).</summary>
    private string SampleIntervalText() => _sampleInterval >= TimeSpan.FromMinutes(1)
        ? $"{(int)_sampleInterval.TotalMinutes}분"
        : $"{_sampleInterval.TotalSeconds:0}초";

    // ---------------------------------------------------------------- 마우스 호버

    private void HoverTick()
    {
        // 팝업(고정 해제)은 숨겨진 동안에도 감시해야 한다 — 열린 뒤 커서 위치로 올라갈지 정한다.
        if (_popupOpen && CursorProbe.TryGet(out Native.POINT popupPoint) &&
            Native.GetWindowRect(_hwnd, out Native.RECT popupRect))
        {
            bool inside = popupPoint.X >= popupRect.Left && popupPoint.X < popupRect.Right &&
                          popupPoint.Y >= popupRect.Top && popupPoint.Y < popupRect.Bottom;
            PopupWatchTick(inside);
        }

        if (!_visible || _cells.Count == 0)
        {
            return;
        }

        if (!CursorProbe.TryGet(out Native.POINT point) || !Native.GetWindowRect(_hwnd, out Native.RECT rect))
        {
            return;
        }

        double scale = CurrentScale();
        double localX = (point.X - rect.Left) / scale;
        double localY = (point.Y - rect.Top) / scale;

        int index = HitTestCell(localX, localY);

        // 같은 셀 위에 있어도 **문구가 바뀌면** 다시 남긴다 —
        // 값이 늦게 도착했을 때 "값 없음"만 로그에 남고 실제로 보인 문구가 안 남던 구멍을 막는다.
        string text = index < 0 ? string.Empty : _cells[index].HoverText;
        if (index == _hoveredIndex && string.Equals(text, _hoveredText, StringComparison.Ordinal))
        {
            return;
        }

        _hoveredIndex = index;
        _hoveredText = text;
        ApplyHoverVisuals();

        Log.Write(index < 0
            ? "hover: 없음"
            : $"hover: {_providers[index].Name} → {_cells[index].HoverText}");
    }

    /// <summary>창 좌표계(논리 픽셀)의 점이 어느 셀 위에 있는지 찾는다.</summary>
    private int HitTestCell(double localX, double localY)
    {
        for (int i = 0; i < _cells.Count; i++)
        {
            ProviderCell root = _cells[i].Root;

            if (root.ActualWidth <= 0 || root.ActualHeight <= 0)
            {
                continue;
            }

            // 셀 경계에 딱 붙어 움찔거리지 않도록 조금 넓힌다.
            Rect bounds = root.TransformToVisual(RootHost)
                .TransformBounds(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bounds.X -= HoverMarginDip;
            bounds.Y -= HoverMarginDip;
            bounds.Width += HoverMarginDip * 2;
            bounds.Height += HoverMarginDip * 2;

            if (localX >= bounds.X && localX <= bounds.X + bounds.Width &&
                localY >= bounds.Y && localY <= bounds.Y + bounds.Height)
            {
                return i;
            }
        }

        return -1;
    }

    // ---------------------------------------------------------------- 항상 위

    /// <summary>항상 위 고정을 켜고 끈다.</summary>
    public void SetAlwaysOnTop(bool value, bool save = true)
    {
        _settings.AlwaysOnTop = value;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = value;
        }

        Native.ApplyExStyle(_hwnd, value ? Native.WS_EX_TOPMOST : 0, value ? 0 : Native.WS_EX_TOPMOST);

        if (value)
        {
            // Z 순서를 실제로 최상단으로 올린다(위치/크기는 건드리지 않는다).
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE);
        }

        long ex = (long)Native.GetWindowLongPtrW(_hwnd, Native.GWL_EXSTYLE);
        bool topmost = (ex & Native.WS_EX_TOPMOST) != 0;
        Log.Write($"alwaysOnTop={value} presenter={(AppWindow.Presenter as OverlappedPresenter)?.IsAlwaysOnTop} " +
                  $"exstyle_topmost={topmost}");

        if (save)
        {
            _settings.Save();
        }

        AlwaysOnTopChanged?.Invoke(this, value);
    }

    // ---------------------------------------------------------------- 테마

    /// <summary>라이트/다크 테마를 적용한다(모드가 Auto 면 Windows 설정을 따른다).</summary>
    public void ApplyTheme(ThemeMode mode)
    {
        _settings.Theme = mode;
        bool light = SystemTheme.ResolveIsLight(mode);
        _isLight = light;

        RootHost.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
        ConfigureBackdrop();

        // Leave XAML ThemeResource bindings intact, including Windows contrast themes.
        // Acrylic backdrop theme follows the root's RequestedTheme.
        int dark = light ? 0 : 1;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        Log.Write($"theme: mode={mode} 적용={(light ? "Light" : "Dark")} (system={(SystemTheme.IsLight() ? "Light" : "Dark")})");
        Refresh();
    }

    // ---------------------------------------------------------------- 표시/숨김/배치

    /// <summary>포커스를 가져오지 않고 표시한다(Activate() 대신 ShowWindow).</summary>
    public void ShowNoActivate()
    {
        IntPtr before = Native.GetForegroundWindow();
        Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        Native.SetWindowPos(
            _hwnd,
            _settings.AlwaysOnTop ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE);

        _visible = true;
        IntPtr after = Native.GetForegroundWindow();
        Log.Write($"widget show: 포커스 유지={before == after} (0x{before:X} → 0x{after:X})");
    }

    /// <summary>위젯을 숨긴다(종료가 아니다).</summary>
    public void HideWidget()
    {
        _popupTimer?.Stop();
        _popupOpen = false;
        _popupAnimating = false;
        _popupRetracting = false;
        Native.ShowWindow(_hwnd, Native.SW_HIDE);
        _visible = false;
        _hoveredIndex = -1;
        _hoveredText = string.Empty;
        ApplyHoverVisuals();
        Log.Write("widget hide");
    }

    // ---------------------------------------------------------------- 팝업(흔들기로 내려옴)

    /// <summary>팝업 한 번 내려오는 시간.</summary>
    private const int PopupSlideMs = 220;

    /// <summary>팝업 전체 높이 + 여유만큼 위로 밀어 화면 밖에 둔다.</summary>
    private const int PopupOffscreenDip = 12;

    /// <summary>내려온 뒤 커서가 들어오지 않으면 자동으로 올라가기까지의 시간.</summary>
    private const int PopupEnterGraceMs = 4000;

    /// <summary>커서가 창을 벗어난 뒤 올라가기까지의 유예 시간(살짝 스쳐도 닫히지 않게).</summary>
    private const int PopupLeaveGraceMs = 350;

    private DispatcherQueueTimer? _popupTimer;
    private DateTimeOffset _popupStartedAt;
    private DateTimeOffset _popupOutsideSince = DateTimeOffset.MinValue;
    private bool _popupEntered;
    private bool _popupOpen;
    private bool _popupAnimating;
    private int _popupRestY;
    private int _popupHiddenY;
    private int _popupFromY;
    private int _popupToY;
    private DateTimeOffset _popupAnimStart;
    private bool _popupRetracting;

    /// <summary>팝업이 펼쳐진(또는 펼치는 중인) 상태인가.</summary>
    public bool IsPopupOpen => _popupOpen;

    /// <summary>
    /// 고정이 아닐 때 상단 흔들기로 호출된다. 고정이면 아무것도 하지 않는다(제스처 무시).
    /// 이미 열려 있으면 다시 열지 않는다.
    /// </summary>
    internal void TryShowPopup(MonitorInfo? monitor)
    {
        if (_settings.Pinned)
        {
            return;
        }

        if (_popupOpen || _popupAnimating)
        {
            return;
        }

        PlaceOnMonitor(monitor ?? ScreenInfo.FromCursor() ?? ScreenInfo.Primary());

        int height = AppWindow.Size.Height;
        Native.GetWindowRect(_hwnd, out Native.RECT placed);
        _popupRestY = placed.Top;
        _popupHiddenY = _popupRestY - height - (int)Math.Round(PopupOffscreenDip * CurrentScale());

        RectInt32 current = new(
            (int)AppWindow.Position.X,
            _popupHiddenY,
            (int)AppWindow.Size.Width,
            (int)AppWindow.Size.Height);
        AppWindow.MoveAndResize(current);

        Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE);
        _visible = true;

        _popupOpen = true;
        _popupEntered = false;
        _popupOutsideSince = DateTimeOffset.MinValue;
        _popupStartedAt = DateTimeOffset.Now;
        StartPopupAnimation(_popupHiddenY, _popupRestY, retracting: false);

        Log.Write($"popup: 흔들기 → 내려옴 (y {_popupHiddenY} → {_popupRestY})");
    }

    /// <summary>팝업을 위로 올려 숨긴다. 애니메이션이 끝나면 실제로 숨긴다.</summary>
    public void RetractPopup(string reason)
    {
        if (!_popupOpen || _popupRetracting)
        {
            return;
        }

        _popupOpen = false;
        _popupRetracting = true;
        _hoveredIndex = -1;
        _hoveredText = string.Empty;
        ApplyHoverVisuals();

        int y = AppWindow.Position.Y;
        StartPopupAnimation(y, _popupHiddenY, retracting: true);
        Log.Write($"popup: 올라감 ({reason})");
    }

    private void StartPopupAnimation(int from, int to, bool retracting)
    {
        _popupFromY = from;
        _popupToY = to;
        _popupAnimStart = DateTimeOffset.Now;
        _popupAnimating = true;
        _popupRetracting = retracting;

        _popupTimer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
        _popupTimer.Interval = TimeSpan.FromMilliseconds(16);
        _popupTimer.IsRepeating = true;
        _popupTimer.Tick -= OnPopupTick;
        _popupTimer.Tick += OnPopupTick;
        _popupTimer.Start();
    }

    private void OnPopupTick(DispatcherQueueTimer sender, object args)
    {
        double t = Math.Clamp((DateTimeOffset.Now - _popupAnimStart).TotalMilliseconds / PopupSlideMs, 0, 1);

        // 빠르게 시작해 부드럽게 멈추는 곡선(ease-out cubic) — 네이티브 팝업의 느낌.
        double eased = 1 - Math.Pow(1 - t, 3);
        int y = (int)Math.Round(_popupFromY + ((_popupToY - _popupFromY) * eased));
        AppWindow.Move(new PointInt32((int)AppWindow.Position.X, y));

        if (t < 1)
        {
            return;
        }

        sender.Stop();
        _popupAnimating = false;

        if (_popupRetracting)
        {
            _popupRetracting = false;
            Native.ShowWindow(_hwnd, Native.SW_HIDE);
            _visible = false;
            Log.Write("popup: 숨김 완료");
        }
        else
        {
            Log.Write("popup: 펼침 완료");
        }
    }

    /// <summary>
    /// 펼쳐진 동안 커서 위치를 보고 올라갈지 정한다. HoverTick 과 같은 주기로 호출된다.
    /// · 커서가 한 번도 안 들어오면 <see cref="PopupEnterGraceMs"/> 뒤 올라간다.
    /// · 들어왔다가 나가면 <see cref="PopupLeaveGraceMs"/> 뒤 올라간다.
    /// </summary>
    private void PopupWatchTick(bool inside)
    {
        if (!_popupOpen || _popupAnimating || _settingsMenuOpen)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;

        if (inside)
        {
            _popupEntered = true;
            _popupOutsideSince = DateTimeOffset.MinValue;
            return;
        }

        if (!_popupEntered)
        {
            if (now - _popupStartedAt >= TimeSpan.FromMilliseconds(PopupEnterGraceMs))
            {
                RetractPopup("진입 없음");
            }

            return;
        }

        if (_popupOutsideSince == DateTimeOffset.MinValue)
        {
            _popupOutsideSince = now;
            return;
        }

        if (now - _popupOutsideSince >= TimeSpan.FromMilliseconds(PopupLeaveGraceMs))
        {
            RetractPopup("커서 이탈");
        }
    }

    /// <summary>보이면 숨기고, 숨겨져 있으면 보여준다(고정 상태에서의 수동 토글 용도).</summary>
    public void ToggleVisibility()
    {
        if (_visible)
        {
            HideWidget();
        }
        else
        {
            PlaceOnMonitor(ScreenInfo.FromCursor() ?? ScreenInfo.Primary());
            ShowNoActivate();
        }
    }

    /// <summary>커서가 있는 모니터 위쪽 중앙에 배치한다(폭은 프로바이더 수에 맞춘다).</summary>
    internal void PlaceOnMonitor(MonitorInfo? monitor)
    {
        if (monitor is null)
        {
            return;
        }

        _monitor = monitor;
        double scale = monitor.Scale;
        RootHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int width = (int)Math.Ceiling(Math.Max(WindowWidthDip(), RootHost.DesiredSize.Width) * scale);
        int height = (int)Math.Ceiling(Math.Max(WidgetHeightDip, RootHost.DesiredSize.Height) * scale);
        int y = monitor.Work.Top + (int)Math.Round(TopGapDip * scale);

        // Move to the monitor before resizing its client area. The invisible Win32 frame
        // must not consume the XAML content height (it used to clip the battery bottom).
        AppWindow.Move(new PointInt32(monitor.CenterX - width / 2, y));
        AppWindow.ResizeClient(new SizeInt32(width, height));
        int x = monitor.CenterX - AppWindow.Size.Width / 2;
        AppWindow.Move(new PointInt32(x, y));
        Native.GetWindowRect(_hwnd, out Native.RECT rect);
        Log.Write($"widget place: 요청=({x},{y}) {width}x{height} 실제=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) " +
                  $"{ScreenInfo.Describe(monitor)}");
    }

    private double CurrentScale() => _monitor?.Scale ?? 1.0;
}
