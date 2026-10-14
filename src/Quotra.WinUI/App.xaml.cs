using System.IO;
using System.Reflection;
using Microsoft.Win32;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Quotra.Forecast;
using Quotra.Providers;
using Quotra.WinUI.Diagnostics;
using Quotra.WinUI.Interop;
using Quotra.WinUI.Models;
using Quotra.WinUI.Services;
using Quotra.WinUI.Themes;
using Quotra.WinUI.Views;
using Windows.UI;

namespace Quotra.WinUI;

/// <summary>
/// 앱 수명주기.
///
/// 카탈로그·소스(데이터) → 위젯 창 → 표본 수집/예측 → 시스템 메시지/핫키 → 엣지 흔들기 → 테마 감시 순으로 묶는다.
/// 창을 여러 개 만들지 않고 하나의 위젯 창을 재사용한다(설정·테마 상태가 한 곳에 모이도록).
/// </summary>
public partial class App : Application
{
    private WidgetWindow? _widget;
    private ProviderSettingsWindow? _providerSettings;
    private SystemMessageSink? _systemMessages;
    private EdgeWatcher? _watcher;
    private UsageService? _usage;
    private WidgetSettings _settings = new();
    private UsageHistory? _history;
    private UsageSampler? _sampler;
    private ForecastOptions _forecastOptions = new();
    private string? _historyPath;
    private TimeSpan _sampleInterval = UsageSampler.DefaultInterval;
    private string? _catalogPath;
    private readonly Dictionary<string, (string? Url, string? File)> _diagnosticOverrides = new(StringComparer.OrdinalIgnoreCase);
    private double _simulatePercentPerHour;
    private TimeSpan _refreshInterval = TimeSpan.FromSeconds(60);
    // Keep diagnostic timers alive across GC during longer preview runs.
    private DispatcherQueueTimer? _exitTimer;
    private DispatcherQueueTimer? _captureTimer;

    public App()
    {
        Log.Write("app: ctor 진입 (InitializeComponent 전)");

        // XAML 파싱 예외는 스토우드 예외(0xc000027b)로 죽어서 메시지가 사라진다.
        // 원인을 남기기 위해 InitializeComponent 를 감싼다(로그는 이미 쓸 수 있는 상태다).
        UnhandledException += (_, e) =>
        {
            Log.Write(e.Exception, "UNHANDLED(XAML)");
            e.Handled = true;
        };

        try
        {
            InitializeComponent();
            Log.Write("app: InitializeComponent 완료 (App.xaml 리소스 병합 성공)");
        }
        catch (Exception ex)
        {
            Log.Write(ex, "App.xaml 초기화 실패");
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Reset();
        Log.Write("=== Quotra WinUI 3 start ===");
        Log.Write($"환경: OS={Environment.OSVersion.VersionString} .NET={Environment.Version} " +
                  $"아크릴지원={Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported()} " +
                  $"테마={(SystemTheme.IsLight() ? "Light" : "Dark")}");

        _settings = WidgetSettings.Load();
        Log.Write($"설정: alwaysOnTop={_settings.AlwaysOnTop} theme={_settings.Theme} acrylic={_settings.Acrylic}");

        ApplyCommandLineOverrides();
        SetStartupRegistration(_settings.RunAtStartup, saveSetting: false);

        _usage = new UsageService(_catalogPath);
        _usage.Load();
        if (_usage.LoadWarning is { } warning)
        {
            Log.Write($"데이터 경고: {warning}");
        }

        Log.Write($"카탈로그: {_usage.CatalogPath} → 프로바이더 {_usage.Providers.Count}곳");

        // 실연동: [UsageSource] 표시가 붙은 소스를 스캔해 카탈로그에 있는 id 만 등록한다.
        // 새 프로바이더는 카탈로그 항목 + 소스 클래스(어트리뷰트)만 추가하면 된다 — 이곳은 손대지 않는다.
        RegisterAnnotatedSources(_usage);

        // 값은 IUsageSource 구현체만 공급한다. 등록이 없으면 화면은 "미연동"으로 남는다.
        if (_simulatePercentPerHour > 0 && _usage.Providers.Count > 0)
        {
            double start = 0.08;
            _usage.RegisterAll(provider =>
            {
                var source = new SimulatedUsageSource(
                    provider.Name,
                    startUsed: start,
                    primaryPerHour: _simulatePercentPerHour / 100.0,
                    secondaryPerHour: _simulatePercentPerHour / 100.0 * 1.5);
                start += 0.14;
                return source;
            });

            Log.Write($"개발용 소스 등록: {_usage.Sources.Count}곳 · 시간당 {_simulatePercentPerHour:0.##}% (난수 없음)");
        }
        else if (_usage.Sources.Count == 0)
        {
            Log.Write("연동된 소스 없음 — 모든 프로바이더가 미연동으로 표시됩니다");
        }

        // 소모 이력(10분 간격 표본) — 없으면 예측이 "측정 중"으로만 남는다.
        string historyPath = _historyPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Quotra",
            "usage-history.json");

        _history = UsageHistory.Load(historyPath, out string? historyWarning);
        if (historyWarning is { } loadIssue)
        {
            Log.Write($"이력 경고: {loadIssue}");
        }

        _sampler = new UsageSampler(_history, historyPath, _sampleInterval);
        Log.Write($"이력: {historyPath} (기존 표본 {_history.TotalSamples}개, 간격 {_sampleInterval.TotalMinutes:0.##}분)");

        _widget = new WidgetWindow(_usage.Providers, _settings, _history, _forecastOptions, _sampleInterval);
        _widget.Refresh();
        if (_settings.Pinned)
        {
            _widget.ShowNoActivate();
        }
        else
        {
            // 고정 해제 상태로 시작하면 위젯은 숨겨 두고, 상단 흔들기로만 내려온다.
            _widget.HideWidget();
        }

        _widget.CommandInvoked += (_, command) => OnWidgetCommand(command);
        _widget.Closed += (_, _) => _systemMessages?.Dispose();

        // 표본 기록 → 화면 갱신 + 로그 한 줄(10분마다 한 줄이면 검증에 충분하다)
        _sampler.Sampled += (_, _) => OnSampled();
        _sampler.Start(DispatcherQueue.GetForCurrentThread(), _usage.Providers);

        // 소스 갱신 → 게이지 갱신(연동이 없으면 갱신할 값도 없다)
        _usage.Updated += (_, _) =>
        {
            _widget.Refresh();

        };
        _usage.StartRefresh(DispatcherQueue.GetForCurrentThread(), _refreshInterval);

        // 알림 영역 메뉴, 전역 핫키, 시스템 메시지를 한 숨은 창에서 받는다.
        _systemMessages = new SystemMessageSink();
        _systemMessages.RunAtStartupEnabled = _settings.RunAtStartup;
        _systemMessages.ToggleRequested += (_, _) =>
        {
            if (_settings.Pinned) _widget.ToggleVisibility();
            else _widget.TryShowPopup(ScreenInfo.FromCursor());
        };
        _systemMessages.SystemThemeChanged += (_, _) => OnSystemThemeChanged();
        _systemMessages.DisplayChanged += (_, _) => _widget.PlaceOnMonitor(ScreenInfo.Primary());
        _systemMessages.TrayCommandRequested += (_, command) => OnTrayCommand(command);
        _systemMessages.Start(Path.Combine(AppContext.BaseDirectory, "Assets", "Quotra.ico"));

        // 상단 엣지 흔들기 → 위젯 표시 전환(창을 새로 만들지 않는다)
        _watcher = new EdgeWatcher { EdgeBand = 6, ShakeCount = 2, ShakeDistance = 40, ShakeWindowMs = 1000 };
        _watcher.Triggered += (_, e) =>
        {
            // 고정 상태면 TryShowPopup 이 아무것도 하지 않는다(제스처는 고정 해제일 때만 동작).
            Log.Write($"edge trigger: ({e.X},{e.Y}) — {(_settings.Pinned ? "고정 중이라 무시" : "팝업 내려옴")}");
            _widget.TryShowPopup(e.Monitor);
        };
        _watcher.Start();

        if (_watcher.StartWarning is { } startWarning)
        {
            Log.Write($"edge watcher 경고: {startWarning}");
        }

        // Explicit visual QA export only. Normal launches do not capture or write images.
        string? captureArg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase));
        if (captureArg is not null)
        {
            var captureTimer = _captureTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            captureTimer.Interval = TimeSpan.FromSeconds(3);
            captureTimer.IsRepeating = false;
            captureTimer.Tick += async (_, _) =>
            {
                try
                {
                    if (Environment.GetCommandLineArgs().Contains("--settings-menu-preview"))
                        _widget.ShowSettingsMenu();
                    if (Environment.GetCommandLineArgs().Contains("--settings-window-preview"))
                        OpenProviderSettings();
                    await WidgetCapture.SaveAsync(_widget, captureArg["--capture=".Length..]);
                }
                catch (Exception ex)
                {
                    Log.Write(ex, "capture failed");
                    Environment.Exit(1);
                }
            };
            captureTimer.Start();
        }
    }

    /// <summary>표본을 남긴 뒤: 화면 갱신 + 소모/고갈 로그.</summary>
    private void OnSampled()
    {
        _widget?.Refresh();

        if (_sampler is null || _history is null || _usage is null)
        {
            return;
        }

        if (_sampler.SaveWarning is { } saveError)
        {
            Log.Write($"이력 저장 실패: {saveError}");
        }

        DateTimeOffset now = DateTimeOffset.Now;

        // 값이 있는 프로바이더만 예측할 수 있다(없으면 상태만 로그에 남긴다).
        List<ProviderUsage> known = [.. _usage.Providers.Where(p => p.Primary is not null)];
        List<string> depleting = [];

        foreach (ProviderUsage provider in known)
        {
            ForecastResult forecast = DepletionForecaster.Compute(
                _history.Points(provider.PrimaryHistoryKey, UsageWindow.Primary), provider.Primary!.Value, now, provider.PrimaryResetsAt, _forecastOptions);

            if (forecast.Status == ForecastStatus.Depleting)
            {
                depleting.Add($"{provider.Name} {ForecastText.Short(forecast, now)}");
            }
        }

        string state = known.Count == 0
            ? "연동된 값 없음(미연동)"
            : $"값 있는 곳 {known.Count}/{_usage.Providers.Count}곳";

        if (known.Count == 0)
        {
            Log.Write($"sample: 표본 {_history.TotalSamples}개 · {state}");
            return;
        }

        // 대표(가장 여유 없는 곳)의 상태는 항상 남긴다 — "왜 예측이 없지"를 로그만 보고 알 수 있게.
        ProviderUsage worst = known.MinBy(p => p.Remaining)!;
        ForecastResult worstForecast = DepletionForecaster.Compute(
            _history.Points(worst.PrimaryHistoryKey, UsageWindow.Primary), worst.Primary!.Value, now, worst.PrimaryResetsAt, _forecastOptions);

        Log.Write($"sample: 표본 {_history.TotalSamples}개 · {_usage.Providers.Count}곳 기록 · {state} · " +
                  $"대표 {worst.Name}: {ForecastText.Detail(worstForecast, now)}" +
                  (depleting.Count > 0 ? $" · 고갈 예상 {depleting.Count}곳: {string.Join(", ", depleting)}" : " · 고갈 예상 없음"));
    }

    /// <summary>
    /// 검증용 명령줄 스위치(사람이 쓰는 옵션이 아니라 자동 테스트용이다).
    ///   --simpath=x,y@ms;...  커서를 스크립트대로 움직인다(실제 마우스를 건드리지 않는다)
    ///   --theme=auto|light|dark
    ///   --ontop=true|false     항상 위 고정을 강제로 켜고 끈다(고정 해제 경로 검증용)
    ///   --pinned=true|false    저장된 설정을 바꾸지 않고 고정/팝업 시작 상태를 검증한다
    ///   --catalog=path         프로바이더 정의 파일(기본 Data\providers.json)
    ///   --simulate=percent     개발용 소스를 붙인다(시간당 %, 난수 없음) — 예측 검증용
    ///   --refresh=ms           소스 갱신 주기(기본 60000)
    ///   --sample-interval=ms   소모 표본 간격(기본 10분)
    ///   --sample-span=minutes  예측에 쓰는 관측 최소 폭(기본 10분)
    ///   --history=path         이력 파일 위치(기본 %LOCALAPPDATA%\Quotra\usage-history.json)
    ///   --forecast-test        예측 엔진 자체 점검만 하고 종료
    ///   --provider-test=id     프로바이더 소스 자체 점검만 하고 종료(실패 시 종료 코드 1)
    ///   --provider-once=id     프로바이더 1회 조회 후 결과만 로그로 남기고 종료(값이 없으면 종료 코드 1)
    ///   --provider-url=id=url  프로바이더 조회 주소 강제(실패 경로 검증용)
    ///   --provider-file=id=path 프로바이더 자격증명 파일 강제
    ///   --codex-parse=path     저장해 둔 응답 파일로 파서만 검증
    ///   (이전 이름: --codex-test · --codex-once · --codex-host · --codex-auth · --opencode-test · --opencode-once)
    ///   --exit-after=ms        지정 시간 뒤 스스로 종료(스크린샷 자동화용)
    ///   --capture=path         3초 뒤 실제 XAML 화면을 PNG로 저장(Windows 프레임 제외)
    /// </summary>
    private void ApplyCommandLineOverrides()
    {
        string[] args = Environment.GetCommandLineArgs();

        // 진단용 재정의 — --provider-url=<id>=<주소> · --provider-file=<id>=<경로>
        ParseDiagnosticOverride(args, "--provider-url=", url: true);
        ParseDiagnosticOverride(args, "--provider-file=", url: false);

        // 이전에 쓰던 이름 — codex 전용 별칭으로 계속 받는다.
        ParseDiagnosticOverride(args, "--codex-host=", url: true, providerId: "codex");
        ParseDiagnosticOverride(args, "--codex-auth=", url: false, providerId: "codex");

        // 일반 진단 스위치 — 카탈로그 항목 + 소스 클래스만 추가하면 프로바이더별 스위치가 자동으로 생긴다.
        if (Value(args, "--provider-once=") is { } providerOnce)
        {
            RunProviderProbe(providerOnce);
        }

        if (Value(args, "--provider-test=") is { } providerTest)
        {
            Environment.Exit(RunProviderSelfTest(providerTest) ? 0 : 1);
        }

        // 이전에 쓰던 이름 — 같은 동작으로 계속 받는다.
        if (args.Contains("--opencode-once"))
        {
            RunProviderProbe("opencode-go");
        }

        if (args.Contains("--opencode-test"))
        {
            Environment.Exit(RunProviderSelfTest("opencode-go") ? 0 : 1);
        }

        string? simPath = Value(args, "--simpath=");
        if (simPath is not null)
        {
            CursorProbe.LoadPath(simPath);
            Log.Write($"simpath={simPath} (커서 시뮬레이션)");
        }

        string? theme = Value(args, "--theme=");
        if (theme is not null && Enum.TryParse(theme, ignoreCase: true, out ThemeMode mode))
        {
            _settings.Theme = mode;
            Log.Write($"theme 강제: {mode}");
        }

        string? onTop = Value(args, "--ontop=");
        if (onTop is not null && bool.TryParse(onTop, out bool pinned))
        {
            _settings.AlwaysOnTop = pinned;
            Log.Write($"alwaysOnTop 강제: {pinned}");
        }

        string? pinnedOverride = Value(args, "--pinned=");
        if (pinnedOverride is not null && bool.TryParse(pinnedOverride, out bool startPinned))
        {
            _settings.Pinned = startPinned;
            Log.Write($"pinned 강제: {startPinned}");
        }

        string? interval = Value(args, "--sample-interval=");
        if (interval is not null && int.TryParse(interval, out int intervalMs) && intervalMs >= 200)
        {
            _sampleInterval = TimeSpan.FromMilliseconds(intervalMs);
            Log.Write($"표본 간격 강제: {_sampleInterval.TotalMinutes:0.##}분");
        }

        string? span = Value(args, "--sample-span=");
        if (span is not null && double.TryParse(span, out double spanMinutes) && spanMinutes > 0)
        {
            _forecastOptions = _forecastOptions with { MinSpan = TimeSpan.FromMinutes(spanMinutes) };
            Log.Write($"예측 최소 관측 폭 강제: {spanMinutes:0.##}분");
        }

        string? consume = Value(args, "--simulate=");
        if (consume is not null && double.TryParse(consume, out double simulatePerHour) && simulatePerHour > 0)
        {
            _simulatePercentPerHour = simulatePerHour;
            Log.Write($"개발용 소스: 시간당 {simulatePerHour:0.##}% (난수 없음)");
        }

        string? catalog = Value(args, "--catalog=");
        if (catalog is not null)
        {
            _catalogPath = Path.GetFullPath(catalog, AppContext.BaseDirectory);
            Log.Write($"카탈로그 강제: {_catalogPath}");
        }

        string? refresh = Value(args, "--refresh=");
        if (refresh is not null && int.TryParse(refresh, out int refreshMs) && refreshMs >= 200)
        {
            _refreshInterval = TimeSpan.FromMilliseconds(refreshMs);
            Log.Write($"갱신 주기 강제: {_refreshInterval.TotalSeconds:0.##}초");
        }

        _historyPath = Value(args, "--history=");

        string? forecastTest = Value(args, "--forecast-test");
        if (forecastTest is not null)
        {
            // 창을 만들 필요가 없으므로 여기서 끝낸다(종료 코드로 성패를 알린다).
            bool allPassed = ForecastSelfTest.Print(ForecastSelfTest.Run(), Log.Write);
            Environment.Exit(allPassed ? 0 : 1);
        }

        // 이전에 쓰던 이름 — 일반 경로로 위임한다.
        if (Value(args, "--codex-test") is not null)
        {
            Environment.Exit(RunProviderSelfTest("codex") ? 0 : 1);
        }

        // 저장해 둔 응답 파일로 파서만 검증한다(네트워크 없음).
        string? codexParse = Value(args, "--codex-parse=");
        if (codexParse is not null)
        {
            RunCodexParse(Path.GetFullPath(codexParse, AppContext.BaseDirectory));
            Environment.Exit(0);
        }

        // 이전에 쓰던 이름 — 일반 경로로 위임한다.
        if (Value(args, "--codex-once") is not null)
        {
            RunProviderProbe("codex");
        }

        string? exitAfter = Value(args, "--exit-after=");
        if (exitAfter is not null && int.TryParse(exitAfter, out int ms) && ms > 0)
        {
            var timer = _exitTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(ms);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                Log.Write($"exit-after {ms}ms — 종료");
                Exit();
            };
            timer.Start();
        }

        static string? Value(string[] args, string prefix)
        {
            string? match = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return match is null ? null : match[prefix.Length..];
        }
    }

    /// <summary>
    /// <c>[UsageSource]</c> 표시가 붙은 소스를 스캔해 카탈로그에 있는 id 만 등록한다.
    /// 새 프로바이더는 카탈로그 항목 + 소스 클래스(어트리뷰트)만 추가하면 된다.
    /// </summary>
    private void RegisterAnnotatedSources(UsageService usage)
    {
        foreach ((UsageSourceAttribute attribute, Type type) in AnnotatedSources())
        {
            if (!usage.Providers.Any(p => string.Equals(p.Id, attribute.ProviderId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // 소스 하나의 실수가 앱 시작을 막지 않게 여기서 격리한다 — 그 프로바이더만
            // 미연동으로 남고 나머지는 정상 동작한다(사유는 로그에 남는다).
            try
            {
                IUsageSource source = CreateSource(type);
                ApplyDiagnostics(attribute.ProviderId, source);
                usage.Register(attribute.ProviderId, source);
            }
            catch (Exception ex)
            {
                Log.Write(ex, $"소스 등록 실패({attribute.ProviderId}) — 이 프로바이더는 미연동으로 남깁니다");
            }
        }
    }

    /// <summary>어트리뷰트가 붙은 소스 타입과 그 프로바이더 id.</summary>
    private static IEnumerable<(UsageSourceAttribute Attribute, Type Type)> AnnotatedSources() =>
        typeof(App).Assembly.GetTypes()
            .Select(type => (Type: type, Attribute: type.GetCustomAttribute<UsageSourceAttribute>()))
            .Where(pair => pair.Attribute is not null)
            .Select(pair => (pair.Attribute!, pair.Type));

    /// <summary>
    /// 소스 인스턴스를 만든다. 기본값을 가진 생성자(선택적 매개변수만)를 그대로 사용하므로
    /// 소스는 생성자 주입 없이 스스로 기본값을 정한다. 진단 재정의는 생성 뒤에 적용한다.
    /// </summary>
    private static IUsageSource CreateSource(Type type)
    {
        ConstructorInfo? constructor = type.GetConstructors()
            .OrderBy(c => c.GetParameters().Length)
            .FirstOrDefault(c => c.GetParameters().All(p => p.HasDefaultValue));

        if (constructor is null)
        {
            throw new InvalidOperationException($"{type.Name}: 자동 등록에는 기본값만 가진 생성자가 필요합니다.");
        }

        object?[] arguments = [.. constructor.GetParameters().Select(_ => (object?)Type.Missing)];
        return constructor.Invoke(arguments) as IUsageSource
            ?? throw new InvalidOperationException($"{type.Name}: IUsageSource 를 구현하지 않습니다.");
    }

    /// <summary>진단 재정의를 소스가 받을 수 있으면 전달한다.</summary>
    private void ApplyDiagnostics(string providerId, IUsageSource source)
    {
        if (source is IUsageDiagnostics diagnostics && _diagnosticOverrides.TryGetValue(providerId, out (string? Url, string? File) overrides))
        {
            diagnostics.ApplyDiagnostics(overrides.Url, overrides.File);
            Log.Write($"진단 재정의({providerId}): url={overrides.Url ?? "기본"} · file={overrides.File ?? "기본"}");
        }
    }

    /// <summary>진단 스위치용 소스 인스턴스(id 로 어트리뷰트를 찾는다).</summary>
    private IUsageSource CreateDiagnosticSource(string providerId)
    {
        foreach ((UsageSourceAttribute attribute, Type type) in AnnotatedSources())
        {
            if (!string.Equals(attribute.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IUsageSource source = CreateSource(type);
            ApplyDiagnostics(providerId, source);
            return source;
        }

        throw new InvalidOperationException($"'{providerId}' 프로바이더 소스를 찾을 수 없습니다.");
    }

    /// <summary><c>--provider-once=&lt;id&gt;</c> — 그 프로바이더만 1회 조회하고 결과를 로그로 남긴다.</summary>
    private void RunProviderProbe(string providerId)
    {
        try
        {
            IUsageSource source = CreateDiagnosticSource(providerId);
            ProviderSnapshot snapshot = Task.Run(() => source.FetchAsync(CancellationToken.None)).GetAwaiter().GetResult();
            Log.Write($"provider-once({providerId}): {Describe(snapshot)}");
            Environment.Exit(snapshot.Primary is null && snapshot.Secondary is null ? 1 : 0);
        }
        catch (Exception ex)
        {
            Log.Write($"provider-once({providerId}): 실패 — {ex.GetBaseException().Message}");
            Environment.Exit(1);
        }
    }

    /// <summary><c>--provider-test=&lt;id&gt;</c> — 자체 점검만 하고 통과 여부를 돌려준다(네트워크 없음).</summary>
    private bool RunProviderSelfTest(string providerId)
    {
        try
        {
            IUsageSource source = CreateDiagnosticSource(providerId);
            if (source is not IUsageSelfTest test)
            {
                Log.Write($"provider-test({providerId}): 이 소스는 자체 점검을 제공하지 않습니다.");
                return false;
            }

            bool passed = test.RunSelfTest();
            Log.Write($"provider-test({providerId}): {test.SelfTestName} " + (passed ? "통과" : "실패"));
            return passed;
        }
        catch (Exception ex)
        {
            Log.Write($"provider-test({providerId}): 실패 — {ex.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>조회 결과를 로그 한 줄로 — 값이 없으면 없는 대로 남긴다.</summary>
    private static string Describe(ProviderSnapshot snapshot)
    {
        static string Window(string name, WindowUsage? usage) => usage is { } value
            ? $"{name}={value.Used * 100:0.#}%" + (value.ResetsAt is { } resetsAt ? $" 리셋={resetsAt:O}" : string.Empty)
            : $"{name}=없음";

        string text = $"{Window("Primary", snapshot.Primary)} · {Window("Secondary", snapshot.Secondary)}";
        if (snapshot.PrimaryLabel is { } label) text += $" · 주 한도={label}";

        if (!string.IsNullOrWhiteSpace(snapshot.Plan))
        {
            text += $" · plan={snapshot.Plan}";
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Note))
        {
            text += $" · {snapshot.Note}";
        }

        return text;
    }

    /// <summary><c>--provider-url=&lt;id&gt;=&lt;주소&gt;</c> / <c>--provider-file=&lt;id&gt;=&lt;경로&gt;</c> 를 재정의 사전에 넣는다.</summary>
    private void ParseDiagnosticOverride(string[] args, string prefix, bool url, string? providerId = null)
    {
        foreach (string argument in args)
        {
            if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = argument[prefix.Length..];
            int separator = providerId is null ? value.IndexOf('=') : -1;
            string id = providerId ?? (separator > 0 ? value[..separator] : string.Empty);
            string payload = providerId is null ? (separator > 0 ? value[(separator + 1)..] : string.Empty) : value;

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(payload))
            {
                Log.Write($"진단 재정의 형식 오류: {argument}");
                continue;
            }

            (string? Url, string? File) current = _diagnosticOverrides.TryGetValue(id, out (string? Url, string? File) existing)
                ? existing
                : (null, null);
            _diagnosticOverrides[id] = url ? (payload, current.File) : (current.Url, payload);
            Log.Write($"진단 재정의({id}): {(url ? "url" : "file")}={payload}");
        }
    }

    /// <summary><c>--codex-parse=&lt;파일&gt;</c> — 저장해 둔 응답을 파서에만 통과시킨다(토큰·네트워크 불필요).</summary>
    private static void RunCodexParse(string path)
    {
        if (!File.Exists(path))
        {
            Log.Write($"codex-parse: 파일이 없습니다 — {path}");
            return;
        }

        CodexFetchResult result = CodexUsageClient.ParseRaw(File.ReadAllText(path), path);
        DescribeCodex(result);
    }

    /// <summary>Codex 조회 결과를 로그 한 덩어리로 남긴다(값이 없으면 사유까지).</summary>
    private static void DescribeCodex(CodexFetchResult result)
    {
        Log.Write($"codex: status={result.Status} endpoint={result.Endpoint}");

        if (!result.Ok)
        {
            Log.Write($"codex: 사유 — {result.Detail}");
            return;
        }

        CodexUsage usage = result.Usage!;
        Log.Write($"codex: windows=[{usage.WindowsSummary}] plan={usage.Plan} limit_reached={usage.LimitReached}");
        Log.Write($"codex: 주간 사용률={Percent(usage.WeeklyUsed)} 리셋={Delay(usage.WeeklyResetsIn)} · " +
                  $"세션 사용률={Percent(usage.SessionUsed)} 리셋={Delay(usage.SessionResetsIn)}");

        static string Percent(double? value) => value is { } v ? $"{v * 100:0.#}%" : "없음";
        static string Delay(TimeSpan? value) => value is { } v ? $"{(int)v.TotalHours}시간 {v.Minutes}분 뒤" : "없음";
    }

    /// <summary>프로바이더 설정 창을 하나만 열어 재사용한다(우클릭 메뉴·캡처 검증 공용).</summary>
    private void OpenProviderSettings()
    {
        if (_usage is null)
        {
            return;
        }

        if (_providerSettings is null)
        {
            _providerSettings = new ProviderSettingsWindow(_usage);
            _providerSettings.Closed += (_, _) => _providerSettings = null;
            Log.Write($"프로바이더 설정 창: 키 입력형 {_usage.KeyedSources.Count}곳 표시");
        }

        _providerSettings.Activate();
    }

    private void OnWidgetCommand(WidgetCommand command)
    {
        Log.Write($"widget command: {command}");

        switch (command)
        {
            case WidgetCommand.ProviderSettings:
                OpenProviderSettings();
                break;
            case WidgetCommand.TogglePinned:
                SetPinned(!_settings.Pinned);
                break;
            case WidgetCommand.ToggleRunAtStartup:
                SetStartupRegistration(!_settings.RunAtStartup, saveSetting: true);
                break;

            case WidgetCommand.ThemeAuto:
                ApplyTheme(ThemeMode.Auto);
                break;

            case WidgetCommand.ThemeLight:
                ApplyTheme(ThemeMode.Light);
                break;

            case WidgetCommand.ThemeDark:
                ApplyTheme(ThemeMode.Dark);
                break;

            case WidgetCommand.ToggleAlwaysOnTop:
                _widget?.SetAlwaysOnTop(!_settings.AlwaysOnTop);
                break;
            case WidgetCommand.ToggleAcrylic:
                _widget?.SetAcrylic(!_settings.Acrylic);
                break;
            case WidgetCommand.Refresh:
                if (_usage is not null) _ = _usage.RefreshAsync();
                break;
            case WidgetCommand.Hide:
                _widget?.HideWidget();
                break;
            case WidgetCommand.Exit:
                ExitApplication();
                break;
        }
    }

    private void OnTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.ToggleWidget:
                _widget?.ToggleVisibility();
                break;
            case TrayCommand.ProviderSettings:
                OpenProviderSettings();
                break;
            case TrayCommand.ToggleRunAtStartup:
                SetStartupRegistration(!_settings.RunAtStartup, saveSetting: true);
                break;
            case TrayCommand.Exit:
                ExitApplication();
                break;
        }
    }

    private void ExitApplication()
    {
        _systemMessages?.Dispose();
        _systemMessages = null;
        Exit();
    }

    private void SetStartupRegistration(bool enabled, bool saveSetting)
    {
        const string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "Quotra";
        bool previous = _settings.RunAtStartup;
        try
        {
            using RegistryKey? runKey = Registry.CurrentUser.CreateSubKey(registryPath, writable: true);
            if (runKey is null)
            {
                throw new InvalidOperationException("Windows 시작 프로그램 설정을 열 수 없습니다.");
            }

            if (enabled)
            {
                string executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("실행 파일 경로를 확인할 수 없습니다.");
                runKey.SetValue(valueName, $"\"{executable}\"");
            }
            else
            {
                runKey.DeleteValue(valueName, throwOnMissingValue: false);
            }

            _settings.RunAtStartup = enabled;
            if (saveSetting) _settings.Save();
            Log.Write($"Windows 시작 시 실행: {enabled}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.SecurityException)
        {
            _settings.RunAtStartup = previous;
            Log.Write(ex, "Windows 시작 프로그램 설정 실패");
        }
        finally
        {
            if (_systemMessages is not null)
            {
                _systemMessages.RunAtStartupEnabled = _settings.RunAtStartup;
            }
        }
    }

    /// <summary>
    /// 위젯 고정 토글. 고정이면 바로 보이고, 해제하면 현재 보이는 위젯을 올려 숨긴다
    /// (이후에는 상단 흔들기로만 내려온다).
    /// </summary>
    private void SetPinned(bool pinned)
    {
        if (_widget is null)
        {
            return;
        }

        _settings.Pinned = pinned;
        _settings.Save();

        if (pinned)
        {
            _widget.HideWidget();
            _widget.PlaceOnMonitor(ScreenInfo.FromCursor() ?? ScreenInfo.Primary());
            _widget.ShowNoActivate();
        }
        else
        {
            _widget.HideWidget();
        }

        Log.Write($"위젯 고정: {(pinned ? "켜짐" : "꺼짐 — 상단 흔들기로만 내려옴")}");
    }

    private void ApplyTheme(ThemeMode mode)
    {
        if (_widget is null)
        {
            return;
        }

        _widget.ApplyTheme(mode);
        _settings.Save();
    }

    /// <summary>Windows 테마가 바뀌면 Auto 모드일 때 즉시 따라간다.</summary>
    private void OnSystemThemeChanged()
    {
        bool light = SystemTheme.IsLight();
        Log.Write($"시스템 테마 변경 감지 → {(light ? "Light" : "Dark")} (mode={_settings.Theme})");

        if (_settings.Theme != ThemeMode.Auto)
        {
            // Contrast themes can change even when light/dark is explicitly selected.
            _widget?.Refresh();
            return;
        }

        _widget?.ApplyTheme(ThemeMode.Auto);
    }

}


