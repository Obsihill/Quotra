using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Quotra.WinUI.Services;

/// <summary>
/// Claude 구독 사용량 — Claude Code 의 OAuth 로그인을 읽어 5시간·주간 한도를 조회한다.
/// 자격증명은 매번 읽기만 하며, 토큰 갱신이나 원문 로깅은 하지 않는다.
/// </summary>
[UsageSource("claude")]
public sealed class ClaudeUsageSource : IUsageSource
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthBeta = "oauth-2025-04-20";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string? _authPath;

    public ClaudeUsageSource(string? authPath = null) => _authPath = authPath;

    public string DisplayName => "Claude (Anthropic 구독 한도)";

    public string ShortName => "Claude";

    public static string DefaultAuthPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        ".credentials.json");

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        string authPath = _authPath ?? DefaultAuthPath;
        ClaudeCredential credential = await ReadCredentialAsync(authPath, cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBeta);

        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Claude Code 로그인이 만료되었거나 거부되었습니다. Claude Code를 열어 계정 로그인을 갱신하세요.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("Claude 사용량 조회가 거부되었습니다(HTTP 403). Claude Code 로그인과 Pro/Max 구독 상태를 확인하세요.");
        }

        if ((int)response.StatusCode == 429)
        {
            throw new InvalidOperationException("Claude 사용량 조회가 일시적으로 제한되었습니다. 다음 자동 갱신 때 다시 시도합니다.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Claude 사용량 조회에 실패했습니다(HTTP {(int)response.StatusCode}).");
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return ParseUsage(json, credential.Plan);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Claude 사용량 응답 형식이 바뀌어 값을 읽지 못했습니다.");
        }
    }

    private static async Task<ClaudeCredential> ReadCredentialAsync(string authPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(authPath))
        {
            throw new InvalidOperationException("Claude Code OAuth 로그인이 없습니다. Claude Code에서 Claude 계정으로 로그인하세요.");
        }

        try
        {
            string json = await File.ReadAllTextAsync(authPath, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("claudeAiOauth", out JsonElement oauth) ||
                oauth.ValueKind != JsonValueKind.Object ||
                !oauth.TryGetProperty("accessToken", out JsonElement tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(tokenElement.GetString()))
            {
                throw new InvalidOperationException("Claude Code OAuth 토큰을 찾지 못했습니다. Claude Code 로그인을 확인하세요.");
            }

            if (oauth.TryGetProperty("expiresAt", out JsonElement expiresAt) &&
                expiresAt.ValueKind == JsonValueKind.Number &&
                expiresAt.TryGetInt64(out long expiresAtMilliseconds) &&
                expiresAtMilliseconds > 0 &&
                expiresAtMilliseconds <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                throw new InvalidOperationException("Claude Code OAuth 로그인이 만료되었습니다. Claude Code를 열어 로그인 정보를 갱신하세요.");
            }

            string? subscriptionType = oauth.TryGetProperty("subscriptionType", out JsonElement subscription) &&
                                       subscription.ValueKind == JsonValueKind.String
                ? subscription.GetString()
                : null;

            return new ClaudeCredential(tokenElement.GetString()!, PlanLabel(subscriptionType));
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Claude Code OAuth 파일을 읽지 못했습니다. Claude Code 로그인을 확인하세요.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("Claude Code OAuth 파일을 읽을 수 없습니다.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Claude Code OAuth 파일에 접근할 수 없습니다.");
        }
    }

    private static ProviderSnapshot ParseUsage(string json, string? plan)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected an object.");
        }

        WindowUsage? weekly = ReadWindow(root, "seven_day");
        if (weekly is null)
        {
            throw new JsonException("The weekly usage window is missing.");
        }

        return new ProviderSnapshot(
            Primary: weekly,
            Secondary: ReadWindow(root, "five_hour"),
            Plan: plan);
    }

    private static WindowUsage? ReadWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement window) || window.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("utilization", out JsonElement utilization) ||
            utilization.ValueKind != JsonValueKind.Number ||
            !utilization.TryGetDouble(out double percent) ||
            !double.IsFinite(percent) || percent < 0)
        {
            throw new JsonException($"The {name} utilization is invalid.");
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resets_at", out JsonElement reset) &&
            reset.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                reset.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsedReset))
        {
            resetsAt = parsedReset;
        }

        // Claude OAuth usage reports utilization as a percentage from 0 to 100.
        return new WindowUsage(Math.Clamp(percent / 100.0, 0, 1), resetsAt);
    }

    private static string? PlanLabel(string? plan) => plan?.ToLowerInvariant() switch
    {
        "pro" => "Claude Pro",
        "max" => "Claude Max",
        "free" => "Claude Free",
        _ => null,
    };

    private sealed record ClaudeCredential(string AccessToken, string? Plan);
}
