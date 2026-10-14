using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Quotra.Providers;
using Quotra.WinUI.Interop;
using Windows.Security.Credentials;

namespace Quotra.WinUI.Services;

/// <summary>Claude Desktop session, an explicit Quotra sign-in, or Claude Code OAuth.</summary>
[UsageSource("claude")]
public sealed class ClaudeUsageSource : IWebSessionUsageSource, IUsageDiagnostics, IUsageSelfTest
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string Resource = "Quotra.Claude";
    private const string Account = "web-session";
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    private string? _authPath;
    private string _endpoint = UsageEndpoint;
    private readonly HttpClient _oauthHttp = Http;
    private readonly ClaudeOAuthClient _oauth = new(Http, Log.Write);

    public ClaudeUsageSource(string? authPath = null) => _authPath = authPath;
    internal ClaudeUsageSource(HttpClient http)
    {
        _oauthHttp = http;
        _oauth = new ClaudeOAuthClient(http, Log.Write);
    }
    public string DisplayName => "Claude (Anthropic 구독 한도)";
    public string ShortName => "Claude";
    public string SelfTestName => "Claude OAuth 갱신·주간 한도";
    public bool RunSelfTest() => ClaudeOAuthSelfTest.Run();
    public Uri LoginUri => new("https://claude.ai/login");
    public string SessionCookieName => "sessionKey";
    public string? OrganizationCookieName => "lastActiveOrg";
    public bool HasSavedSession => LoadSession() is not null;
    public static string DefaultAuthPath => Path.Combine(ProviderAuthPaths.ClaudeHome, ".credentials.json");

    public void ApplyDiagnostics(string? url, string? file)
    {
        if (!string.IsNullOrWhiteSpace(file)) _authPath = Path.GetFullPath(file, AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(url)) _endpoint = url;
    }

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Explicitly chosen Quotra login wins. Do not silently change accounts if it expires.
            if (_authPath is null && _endpoint == UsageEndpoint)
            {
                if (LoadSession() is { } saved)
                    return await FetchSessionAsync(saved, cancellationToken).ConfigureAwait(false);
                var desktop = ClaudeDesktopSessionReader.Read();
                if (desktop.Session is not null)
                    return await FetchSessionAsync(new ClaudeSession(desktop.Session, desktop.OrganizationId), cancellationToken).ConfigureAwait(false);
                if (!File.Exists(DefaultAuthPath))
                    throw new InvalidOperationException(desktop.Error
                        ?? "Claude 로그인이 발견되지 않았습니다. 데스크톱에서 로그인한 뒤 새로고침하거나 설정에서 Claude 로그인을 사용하세요.");
            }
            return await FetchOAuthAsync(_authPath ?? DefaultAuthPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Claude 조회 시간이 초과되었습니다. 네트워크를 확인한 뒤 새로고침하세요.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Claude에 연결할 수 없습니다. 네트워크를 확인한 뒤 새로고침하세요.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Claude 응답에 사용 가능한 한도 값이 없습니다. 구독 상태를 확인하거나 잠시 후 다시 조회하세요.");
        }
    }

    public async Task VerifySessionAsync(string session, string? organizationId, CancellationToken cancellationToken)
    {
        try { await FetchSessionAsync(new ClaudeSession(ClaudeDesktopSessionReader.ValidateSession(session), organizationId), cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new InvalidOperationException("Claude에 연결할 수 없습니다. 네트워크를 확인하고 다시 시도하세요."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InvalidOperationException("Claude 연결 확인 시간이 초과되었습니다."); }
        catch (JsonException) { throw new InvalidOperationException("Claude 계정에서 숫자로 된 사용 한도를 확인할 수 없습니다."); }
    }

    public void SaveSession(string session, string? organizationId)
    {
        var credential = new ClaudeSession(ClaudeDesktopSessionReader.ValidateSession(session), NormalizeOrganizationId(organizationId));
        new PasswordVault().Add(new PasswordCredential(Resource, Account, JsonSerializer.Serialize(credential)));
    }

    public void DeleteSession()
    {
        var vault = new PasswordVault();
        try { vault.Remove(vault.Retrieve(Resource, Account)); }
        catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80070490) { }
    }

    private static ClaudeSession? LoadSession()
    {
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, Account);
            credential.RetrievePassword();
            return JsonSerializer.Deserialize<ClaudeSession>(credential.Password)
                ?? throw new InvalidOperationException("Quotra에 저장된 Claude 로그인을 읽을 수 없습니다. 설정에서 다시 로그인하세요.");
        }
        catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80070490) { return null; }
        catch (System.Runtime.InteropServices.COMException) { throw new InvalidOperationException("Windows 자격 증명 보관함에 접근할 수 없습니다. 현재 Windows 사용자로 실행하세요."); }
        catch (JsonException) { throw new InvalidOperationException("Quotra에 저장된 Claude 로그인 형식이 올바르지 않습니다. 설정에서 다시 로그인하세요."); }
    }

    private static async Task<ProviderSnapshot> FetchSessionAsync(ClaudeSession credential, CancellationToken cancellationToken)
    {
        string session = ClaudeDesktopSessionReader.ValidateSession(credential.Session);
        using JsonDocument organizations = await GetWebJsonAsync("/api/organizations", session, cancellationToken).ConfigureAwait(false);
        string organization = SelectOrganization(organizations.RootElement, credential.OrganizationId);
        using JsonDocument usage = await GetWebJsonAsync($"/api/organizations/{organization}/usage", session, cancellationToken).ConfigureAwait(false);
        ProviderSnapshot snapshot = ParseUsage(usage.RootElement, null);
        string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(organization)))[..16];
        return snapshot with { Note = "Claude 웹 로그인으로 조회", HistoryKey = $"claude-web-{identity}-{snapshot.PrimaryLabel}" };
    }

    private static async Task<JsonDocument> GetWebJsonAsync(string path, string session, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://claude.ai" + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", $"Quotra/{ProductVersion.Value}");
        request.Headers.TryAddWithoutValidation("Referer", "https://claude.ai/settings/usage");
        request.Headers.TryAddWithoutValidation("Cookie", "sessionKey=" + session);
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, web: true);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    internal static string SelectOrganization(JsonElement organizations, string? preferredId)
    {
        if (organizations.ValueKind != JsonValueKind.Array) throw new JsonException();
        var choices = organizations.EnumerateArray().Where(org => org.ValueKind == JsonValueKind.Object
            && org.TryGetProperty("uuid", out JsonElement uuid) && uuid.ValueKind == JsonValueKind.String
            && Guid.TryParse(uuid.GetString(), out _)
            && (!org.TryGetProperty("capabilities", out JsonElement capabilities) || capabilities.ValueKind != JsonValueKind.Array
                || capabilities.EnumerateArray().Any(cap => cap.ValueKind == JsonValueKind.String && cap.GetString() == "chat"))).ToArray();
        string? normalized = NormalizeOrganizationId(preferredId);
        if (normalized is not null)
        {
            foreach (JsonElement org in choices)
                if (string.Equals(org.GetProperty("uuid").GetString(), normalized, StringComparison.OrdinalIgnoreCase)) return normalized;
            throw new InvalidOperationException("선택한 Claude 조직을 찾지 못했습니다. Claude 로그인 창에서 사용할 조직으로 전환한 뒤 연결하세요.");
        }
        if (choices.Length == 1) return choices[0].GetProperty("uuid").GetString()!;
        throw new InvalidOperationException(choices.Length == 0 ? "Claude 채팅 구독 조직이 없습니다. 계정과 구독 상태를 확인하세요."
            : "Claude 조직이 여러 개 있습니다. 설정의 Claude 로그인 창에서 사용할 조직으로 전환한 뒤 연결하세요.");
    }

    private static string? NormalizeOrganizationId(string? id) => Guid.TryParse(id, out Guid parsed) ? parsed.ToString() : null;

    internal async Task<ProviderSnapshot> FetchOAuthAsync(string path, CancellationToken cancellationToken)
    {
        // Explicit diagnostic overrides must never rotate a real login or send a test token to OAuth.
        if (_endpoint != UsageEndpoint && (_authPath is null
            || string.Equals(Path.GetFullPath(path), Path.GetFullPath(DefaultAuthPath), StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(_endpoint, UriKind.Absolute, out Uri? diagnosticUri) || !diagnosticUri.IsLoopback
            || diagnosticUri.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("Claude 진단 URL은 별도의 테스트 로그인 파일과 로컬 루프백 주소를 함께 지정해야 합니다.");
        bool allowRefresh = _authPath is null && _endpoint == UsageEndpoint;
        ClaudeOAuthCredential credential = await _oauth.GetAsync(path, cancellationToken, allowRefresh: allowRefresh).ConfigureAwait(false);
        using HttpResponseMessage first = await GetOAuthUsageAsync(credential.AccessToken, cancellationToken).ConfigureAwait(false);
        if (first.StatusCode == HttpStatusCode.Unauthorized && !credential.Refreshed && allowRefresh)
        {
            credential = await _oauth.GetAsync(path, cancellationToken, credential.AccessToken).ConfigureAwait(false);
            using HttpResponseMessage retry = await GetOAuthUsageAsync(credential.AccessToken, cancellationToken).ConfigureAwait(false);
            return await ReadOAuthUsageAsync(retry, credential, cancellationToken).ConfigureAwait(false);
        }
        return await ReadOAuthUsageAsync(first, credential, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> GetOAuthUsageAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        return await _oauthHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProviderSnapshot> ReadOAuthUsageAsync(HttpResponseMessage response, ClaudeOAuthCredential credential, CancellationToken cancellationToken)
    {
        EnsureSuccess(response, web: false);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return ParseUsage(document.RootElement, PlanLabel(credential.Plan)) with { Note = "Claude Code 로그인으로 조회" };
    }

    private static void EnsureSuccess(HttpResponseMessage response, bool web)
    {
        int code = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException(web
                ? $"Claude 웹 로그인이 만료되었거나 조회가 차단되었습니다(HTTP {code}). 설정에서 다시 로그인하세요."
                : $"Claude Code 로그인이 거부되었습니다(HTTP {code}). Claude Code 로그인을 갱신하거나 설정에서 Claude 로그인을 사용하세요.");
        if (code == 429) throw new InvalidOperationException("Claude 조회가 일시적으로 제한되었습니다. 다음 자동 갱신 때 다시 시도합니다.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Claude 사용량 조회에 실패했습니다(HTTP {code}). 잠시 후 다시 시도하세요.");
    }

    internal static ProviderSnapshot ParseUsage(JsonElement root, string? plan)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        WindowUsage? weekly = ReadWindow(root, "seven_day"), session = ReadWindow(root, "five_hour");
        if (weekly is null && session is null) throw new JsonException();
        bool sessionOnly = weekly is null;
        return new ProviderSnapshot(Primary: weekly ?? session, Secondary: sessionOnly ? null : session, Plan: plan,
            HistoryKey: sessionOnly ? "claude-five-hour" : "claude", PrimaryLabel: sessionOnly ? "5시간" : "주간", SecondaryLabel: "5시간");
    }

    private static WindowUsage? ReadWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement window) || window.ValueKind == JsonValueKind.Null) return null;
        if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("utilization", out JsonElement utilization)
            || utilization.ValueKind != JsonValueKind.Number || !utilization.TryGetDouble(out double percent)
            || !double.IsFinite(percent) || percent < 0) throw new JsonException();
        DateTimeOffset? resetAt = null;
        if (window.TryGetProperty("resets_at", out JsonElement reset) && reset.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)) resetAt = parsed;
        return new WindowUsage(Math.Clamp(percent / 100, 0, 1), resetAt);
    }

    private static string? PlanLabel(string? plan) => plan?.ToLowerInvariant() switch
    {
        "pro" => "Claude Pro", "max" => "Claude Max", "free" => "Claude Free", _ => null,
    };
    private sealed record ClaudeSession(string Session, string? OrganizationId);
}
