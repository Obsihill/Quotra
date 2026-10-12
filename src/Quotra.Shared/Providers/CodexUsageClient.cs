using System.Net;
using System.Text.Json;

namespace Quotra.Providers;

/// <summary>Codex 조회 실패의 종류.</summary>
public enum CodexFetchStatus
{
    /// <summary>값을 받았다.</summary>
    Ok,

    /// <summary><c>auth.json</c> 에 ChatGPT 토큰이 없다(API 키 로그인이거나 로그인 전).</summary>
    NoCredential,

    /// <summary>토큰이 거부됐다(만료 가능).</summary>
    Expired,

    /// <summary>요청이 제한됐다(429).</summary>
    RateLimited,

    /// <summary>네트워크 오류/시간 초과.</summary>
    NetworkError,

    /// <summary>200 인데 해석할 수 없다(스키마 변경 가능).</summary>
    SchemaChanged,
}

/// <summary>조회 결과. 실패해도 <see cref="Detail"/> 에 사람이 읽을 사유가 담긴다.</summary>
public sealed record CodexFetchResult(
    CodexFetchStatus Status,
    CodexUsage? Usage,
    string Detail,
    string Endpoint = "",
    string? RawJson = null)
{
    public bool Ok => Status == CodexFetchStatus.Ok && Usage is not null;

    public static CodexFetchResult Fail(CodexFetchStatus status, string detail) =>
        new(status, null, detail);
}

/// <summary>
/// ChatGPT 플랜(Codex) 사용 한도를 읽어온다.
///
/// 공식 API 가 없어 Codex 클라이언트가 쓰는 엔드포인트를 그대로 쓴다.
/// 토큰은 <c>%USERPROFILE%\.codex\auth.json</c> 에서 <b>읽기만</b> 하고 만료 시 갱신하지 않는다
/// (갱신은 Codex 앱이 담당). 어떤 경로로도 토큰을 로그·오류 메시지에 넣지 않는다.
/// </summary>
public static class CodexUsageClient
{
    private const string UserAgent = "codex_cli_rs/0.44.0 (Windows 10; x86_64)";

    /// <summary>시도할 경로(위에서부터). 실측: /api/codex/usage 는 403, /backend-api/wham/usage 가 200.</summary>
    public static readonly string[] CandidatePaths =
    [
        "/api/codex/usage",
        "/backend-api/wham/usage",
    ];

    /// <summary>Codex 자격증명 파일 경로.</summary>
    public static string AuthPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex",
        "auth.json");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>사용량을 조회한다. 실패는 예외가 아니라 <see cref="CodexFetchResult.Status"/> 로 알린다.</summary>
    /// <param name="host">조회 주소(기본 https://chatgpt.com).</param>
    /// <param name="authPath">자격증명 파일(기본 <see cref="AuthPath"/>, 검증용으로 바꿀 수 있다).</param>
    public static async Task<CodexFetchResult> FetchAsync(
        string host = "https://chatgpt.com",
        string? authPath = null,
        CancellationToken cancellationToken = default)
    {
        string credentialPath = authPath ?? AuthPath;
        (string Token, string AccountId, string AuthMode)? credential = ReadCredential(credentialPath);
        if (credential is null)
        {
            return CodexFetchResult.Fail(
                CodexFetchStatus.NoCredential,
                $"{credentialPath} 에서 ChatGPT 플랜 토큰을 찾지 못했습니다(API 키 로그인이거나 로그인 전).");
        }

        (string token, string accountId, string authMode) = credential.Value;
        string lastDetail = "알려진 경로에서 응답을 받지 못했습니다.";
        CodexFetchResult? authFailure = null;
        CodexFetchResult? networkFailure = null;

        foreach (string path in CandidatePaths)
        {
            string url = host + path;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

                if (!string.IsNullOrWhiteSpace(accountId))
                {
                    request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
                }

                using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                int code = (int)response.StatusCode;

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    lastDetail = $"{path} → 404(경로 없음)";
                    continue;
                }

                // 인증 실패/요청 제한은 "다른 경로가 맞을 수도 있으므로" 즉시 끝내지 않고 기억만 해둔다.
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    authFailure ??= new CodexFetchResult(
                        CodexFetchStatus.Expired,
                        null,
                        $"{path} → {code} (auth_mode={authMode})",
                        url);
                    continue;
                }

                if (code == 429)
                {
                    authFailure ??= new CodexFetchResult(
                        CodexFetchStatus.RateLimited,
                        null,
                        $"{path} → 429",
                        url);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    lastDetail = $"{path} → {code}";
                    continue;
                }

                if (CodexUsageParser.TryParse(body, out CodexUsage usage, out string? parseError))
                {
                    return new CodexFetchResult(CodexFetchStatus.Ok, usage, $"{path} → 200", url, body);
                }

                // 200 인데 해석 불가 = 가장 정보가 많은 실패(원문을 그대로 보관해 파서를 고칠 수 있게 한다).
                return new CodexFetchResult(
                    CodexFetchStatus.SchemaChanged,
                    null,
                    $"{path} → 200 이지만 해석하지 못했습니다: {parseError}",
                    url,
                    body);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                networkFailure ??= new CodexFetchResult(
                    CodexFetchStatus.NetworkError, null, $"{path} → 시간 초과(10초)", url);
            }
            catch (HttpRequestException ex)
            {
                networkFailure ??= new CodexFetchResult(
                    CodexFetchStatus.NetworkError, null, $"{path} → {ex.Message}", url);
            }
        }

        return authFailure
            ?? networkFailure
            ?? new CodexFetchResult(CodexFetchStatus.SchemaChanged, null, lastDetail);
    }

    /// <summary>네트워크 없이 원시 응답만 해석한다(<c>--codex-parse</c>).</summary>
    public static CodexFetchResult ParseRaw(string json, string endpoint = "(local)")
    {
        return CodexUsageParser.TryParse(json, out CodexUsage usage, out string? error)
            ? new CodexFetchResult(CodexFetchStatus.Ok, usage, endpoint, endpoint, json)
            : new CodexFetchResult(CodexFetchStatus.SchemaChanged, null, error ?? "해석 실패", endpoint, json);
    }

    /// <summary><c>auth.json</c> 에서 토큰/계정/모드를 읽는다. 값은 절대 로그로 내보내지 않는다.</summary>
    private static (string Token, string AccountId, string AuthMode)? ReadCredential(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;

            string mode = root.TryGetProperty("auth_mode", out JsonElement modeElement) &&
                          modeElement.ValueKind == JsonValueKind.String
                ? modeElement.GetString() ?? string.Empty
                : string.Empty;

            if (root.TryGetProperty("tokens", out JsonElement tokens) && tokens.ValueKind == JsonValueKind.Object)
            {
                string token = GetString(tokens, "access_token");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return (token, GetString(tokens, "account_id"), mode);
                }
            }

            // API 키 로그인 상태면 구독 한도를 알 수 없다.
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
