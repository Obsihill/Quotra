using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Windows.Security.Credentials;

namespace Quotra.WinUI.Services;

/// <summary>
/// Command Code 계정의 월간 크레딧·5시간 사용량.
/// 설정에서 입력한 API 키로 계정 사용량 경로에서 한도 정보를 가져온다.
/// 키는 Windows 자격 증명 보관함에만 저장한다.
/// </summary>
[UsageSource("commandcode")]
public sealed class CommandCodeUsageSource : IKeyedUsageSource, IUsageDiagnostics, IUsageSelfTest
{
    private const string Resource = "Quotra.CommandCode";
    private const string Account = "api-key";
    private const string DefaultApiBaseUrl = "https://api.commandcode.ai";
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly HttpClient _http;
    private readonly Func<string?> _loadKey;
    private string _apiBaseUrl = DefaultApiBaseUrl;

    public CommandCodeUsageSource()
    {
        _http = Http;
        _loadKey = LoadKey;
    }

    internal CommandCodeUsageSource(HttpClient http, Func<string?> loadKey)
    {
        _http = http;
        _loadKey = loadKey;
    }

    public string DisplayName => "Command Code 사용량";

    public string ShortName => "Command Code";

    public string KeyLabel => "Command Code";

    public string KeyHint => "Command Code Studio에서 발급한 API 키";

    public string SelfTestName => "Command Code API 키 연결";

    public bool RunSelfTest() => CommandCodeUsageSelfTest.Run();

    public string? LoadKey()
    {
        try
        {
            PasswordCredential credential = new PasswordVault().Retrieve(Resource, Account);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public void SaveKey(string key)
    {
        new PasswordVault().Add(new PasswordCredential(Resource, Account, NormalizeKey(key)));
    }

    public void DeleteKey()
    {
        var vault = new PasswordVault();
        try
        {
            vault.Remove(vault.Retrieve(Resource, Account));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }

    public Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        string? key = _loadKey();
        return string.IsNullOrWhiteSpace(key)
            ? Task.FromResult(new ProviderSnapshot())
            : FetchWithKeyAsync(key, cancellationToken);
    }

    public async Task<string> VerifyKeyAsync(string key, CancellationToken cancellationToken)
    {
        ProviderSnapshot snapshot = await FetchWithKeyAsync(key, cancellationToken).ConfigureAwait(false);
        return snapshot.Note ?? "사용량 조회에 성공했습니다.";
    }

    private async Task<ProviderSnapshot> FetchWithKeyAsync(string key, CancellationToken cancellationToken)
    {
        string apiKey = NormalizeKey(key);
        // 진단 재정의는 로컬 루프백만 — 실계정 토큰이 임의 호스트로 실려 나가지 않게 한다.
        if (!string.Equals(_apiBaseUrl, DefaultApiBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticGuard.EnsureLoopback(_apiBaseUrl, "Command Code");
        }

        // Command Code CLI resolves the active account namespace before requesting its usage.
        using JsonDocument identity = await GetJsonAsync("/alpha/whoami?limits=1", apiKey, cancellationToken).ConfigureAwait(false);
        string? orgId = ReadOrgId(identity.RootElement);
        string query = string.IsNullOrWhiteSpace(orgId) ? string.Empty : $"?orgId={Uri.EscapeDataString(orgId)}";

        using JsonDocument credits = await GetJsonAsync($"/alpha/billing/credits{query}", apiKey, cancellationToken).ConfigureAwait(false);
        using JsonDocument subscription = await GetJsonAsync($"/alpha/billing/subscriptions{query}", apiKey, cancellationToken).ConfigureAwait(false);
        JsonElement account = UnwrapData(identity.RootElement);
        string accountId = orgId ?? (account.TryGetProperty("user", out JsonElement user) && user.ValueKind == JsonValueKind.Object
            && user.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null) ?? apiKey;
        string accountHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(accountId)))[..16];
        return ParseUsage(credits.RootElement, subscription.RootElement, accountHash);
    }

    public void ApplyDiagnostics(string? url, string? file)
    {
        if (!string.IsNullOrWhiteSpace(file))
        {
            throw new InvalidOperationException("Command Code는 로그인 파일을 사용하지 않습니다. 프로바이더 설정에서 API 키를 연결하세요.");
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            _apiBaseUrl = url.TrimEnd('/');
        }
    }

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Trim().Any(char.IsWhiteSpace) || key.Any(char.IsControl))
        {
            throw new InvalidOperationException("올바른 Command Code API 키를 입력하세요.");
        }

        return key.Trim();
    }

    private async Task<JsonDocument> GetJsonAsync(string path, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_apiBaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("cli");

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Command Code API 키가 유효하지 않거나 만료되었습니다. 프로바이더 설정에서 새 API 키를 연결하세요.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("Command Code 사용량 조회가 거부되었습니다. 계정 또는 조직 접근 권한을 확인하세요.");
        }

        if ((int)response.StatusCode == 429)
        {
            throw new InvalidOperationException("Command Code 사용량 조회가 일시적으로 제한되었습니다. 다음 자동 갱신 때 다시 시도합니다.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Command Code 사용량 조회에 실패했습니다(HTTP {(int)response.StatusCode}).");
        }

        try
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Command Code 사용량 응답 형식이 바뀌어 값을 읽지 못했습니다.");
        }
    }

    private static string? ReadOrgId(JsonElement root)
    {
        JsonElement payload = UnwrapData(root);
        return payload.TryGetProperty("org", out JsonElement org) &&
               org.ValueKind == JsonValueKind.Object &&
               org.TryGetProperty("id", out JsonElement id) &&
               id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
    }

    private static ProviderSnapshot ParseUsage(JsonElement root, JsonElement subscriptionRoot, string accountHash)
    {
        try
        {
            JsonElement payload = UnwrapData(root);
            JsonElement subscription = UnwrapData(subscriptionRoot);
            if (payload.ValueKind != JsonValueKind.Object || subscription.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            WindowUsage? fiveHour = null;
            string weeklyNote = string.Empty;
            if (payload.TryGetProperty("windowLimits", out JsonElement limits) && limits.ValueKind == JsonValueKind.Object)
            {
                fiveHour = ReadWindow(limits, "fiveHour");
                if (ReadWindow(limits, "weekly") is { } weekly)
                    weeklyNote = $" · 별도 주간 제한 {weekly.Used:P0} 사용";
            }
            if (!payload.TryGetProperty("credits", out JsonElement credits) || credits.ValueKind != JsonValueKind.Object
                || !credits.TryGetProperty("monthlyCredits", out JsonElement remainingElement)
                || remainingElement.ValueKind != JsonValueKind.Number
                || !remainingElement.TryGetDouble(out double remaining) || !double.IsFinite(remaining) || remaining < 0)
                throw new JsonException();
            string? planId = ReadString(subscription, "planId");
            (string Name, double Cap)? plan = PlanInfo(planId);
            DateTimeOffset? resetsAt = ReadResetAt(subscription, "currentPeriodEnd");
            bool active = ReadString(subscription, "status") == "active";
            WindowUsage? monthly = active && plan is { } known && remaining <= known.Cap
                ? new WindowUsage(Math.Clamp(1 - remaining / known.Cap, 0, 1), resetsAt) : null;
            string note = monthly is not null
                ? $"월간 크레딧 {remaining.ToString("0.##", CultureInfo.InvariantCulture)}/{plan!.Value.Cap.ToString("0.##", CultureInfo.InvariantCulture)} 남음 · 총량은 공식 CLI 플랜 기준"
                : $"월간 크레딧 {remaining.ToString("0.##", CultureInfo.InvariantCulture)} 남음 · 월간 총량을 확인할 수 없어 사용률을 표시하지 않습니다";
            return new ProviderSnapshot(
                Primary: monthly,
                Secondary: fiveHour,
                Plan: plan?.Name,
                PrimaryLabel: "월간",
                SecondaryLabel: "5시간",
                HistoryKey: $"commandcode-monthly-{accountHash}-{planId}",
                Note: note + weeklyNote);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Command Code 사용량 응답 형식이 바뀌어 값을 읽지 못했습니다.");
        }
    }

    private static string? ReadString(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static (string Name, double Cap)? PlanInfo(string? planId)
    {
        // Verified against the published official command-code 1.82.1 CLI on 2026-10-15.
        // Match longer versioned IDs first; unknown/custom plans never receive an invented quota.
        string normalized = planId?.ToLowerInvariant().Replace('_', '-') ?? string.Empty;
        (string Id, string Name, double Cap)[] plans =
        [
            ("individual-pro-v1", "Pro", 80), ("individual-go-v1", "Go", 10),
            ("individual-provider", "Provider", 15), ("individual-goat", "GOAT", 70),
            ("individual-ultra", "Ultra", 300), ("individual-max", "Max", 150),
            ("individual-pro", "Pro", 30), ("individual-go", "Go", 10), ("teams-pro", "Teams Pro", 40),
        ];
        foreach (var plan in plans)
            if (normalized == plan.Id || normalized.StartsWith(plan.Id + "-", StringComparison.Ordinal)) return (plan.Name, plan.Cap);
        return null;
    }

    private static WindowUsage? ReadWindow(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out JsonElement window) || window.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("used", out JsonElement usedElement) ||
            usedElement.ValueKind != JsonValueKind.Number || !usedElement.TryGetDouble(out double used) || !double.IsFinite(used) ||
            !window.TryGetProperty("cap", out JsonElement capElement) ||
            capElement.ValueKind != JsonValueKind.Number || !capElement.TryGetDouble(out double cap) || !double.IsFinite(cap))
        {
            throw new JsonException($"The {name} window is invalid.");
        }

        if (cap <= 0)
        {
            return null;
        }

        return new WindowUsage(Math.Clamp(used / cap, 0, 1), ReadResetAt(window));
    }

    private static DateTimeOffset? ReadResetAt(JsonElement window, string field = "resetAt")
    {
        if (!window.TryGetProperty(field, out JsonElement reset))
        {
            return null;
        }

        try
        {
            if (reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out long milliseconds) && milliseconds > 0)
            {
                return milliseconds < 1_000_000_000_000
                    ? DateTimeOffset.FromUnixTimeSeconds(milliseconds) : DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            }

            if (reset.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            {
                return parsed;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        return null;
    }

    private static JsonElement UnwrapData(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("data", out JsonElement data) &&
        data.ValueKind == JsonValueKind.Object
            ? data
            : root;

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }
}
