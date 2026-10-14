using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Quotra.Providers;
using Quotra.WinUI.Interop;

namespace Quotra.WinUI.Services;

/// <summary>Automatically use an existing Claude Code OAuth login or Claude Desktop session.</summary>
[UsageSource("claude")]
public sealed class ClaudeUsageSource : IUsageSource, IUsageDiagnostics, IUsageSelfTest
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    private string? _authPath;
    private string _endpoint = UsageEndpoint;
    private readonly HttpClient _oauthHttp = Http;
    private readonly ClaudeOAuthClient _oauth = new(Http, Log.Write);
    private readonly IClaudeCliUsageProbe _cli = new ClaudeCliUsageProbe();
    private readonly Func<ClaudeDesktopSessionReader.Result> _desktop = ClaudeDesktopSessionReader.Read;
    private readonly ClaudeIdentityCache _identities = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quotra", "claude-identities"));
    private readonly string? _autoHome;
    private DateTimeOffset _usageRetryAt;
    private DateTimeOffset _nextCliAttempt;
    private string? _cliCredentialStamp;
    private string? _lastOAuthTokenHash;

    public ClaudeUsageSource(string? authPath = null) => _authPath = authPath;
    internal ClaudeUsageSource(HttpClient http, IClaudeCliUsageProbe? cli = null,
        Func<ClaudeDesktopSessionReader.Result>? desktop = null, string? autoHome = null, string? identityCacheDirectory = null)
    {
        _oauthHttp = http;
        _oauth = new ClaudeOAuthClient(http, Log.Write);
        if (cli is not null) _cli = cli;
        if (desktop is not null) _desktop = desktop;
        _autoHome = autoHome;
        if (identityCacheDirectory is not null) _identities = new(identityCacheDirectory);
    }
    public string DisplayName => "Claude (Anthropic 구독 한도)";
    public string ShortName => "Claude";
    public string SelfTestName => "Claude 자동 연결·로그인 갱신";
    public bool RunSelfTest() => ClaudeOAuthSelfTest.Run() & ClaudeCliUsageSelfTest.Run() & ClaudeAutoUsageSelfTest.Run();
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
            if (_authPath is null && _endpoint == UsageEndpoint)
                return await FetchAutomaticAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<ProviderSnapshot> FetchAutomaticAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(45));
        CancellationToken token = budget.Token;
        string home = _autoHome ?? ProviderAuthPaths.ClaudeHome;
        string path = Path.Combine(home, ".credentials.json");
        bool hadCredential = File.Exists(path);
        Exception failure = new ProviderNotConnectedException("Claude 로그인이 발견되지 않았습니다. Claude Code 또는 Claude Desktop에서 로그인한 뒤 새로고침하세요.");
        if (hadCredential)
        {
            try
            {
                ProviderSnapshot snapshot = await FetchOAuthAsync(path, token).ConfigureAwait(false);
                TokenState? state = ReadToken(path);
                if (state is null || state.Hash != _lastOAuthTokenHash) throw LoginChanged();
                ClaudeAccountIdentity? identity = await ResolveIdentityAsync(path, state, token).ConfigureAwait(false);
                if (ReadToken(path)?.Hash != state.Hash) throw LoginChanged();
                return Bind(snapshot, "oauth", identity?.HistoryId ?? state.Hash[..16], "Claude Code 로그인으로 자동 조회");
            }
            catch (Exception ex) when (Recoverable(ex, token)) { failure = ex; }
            if (ClaudeStatusLineBridge.Read(path) is { } sample)
                return sample with { Note = sample.Note + " · OAuth 조회 실패: " + SafeFailure(failure) };
        }
        token.ThrowIfCancellationRequested();
        if (failure is ClaudeRateLimitedException limited) { _usageRetryAt = limited.RetryAt; throw limited; }
        ThrowIfRateLimited();

        TokenState? beforeCli = ReadToken(path);
        ClaudeAccountIdentity? expected = beforeCli is null ? null : await ResolveIdentityAsync(path, beforeCli, token).ConfigureAwait(false);
        ThrowIfRateLimited();
        string stamp = beforeCli?.Hash ?? (hadCredential ? "unreadable" : "absent");
        if (_cliCredentialStamp != stamp) { _cliCredentialStamp = stamp; _nextCliAttempt = default; }
        string? cliFailure = null;
        if (DateTimeOffset.UtcNow >= _nextCliAttempt)
        {
            try
            {
                ClaudeCliUsageResult? cli = await _cli.TryFetchAsync(home, token).ConfigureAwait(false);
                if (cli is not null)
                {
                    TokenState? afterCli = ReadToken(path);
                    ClaudeAccountIdentity? current = afterCli is null ? null
                        : await ResolveIdentityAsync(path, afterCli, token).ConfigureAwait(false);
                    if (beforeCli is not null && afterCli is null) throw LoginChanged();
                    if (beforeCli?.Hash != afterCli?.Hash && afterCli is not null && current is null) throw LoginChanged();
                    if (expected is not null && current is not null && !expected.Matches(current)) throw LoginChanged();
                    ClaudeAccountIdentity? owner = current ?? expected;
                    if (afterCli is not null && owner is null)
                        throw new ProviderNotConnectedException("Claude CLI 사용량의 로그인 계정을 확인할 수 없습니다. Claude Code에서 다시 로그인한 뒤 새로고침하세요.");
                    if (owner is not null &&
                        ((cli.AccountUuid is { } account && !string.Equals(account, owner.AccountUuid, StringComparison.OrdinalIgnoreCase))
                         || (cli.OrganizationUuid is { } org && !string.Equals(org, owner.OrganizationUuid, StringComparison.OrdinalIgnoreCase))))
                        throw LoginChanged();
                    if (ReadToken(path)?.Hash != afterCli?.Hash) throw LoginChanged();
                    if (afterCli is null && (cli.OrganizationUuid is null || (cli.AccountUuid is null && cli.Email is null)))
                        throw new ProviderNotConnectedException("Claude CLI 로그인 계정을 확인할 수 없습니다. Claude Code에서 다시 로그인한 뒤 새로고침하세요.");
                    string scope = owner?.HistoryId ?? afterCli?.Hash[..16]
                        ?? ClaudeAccountIdentity.Hash((cli.AccountUuid ?? cli.Email ?? "cli") + ":" + cli.OrganizationUuid)[..16];
                    return Bind(cli.Snapshot, "cli", scope, "Claude Code /usage로 자동 조회 · OAuth 대신 CLI 사용");
                }
                _nextCliAttempt = DateTimeOffset.UtcNow.AddMinutes(2);
            }
            catch (Exception ex) when (Recoverable(ex, token))
            {
                _nextCliAttempt = DateTimeOffset.UtcNow.AddMinutes(2);
                if (ex is ProviderNotConnectedException) failure = ex;
                cliFailure = SafeFailure(ex);
            }
        }
        token.ThrowIfCancellationRequested();
        ThrowIfRateLimited();

        // The CLI can refresh the same profile itself; use its latest server-verified owner.
        TokenState? currentToken = ReadToken(path);
        if (currentToken?.Hash != beforeCli?.Hash)
        {
            ClaudeAccountIdentity? current = currentToken is null ? null : await ResolveIdentityAsync(path, currentToken, token).ConfigureAwait(false);
            if (expected is not null && current is not null && !expected.Matches(current)) throw LoginChanged();
            expected = current;
        }
        ThrowIfRateLimited();
        var desktop = _desktop();
        if (desktop.Session is not null)
        {
            if ((hadCredential || File.Exists(path)) && expected is null)
                throw new ProviderNotConnectedException("Claude Code와 Desktop의 계정이 같은지 확인할 수 없습니다. Claude Code에서 다시 로그인한 뒤 새로고침하세요.");
            try
            {
                ProviderSnapshot snapshot = await FetchSessionAsync(new(desktop.Session, desktop.OrganizationId), expected, token).ConfigureAwait(false);
                var latestDesktop = _desktop();
                if (ReadToken(path)?.Hash != currentToken?.Hash || latestDesktop.Session != desktop.Session
                    || latestDesktop.OrganizationId != desktop.OrganizationId) throw LoginChanged();
                return snapshot;
            }
            catch (Exception ex) when (Recoverable(ex, token)) { failure = ex; }
        }
        else if (desktop.Error is { } desktopError)
            failure = new InvalidOperationException(desktopError);
        if (cliFailure is not null && failure is ProviderNotConnectedException)
            throw new ProviderNotConnectedException($"{failure.Message} CLI 확인: {cliFailure}");
        throw failure;
    }

    private static bool Recoverable(Exception ex, CancellationToken token) => !token.IsCancellationRequested && ex is not ClaudeAccountChangedException
        && ex is InvalidOperationException or HttpRequestException or JsonException or IOException or UnauthorizedAccessException or OperationCanceledException;

    private static ClaudeAccountChangedException LoginChanged() => new();

    private static ProviderSnapshot Bind(ProviderSnapshot snapshot, string source, string scope, string note) => snapshot with
    {
        HistoryKey = $"claude-{source}-{scope}-{snapshot.PrimaryLabel}",
        Note = note + (source == "cli" && snapshot.Primary?.ResetsAt is null ? " · 리셋 시각 확인 안 됨" : ""),
    };

    private sealed class TokenState(string accessToken)
    {
        internal string AccessToken { get; } = accessToken;
        internal string Hash { get; } = ClaudeAccountIdentity.Hash(accessToken);
        public override string ToString() => "Claude token (redacted)";
    }

    private static TokenState? ReadToken(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("claudeAiOauth", out JsonElement oauth)) return null;
            string? access = ClaudeAccountIdentity.Text(oauth, "accessToken");
            return access is { Length: > 0 and <= 16_384 } && access.All(c => c > ' ' && c < 127) ? new(access) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    private async Task<ClaudeAccountIdentity?> ResolveIdentityAsync(string path, TokenState state, CancellationToken token)
    {
        if (_identities.Read(path, state.Hash) is { } cached) return cached;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/profile");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.AccessToken);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            using var response = await _oauthHttp.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode == 429) { SetUsageBackoff(response); return null; }
            if (!response.IsSuccessStatusCode) return null;
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            ClaudeAccountIdentity? identity = ClaudeAccountIdentity.FromOAuth(document.RootElement);
            if (identity is not null && ReadToken(path)?.Hash == state.Hash) _identities.Write(path, state.Hash, identity);
            return identity;
        }
        catch (Exception ex) when (Recoverable(ex, token)) { return null; }
    }

    private void ThrowIfRateLimited()
    {
        if (DateTimeOffset.UtcNow < _usageRetryAt)
            throw new ClaudeRateLimitedException($"Claude 사용량 조회가 일시 제한되었습니다(HTTP 429). {_usageRetryAt.ToLocalTime():HH:mm:ss} 이후 다시 조회합니다.", _usageRetryAt);
    }

    private void SetUsageBackoff(HttpResponseMessage response)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - now : TimeSpan.FromMinutes(2));
        _usageRetryAt = now.AddSeconds(Math.Clamp(delay.TotalSeconds, 60, 86400));
    }

    private async Task<ProviderSnapshot> FetchSessionAsync(ClaudeSession credential, ClaudeAccountIdentity? expected, CancellationToken cancellationToken)
    {
        string session = ClaudeDesktopSessionReader.ValidateSession(credential.Session);
        using JsonDocument organizations = await GetWebJsonAsync("/api/organizations", session, cancellationToken).ConfigureAwait(false);
        string organization;
        try { organization = SelectOrganization(organizations.RootElement, expected?.OrganizationUuid ?? credential.OrganizationId); }
        catch (InvalidOperationException) when (expected is not null) { throw LoginChanged(); }
        using JsonDocument account = await GetWebJsonAsync("/api/account", session, cancellationToken).ConfigureAwait(false);
        ClaudeAccountIdentity? owner = ClaudeAccountIdentity.FromDesktop(account.RootElement, organization);
        if (expected is not null && (owner is null || !expected.Matches(owner))) throw LoginChanged();
        using JsonDocument usage = await GetWebJsonAsync($"/api/organizations/{organization}/usage", session, cancellationToken).ConfigureAwait(false);
        ProviderSnapshot snapshot = ParseUsage(usage.RootElement, null);
        return Bind(snapshot, "desktop", owner?.HistoryId ?? ClaudeAccountIdentity.Hash(organization)[..16], "Claude Desktop 기존 로그인으로 자동 조회");
    }

    private async Task<JsonDocument> GetWebJsonAsync(string path, string session, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://claude.ai" + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", $"Quotra/{ProductVersion.Value}");
        request.Headers.TryAddWithoutValidation("Referer", "https://claude.ai/settings/usage");
        request.Headers.TryAddWithoutValidation("Cookie", "sessionKey=" + session);
        using HttpResponseMessage response = await _oauthHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode == 429) { SetUsageBackoff(response); ThrowIfRateLimited(); }
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
            throw new InvalidOperationException("선택한 Claude 조직을 찾지 못했습니다. Claude Desktop에서 사용할 조직으로 전환한 뒤 새로고침하세요.");
        }
        if (choices.Length == 1) return choices[0].GetProperty("uuid").GetString()!;
        throw new InvalidOperationException(choices.Length == 0 ? "Claude 채팅 구독 조직이 없습니다. 계정과 구독 상태를 확인하세요."
            : "Claude 조직이 여러 개 있습니다. Claude Desktop에서 사용할 조직으로 전환한 뒤 새로고침하세요.");
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
        ThrowIfRateLimited();
        ClaudeOAuthCredential credential = await _oauth.GetAsync(path, cancellationToken, allowRefresh: allowRefresh).ConfigureAwait(false);
        if (!credential.HasUsageScope)
            throw new InvalidOperationException("Claude 로그인에 사용량 조회 권한(user:profile)이 없습니다. Claude Code의 /login으로 구독 계정에 다시 로그인하세요. setup-token이나 API 키로는 이 권한을 복구할 수 없습니다.");
        _lastOAuthTokenHash = ClaudeAccountIdentity.Hash(credential.AccessToken);
        using HttpResponseMessage first = await GetOAuthUsageAsync(credential.AccessToken, cancellationToken).ConfigureAwait(false);
        if (first.StatusCode == HttpStatusCode.Unauthorized && !credential.Refreshed && allowRefresh)
        {
            credential = await _oauth.GetAsync(path, cancellationToken, credential.AccessToken).ConfigureAwait(false);
            _lastOAuthTokenHash = ClaudeAccountIdentity.Hash(credential.AccessToken);
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

    private async Task<ProviderSnapshot> ReadOAuthUsageAsync(HttpResponseMessage response, ClaudeOAuthCredential credential, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode == 429)
        {
            SetUsageBackoff(response);
            ThrowIfRateLimited();
        }
        EnsureSuccess(response, web: false);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return ParseUsage(document.RootElement, PlanLabel(credential.Plan)) with { Note = "Claude Code 로그인으로 조회" };
    }

    private static void EnsureSuccess(HttpResponseMessage response, bool web)
    {
        int code = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ProviderNotConnectedException(web
                ? "Claude Desktop 로그인이 만료되었습니다(HTTP 401). Claude Desktop에서 다시 로그인한 뒤 새로고침하세요."
                : "Claude Code 로그인이 만료되었거나 취소되었습니다(HTTP 401). Claude Code의 /login으로 다시 로그인하세요.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException(web
                ? "Claude 웹 사용량 접근이 차단되었습니다(HTTP 403). 웹 접근 정책이나 계정 권한을 확인하세요. 재로그인만으로 해결되지 않을 수 있습니다."
                : "Claude 사용량 조회 권한이 거부되었습니다(HTTP 403). 구독 로그인과 user:profile 권한을 확인하세요.");
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
    private static string SafeFailure(Exception ex) => ex switch
    {
        InvalidOperationException => ex.Message,
        HttpRequestException => "네트워크 연결 실패",
        OperationCanceledException => "조회 시간 초과",
        _ => "응답 형식 변경",
    };
}
