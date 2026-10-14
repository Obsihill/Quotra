using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quotra.Providers;

namespace Quotra.WinUI.Services;

/// <summary>Offline regression checks for Claude OAuth rotation and usage retry behavior.</summary>
internal static class ClaudeOAuthSelfTest
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    private const string AccessA = "fake-access-a";
    private const string AccessB = "fake-access-b";
    private const string RefreshA = "fake-refresh-a";
    private const string RefreshB = "fake-refresh-b";
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string UsageJson = """
        {"seven_day":{"utilization":42},"five_hour":{"utilization":12}}
        """;

    /// <summary>Runs off the caller's synchronization context because this is also called from UI diagnostics.</summary>
    internal static bool Run() => Task.Run(RunAsync).GetAwaiter().GetResult();

    private static async Task<bool> RunAsync()
    {
        using var temp = new TemporaryDirectory();
        (string Name, Func<string, Task> Run)[] cases =
        [
            ("unexpired credential is read-only", UnexpiredCredentialAsync),
            ("near-expiry rotation preserves data and request shape", RotationAndPreservationAsync),
            ("optional response fields retain prior values", OptionalResponseFieldsAsync),
            ("missing and malformed expiry handling", ExpiryValidationAsync),
            ("invalid refresh responses leave original file", InvalidResponsesAsync),
            ("allowRefresh false fails closed for expired credentials", RefreshDisabledAsync),
            ("invalid_grant is redacted and cooled down", InvalidGrantAsync),
            ("429 honors the minimum retry delay", RateLimitAsync),
            ("parallel clients perform one rotation", ParallelRotationAsync),
            ("a newer credential wins over a rejected token", RejectedTokenChangedAsync),
            ("cancellation leaves original credentials intact", CancellationAsync),
            ("an existing refresh lock is not stolen", ExistingLockAsync),
            ("failed replace recovers rotated tokens without another POST", PendingRecoveryAsync),
            ("usage 401 refreshes once and retries successfully", UsageUnauthorizedAsync),
            ("401 after proactive refresh does not refresh twice", UsageUnauthorizedAfterRefreshAsync),
            ("usage 403 and 429 do not refresh", UsageNonRetryStatusesAsync),
            ("diagnostic URL requires a test file and never refreshes", DiagnosticUrlAsync),
            ("weekly usage and five-hour fallback parse", UsageParserAsync),
        ];

        int passed = 0;
        foreach ((string name, Func<string, Task> run) in cases)
        {
            try
            {
                await run(temp.CreateCaseDirectory()).ConfigureAwait(false);
                passed++;
                Log.Write($"Claude OAuth self-test PASS: {name}");
            }
            catch (Exception ex)
            {
                // Exception messages from HTTP and file APIs are intentionally omitted from logs.
                Log.Write($"Claude OAuth self-test FAIL: {name} ({ex.GetType().Name})");
            }
        }

        Log.Write($"Claude OAuth self-test total: {passed}/{cases.Length} passed");
        return passed == cases.Length;
    }

    private static async Task UnexpiredCredentialAsync(string directory)
    {
        string path = CredentialPath(directory);
        WriteCredential(path, Now.AddHours(2).ToUnixTimeMilliseconds());
        byte[] before = File.ReadAllBytes(path);
        int requests = 0;
        using var http = CreateHttp(new FakeHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected HTTP request.");
        }));
        var client = NewClient(http);

        ClaudeOAuthCredential credential = await client.GetAsync(path, CancellationToken.None).ConfigureAwait(false);

        Ensure(credential.AccessToken == AccessA && credential.Plan == "max" && !credential.Refreshed,
            "Unexpired credential values changed.");
        Ensure(!credential.ToString()!.Contains(AccessA, StringComparison.Ordinal), "Credential ToString exposed its access token.");
        Ensure(requests == 0 && before.SequenceEqual(File.ReadAllBytes(path)), "Unexpired credential caused I/O or a write.");
    }

    private static async Task RotationAndPreservationAsync(string directory)
    {
        string path = CredentialPath(directory);
        WriteCredential(path, Now.AddSeconds(30).ToUnixTimeMilliseconds());
        var requests = new List<CapturedRequest>();
        var logs = new List<string>();
        using var http = CreateHttp(new FakeHandler(async (request, cancellationToken) =>
        {
            requests.Add(await CaptureRequestAsync(request, cancellationToken).ConfigureAwait(false));
            return JsonResponse(HttpStatusCode.OK, RefreshBody(AccessB, RefreshB, includeScope: true));
        }));
        var client = NewClient(http, logs.Add);

        ClaudeOAuthCredential credential = await client.GetAsync(path, CancellationToken.None).ConfigureAwait(false);

        Ensure(credential.AccessToken == AccessB && credential.Plan == "max" && credential.Refreshed,
            "Rotated credential values are wrong.");
        Ensure(requests.Count == 1, "Expected exactly one token request.");
        AssertRefreshRequest(requests[0], RefreshA);
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = saved.RootElement;
        JsonElement oauth = root.GetProperty("claudeAiOauth");
        Ensure(root.GetProperty("rootMarker").GetString() == "keep-root", "Unrelated root property was lost.");
        Ensure(oauth.GetProperty("futureField").GetProperty("marker").GetString() == "keep-oauth",
            "Unrelated OAuth property was lost.");
        Ensure(oauth.GetProperty("accessToken").GetString() == AccessB
            && oauth.GetProperty("refreshToken").GetString() == RefreshB,
            "Rotated tokens were not saved.");
        Ensure(oauth.GetProperty("expiresAt").GetInt64() == Now.AddHours(1).ToUnixTimeMilliseconds(),
            "The refreshed expiry is incorrect.");
        string[] scopes = oauth.GetProperty("scopes").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Ensure(scopes.SequenceEqual(new[] { "user:inference", "user:profile" }), "Response scopes were not saved.");
        Ensure(!string.Join('\n', logs).Contains(AccessA, StringComparison.Ordinal)
            && !string.Join('\n', logs).Contains(RefreshA, StringComparison.Ordinal), "A token appeared in client logs.");
        Ensure(Directory.GetFiles(directory).SequenceEqual(new[] { path }), "Temporary refresh files were left behind.");
    }

    private static async Task OptionalResponseFieldsAsync(string directory)
    {
        string path = CredentialPath(directory);
        WriteCredential(path, Now.AddSeconds(20).ToUnixTimeMilliseconds());
        using var http = CreateHttp(new FakeHandler((_, _) =>
        {
            JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            JsonObject oauth = (JsonObject)root["claudeAiOauth"]!;
            oauth["scopes"] = new JsonArray(JsonValue.Create("user:inference"), JsonValue.Create("user:profile"), JsonValue.Create("user:concurrent"));
            File.WriteAllText(path, root.ToJsonString());
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"access_token":"fake-access-b","expires_in":3600}"""));
        }));

        ClaudeOAuthCredential credential = await NewClient(http).GetAsync(path, CancellationToken.None).ConfigureAwait(false);

        Ensure(credential.AccessToken == AccessB && credential.Refreshed, "Optional response fields prevented rotation.");
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement oauth = saved.RootElement.GetProperty("claudeAiOauth");
        Ensure(oauth.GetProperty("refreshToken").GetString() == RefreshA, "Missing response refresh_token did not fall back.");
        string[] scopes = oauth.GetProperty("scopes").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Ensure(scopes.SequenceEqual(new[] { "user:inference", "user:profile", "user:concurrent" }),
            "Missing response scope discarded newer concurrent scopes.");
    }

    private static async Task ExpiryValidationAsync(string directory)
    {
        string missingDirectory = Path.Combine(directory, "missing");
        Directory.CreateDirectory(missingDirectory);
        string missingPath = CredentialPath(missingDirectory);
        WriteCredential(missingPath, null, includeExpiresAt: false);
        byte[] missingBefore = File.ReadAllBytes(missingPath);
        int requests = 0;
        using var http = CreateHttp(new FakeHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected HTTP request.");
        }));
        ClaudeOAuthCredential unknownExpiry = await NewClient(http).GetAsync(missingPath, CancellationToken.None).ConfigureAwait(false);
        Ensure(unknownExpiry.AccessToken == AccessA && !unknownExpiry.Refreshed, "Missing expiry did not remain an unknown-expiry credential.");
        Ensure(missingBefore.SequenceEqual(File.ReadAllBytes(missingPath)), "Missing expiry caused a write.");

        foreach (object? invalidExpiry in new object?[] { "not-a-number", 0L, -1L })
        {
            string invalidDirectory = Path.Combine(directory, "invalid-" + requests + "-" + (invalidExpiry?.ToString() ?? "null").Replace('-', 'n'));
            Directory.CreateDirectory(invalidDirectory);
            string path = CredentialPath(invalidDirectory);
            WriteCredential(path, invalidExpiry);
            byte[] before = File.ReadAllBytes(path);
            Exception error = await CaptureFailureAsync(() => NewClient(http).GetAsync(path, CancellationToken.None)).ConfigureAwait(false);
            Ensure(error is InvalidOperationException, "Malformed expiry was not rejected.");
            Ensure(before.SequenceEqual(File.ReadAllBytes(path)), "Malformed expiry changed the credential file.");
        }

        Ensure(requests == 0, "Expiry validation made an HTTP request.");
    }

    private static async Task InvalidResponsesAsync(string directory)
    {
        string[] responses =
        [
            """{"refresh_token":"fake-refresh-b","expires_in":3600}""",
            """{"access_token":"","expires_in":3600}""",
            """{"access_token":"fake-access-b","expires_in":0}""",
            """{"access_token":"fake-access-b"}""",
        ];

        for (int index = 0; index < responses.Length; index++)
        {
            string caseDirectory = Path.Combine(directory, index.ToString());
            Directory.CreateDirectory(caseDirectory);
            string path = CredentialPath(caseDirectory);
            WriteCredential(path, Now.AddSeconds(10).ToUnixTimeMilliseconds());
            byte[] before = File.ReadAllBytes(path);
            int requests = 0;
            string responseBody = responses[index];
            using var http = CreateHttp(new FakeHandler((_, _) =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, responseBody));
            }));

            Exception error = await CaptureFailureAsync(() => NewClient(http).GetAsync(path, CancellationToken.None)).ConfigureAwait(false);

            Ensure(error is InvalidOperationException, "Malformed token response was not sanitized.");
            Ensure(requests == 1 && before.SequenceEqual(File.ReadAllBytes(path)), "Invalid response wrote credentials or retried.");
        }
    }

    private static async Task RefreshDisabledAsync(string directory)
    {
        string path = CredentialPath(directory);
        WriteCredential(path, Now.AddSeconds(-1).ToUnixTimeMilliseconds());
        byte[] before = File.ReadAllBytes(path);
        int requests = 0;
        using var http = CreateHttp(new FakeHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected HTTP request.");
        }));

        Exception error = await CaptureFailureAsync(() => NewClient(http).GetAsync(path, CancellationToken.None, allowRefresh: false)).ConfigureAwait(false);

        Ensure(error is InvalidOperationException && requests == 0, "Refresh-disabled expired credential did not fail closed.");
        Ensure(before.SequenceEqual(File.ReadAllBytes(path)), "Refresh-disabled credential was changed.");
    }

    private static async Task InvalidGrantAsync(string directory)
    {
        string path = CredentialPath(directory);
        WriteCredential(path, Now.AddSeconds(-1).ToUnixTimeMilliseconds());
        byte[] before = File.ReadAllBytes(path);
        var logs = new List<string>();
        int requests = 0;
        using var http = CreateHttp(new FakeHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(JsonResponse(HttpStatusCode.BadRequest,
                """{"error":"invalid_grant","error_description":"fake-refresh-a provider-private-error"}"""));
        }));
        var client = NewClient(http, logs.Add);

        Exception first = await CaptureFailureAsync(() => client.GetAsync(path, CancellationToken.None)).ConfigureAwait(false);
        Exception second = await CaptureFailureAsync(() => client.GetAsync(path, CancellationToken.None)).ConfigureAwait(false);

        Ensure(first is InvalidOperationException && second is InvalidOperationException && requests == 1,
            "Revoked credentials were retried or did not produce a sanitized error.");
        string diagnostics = first.Message + "\n" + second.Message + "\n" + string.Join('\n', logs);
        AssertNoSecrets(diagnostics);
        Ensure(!diagnostics.Contains("provider-private-error", StringComparison.Ordinal), "Provider error detail escaped sanitization.");
        Ensure(before.SequenceEqual(File.ReadAllBytes(path)), "Rejected refresh changed the credential file.");
    }

    private static async Task RateLimitAsync(string directory)
    {
        string path = CredentialPath(directory);
        WriteCredential(path, Now.AddSeconds(-1).ToUnixTimeMilliseconds());
        DateTimeOffset now = Now;
        int requests = 0;
        using var http = CreateHttp(new FakeHandler((_, _) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                var limited = JsonResponse(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}""");
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return Task.FromResult(limited);
            }
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, RefreshBody(AccessB, RefreshB)));
        }));
        var client = NewClient(http, utcNow: () => now);

        Exception first = await CaptureFailureAsync(() => client.GetAsync(path, CancellationToken.None)).ConfigureAwait(false);
        Ensure(!first.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
            && !first.Message.Contains("revoked", StringComparison.OrdinalIgnoreCase), "429 was reported as an authentication failure.");
        now = Now.AddSeconds(59);
        _ = await CaptureFailureAsync(() => client.GetAsync(path, CancellationToken.None)).ConfigureAwait(false);
        Ensure(requests == 1, "Retry-After shorter than 60 seconds was not clamped to the minimum.");
        now = Now.AddSeconds(61);
        ClaudeOAuthCredential credential = await client.GetAsync(path, CancellationToken.None).ConfigureAwait(false);
        Ensure(requests == 2 && credential.AccessToken == AccessB, "Rate limit did not retry after the minimum backoff.");
    }

    private static async Task ParallelRotationAsync(string directory)
    {
        await AssertParallelRotationAsync(Path.Combine(directory, "same-client"), shareClient: true).ConfigureAwait(false);
        await AssertParallelRotationAsync(Path.Combine(directory, "separate-clients"), shareClient: false).ConfigureAwait(false);
    }

    private static async Task AssertParallelRotationAsync(string directory, bool shareClient)
    {
        Directory.CreateDirectory(directory);
        string path = CredentialPath(directory);
        WriteCredential(path, Now.AddSeconds(30).ToUnixTimeMilliseconds());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        using var http = CreateHttp(new FakeHandler(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return JsonResponse(HttpStatusCode.OK, RefreshBody(AccessB, RefreshB));
        }));
        var firstClient = NewClient(http);
        ClaudeOAuthClient secondClient = shareClient ? firstClient : NewClient(http);
        Task<ClaudeOAuthCredential> first = firstClient.GetAsync(path, CancellationToken.None, AccessA);
        Task<ClaudeOAuthCredential>? second = null;
        ClaudeOAuthCredential[] results;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            second = secondClient.GetAsync(path, CancellationToken.None, AccessA);
            await Task.Delay(150).ConfigureAwait(false);
            release.TrySetResult();
            results = await Task.WhenAll(first, second!).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            release.TrySetResult();
        }

        Ensure(requests == 1, "Parallel callers rotated more than once.");
        Ensure(results.All(result => result.AccessToken == AccessB), "Parallel callers did not observe the latest token.");
    }

    private static async Task RejectedTokenChangedAsync(string directory)
    {
        string path = CredentialPath(directory);
        WriteCredential(path, Now.AddHours(2).ToUnixTimeMilliseconds(), AccessB, RefreshB);
        byte[] before = File.ReadAllBytes(path);
        int requests = 0;
        using var http = CreateHttp(new FakeHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected HTTP request.");
        }));

        ClaudeOAuthCredential credential = await NewClient(http).GetAsync(path, CancellationToken.None, AccessA).ConfigureAwait(false);

        Ensure(credential.AccessToken == AccessB && !credential.Refreshed, "A stale rejection did not return the newer credential.");
        Ensure(requests == 0 && before.SequenceEqual(File.ReadAllBytes(path)), "A stale rejection rotated or wrote credentials.");
    }

    private static async Task CancellationAsync(string directory)
    {
        string path = CredentialPath(directory);
        WriteCredential(path, Now.AddSeconds(-1).ToUnixTimeMilliseconds());
        byte[] before = File.ReadAllBytes(path);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var http = CreateHttp(new FakeHandler(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return JsonResponse(HttpStatusCode.OK, RefreshBody(AccessB, RefreshB));
        }));

        Task<ClaudeOAuthCredential> pending = NewClient(http).GetAsync(path, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        cancellation.Cancel();
        Exception error = await CaptureFailureAsync(() => pending).ConfigureAwait(false);

        Ensure(error is OperationCanceledException, "Canceled refresh did not propagate cancellation.");
        Ensure(before.SequenceEqual(File.ReadAllBytes(path)), "Canceled refresh changed the original credential file.");
    }

    private static async Task ExistingLockAsync(string directory)
    {
        string path = CredentialPath(directory);
        WriteCredential(path, Now.AddSeconds(-1).ToUnixTimeMilliseconds());
        byte[] before = File.ReadAllBytes(path);
        string lockDirectory = Path.Combine(directory, ".oauth_refresh.lock");
        Directory.CreateDirectory(lockDirectory);
        string marker = Path.Combine(lockDirectory, "owner.marker");
        File.WriteAllText(marker, "existing-owner");
        int requests = 0;
        using var http = CreateHttp(new FakeHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected HTTP request.");
        }));
        var timer = Stopwatch.StartNew();

        Exception error = await CaptureFailureAsync(() => NewClient(http).GetAsync(path, CancellationToken.None)).ConfigureAwait(false);

        timer.Stop();
        Ensure(error is InvalidOperationException && timer.Elapsed >= TimeSpan.FromSeconds(2.8)
            && timer.Elapsed <= TimeSpan.FromSeconds(5), "Existing lock did not time out near three seconds.");
        Ensure(requests == 0 && before.SequenceEqual(File.ReadAllBytes(path)), "Lock timeout made a request or changed credentials.");
        Ensure(Directory.Exists(lockDirectory) && File.ReadAllText(marker) == "existing-owner", "Existing lock was modified or removed.");
        Ensure(!Directory.Exists(path + ".lock"), "The second lock was touched after the first lock failed.");
    }

    private static async Task PendingRecoveryAsync(string directory)
    {
        string path = CredentialPath(directory);
        WriteCredential(path, Now.AddSeconds(-1).ToUnixTimeMilliseconds());
        byte[] original = File.ReadAllBytes(path);
        string pendingPath = path + ".quotra-pending";
        FileStream? blocker = null;
        int posts = 0;
        using var http = CreateHttp(new FakeHandler((request, _) =>
        {
            if (request.Method != HttpMethod.Post) throw new InvalidOperationException("Unexpected HTTP request.");
            if (Interlocked.Increment(ref posts) == 1)
                blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, RefreshBody(AccessB, RefreshB)));
        }));

        Exception replaceFailure;
        try
        {
            replaceFailure = await CaptureFailureAsync(() => NewClient(http).GetAsync(path, CancellationToken.None)).ConfigureAwait(false);
            Ensure(replaceFailure is InvalidOperationException && File.Exists(pendingPath),
                "Failed replacement did not leave a recoverable pending sidecar.");
            Ensure(original.SequenceEqual(File.ReadAllBytes(path)), "Failed replacement changed the original credential file.");
        }
        finally
        {
            blocker?.Dispose();
        }

        ClaudeOAuthCredential recovered = await NewClient(http).GetAsync(path, CancellationToken.None).ConfigureAwait(false);
        Ensure(recovered.AccessToken == AccessB && recovered.Plan == "max", "Pending sidecar did not restore the rotated credentials.");
        Ensure(posts == 1 && !File.Exists(pendingPath), "Recovery issued another refresh POST or left the sidecar behind.");
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement oauth = saved.RootElement.GetProperty("claudeAiOauth");
        Ensure(oauth.GetProperty("accessToken").GetString() == AccessB
            && oauth.GetProperty("refreshToken").GetString() == RefreshB,
            "Recovered access and refresh tokens were not saved together.");
    }

    private static async Task UsageUnauthorizedAsync(string directory)
    {
        string path = CredentialPath(directory);
        WriteCredential(path, DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds());
        var requests = new List<CapturedRequest>();
        using var http = CreateHttp(new FakeHandler(async (request, cancellationToken) =>
        {
            CapturedRequest captured = await CaptureRequestAsync(request, cancellationToken).ConfigureAwait(false);
            requests.Add(captured);
            if (captured.Method == HttpMethod.Post)
                return JsonResponse(HttpStatusCode.OK, RefreshBody(AccessB, RefreshB));
            return captured.Authorization == "Bearer " + AccessA
                ? JsonResponse(HttpStatusCode.Unauthorized, "{}")
                : JsonResponse(HttpStatusCode.OK, UsageJson);
        }));
        var source = new ClaudeUsageSource(http);

        ProviderSnapshot snapshot = await source.FetchOAuthAsync(path, CancellationToken.None).ConfigureAwait(false);

        Ensure(requests.Count == 3 && requests[0].Method == HttpMethod.Get
            && requests[1].Method == HttpMethod.Post && requests[2].Method == HttpMethod.Get,
            "401 did not produce exactly one refresh and one usage retry.");
        Ensure(requests[0].Authorization == "Bearer " + AccessA && requests[2].Authorization == "Bearer " + AccessB,
            "Usage retry did not use the rotated access token.");
        AssertRefreshRequest(requests[1], RefreshA);
        Ensure(snapshot.Plan == "Claude Max" && snapshot.PrimaryLabel == "주간"
            && Math.Abs((snapshot.Primary?.Used ?? -1) - 0.42) < 0.00001,
            "Successful OAuth retry produced an unexpected usage snapshot.");
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
        Ensure(saved.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString() == AccessB,
            "Usage retry did not persist the rotated credential.");
    }

    private static async Task UsageUnauthorizedAfterRefreshAsync(string directory)
    {
        string path = CredentialPath(directory);
        WriteCredential(path, DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds());
        var requests = new List<CapturedRequest>();
        using var http = CreateHttp(new FakeHandler(async (request, cancellationToken) =>
        {
            CapturedRequest captured = await CaptureRequestAsync(request, cancellationToken).ConfigureAwait(false);
            requests.Add(captured);
            return captured.Method == HttpMethod.Post
                ? JsonResponse(HttpStatusCode.OK, RefreshBody(AccessB, RefreshB))
                : JsonResponse(HttpStatusCode.Unauthorized, "{}");
        }));
        var source = new ClaudeUsageSource(http);

        _ = await CaptureFailureAsync(() => source.FetchOAuthAsync(path, CancellationToken.None)).ConfigureAwait(false);

        Ensure(requests.Count == 2 && requests.Count(request => request.Method == HttpMethod.Post) == 1
            && requests.Count(request => request.Method == HttpMethod.Get) == 1,
            "An already-refreshed 401 triggered another refresh or usage request.");
        Ensure(requests[0].Method == HttpMethod.Post && requests[1].Method == HttpMethod.Get,
            "Proactive refresh and usage request order changed.");
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
        Ensure(saved.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString() == AccessB,
            "Successful proactive refresh was not persisted after usage 401.");
    }

    private static async Task UsageNonRetryStatusesAsync(string directory)
    {
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            string caseDirectory = Path.Combine(directory, ((int)status).ToString());
            Directory.CreateDirectory(caseDirectory);
            string path = CredentialPath(caseDirectory);
            WriteCredential(path, DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds());
            byte[] before = File.ReadAllBytes(path);
            var requests = new List<CapturedRequest>();
            using var http = CreateHttp(new FakeHandler(async (request, cancellationToken) =>
            {
                requests.Add(await CaptureRequestAsync(request, cancellationToken).ConfigureAwait(false));
                return JsonResponse(status, "{}");
            }));
            var source = new ClaudeUsageSource(http);

            _ = await CaptureFailureAsync(() => source.FetchOAuthAsync(path, CancellationToken.None)).ConfigureAwait(false);

            Ensure(requests.Count == 1 && requests[0].Method == HttpMethod.Get,
                "403 or 429 caused an OAuth refresh request.");
            Ensure(before.SequenceEqual(File.ReadAllBytes(path)), "403 or 429 changed the credential file.");
        }
    }

    private static async Task DiagnosticUrlAsync(string directory)
    {
        string path = CredentialPath(directory);
        int rejectedRequests = 0;
        using (var http = CreateHttp(new FakeHandler((_, _) =>
        {
            Interlocked.Increment(ref rejectedRequests);
            throw new InvalidOperationException("Unexpected HTTP request.");
        })))
        {
            var source = new ClaudeUsageSource(http);
            source.ApplyDiagnostics("https://example.invalid/api/oauth/usage", path);
            Exception error = await CaptureFailureAsync(() => source.FetchOAuthAsync(path, CancellationToken.None)).ConfigureAwait(false);
            Ensure(error.Message.Contains("진단 URL", StringComparison.Ordinal) && !File.Exists(path) && rejectedRequests == 0,
                "A non-loopback diagnostic URL was not rejected before reading credentials.");
        }

        WriteCredential(path, DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds());
        byte[] before = File.ReadAllBytes(path);
        var requests = new List<CapturedRequest>();
        using var loopbackHttp = CreateHttp(new FakeHandler(async (request, cancellationToken) =>
        {
            requests.Add(await CaptureRequestAsync(request, cancellationToken).ConfigureAwait(false));
            return JsonResponse(HttpStatusCode.Unauthorized, "{}");
        }));
        var loopbackSource = new ClaudeUsageSource(loopbackHttp);
        loopbackSource.ApplyDiagnostics("http://127.0.0.1:43210/api/oauth/usage", path);

        _ = await CaptureFailureAsync(() => loopbackSource.FetchOAuthAsync(path, CancellationToken.None)).ConfigureAwait(false);

        Ensure(requests.Count == 1 && requests[0].Method == HttpMethod.Get,
            "A diagnostic 401 triggered a token refresh or usage retry.");
        Ensure(before.SequenceEqual(File.ReadAllBytes(path)), "Diagnostic fetch changed the test credential file.");
    }

    private static Task UsageParserAsync(string _)
    {
        using JsonDocument both = JsonDocument.Parse(UsageJson);
        ProviderSnapshot weekly = ClaudeUsageSource.ParseUsage(both.RootElement, "max");
        Ensure(weekly.PrimaryLabel == "주간" && weekly.Secondary is not null
            && Math.Abs(weekly.Primary!.Value.Used - 0.42) < 0.00001
            && Math.Abs(weekly.Secondary.Value.Used - 0.12) < 0.00001,
            "Weekly and five-hour usage windows were not separated.");

        using JsonDocument onlyFiveHour = JsonDocument.Parse("""{"five_hour":{"utilization":25}}""");
        ProviderSnapshot fallback = ClaudeUsageSource.ParseUsage(onlyFiveHour.RootElement, "pro");
        Ensure(fallback.PrimaryLabel == "5시간" && fallback.Primary?.Used == 0.25 && fallback.Secondary is null,
            "Five-hour-only usage did not become the primary fallback.");
        return Task.CompletedTask;
    }

    private static void AssertRefreshRequest(CapturedRequest request, string expectedRefreshToken)
    {
        Ensure(request.Method == HttpMethod.Post && request.Uri?.AbsoluteUri == ClaudeOAuthClient.TokenEndpoint,
            "Refresh request did not use the verified fixed endpoint.");
        Ensure(request.AcceptJson && request.ContentType == "application/json", "Refresh request JSON headers are incorrect.");
        using JsonDocument body = JsonDocument.Parse(request.Body);
        JsonElement root = body.RootElement;
        Ensure(root.GetProperty("grant_type").GetString() == "refresh_token"
            && root.GetProperty("refresh_token").GetString() == expectedRefreshToken
            && root.GetProperty("client_id").GetString() == ClientId,
            "Refresh request JSON fields are incorrect.");
        Ensure(!root.TryGetProperty("access_token", out _), "Access token was included in the refresh request.");
    }

    private static async Task<CapturedRequest> CaptureRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new CapturedRequest(request.Method, request.RequestUri, request.Content?.Headers.ContentType?.MediaType,
            request.Headers.Accept.Any(value => value.MediaType == "application/json"), body,
            request.Headers.Authorization?.ToString());
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string RefreshBody(string accessToken, string refreshToken, bool includeScope = false) => includeScope
        ? $$"""{"access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}","expires_in":3600,"scope":"user:inference user:profile"}"""
        : $$"""{"access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}","expires_in":3600}""";

    private static HttpClient CreateHttp(HttpMessageHandler handler) => new(handler)
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    private static ClaudeOAuthClient NewClient(HttpClient http, Action<string>? log = null, Func<DateTimeOffset>? utcNow = null) =>
        new(http, log, utcNow ?? (() => Now));

    private static string CredentialPath(string directory) => Path.Combine(directory, ".credentials.json");

    private static void WriteCredential(string path, object? expiresAt, string accessToken = AccessA,
        string refreshToken = RefreshA, bool includeExpiresAt = true)
    {
        var oauth = new JsonObject
        {
            ["accessToken"] = accessToken,
            ["refreshToken"] = refreshToken,
            ["subscriptionType"] = "max",
            ["scopes"] = new JsonArray(JsonValue.Create("user:inference"), JsonValue.Create("user:profile")),
            ["futureField"] = new JsonObject { ["marker"] = "keep-oauth" },
        };
        if (includeExpiresAt) oauth["expiresAt"] = JsonSerializer.SerializeToNode(expiresAt);
        var root = new JsonObject
        {
            ["rootMarker"] = "keep-root",
            ["claudeAiOauth"] = oauth,
        };
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static async Task<Exception> CaptureFailureAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected operation to fail.");
    }

    private static void AssertNoSecrets(string value)
    {
        Ensure(!value.Contains(AccessA, StringComparison.Ordinal) && !value.Contains(AccessB, StringComparison.Ordinal)
            && !value.Contains(RefreshA, StringComparison.Ordinal) && !value.Contains(RefreshB, StringComparison.Ordinal),
            "A synthetic credential appeared in diagnostics.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string? ContentType, bool AcceptJson, string Body, string? Authorization);

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Quotra-ClaudeOAuthSelfTest-" + Guid.NewGuid().ToString("N"));
        private int _next;

        internal string CreateCaseDirectory()
        {
            string directory = Path.Combine(_root, Interlocked.Increment(ref _next).ToString("D2"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
