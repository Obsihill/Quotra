using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Quotra.WinUI.Services;
using Windows.Graphics;

namespace Quotra.WinUI.Views;

/// <summary>Provider-owned website sign-in in a Quotra-only WebView profile.</summary>
internal sealed class ProviderLoginWindow : Window
{
    private readonly IWebSessionUsageSource _source;
    private readonly WebView2 _browser = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _connect = new() { Content = "로그인 확인 및 연결", IsEnabled = false };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closed, _started;
    public event EventHandler? Connected;

    public ProviderLoginWindow(IWebSessionUsageSource source)
    {
        _source = source;
        Title = $"Quotra · {source.ShortName} 로그인";
        AppWindow.Resize(new SizeInt32(1000, 760));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Quotra.ico"));
        var root = new Grid { Padding = new Thickness(16), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _status.Text = $"{source.LoginUri.Host}에서 로그인한 뒤 ‘로그인 확인 및 연결’을 누르세요. 로그인은 조회 성공 후 Windows 자격 증명 보관함에 저장됩니다.";
        root.Children.Add(_status);
        Grid.SetRow(_browser, 1);
        root.Children.Add(_browser);
        Grid.SetRow(_connect, 2);
        root.Children.Add(_connect);
        Content = root;
        root.Loaded += async (_, _) => await InitializeAsync();
        _connect.Click += async (_, _) => await ConnectAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            // Clear Quotra's temporary login cookies, never the user's desktop/browser login.
            try { _browser.CoreWebView2?.CookieManager.DeleteAllCookies(); _browser.Close(); } catch { }
        };
    }

    private async Task InitializeAsync()
    {
        if (_started) return;
        _started = true;
        try
        {
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quotra", "WebView", _source.ShortName);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, null);
            if (_closed) return;
            await _browser.EnsureCoreWebView2Async(environment);
            if (_closed) { _browser.Close(); return; }
            _browser.CoreWebView2.CookieManager.DeleteAllCookies();
            _browser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
                    args.Cancel = true;
            };
            _browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps)
                    _browser.CoreWebView2.Navigate(args.Uri);
            };
            _browser.Source = _source.LoginUri;
            _connect.IsEnabled = true;
        }
        catch
        {
            if (!_closed) _status.Text = "로그인 화면을 열 수 없습니다. Microsoft Edge WebView2 Runtime 설치와 네트워크를 확인하세요.";
        }
    }

    private async Task ConnectAsync()
    {
        if (_browser.CoreWebView2 is null) return;
        _connect.IsEnabled = false;
        try
        {
            var cookies = await _browser.CoreWebView2.CookieManager.GetCookiesAsync(_source.LoginUri.GetLeftPart(UriPartial.Authority));
            if (_closed) return;
            var session = cookies.FirstOrDefault(cookie => cookie.Name == _source.SessionCookieName
                && cookie.Domain.TrimStart('.').Equals(_source.LoginUri.Host, StringComparison.OrdinalIgnoreCase)
                && (cookie.IsSession || cookie.Expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            if (session is null)
            {
                _status.Text = "로그인이 아직 완료되지 않았습니다. 로그인을 마친 뒤 다시 눌러 주세요.";
                return;
            }
            string? organization = cookies.FirstOrDefault(cookie => cookie.Name == _source.OrganizationCookieName)?.Value;
            _status.Text = "사용량 조회 권한을 확인하는 중…";
            await _source.VerifySessionAsync(session.Value, organization, _lifetime.Token);
            if (_closed) return;
            _source.SaveSession(session.Value, organization);
            Connected?.Invoke(this, EventArgs.Empty);
            Close();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (!_closed) _status.Text = ex is InvalidOperationException ? ex.Message : "로그인 확인 또는 저장에 실패했습니다. 다시 시도하세요.";
        }
        finally { if (!_closed) _connect.IsEnabled = true; }
    }
}
