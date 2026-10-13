using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Quotra.WinUI.Services;
using Windows.Graphics;

namespace Quotra.WinUI.Views;

/// <summary>
/// 등록된 모든 프로바이더의 상태·오류와 연결 설정을 보여준다.
/// 입력한 키는 조회 성공을 확인한 뒤에만 저장하고, 저장 위치는 각 소스가 정한다(자격 증명 보관함).
/// </summary>
public sealed class ProviderSettingsWindow : Window
{
    private readonly UsageService _usage;
    private readonly ListView _providerList = new() { SelectionMode = ListViewSelectionMode.Single, MinWidth = 200 };
    private readonly TextBlock _title = new() { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _providerStatusSummary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly PasswordBox _key = new() { Header = "API 키", PasswordRevealMode = PasswordRevealMode.Peek };
    private readonly TextBlock _vaultNote = new() { Text = "키는 조회 성공을 확인한 뒤 Windows 자격 증명 보관함에 저장됩니다.", FontSize = 12 };
    private readonly Button _save = new() { Content = "연결 확인 및 저장" };
    private readonly Button _remove = new() { Content = "연결 해제" };
    private readonly Button _refresh = new() { Content = "새로고침" };
    private readonly Button _login = new() { Content = "Claude 로그인" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _detail = new() { Spacing = 10 };

    private bool _closed;
    private string? _selectedId;
    private ProviderLoginWindow? _loginWindow;
    private readonly List<(string Id, IUsageSource Source)> _listedSources = [];

    public ProviderSettingsWindow(UsageService usage)
    {
        _usage = usage;
        Title = "Quotra · 프로바이더 설정";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(800, 620));
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Quotra.ico"));

        _detail.Padding = new Thickness(24, 4, 4, 4);
        _detail.Children.Add(_title);
        _detail.Children.Add(_hint);
        _detail.Children.Add(new TextBlock
        {
            Text = "전체 서비스 상태",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        _detail.Children.Add(new ScrollViewer
        {
            Content = _providerStatusSummary,
            Height = 120,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        _detail.Children.Add(_key);
        _detail.Children.Add(_vaultNote);
        _detail.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { _save, _login, _remove, _refresh },
        });
        _detail.Children.Add(_status);

        var root = new Grid { Padding = new Thickness(20), ColumnSpacing = 20 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_providerList, 0);
        var detailScroll = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(detailScroll, 1);
        root.Children.Add(_providerList);
        root.Children.Add(detailScroll);
        Content = root;

        _providerList.SelectionChanged += (_, _) => SelectProvider();
        _save.Click += async (_, _) => await SaveAsync();
        _remove.Click += (_, _) => Remove();
        _refresh.Click += async (_, _) =>
        {
            _refresh.IsEnabled = false;
            try { await _usage.RefreshAsync(); }
            finally { if (!_closed) _refresh.IsEnabled = true; }
        };
        _login.Click += (_, _) => Login();
        _usage.Updated += OnUsageUpdated;
        Closed += (_, _) =>
        {
            _closed = true;
            _usage.Updated -= OnUsageUpdated;
            _loginWindow?.Close();
        };

        BuildProviderList();
        UpdateProviderStatusSummary();
    }

    private void OnUsageUpdated(object? sender, EventArgs e)
    {
        UpdateProviderStatusSummary();
        UpdateSelectedStatus();
    }

    private void UpdateProviderStatusSummary()
    {
        List<string> lines = [];
        if (_usage.LastRefreshAt is { } refreshed)
        {
            lines.Add($"마지막 조회 {refreshed.LocalDateTime:yyyy-MM-dd HH:mm}");
        }

        foreach (var provider in _usage.Providers)
        {
            string status = provider.Error is not null
                ? (provider.HasAnyValue ? "조회 실패 · 이전 성공 값 유지" : "연결 확인 필요")
                : provider.StatusText;
            string updated = provider.UpdatedAt is { } at
                ? $" · 성공 {at.LocalDateTime:HH:mm}"
                : string.Empty;
            lines.Add($"{provider.Name}: {status}{updated}");
            if (provider.Error is { } error) lines.Add($"  {error}");
        }

        if (_usage.Providers.Count == 0)
        {
            lines.Add("등록된 서비스가 없습니다.");
        }

        lines.Add("예측은 사용량 표본을 10분 이상 모은 뒤 표시됩니다.");
        _providerStatusSummary.Text = string.Join(Environment.NewLine, lines);
    }

    private void BuildProviderList()
    {
        _providerList.Items.Clear();
        _listedSources.Clear();

        foreach (var provider in _usage.Providers)
        {
            if (!_usage.Sources.TryGetValue(provider.Id, out IUsageSource? source)) continue;
            _listedSources.Add((provider.Id, source));
            _providerList.Items.Add(provider.Name);
        }

        if (_listedSources.Count == 0)
        {
            _title.Text = "등록된 프로바이더가 없습니다";
            _hint.Text = "서비스가 등록되면 여기에 나타납니다.";
            _key.Visibility = Visibility.Collapsed;
            _vaultNote.Visibility = Visibility.Collapsed;
            _save.Visibility = Visibility.Collapsed;
            _remove.Visibility = Visibility.Collapsed;
            _login.Visibility = Visibility.Collapsed;
            return;
        }

        _providerList.SelectedIndex = 0;
    }

    private (string Id, IUsageSource Source)? Selected => _selectedId is not null && _usage.Sources.TryGetValue(_selectedId, out IUsageSource? source)
        ? (_selectedId, source) : null;

    private void SelectProvider()
    {
        int index = _providerList.SelectedIndex;
        if (index < 0 || index >= _listedSources.Count)
        {
            return;
        }

        (string id, IUsageSource source) = _listedSources[index];
        _selectedId = id;
        _title.Text = _usage.Providers.First(p => p.Id == id).Name;
        bool keyed = source is IKeyedUsageSource;
        _key.Visibility = _save.Visibility = keyed ? Visibility.Visible : Visibility.Collapsed;
        _vaultNote.Visibility = source is IKeyedUsageSource or IWebSessionUsageSource ? Visibility.Visible : Visibility.Collapsed;
        _remove.Visibility = source is IKeyedUsageSource or IWebSessionUsageSource ? Visibility.Visible : Visibility.Collapsed;
        _login.Visibility = source is IWebSessionUsageSource ? Visibility.Visible : Visibility.Collapsed;
        _login.Content = $"{source.ShortName} 로그인";
        _remove.Content = source is IWebSessionUsageSource ? "Quotra 로그인 삭제" : "연결 해제";
        _hint.Text = source switch
        {
            IKeyedUsageSource api => $"{api.KeyHint}를 입력하세요.",
            IWebSessionUsageSource => "Claude 데스크톱과 Claude Code 로그인을 자동으로 찾습니다. 연결되지 않으면 아래 로그인 버튼으로 연결하세요.",
            CodexUsageSource => "Codex 데스크톱·CLI의 로그인 저장소와 CODEX_HOME 폴더를 자동으로 확인합니다. Codex에서 ChatGPT 계정으로 로그인한 뒤 새로고침하세요.",
            _ => "설치된 클라이언트의 기존 로그인 정보를 자동으로 확인합니다. 로그인한 뒤 새로고침하세요.",
        };
        _vaultNote.Text = source is IWebSessionUsageSource
            ? "Quotra 로그인은 사용량 조회를 확인한 뒤 Windows 자격 증명 보관함에 저장됩니다. 기존 앱 로그인 파일은 변경하지 않습니다."
            : "키는 조회 성공을 확인한 뒤 Windows 자격 증명 보관함에 저장됩니다.";
        if (source is IKeyedUsageSource apiSource) _key.PlaceholderText = apiSource.KeyHint;
        _key.Password = string.Empty;
        UpdateSelectedStatus();
    }

    private void UpdateSelectedStatus()
    {
        if (Selected is not { } selected) return;
        var provider = _usage.Providers.First(p => p.Id == selected.Id);
        string updated = provider.UpdatedAt is { } at ? $"마지막 성공 {at.LocalDateTime:yyyy-MM-dd HH:mm}" : "성공한 조회 없음";
        _status.Text = $"{provider.StatusText} · {updated}" + (provider.Error is null ? string.Empty : $"\n{provider.Error}");
        try
        {
            if (selected.Source is IKeyedUsageSource keyed && keyed.LoadKey() is not null) _status.Text += "\n저장된 키가 있습니다. 새 키를 입력하면 교체합니다.";
            if (selected.Source is IWebSessionUsageSource web) _remove.IsEnabled = web.HasSavedSession;
        }
        catch (Exception ex) { _status.Text += ex is InvalidOperationException ? $"\n{ex.Message}" : "\n저장된 자격 증명을 읽을 수 없습니다."; }
    }

    private void Login()
    {
        if (Selected is not { } selected || selected.Source is not IWebSessionUsageSource source) return;
        if (_loginWindow is not null) { _loginWindow.Activate(); return; }
        var window = _loginWindow = new ProviderLoginWindow(source);
        window.Connected += async (_, _) =>
        {
            _usage.ClearValues(selected.Id);
            await _usage.RefreshAsync();
        };
        window.Closed += (_, _) => _loginWindow = null;
        window.Activate();
    }

    private async Task SaveAsync()
    {
        if (Selected is not { } selected || selected.Source is not IKeyedUsageSource source)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_key.Password))
        {
            _status.Text = "API 키를 입력하세요.";
            return;
        }

        _save.IsEnabled = _remove.IsEnabled = _providerList.IsEnabled = false;
        _status.Text = "연결 확인 중…";

        try
        {
            string key = _key.Password.Trim();
            string summary = await source.VerifyKeyAsync(key, CancellationToken.None);
            if (_closed)
            {
                return;
            }

            source.SaveKey(key);
            _usage.ClearValues(selected.Id);
            _key.Password = string.Empty;
            _status.Text = $"연결되었습니다. {summary}";
            await _usage.RefreshAsync();
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _status.Text = ex is InvalidOperationException ? ex.Message : "연결 또는 저장에 실패했습니다. 다시 시도하세요.";
            }
        }
        finally
        {
            if (!_closed)
            {
                _save.IsEnabled = _remove.IsEnabled = _providerList.IsEnabled = true;
            }
        }
    }

    private void Remove()
    {
        if (Selected is not { } selected)
        {
            return;
        }

        try
        {
            if (selected.Source is IKeyedUsageSource keyed) keyed.DeleteKey();
            else if (selected.Source is IWebSessionUsageSource web) web.DeleteSession();
            else return;
            _key.Password = string.Empty;
            _usage.ClearValues(selected.Id);
            _status.Text = selected.Source is IWebSessionUsageSource
                ? "Quotra의 저장된 로그인을 삭제했습니다. 새로고침하면 데스크톱·CLI 로그인을 다시 자동으로 찾습니다."
                : "연결을 해제하고 저장된 키를 삭제했습니다.";
        }
        catch
        {
            _status.Text = "키를 삭제하지 못했습니다. 다시 시도하세요.";
        }
    }
}
