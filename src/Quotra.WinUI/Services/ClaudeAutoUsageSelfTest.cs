using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Quotra.Providers;
using Quotra.WinUI.Interop;

namespace Quotra.WinUI.Services;

/// <summary>Exercise source selection with synthetic logins only; never touches a real profile.</summary>
internal static class ClaudeAutoUsageSelfTest
{
    private const string AccountA = "11111111-1111-4111-8111-111111111111";
    private const string AccountB = "22222222-2222-4222-8222-222222222222";
    private const string OrgA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string OrgB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string AccessA = "fake-auto-access-a";
    private const string AccessB = "fake-auto-access-b";
    private const string Usage = """{"seven_day":{"utilization":42},"five_hour":{"utilization":12}}""";

    internal static bool Run() => Task.Run(RunAsync).GetAwaiter().GetResult();

    private static async Task<bool> RunAsync()
    {
        (string Name, Func<Fixture, Task> Run)[] cases =
        [
            ("OAuth wins and caches only verified identity", OAuthFirst),
            ("optional profile failure preserves successful OAuth usage", ProfileOptional),
            ("forbidden OAuth falls back to CLI in the same profile", CliFallback),
            ("revoked refresh token can still fall back to CLI", RevokedFallback),
            ("unverified Code owner cannot accept a different CLI identity", UnverifiedCliOwner),
            ("missing profile scope tries CLI without querying OAuth usage", ScopeFallback),
            ("matching Desktop account and organization can recover usage", MatchingDesktop),
            ("different Desktop account is rejected before usage", DifferentDesktopAccount),
            ("different Desktop organization is rejected before usage", DifferentDesktopOrganization),
            ("unverified stale Code login cannot switch to Desktop", UnknownOwner),
            ("Desktop-only login works without a Code credential", DesktopOnly),
            ("CLI refresh of the same account is accepted", CliRefresh),
            ("CLI account switch during polling is rejected", CliSwitch),
            ("CLI reported identity must match the verified owner", CliWrongIdentity),
            ("failed CLI cooldown resets when login changes", CliCooldown),
            ("CLI failure preserves the unregistered state of a revoked login", CliFailureState),
            ("identity-free CLI result without a credential is rejected", IdentityFreeCli),
            ("usage 429 stops every active fallback until Retry-After", UsageRateLimit),
            ("refresh 429 stops every active fallback until Retry-After", RefreshRateLimit),
            ("profile 429 stops CLI and Desktop", ProfileRateLimit),
            ("cancellation stops fallback and keeps caller cancellation", Cancellation),
            ("diagnostic overrides never invoke automatic discovery", Diagnostics),
            ("Desktop session changes cannot publish stale usage", DesktopSessionChanged),
            ("token changes after OAuth response cannot publish stale usage", OAuthTokenChanged),
            ("identity cache is scoped to profile and current token", IdentityCache),
        ];
        int passed = 0;
        foreach (var test in cases)
        {
            using var fixture = new Fixture();
            try
            {
                await test.Run(fixture).ConfigureAwait(false);
                passed++;
                Log.Write($"Claude auto self-test PASS: {test.Name}");
            }
            catch (Exception ex)
            {
                Log.Write($"Claude auto self-test FAIL: {test.Name} ({ex.GetType().Name})");
            }
        }
        Log.Write($"Claude auto self-test total: {passed}/{cases.Length} passed");
        return passed == cases.Length;
    }

    private static async Task OAuthFirst(Fixture f)
    {
        f.WriteCredential();
        f.OAuthStatus = HttpStatusCode.OK;
        byte[] original = File.ReadAllBytes(f.Credential);
        ProviderSnapshot first = await f.Fetch();
        ProviderSnapshot second = await f.Fetch();
        Ensure(first.Primary?.Used == .42 && first.HistoryKey!.StartsWith("claude-oauth-"));
        Ensure(first.HistoryKey == second.HistoryKey && f.Count("/api/oauth/profile") == 1);
        Ensure(f.Cli.Calls == 0 && f.DesktopReads == 0 && original.SequenceEqual(File.ReadAllBytes(f.Credential)));
        string cache = string.Join("", Directory.GetFiles(f.Cache).Select(File.ReadAllText));
        Ensure(cache.Contains(AccountA) && !cache.Contains(AccessA) && !cache.Contains("@"));
    }

    private static async Task ProfileOptional(Fixture f)
    {
        f.WriteCredential();
        f.OAuthStatus = HttpStatusCode.OK;
        f.ProfileStatus = HttpStatusCode.Forbidden;
        Ensure((await f.Fetch()).Primary?.Used == .42 && f.Cli.Calls == 0);
    }

    private static async Task CliFallback(Fixture f)
    {
        f.WriteCredential();
        f.Cli.Action = _ => Task.FromResult<ClaudeCliUsageResult?>(CliResult());
        ProviderSnapshot result = await f.Fetch();
        Ensure(result.Primary?.Used == .63 && result.Secondary?.Used == .21 && result.HistoryKey!.StartsWith("claude-cli-"));
        Ensure(result.Primary?.ResetsAt is null && result.Note!.Contains("리셋 시각 확인 안 됨"));
        Ensure(f.Cli.Calls == 1 && f.Cli.Home == f.Home && f.DesktopReads == 0);
    }

    private static async Task RevokedFallback(Fixture f)
    {
        f.WriteCredential(expired: true);
        new ClaudeIdentityCache(f.Cache).Write(f.Credential, ClaudeAccountIdentity.Hash(AccessA), ClaudeAccountIdentity.Create(AccountA, OrgA)!);
        f.ProfileStatus = HttpStatusCode.Unauthorized;
        f.Cli.Action = _ => Task.FromResult<ClaudeCliUsageResult?>(CliResult());
        Ensure((await f.Fetch()).HistoryKey!.StartsWith("claude-cli-"));
        Ensure(f.Count("/v1/oauth/token") == 1 && f.Count("/api/oauth/usage") == 0 && f.DesktopReads == 0);
    }

    private static async Task UnverifiedCliOwner(Fixture f)
    {
        f.WriteCredential();
        f.ProfileStatus = HttpStatusCode.Unauthorized;
        f.Cli.Action = _ => Task.FromResult<ClaudeCliUsageResult?>(CliResult(AccountB));
        Ensure(await Failure(f.Fetch) is InvalidOperationException);
    }

    private static async Task ScopeFallback(Fixture f)
    {
        f.WriteCredential(profileScope: false);
        f.Cli.Action = _ => Task.FromResult<ClaudeCliUsageResult?>(CliResult());
        Ensure((await f.Fetch()).Primary?.Used == .63 && f.Count("/api/oauth/usage") == 0);
    }

    private static async Task MatchingDesktop(Fixture f)
    {
        f.WriteCredential();
        f.HasDesktop = true;
        ProviderSnapshot result = await f.Fetch();
        Ensure(result.HistoryKey!.StartsWith("claude-desktop-") && result.Primary?.Used == .42);
        Ensure(f.Cli.Calls == 1 && f.Count("/api/account") == 1 && f.WebUsageCalls == 1);
    }

    private static async Task DifferentDesktopAccount(Fixture f)
    {
        f.WriteCredential();
        f.HasDesktop = true;
        f.DesktopAccount = AccountB;
        Ensure(await Failure(f.Fetch) is ProviderNotConnectedException && f.WebUsageCalls == 0);
    }

    private static async Task DifferentDesktopOrganization(Fixture f)
    {
        f.WriteCredential();
        f.HasDesktop = true;
        f.DesktopOrg = OrgB;
        Ensure(await Failure(f.Fetch) is InvalidOperationException && f.WebUsageCalls == 0);
    }

    private static async Task UnknownOwner(Fixture f)
    {
        f.WriteCredential();
        f.HasDesktop = true;
        f.ProfileStatus = HttpStatusCode.Unauthorized;
        Ensure(await Failure(f.Fetch) is ProviderNotConnectedException && f.Count("/api/account") == 0 && f.WebUsageCalls == 0);
    }

    private static async Task DesktopOnly(Fixture f)
    {
        f.HasDesktop = true;
        ProviderSnapshot result = await f.Fetch();
        Ensure(result.Primary?.Used == .42 && result.HistoryKey!.StartsWith("claude-desktop-") && f.Cli.Calls == 1);
        Ensure(f.Count("/api/oauth/profile") == 0 && f.Count("/api/oauth/usage") == 0);
    }

    private static async Task CliRefresh(Fixture f)
    {
        f.WriteCredential();
        f.Cli.Action = _ => { f.WriteCredential(AccessB); return Task.FromResult<ClaudeCliUsageResult?>(CliResult()); };
        Ensure((await f.Fetch()).Primary?.Used == .63 && f.Count("/api/oauth/profile") == 2 && f.DesktopReads == 0);
    }

    private static async Task CliSwitch(Fixture f)
    {
        f.WriteCredential();
        f.HasDesktop = true;
        f.Cli.Action = _ =>
        {
            f.WriteCredential(AccessB);
            f.ProfileAccount = AccountB;
            return Task.FromResult<ClaudeCliUsageResult?>(CliResult(AccountB));
        };
        Ensure(await Failure(f.Fetch) is ProviderNotConnectedException && f.WebUsageCalls == 0);
    }

    private static async Task CliWrongIdentity(Fixture f)
    {
        f.WriteCredential();
        f.Cli.Action = _ => Task.FromResult<ClaudeCliUsageResult?>(CliResult(AccountB));
        Ensure(await Failure(f.Fetch) is InvalidOperationException && f.WebUsageCalls == 0);
    }

    private static async Task CliCooldown(Fixture f)
    {
        f.WriteCredential();
        _ = await Failure(f.Fetch);
        _ = await Failure(f.Fetch);
        Ensure(f.Cli.Calls == 1);
        f.WriteCredential(AccessB);
        _ = await Failure(f.Fetch);
        Ensure(f.Cli.Calls == 2);
    }

    private static async Task CliFailureState(Fixture f)
    {
        f.WriteCredential(expired: true);
        f.Cli.Action = _ => throw new InvalidOperationException("Synthetic CLI failed.");
        Exception error = await Failure(f.Fetch);
        Ensure(error is ProviderNotConnectedException);
        var provider = new Quotra.WinUI.Models.ProviderUsage { Id = "claude", IsLinked = true, Primary = .5 };
        UsageService.ApplyFailure(provider, error);
        Ensure(!provider.IsLinked && !provider.HasAnyValue && provider.StatusText == "등록 안 됨");
    }

    private static async Task IdentityFreeCli(Fixture f)
    {
        f.Cli.Action = _ => Task.FromResult<ClaudeCliUsageResult?>(new(CliResult().Snapshot));
        Ensure(await Failure(f.Fetch) is ProviderNotConnectedException);
    }

    private static async Task UsageRateLimit(Fixture f)
    {
        f.WriteCredential();
        f.OAuthStatus = HttpStatusCode.TooManyRequests;
        Ensure(await Failure(f.Fetch) is ClaudeRateLimitedException);
        Ensure(await Failure(f.Fetch) is ClaudeRateLimitedException);
        Ensure(f.Calls.Count == 1 && f.Cli.Calls == 0 && f.DesktopReads == 0);
    }

    private static async Task RefreshRateLimit(Fixture f)
    {
        f.WriteCredential(expired: true);
        f.RefreshStatus = HttpStatusCode.TooManyRequests;
        Ensure(await Failure(f.Fetch) is ClaudeRateLimitedException);
        Ensure(await Failure(f.Fetch) is ClaudeRateLimitedException);
        Ensure(f.Calls.Count == 1 && f.Cli.Calls == 0 && f.DesktopReads == 0);
    }

    private static async Task ProfileRateLimit(Fixture f)
    {
        f.WriteCredential();
        f.ProfileStatus = HttpStatusCode.TooManyRequests;
        Ensure(await Failure(f.Fetch) is ClaudeRateLimitedException);
        Ensure(await Failure(f.Fetch) is ClaudeRateLimitedException);
        Ensure(f.Calls.Count == 2 && f.Cli.Calls == 0 && f.DesktopReads == 0);
    }

    private static async Task Cancellation(Fixture f)
    {
        using var cancel = new CancellationTokenSource();
        f.Cli.Action = ct => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult<ClaudeCliUsageResult?>(null); };
        Ensure(await Failure(() => f.Source.FetchAsync(cancel.Token)) is OperationCanceledException && f.DesktopReads == 0);
    }

    private static async Task Diagnostics(Fixture f)
    {
        f.WriteCredential();
        f.Source.ApplyDiagnostics(null, f.Credential);
        Ensure(await Failure(f.Fetch) is InvalidOperationException && f.Cli.Calls == 0 && f.DesktopReads == 0);
        Ensure(f.Count("/api/oauth/profile") == 0);
    }

    private static async Task DesktopSessionChanged(Fixture f)
    {
        f.HasDesktop = true;
        f.AfterWebUsage = () => f.DesktopSession = "fake-desktop-session-b";
        Ensure(await Failure(f.Fetch) is ProviderNotConnectedException);
    }

    private static async Task OAuthTokenChanged(Fixture f)
    {
        f.WriteCredential();
        f.OAuthStatus = HttpStatusCode.OK;
        f.AfterOAuthUsage = () => f.WriteCredential(AccessB);
        Ensure(await Failure(f.Fetch) is ProviderNotConnectedException);
    }

    private static Task IdentityCache(Fixture f)
    {
        var cache = new ClaudeIdentityCache(f.Cache);
        var owner = ClaudeAccountIdentity.Create(AccountA, OrgA)!;
        string hash = ClaudeAccountIdentity.Hash(AccessA);
        cache.Write(f.Credential, hash, owner);
        Ensure(cache.Read(f.Credential, hash)?.Matches(owner) == true);
        Ensure(cache.Read(f.Credential, ClaudeAccountIdentity.Hash(AccessB)) is null);
        Ensure(cache.Read(Path.Combine(f.Home, "another", ".credentials.json"), hash) is null);
        foreach (string file in Directory.GetFiles(f.Cache)) File.WriteAllText(file, "{broken");
        Ensure(cache.Read(f.Credential, hash) is null);
        using var invalid = JsonDocument.Parse("""{"account":{"email":"same@example.invalid"},"organization":{"uuid":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"}}""");
        Ensure(ClaudeAccountIdentity.FromOAuth(invalid.RootElement) is null);
        return Task.CompletedTask;
    }

    private static ClaudeCliUsageResult CliResult(string account = AccountA) => new(
        new ProviderSnapshot(new WindowUsage(.63, null), new WindowUsage(.21, null), PrimaryLabel: "주간", SecondaryLabel: "5시간"), account, OrgA);
    private static void Ensure(bool condition) { if (!condition) throw new InvalidOperationException("Synthetic regression assertion failed."); }
    private static async Task<Exception> Failure(Func<Task<ProviderSnapshot>> run)
    {
        try { _ = await run().ConfigureAwait(false); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("Expected the synthetic request to fail.");
    }

    private sealed class FakeCli : IClaudeCliUsageProbe
    {
        internal int Calls;
        internal string? Home;
        internal Func<CancellationToken, Task<ClaudeCliUsageResult?>> Action = _ => Task.FromResult<ClaudeCliUsageResult?>(null);
        public Task<ClaudeCliUsageResult?> TryFetchAsync(string claudeHome, CancellationToken cancellationToken)
        { Calls++; Home = claudeHome; return Action(cancellationToken); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Home = Path.Combine(Path.GetTempPath(), "Quotra-Claude-auto-" + Guid.NewGuid().ToString("N"));
        internal string Credential => Path.Combine(Home, ".credentials.json");
        internal string Cache => Path.Combine(Home, "verified-identities");
        internal readonly FakeCli Cli = new();
        internal readonly List<string> Calls = [];
        internal readonly ClaudeUsageSource Source;
        private readonly HttpClient _http;
        internal HttpStatusCode OAuthStatus = HttpStatusCode.Forbidden;
        internal HttpStatusCode ProfileStatus = HttpStatusCode.OK;
        internal HttpStatusCode RefreshStatus = HttpStatusCode.BadRequest;
        internal string ProfileAccount = AccountA;
        internal string DesktopAccount = AccountA;
        internal string DesktopOrg = OrgA;
        internal string DesktopSession = "fake-desktop-session-a";
        internal bool HasDesktop;
        internal int DesktopReads;
        internal Action? AfterWebUsage;
        internal Action? AfterOAuthUsage;
        internal int Count(string path) => Calls.Count(x => x == path);
        internal int WebUsageCalls => Calls.Count(x => x.StartsWith("/api/organizations/", StringComparison.Ordinal) && x.EndsWith("/usage", StringComparison.Ordinal));

        internal Fixture()
        {
            Directory.CreateDirectory(Home);
            _http = new HttpClient(new Handler(Send));
            Source = new ClaudeUsageSource(_http, Cli, () =>
            {
                DesktopReads++;
                return new ClaudeDesktopSessionReader.Result(HasDesktop ? DesktopSession : null, null, HasDesktop ? DesktopOrg : null);
            }, Home, Cache);
        }

        internal Task<ProviderSnapshot> Fetch() => Source.FetchAsync(CancellationToken.None);
        internal void WriteCredential(string access = AccessA, bool expired = false, bool profileScope = true) => File.WriteAllText(Credential,
            JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = access, refreshToken = "fake-auto-refresh", subscriptionType = "max",
                expiresAt = DateTimeOffset.UtcNow.AddHours(expired ? -2 : 2).ToUnixTimeMilliseconds(), scopes = profileScope ? new[] { "user:profile", "user:inference" } : new[] { "user:inference" } } }));

        private Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string path = request.RequestUri!.AbsolutePath;
            Calls.Add(path);
            HttpStatusCode status = HttpStatusCode.OK;
            string body;
            switch (path)
            {
                case "/api/oauth/usage": status = OAuthStatus; body = Usage; AfterOAuthUsage?.Invoke(); break;
                case "/api/oauth/profile":
                    status = ProfileStatus;
                    body = JsonSerializer.Serialize(new { account = new { uuid = ProfileAccount, email = "fixture@example.invalid" }, organization = new { uuid = OrgA } }); break;
                case "/v1/oauth/token": status = RefreshStatus; body = """{"error":"invalid_grant"}"""; break;
                case "/api/organizations": body = JsonSerializer.Serialize(new[] { new { uuid = DesktopOrg, capabilities = new[] { "chat" } } }); break;
                case "/api/account": body = JsonSerializer.Serialize(new { uuid = DesktopAccount, email = "fixture@example.invalid" }); break;
                default:
                    Ensure(path == $"/api/organizations/{DesktopOrg}/usage");
                    body = Usage; AfterWebUsage?.Invoke(); break;
            }
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (status == HttpStatusCode.TooManyRequests) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        }

        public void Dispose()
        {
            _http.Dispose();
            string resolved = Path.GetFullPath(Home);
            string expectedRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("Quotra-Claude-auto-", StringComparison.Ordinal)) return;
            try { Directory.Delete(resolved, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
