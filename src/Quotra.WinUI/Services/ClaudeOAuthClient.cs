using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quotra.WinUI.Services;

/// <summary>Refresh the existing Claude Code login, including rotated refresh tokens.</summary>
internal sealed class ClaudeOAuthClient(HttpClient http, Action<string>? log = null, Func<DateTimeOffset>? utcNow = null)
{
    // Public OAuth client identifier, not a secret. Never send credentials to a diagnostic URL.
    internal const string TokenEndpoint = "https://platform.claude.com/v1/oauth/token";
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Failure> _failures = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset Now => utcNow?.Invoke() ?? DateTimeOffset.UtcNow;

    internal async Task<ClaudeOAuthCredential> GetAsync(string path, CancellationToken cancellationToken,
        string? rejectedAccessToken = null, bool allowRefresh = true)
    {
        path = Path.GetFullPath(path);
        SemaphoreSlim gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CredentialFile current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (!allowRefresh)
            {
                if (!NeedsRefresh(current, rejectedAccessToken)) return current.Credential;
                throw new InvalidOperationException("Claude 진단 모드에서는 로그인 자동 갱신을 하지 않습니다. 유효한 테스트 로그인을 사용하세요.");
            }
            if (!NeedsRefresh(current, rejectedAccessToken) && !File.Exists(RecoveryPath(path))) return current.Credential;

            // Match Claude Code's current and legacy mkdir locks; never remove another owner's lock.
            using var refreshLock = await RefreshLock.AcquireAsync(path, cancellationToken).ConfigureAwait(false);
            current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (File.Exists(RecoveryPath(path)))
            {
                RecoverPending(path, current);
                current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            }
            if (!NeedsRefresh(current, rejectedAccessToken)) return current.Credential;
            if (current.RefreshToken is null)
                throw new InvalidOperationException("Claude 로그인 갱신 토큰이 없습니다. Claude Code 또는 설정에서 다시 로그인하세요.");
            if (_failures.TryGetValue(path, out Failure? failure) && failure.Fingerprint == current.Fingerprint && Now < failure.Until)
                throw new InvalidOperationException(failure.Message);

            string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".quotra-oauth-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // Check write access before rotating anything. A temp file in the same directory allows
                // an atomic replace; preserve the original ACL before putting any secrets in the temp file.
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Claude 로그인 파일이 링크입니다. Claude Code에서 로그인을 갱신한 뒤 새로고침하세요.");
                using (var permissionCheck = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) { }
                using (var empty = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                new FileInfo(temporary).SetAccessControl(new FileInfo(path).GetAccessControl());

                log?.Invoke("Claude OAuth: 기존 로그인 자동 갱신 시도");
                using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    grant_type = "refresh_token", refresh_token = current.RefreshToken, client_id = ClientId,
                }), Encoding.UTF8, "application/json");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using HttpResponseMessage response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                // Once rotation succeeds, save the new chain even if the caller cancels the usage query.
                string body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    // Another application may have renewed/replaced the account despite our lock.
                    CredentialFile latest = await ReadAsync(path, CancellationToken.None).ConfigureAwait(false);
                    if (latest.Fingerprint != current.Fingerprint && !NeedsRefresh(latest, rejectedAccessToken))
                        return latest.Credential;
                    throw RecordFailure(path, current, response, body);
                }

                JsonObject updated = ParseRefreshed(body, current);
                // A rotated refresh token cannot be rolled back. Persist a protected recovery record
                // before replacing the shared file, and recover it before any subsequent refresh.
                var pending = new JsonObject { ["originalFingerprint"] = current.Fingerprint, ["oauth"] = updated.DeepClone() };
                WriteProtectedJson(temporary, path, pending);
                File.Move(temporary, RecoveryPath(path));
                CredentialFile beforeSave = await ReadAsync(path, CancellationToken.None).ConfigureAwait(false);
                if (beforeSave.Fingerprint != current.Fingerprint)
                {
                    // Never overwrite a login that the user changed while the request was in flight.
                    File.Delete(RecoveryPath(path));
                    if (!NeedsRefresh(beforeSave, rejectedAccessToken)) return beforeSave.Credential;
                    throw new InvalidOperationException("갱신 중 Claude 로그인이 변경되었습니다. 다시 새로고침하세요.");
                }
                // Keep unrelated properties changed by a concurrent settings writer.
                MergeOAuth(beforeSave.Root, updated);
                WriteProtectedJson(temporary, path, beforeSave.Root);
                File.Replace(temporary, path, null);
                File.Delete(RecoveryPath(path));
                _failures.TryRemove(path, out _);
                log?.Invoke("Claude OAuth: 자동 갱신 및 로그인 파일 저장 완료");
                return ParseFile(beforeSave.Root).Credential.WithRefreshed();
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("Claude 로그인 갱신 응답 형식이 올바르지 않습니다. 기존 로그인 파일은 유지했습니다.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Claude 로그인 파일을 갱신할 권한이 없습니다. 현재 Windows 사용자로 실행하세요. 저장 대기 중인 토큰은 다음 조회에서 복구합니다.");
            }
            catch (IOException)
            {
                throw new InvalidOperationException("Claude 로그인 파일을 갱신할 수 없습니다. 다른 앱의 파일 잠금과 저장 공간을 확인하세요. 저장 대기 중인 토큰은 다음 조회에서 복구합니다.");
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        finally { gate.Release(); }
    }

    private bool NeedsRefresh(CredentialFile file, string? rejected) =>
        rejected == file.Credential.AccessToken || file.ExpiresAt is { } expiry && expiry <= Now.AddSeconds(60);

    private static string RecoveryPath(string path) => path + ".quotra-pending";

    private void RecoverPending(string path, CredentialFile current)
    {
        string recovery = RecoveryPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".quotra-oauth-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            JsonObject pending = JsonNode.Parse(File.ReadAllText(recovery)) as JsonObject ?? throw new JsonException();
            if (String(pending["originalFingerprint"]) != current.Fingerprint)
            {
                // A different login (or an already completed save) wins over an old recovery file.
                File.Delete(recovery);
                return;
            }
            JsonObject oauth = pending["oauth"] as JsonObject ?? throw new JsonException();
            MergeOAuth(current.Root, oauth);
            _ = ParseFile(current.Root); // Validate before touching the original.
            WriteProtectedJson(temporary, path, current.Root);
            File.Replace(temporary, path, null);
            File.Delete(recovery);
            log?.Invoke("Claude OAuth: 저장 대기 중인 로그인 복구 완료");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException("Claude 로그인 갱신 결과를 복구할 수 없습니다. 로그인 폴더의 파일 잠금과 쓰기 권한을 확인하세요.");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void MergeOAuth(JsonObject root, JsonObject updated)
    {
        var target = (JsonObject)root["claudeAiOauth"]!;
        foreach (string key in new[] { "accessToken", "refreshToken", "expiresAt", "scopes" })
            if (updated.ContainsKey(key)) target[key] = updated[key]?.DeepClone();
    }

    private static void WriteProtectedJson(string temporary, string original, JsonObject data)
    {
        // Caller owns this randomly named same-directory temporary file.
        using (var empty = new FileStream(temporary, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None)) { }
        new FileInfo(temporary).SetAccessControl(new FileInfo(original).GetAccessControl());
        byte[] bytes = Encoding.UTF8.GetBytes(data.ToJsonString());
        try
        {
            using var output = new FileStream(temporary, FileMode.Truncate, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task<CredentialFile> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return ParseFile(JsonNode.Parse(json) as JsonObject ?? throw new JsonException());
        }
        catch (FileNotFoundException) { throw new InvalidOperationException("Claude Code 로그인 파일이 없습니다. 설정에서 Claude 로그인을 사용하세요."); }
        catch (DirectoryNotFoundException) { throw new InvalidOperationException("Claude Code 로그인 폴더가 없습니다. CLAUDE_CONFIG_DIR 경로를 확인하세요."); }
        catch (JsonException) { throw new InvalidOperationException("Claude Code 로그인 파일 형식이 올바르지 않습니다. Claude Code에서 다시 로그인하세요."); }
        catch (IOException) { throw new InvalidOperationException("Claude Code 로그인 파일을 읽을 수 없습니다."); }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException("Claude Code 로그인 파일에 접근할 권한이 없습니다."); }
    }

    private static CredentialFile ParseFile(JsonObject root)
    {
        if (root["claudeAiOauth"] is not JsonObject oauth || String(oauth["accessToken"]) is not { } access)
            throw new InvalidOperationException("Claude Code 구독 OAuth 토큰이 없습니다. 설정에서 Claude 로그인을 사용하세요.");
        ValidateToken(access);
        string? refresh = String(oauth["refreshToken"]);
        if (refresh is not null) ValidateToken(refresh);
        DateTimeOffset? expiry = null;
        if (oauth["expiresAt"] is { } expires)
        {
            if (expires is not JsonValue value || !value.TryGetValue<long>(out long milliseconds) || milliseconds <= 0
                || milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()) throw new JsonException();
            expiry = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(access + "\n" + refresh + "\n" + expiry?.ToUnixTimeMilliseconds())));
        return new CredentialFile(root, new ClaudeOAuthCredential(access, String(oauth["subscriptionType"])), refresh, expiry, fingerprint);
    }

    private JsonObject ParseRefreshed(string body, CredentialFile current)
    {
        JsonObject response = JsonNode.Parse(body) as JsonObject ?? throw new JsonException();
        string access = String(response["access_token"]) ?? throw new JsonException();
        ValidateToken(access);
        string refresh = response["refresh_token"] is null ? current.RefreshToken! : String(response["refresh_token"]) ?? throw new JsonException();
        ValidateToken(refresh);
        if (response["expires_in"] is not JsonValue secondsValue || !secondsValue.TryGetValue<long>(out long seconds)
            || seconds <= 0 || seconds > 31_536_000) throw new JsonException();
        if (response["token_type"] is not null && !string.Equals(String(response["token_type"]), "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new JsonException();
        var updated = new JsonObject();
        updated["accessToken"] = access;
        updated["refreshToken"] = refresh;
        updated["expiresAt"] = Now.AddSeconds(seconds).ToUnixTimeMilliseconds();
        if (response["scope"] is not null)
        {
            string scope = String(response["scope"]) ?? throw new JsonException();
            updated["scopes"] = new JsonArray(scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        }
        return updated;
    }

    private InvalidOperationException RecordFailure(string path, CredentialFile current, HttpResponseMessage response, string body)
    {
        int status = (int)response.StatusCode;
        string? error = null;
        try { error = String((JsonNode.Parse(body) as JsonObject)?["error"]); }
        catch (JsonException) { }
        bool rejected = status == 401 || status == 400 && error == "invalid_grant";
        string message = rejected
            ? $"Claude 자동 갱신이 거부되었습니다(HTTP {status}{(error == "invalid_grant" ? ", invalid_grant" : "")}). 갱신 토큰이 만료되었거나 취소되어 다시 로그인해야 합니다."
            : status == 429 ? "Claude 로그인 자동 갱신이 일시 제한되었습니다(HTTP 429). 잠시 후 자동으로 재시도합니다."
            : $"Claude 로그인 자동 갱신에 실패했습니다(HTTP {status}). 잠시 후 다시 시도합니다.";
        TimeSpan wait = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is { } date ? date - Now : TimeSpan.FromMinutes(1));
        // Respect long Retry-After values; short/negative values still back off for a minute.
        DateTimeOffset until = rejected ? DateTimeOffset.MaxValue : Now.AddSeconds(Math.Clamp(wait.TotalSeconds, 60, 86400));
        _failures[path] = new Failure(current.Fingerprint, until, message);
        return new InvalidOperationException(message);
    }

    private static string? String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out string? text)
        && !string.IsNullOrWhiteSpace(text) ? text : null;
    private static void ValidateToken(string token)
    {
        if (token.Length > 16_384 || token.Any(c => c <= ' ' || c >= 127)) throw new JsonException();
    }

    private sealed record Failure(string Fingerprint, DateTimeOffset Until, string Message);
    private sealed record CredentialFile(JsonObject Root, ClaudeOAuthCredential Credential, string? RefreshToken, DateTimeOffset? ExpiresAt, string Fingerprint);

    private sealed class RefreshLock : IDisposable
    {
        private readonly List<string> _owned = [];

        internal static async Task<RefreshLock> AcquireAsync(string credentials, CancellationToken cancellationToken)
        {
            var result = new RefreshLock();
            var timer = Stopwatch.StartNew();
            try
            {
                foreach (string path in new[] { Path.Combine(Path.GetDirectoryName(credentials)!, ".oauth_refresh.lock"), credentials + ".lock" })
                {
                    while (!CreateDirectory(path, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error is not (80 or 183))
                            throw new InvalidOperationException("Claude 로그인 갱신 잠금을 만들 수 없습니다. 로그인 폴더의 쓰기 권한을 확인하세요.");
                        if (timer.Elapsed >= TimeSpan.FromSeconds(3))
                            throw new InvalidOperationException("다른 Claude 프로세스가 로그인을 갱신 중이거나 잠금이 남아 있습니다. 잠시 후 새로고침하세요.");
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }
                    result._owned.Add(path);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        public void Dispose()
        {
            foreach (string path in _owned.AsEnumerable().Reverse())
            {
                try { Directory.Delete(path, recursive: false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            _owned.Clear();
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectory(string path, IntPtr securityAttributes);
    }
}

/// <summary>Do not use a record: its generated ToString would expose credentials in diagnostics.</summary>
internal sealed class ClaudeOAuthCredential(string accessToken, string? plan, bool refreshed = false)
{
    internal string AccessToken { get; } = accessToken;
    internal string? Plan { get; } = plan;
    internal bool Refreshed { get; } = refreshed;
    internal ClaudeOAuthCredential WithRefreshed() => new(AccessToken, Plan, true);
}
