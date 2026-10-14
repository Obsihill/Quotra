using System.Net;
using System.Text;
using System.Text.Json;

namespace Quotra.WinUI.Services;

/// <summary>합성 키와 HTTP 대역만 사용한다. 실제 보관함·로그인 파일·네트워크에 접근하지 않는다.</summary>
internal static class CommandCodeUsageSelfTest
{
    private const string Key = "fake-commandcode-key";
    private const string Usage = """
        {"data":{"credits":{"monthlyCredits":60,"purchasedCredits":1000,"freeCredits":50},
        "windowLimits":{"weekly":{"used":90,"cap":100,"resetAt":1800000000000},
        "fiveHour":{"used":0,"cap":20,"resetAt":"2027-01-15T08:00:00Z"}}}}
        """;
    private const string Subscription = """
        {"data":{"planId":"individual-pro-v1","status":"active","currentPeriodEnd":"2027-02-02T00:00:00Z"}}
        """;

    internal static bool Run() => Task.Run(RunAsync).GetAwaiter().GetResult();

    private static async Task<bool> RunAsync()
    {
        (string Name, Func<Task> Check)[] cases =
        [
            ("saved key fetch and organization scope", SavedKeyAsync),
            ("verification uses entered key without loading saved key", VerifyKeyAsync),
            ("missing key does not auto-connect", MissingKeyAsync),
            ("invalid key fails before HTTP", InvalidKeyAsync),
            ("HTTP errors and malformed responses do not expose keys", ErrorsAsync),
            ("diagnostics reject login files and remote hosts", DiagnosticsAsync),
            ("monthly plan totals preserve legacy and versioned plans", MonthlyPlansAsync),
            ("unknown and inactive plans do not relabel weekly usage", UnknownPlansAsync),
            ("monthly billing reset accepts seconds and milliseconds", MonthlyResetAsync),
            ("malformed monthly balances fail safely", InvalidMonthlyBalanceAsync),
        ];
        int passed = 0;
        foreach (var (name, check) in cases)
        {
            try
            {
                await check().ConfigureAwait(false);
                passed++;
                Log.Write($"Command Code self-test PASS: {name}");
            }
            catch (Exception ex)
            {
                Log.Write($"Command Code self-test FAIL: {name} ({ex.GetType().Name})");
            }
        }
        Log.Write($"Command Code self-test: {passed}/{cases.Length} 통과");
        return passed == cases.Length;
    }

    private static async Task SavedKeyAsync()
    {
        int requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Ensure(request.Method == HttpMethod.Get && request.Headers.Authorization?.Scheme == "Bearer" &&
                request.Headers.Authorization.Parameter == Key);
            Ensure(request.RequestUri!.AbsoluteUri == (requests++ switch
            {
                0 => "https://api.commandcode.ai/alpha/whoami?limits=1",
                1 => "https://api.commandcode.ai/alpha/billing/credits?orgId=org%2Ftest%20%26",
                _ => "https://api.commandcode.ai/alpha/billing/subscriptions?orgId=org%2Ftest%20%26",
            }));
            return Ok(requests switch { 1 => """{"data":{"org":{"id":"org/test &"}}}""", 2 => Usage, _ => Subscription });
        }));
        var source = new CommandCodeUsageSource(http, () => Key);
        ProviderSnapshot snapshot = await source.FetchAsync(CancellationToken.None);
        Ensure(requests == 3 && snapshot.Primary?.Used == .25 && snapshot.Secondary?.Used == 0 &&
            snapshot.PrimaryLabel == "월간" && snapshot.HistoryKey!.StartsWith("commandcode-monthly-", StringComparison.Ordinal) &&
            snapshot.Primary?.ResetsAt == DateTimeOffset.Parse("2027-02-02T00:00:00Z") &&
            snapshot.Secondary?.ResetsAt == DateTimeOffset.Parse("2027-01-15T08:00:00Z"));
    }

    private static async Task VerifyKeyAsync()
    {
        int requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Ensure(request.Headers.Authorization?.Parameter == Key);
            Ensure(request.RequestUri!.Query == (requests++ == 0 ? "?limits=1" : string.Empty));
            return Ok(requests switch { 1 => "{}", 2 => Usage, _ => Subscription });
        }));
        var source = new CommandCodeUsageSource(http, () => throw new Exception("Must not load saved key."));
        string summary = await source.VerifyKeyAsync($"  {Key}  ", CancellationToken.None);
        Ensure(requests == 3 && !summary.Contains(Key));
    }

    private static async Task MissingKeyAsync()
    {
        // A legacy env key must not restore the connection after the saved key is removed.
        string? previous = Environment.GetEnvironmentVariable("COMMAND_CODE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("COMMAND_CODE_API_KEY", Key);
            using var http = new HttpClient(new Handler(_ => throw new Exception("Unexpected HTTP.")));
            foreach (string? key in new string?[] { null, "  " })
            {
                var source = new CommandCodeUsageSource(http, () => key);
                ProviderSnapshot snapshot = await source.FetchAsync(CancellationToken.None);
                Ensure(snapshot.Primary is null && snapshot.Secondary is null);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMMAND_CODE_API_KEY", previous);
        }
    }

    private static async Task InvalidKeyAsync()
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("Unexpected HTTP.")));
        var source = new CommandCodeUsageSource(http, () => null);
        foreach (string key in new[] { "", "  ", "bad key", "bad\r\nkey" })
            await ExpectFailureAsync(() => source.VerifyKeyAsync(key, CancellationToken.None), "API 키");
    }

    private static async Task ErrorsAsync()
    {
        foreach (int status in new[] { 401, 403, 429, 500, 200 })
        {
            using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("whoami")
                ? Ok("{}")
                : new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Key) }));
            var source = new CommandCodeUsageSource(http, () => Key);
            await ExpectFailureAsync(() => source.VerifyKeyAsync(Key, CancellationToken.None),
                status == 401 ? "프로바이더 설정" : "Command Code");
        }
    }

    private static async Task DiagnosticsAsync()
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("Unexpected HTTP.")));
        var source = new CommandCodeUsageSource(http, () => Key);
        await ExpectFailureAsync(() => Task.Run(() => source.ApplyDiagnostics(null, "old-auth.json")), "로그인 파일");
        source.ApplyDiagnostics("https://example.com", null);
        await ExpectFailureAsync(() => source.VerifyKeyAsync(Key, CancellationToken.None), "루프백");
    }

    private static async Task<ProviderSnapshot> FetchMonthlyAsync(string credits, string subscription)
    {
        using var http = new HttpClient(new Handler(request => Ok(request.RequestUri!.AbsolutePath switch
        {
            "/alpha/billing/credits" => credits,
            "/alpha/billing/subscriptions" => subscription,
            _ => "{}",
        })));
        return await new CommandCodeUsageSource(http, () => Key).FetchAsync(CancellationToken.None);
    }

    private static async Task MonthlyPlansAsync()
    {
        foreach (var (id, cap) in new[] { ("individual-pro", 30d), ("individual-pro-v1", 80d),
            ("individual-go", 10d), ("individual-go-v1", 10d), ("individual-goat", 70d),
            ("individual-max", 150d), ("individual-ultra", 300d), ("teams-pro", 40d), ("individual-provider", 15d) })
        {
            string subscription = JsonSerializer.Serialize(new { data = new { planId = id, status = "active" } });
            ProviderSnapshot result = await FetchMonthlyAsync("""{"credits":{"monthlyCredits":1}}""", subscription);
            Ensure(Math.Abs(result.Primary!.Value.Used - (1 - 1 / cap)) < 1e-10 && result.PrimaryLabel == "월간");
        }
    }

    private static async Task UnknownPlansAsync()
    {
        foreach (string subscription in new[] { Subscription.Replace("individual-pro-v1", "custom-enterprise"),
            Subscription.Replace("active", "canceled"), "{}" })
        {
            ProviderSnapshot result = await FetchMonthlyAsync(Usage, subscription);
            Ensure(result.Primary is null && result.Secondary?.Used == 0 && result.PrimaryLabel == "월간"
                && result.Note!.Contains("월간 총량을 확인할 수 없어"));
        }
        ProviderSnapshot boosted = await FetchMonthlyAsync(Usage.Replace("monthlyCredits\":60", "monthlyCredits\":100"), Subscription);
        Ensure(boosted.Primary is null);
    }

    private static async Task MonthlyResetAsync()
    {
        DateTimeOffset expected = DateTimeOffset.Parse("2027-02-02T00:00:00Z");
        foreach (long timestamp in new[] { expected.ToUnixTimeSeconds(), expected.ToUnixTimeMilliseconds() })
        {
            string subscription = JsonSerializer.Serialize(new { data = new { planId = "individual-pro-v1", status = "active", currentPeriodEnd = timestamp } });
            ProviderSnapshot result = await FetchMonthlyAsync(Usage, subscription);
            Ensure(result.Primary?.ResetsAt == expected);
        }
    }

    private static async Task InvalidMonthlyBalanceAsync()
    {
        foreach (string value in new[] { "null", "\"60\"", "-1", "true" })
            await ExpectFailureAsync(() => FetchMonthlyAsync(
                Usage.Replace("\"monthlyCredits\":60", "\"monthlyCredits\":" + value), Subscription), "응답 형식");
    }

    private static async Task ExpectFailureAsync(Func<Task> action, string expected)
    {
        try { await action(); }
        catch (InvalidOperationException ex)
        {
            Ensure(ex.Message.Contains(expected) && !ex.Message.Contains(Key));
            return;
        }
        throw new Exception("Expected a safe failure.");
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static void Ensure(bool condition)
    {
        if (!condition) throw new Exception("Command Code assertion failed.");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
