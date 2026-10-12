using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Quotra.WinUI.Services;
using Windows.Graphics;

namespace Quotra.WinUI.Views;

/// <summary>
/// 프로바이더 설정 창 — <see cref="IKeyedUsageSource"/> 를 구현한 프로바이더를 자동으로 나열한다.
/// 화면 코드는 프로바이더 이름을 모른다: 새 키 입력형 프로바이더는 소스 클래스만 추가하면 목록에 나타난다.
/// 입력한 키는 조회 성공을 확인한 뒤에만 저장하고, 저장 위치는 각 소스가 정한다(자격 증명 보관함).
/// </summary>
public sealed class ProviderSettingsWindow : Window
{
    private readonly UsageService _usage;
    private readonly ListView _providerList = new() { SelectionMode = ListViewSelectionMode.Single, MinWidth = 200 };
    private readonly TextBlock _title = new() { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly PasswordBox _key = new() { Header = "API 키", PasswordRevealMode = PasswordRevealMode.Peek };
    private readonly TextBlock _vaultNote = new() { Text = "키는 조회 성공을 확인한 뒤 Windows 자격 증명 보관함에 저장됩니다.", FontSize = 12 };
    private readonly Button _save = new() { Content = "연결 확인 및 저장" };
    private readonly Button _remove = new() { Content = "연결 해제" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _detail = new() { Spacing = 16 };

    private bool _closed;
    private string? _selectedId;

    public ProviderSettingsWindow(UsageService usage)
    {
        _usage = usage;
        Title = "Quotra · 프로바이더 설정";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(680, 460));

        _detail.Padding = new Thickness(24, 4, 4, 4);
        _detail.Children.Add(_title);
        _detail.Children.Add(_hint);
        _detail.Children.Add(_key);
        _detail.Children.Add(_vaultNote);
        _detail.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { _save, _remove },
        });
        _detail.Children.Add(_status);

        var root = new Grid { Padding = new Thickness(20), ColumnSpacing = 20 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_providerList, 0);
        Grid.SetColumn(_detail, 1);
        root.Children.Add(_providerList);
        root.Children.Add(_detail);
        Content = root;

        _providerList.SelectionChanged += (_, _) => SelectProvider();
        _save.Click += async (_, _) => await SaveAsync();
        _remove.Click += (_, _) => Remove();
        Closed += (_, _) => _closed = true;

        BuildProviderList();
    }

    private IReadOnlyList<(string Id, IKeyedUsageSource Source)> Keyed => _usage.KeyedSources;

    private void BuildProviderList()
    {
        IReadOnlyList<(string Id, IKeyedUsageSource Source)> keyed = Keyed;
        _providerList.Items.Clear();

        foreach ((_, IKeyedUsageSource source) in keyed)
        {
            _providerList.Items.Add(source.KeyLabel);
        }

        if (keyed.Count == 0)
        {
            _title.Text = "API 키가 필요한 프로바이더가 없습니다";
            _hint.Text = "카탈로그에 키 입력형 프로바이더가 등록되면 여기에 나타납니다.";
            _key.Visibility = Visibility.Collapsed;
            _vaultNote.Visibility = Visibility.Collapsed;
            _save.Visibility = Visibility.Collapsed;
            _remove.Visibility = Visibility.Collapsed;
            return;
        }

        _providerList.SelectedIndex = 0;
    }

    private (string Id, IKeyedUsageSource Source)? Selected => _selectedId is null
        ? null
        : Keyed.FirstOrDefault(pair => string.Equals(pair.Id, _selectedId, StringComparison.OrdinalIgnoreCase));

    private void SelectProvider()
    {
        int index = _providerList.SelectedIndex;
        IReadOnlyList<(string Id, IKeyedUsageSource Source)> keyed = Keyed;
        if (index < 0 || index >= keyed.Count)
        {
            return;
        }

        (string id, IKeyedUsageSource source) = keyed[index];
        _selectedId = id;
        _title.Text = source.KeyLabel;
        _hint.Text = $"{source.KeyHint}를 입력하세요.";
        _key.PlaceholderText = source.KeyHint;
        _key.Password = string.Empty;
        _status.Text = source.LoadKey() is null
            ? "미연결 · 키를 입력하면 사용량을 조회합니다."
            : "저장된 키가 있습니다. 새 키를 입력하면 교체합니다.";
    }

    private async Task SaveAsync()
    {
        if (Selected is not { } selected)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_key.Password))
        {
            _status.Text = "API 키를 입력하세요.";
            return;
        }

        _save.IsEnabled = _remove.IsEnabled = false;
        _status.Text = "연결 확인 중…";

        try
        {
            string key = _key.Password.Trim();
            string summary = await selected.Source.VerifyKeyAsync(key, CancellationToken.None);
            if (_closed)
            {
                return;
            }

            selected.Source.SaveKey(key);
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
                _save.IsEnabled = _remove.IsEnabled = true;
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
            selected.Source.DeleteKey();
            _key.Password = string.Empty;
            _usage.ClearValues(selected.Id);
            _status.Text = "연결을 해제하고 저장된 키를 삭제했습니다.";
        }
        catch
        {
            _status.Text = "키를 삭제하지 못했습니다. 다시 시도하세요.";
        }
    }
}
