using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Quotra.WinUI.Services;

/// <summary>
/// Command Code 계정의 주간·5시간 사용량.
/// Command Code CLI 로그인 파일(또는 COMMAND_CODE_API_KEY)을 읽고,
/// CLI /usage가 사용하는 계정 사용량 경로에서 한도 정보를 가져온다.
/// </summary>
[UsageSource("commandcode")]
public sealed class CommandCodeUsageSource : IUsageSource, IUsageDiagnostics
{
    private const string DefaultApiBaseUrl = "https://api.commandcode.ai";
    private static readonly HttpClient Http = CreateHttpClient();
    private string? _authPath;
    private string _apiBaseUrl = DefaultApiBaseUrl;

    public string DisplayName => "Command Code 사용량";

    public string ShortName => "Command Code";

    public static string DefaultAuthPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".commandcode",
        "auth.json");

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        string apiKey = await ReadApiKeyAsync(cancellationToken).ConfigureAwait(false);

        // Command Code CLI resolves the active account namespace before requesting its usage.
        using JsonDocument identity = await GetJsonAsync("/alpha/whoami?limits=1", apiKey, cancellationToken).ConfigureAwait(false);
        string? orgId = ReadOrgId(identity.RootElement);
        string query = string.IsNullOrWhiteSpace(orgId) ? string.Empty : $"?orgId={Uri.EscapeDataString(orgId)}";

        using JsonDocument credits = await GetJsonAsync($"/alpha/billing/credits{query}", apiKey, cancellationToken).ConfigureAwait(false);
        return ParseUsage(credits.RootElement);
    }

    public void ApplyDiagnostics(string? url, string? file)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            _apiBaseUrl = url.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(file))
        {
            _authPath = file;
        }
    }

    private async Task<string> ReadApiKeyAsync(CancellationToken cancellationToken)
    {
        if (_authPath is null && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COMMAND_CODE_API_KEY")))
        {
            return Environment.GetEnvironmentVariable("COMMAND_CODE_API_KEY")!.Trim();
        }

        string authPath = _authPath ?? DefaultAuthPath;
        if (!File.Exists(authPath))
        {
            throw new InvalidOperationException(
                $"Command Code 로그인이 없습니다. Command Code에서 로그인하거나 {authPath}에 로그인 정보를 만들어 주세요.");
        }

        try
        {
            string json = await File.ReadAllTextAsync(authPath, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("apiKey", out JsonElement apiKey) &&
                apiKey.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(apiKey.GetString()))
            {
                return apiKey.GetString()!.Trim();
            }

            throw new InvalidOperationException(
                "Command Code 로그인 파일에서 API 키를 찾지 못했습니다. Command Code에서 다시 로그인하세요.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Command Code 로그인 파일을 읽지 못했습니다. Command Code에서 다시 로그인하세요.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("Command Code 로그인 파일을 읽을 수 없습니다.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Command Code 로그인 파일에 접근할 수 없습니다.");
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_apiBaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("cli");

        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Command Code 인증이 만료되었거나 거부되었습니다. Command Code에서 다시 로그인하세요.");
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

    private static ProviderSnapshot ParseUsage(JsonElement root)
    {
        try
        {
            JsonElement payload = UnwrapData(root);
            if (!payload.TryGetProperty("windowLimits", out JsonElement limits) ||
                limits.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Missing windowLimits.");
            }

            WindowUsage? weekly = ReadWindow(limits, "weekly");
            WindowUsage? fiveHour = ReadWindow(limits, "fiveHour");

            if (weekly is null && fiveHour is null &&
                (!limits.TryGetProperty("limited", out JsonElement limited) || limited.ValueKind != JsonValueKind.False))
            {
                throw new JsonException("No recognized usage windows.");
            }

            return new ProviderSnapshot(
                Primary: weekly,
                Secondary: fiveHour,
                Note: "Command Code 계정의 주간·5시간 사용 한도");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Command Code 사용량 응답 형식이 바뀌어 값을 읽지 못했습니다.");
        }
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

    private static DateTimeOffset? ReadResetAt(JsonElement window)
    {
        if (!window.TryGetProperty("resetAt", out JsonElement reset))
        {
            return null;
        }

        try
        {
            if (reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out long milliseconds) && milliseconds > 0)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
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
