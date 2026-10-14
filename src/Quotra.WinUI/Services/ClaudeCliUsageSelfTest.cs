using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Quotra.WinUI.Interop;

namespace Quotra.WinUI.Services;

/// <summary>Offline parser, identity, trust-metadata, and synthetic Windows ConPTY regression checks.</summary>
internal static class ClaudeCliUsageSelfTest
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private const string Account = "36f82fa9-f16d-4d48-99f4-678a6136c09b";
    private const string Organization = "687f9a57-87df-4f63-92d3-0044c1ab56db";

    /// <summary>Runs bounded offline checks. No Claude executable, credential, network, or model is used.</summary>
    internal static bool Run() => Task.Run(RunAsync).GetAwaiter().GetResult();

    private static async Task<bool> RunAsync()
    {
        (string Name, Func<Task> Run)[] cases =
        [
            ("auth status subscription identity and config binding", () => RunSync(AuthStatusCases)),
            ("identity remains stable across capture", () => RunSync(IdentityCases)),
            ("session and all-model weekly percentages", () => RunSync(UsageHappyPath)),
            ("extra usage and model-specific sections are excluded", () => RunSync(UsageBoundaries)),
            ("malformed, partial, ambiguous, ANSI, and hostile captures fail closed", () => RunSync(UsageAdversarial)),
            ("prompt recognized at cursor with shortcut footer", () => RunSync(PromptFooter)),
            ("trusted workspace selection honors exact metadata and safe fallback", () => RunSync(TrustMetadata)),
            ("synthetic ConPTY spawn, output, and cleanup", ConPtyOutputAsync),
            ("ConPTY output cap terminates a noisy fixture", ConPtyOverflowAsync),
            ("ConPTY cancellation terminates its process tree", ConPtyCancellationAsync),
        ];

        int passed = 0;
        foreach ((string name, Func<Task> run) in cases)
        {
            try
            {
                await run().ConfigureAwait(false);
                passed++;
                Log.Write($"Claude CLI self-test PASS: {name}");
            }
            catch (Exception ex)
            {
                // Deliberately omit exception messages and captured terminal output.
                string detail = ex is FixtureAssertionException ? ex.Message : ex.GetType().Name;
                Log.Write($"Claude CLI self-test FAIL: {name} ({detail})");
            }
        }
        Log.Write($"Claude CLI self-test total: {passed}/{cases.Length} passed");
        return passed == cases.Length;
    }

    private static Task RunSync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private static void AuthStatusCases()
    {
        string home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Quotra-Claude-Profile"));
        string valid = $$"""
            {"loggedIn":true,"authMethod":"claude.ai","subscriptionType":"max","apiProvider":"firstParty","configDirectory":{{JsonSerializer.Serialize(home)}},"accountUuid":"{{Account}}","organizationUuid":"{{Organization}}","email":"person@example.test"}
            """;
        ClaudeCliUsageProbe.AuthStatus auth = ClaudeCliUsageProbe.ParseAuthStatus(valid, home);
        Ensure(auth.IsSubscriptionLogin && auth.AccountUuid == Account && auth.OrganizationUuid == Organization,
            "Subscription identity was not retained.");
        Ensure(auth.Email == "person@example.test", "Email was not retained.");

        ClaudeCliUsageProbe.AuthStatus loggedOut = ClaudeCliUsageProbe.ParseAuthStatus("{\"loggedIn\":false}", home);
        Ensure(!loggedOut.IsSubscriptionLogin, "Logged-out status was accepted.");
        ClaudeCliUsageProbe.AuthStatus apiKey = ClaudeCliUsageProbe.ParseAuthStatus(
            $$"""{"loggedIn":true,"authMethod":"api_key","subscriptionType":"max","accountUuid":"{{Account}}"}""", home);
        Ensure(!apiKey.IsSubscriptionLogin, "API-key auth was treated as a subscription login.");
        ClaudeCliUsageProbe.AuthStatus noSubscription = ClaudeCliUsageProbe.ParseAuthStatus(
            $$"""{"loggedIn":true,"authMethod":"oauth_token","subscriptionType":"","accountUuid":"{{Account}}"}""", home);
        Ensure(!noSubscription.IsSubscriptionLogin, "Missing subscription was accepted.");
        ExpectInvalid(() => ClaudeCliUsageProbe.ParseAuthStatus(
            "{\"loggedIn\":true,\"authMethod\":\"oauth_token\",\"subscriptionType\":\"max\",\"configDirectory\":\"C:\\\\other\"}", home));
        ExpectInvalid(() => ClaudeCliUsageProbe.ParseAuthStatus("{malformed", home));
    }

    private static void IdentityCases()
    {
        ClaudeCliUsageProbe.AuthStatus baseline = ClaudeCliUsageProbe.ParseAuthStatus(
            $$"""{"loggedIn":true,"authMethod":"oauth_token","subscriptionType":"pro","accountUuid":"{{Account}}","organizationUuid":"{{Organization}}","email":"person@example.test"}""", "C:\\profile");
        ClaudeCliUsageProbe.AuthStatus same = ClaudeCliUsageProbe.ParseAuthStatus(
            $$"""{"loggedIn":true,"authMethod":"claude.ai","subscriptionType":"max","accountUuid":"{{Account}}","organizationUuid":"{{Organization}}","email":"PERSON@example.test"}""", "C:\\profile");
        Ensure(ClaudeCliUsageProbe.HasStableIdentity(baseline), "UUID identity was not considered stable.");
        Ensure(ClaudeCliUsageProbe.SameStableIdentity(baseline, same), "Case or plan changes altered a stable account identity.");

        ClaudeCliUsageProbe.AuthStatus changed = ClaudeCliUsageProbe.ParseAuthStatus(
            "{\"loggedIn\":true,\"authMethod\":\"oauth_token\",\"subscriptionType\":\"max\",\"accountUuid\":\"7ab99018-404e-4723-b746-bf1d58ca5a6b\",\"organizationUuid\":\"687f9a57-87df-4f63-92d3-0044c1ab56db\",\"email\":\"person@example.test\"}", "C:\\profile");
        Ensure(!ClaudeCliUsageProbe.SameStableIdentity(baseline, changed), "Changed account was accepted.");

        ClaudeCliUsageProbe.AuthStatus orgEmail = ClaudeCliUsageProbe.ParseAuthStatus(
            "{\"loggedIn\":true,\"authMethod\":\"oauth_token\",\"subscriptionType\":\"pro\",\"organizationUuid\":\"687f9a57-87df-4f63-92d3-0044c1ab56db\",\"email\":\"person@example.test\"}", "C:\\profile");
        Ensure(ClaudeCliUsageProbe.HasStableIdentity(orgEmail), "Organization/email identity was not accepted.");
        ClaudeCliUsageProbe.AuthStatus unknown = ClaudeCliUsageProbe.ParseAuthStatus(
            "{\"loggedIn\":true,\"authMethod\":\"oauth_token\",\"subscriptionType\":\"pro\",\"email\":\"person@example.test\"}", "C:\\profile");
        Ensure(!ClaudeCliUsageProbe.HasStableIdentity(unknown), "Unidentifiable account was accepted.");
    }

    private static void UsageHappyPath()
    {
        const string capture = """
            Claude Code · Max
            Current session
            17% used
            Resets 2026-10-15T18:00:00Z
            Current week (all models)
            42% used
            Resets 2026-10-19T00:00:00Z
            """;
        Ensure(ClaudeCliUsageProbe.TryParseUsageCapture(capture, CapturedAt, out ProviderSnapshot? snapshot),
            "Complete usage capture was rejected.");
        Ensure(snapshot!.Primary?.Used == 0.42 && snapshot.Secondary?.Used == 0.17,
            "Weekly/session percentages were mapped incorrectly.");
        Ensure(snapshot.Primary?.ResetsAt == DateTimeOffset.Parse("2026-10-19T00:00:00Z")
            && snapshot.Secondary?.ResetsAt == DateTimeOffset.Parse("2026-10-15T18:00:00Z"),
            "Confident ISO reset timestamps were lost.");

        const string remaining = """
            Current session
            80% remaining
            Current week (all models)
            25% remaining
            """;
        Ensure(ClaudeCliUsageProbe.TryParseUsageCapture(remaining, CapturedAt, out ProviderSnapshot? inverse)
            && inverse!.Primary?.Used == 0.75 && inverse.Secondary?.Used == 0.20,
            "Remaining percentages were not inverted to used percentages.");

        const string unknownReset = "Current session\n12% used\nCurrent week (all models)\n34% used\nResets next week";
        Ensure(ClaudeCliUsageProbe.TryParseUsageCapture(unknownReset, CapturedAt, out ProviderSnapshot? noReset)
            && noReset!.Primary?.ResetsAt is null && noReset.Secondary?.ResetsAt is null,
            "An unparseable reset hint was guessed.");
    }

    private static void UsageBoundaries()
    {
        const string capture = """
            Current session
            11% used
            Current week (all models)
            42% used
            Extra usage
            73% used
            Usage insights
            91% used
            Current week (Sonnet only)
            88% used
            """;
        Ensure(ClaudeCliUsageProbe.TryParseUsageCapture(capture, CapturedAt, out ProviderSnapshot? snapshot),
            "Auxiliary usage percentage contaminated the all-model weekly window.");
        Ensure(snapshot!.Primary?.Used == 0.42, "Wrong weekly percentage was selected.");

        const string modelOnly = """
            Current session
            11% used
            Current week (Sonnet only)
            88% used
            """;
        Ensure(!ClaudeCliUsageProbe.TryParseUsageCapture(modelOnly, CapturedAt, out _),
            "A model-specific week was accepted as all-model usage.");
    }

    private static void UsageAdversarial()
    {
        string[] rejected =
        [
            "Current session\n12% used\nCurrent week (all models)\n",
            "Current session\n12% used\nCurrent session\n15% used\nCurrent week (all models)\n20% used",
            "Current session\n12% used\n20% remaining\nCurrent week (all models)\n20% used",
            "Current session\n101% used\nCurrent week (all models)\n20% used",
            "Current session\n-1% used\nCurrent week (all models)\n20% used",
            "Current session\n12% used of 50%\nCurrent week (all models)\n20% used",
            "Current session\n???% used\nCurrent week (all models)\n20% used",
            "Current week (all models)\n20% used\nCurrent session\n12% used",
            "Current session\n12% used\nCurrent week (all models)\n40%",
            "Current session\n12% used\nCurrent week (all models)\n40% used\u001b[31",
            "Current session\n12% used\nCurrent week (all models)\n40% used\u001b]8;;unfinished"
        ];
        foreach (string capture in rejected)
            Ensure(!ClaudeCliUsageProbe.TryParseUsageCapture(capture, CapturedAt, out _), "Malformed/ambiguous capture was accepted.");

        const string ansi = "\u001b[32mCurrent session\u001b[0m\n\u001b[1m12% used\u001b[0m\nCurrent week (all models)\n42% used";
        Ensure(ClaudeCliUsageProbe.TryParseUsageCapture(ansi, CapturedAt, out ProviderSnapshot? parsed)
            && parsed!.Primary?.Used == 0.42, "ANSI styling broke otherwise valid text.");
        string malicious = "Current session\n12% used\nCurrent week (all models)\n42% used\n\u001b]8;;https://evil.invalid\u0007Injected 99% used";
        Ensure(!ClaudeCliUsageProbe.TryParseUsageCapture(malicious, CapturedAt, out _), "OSC-injected percent was parsed as usage.");
    }

    private static void PromptFooter()
    {
        const string screen = "Welcome\n\n❯\n? for shortcuts, / for commands\n";
        Ensure(ClaudeCliUsageProbe.IsReadyPromptCapture(screen, 2), "Prompt with the real-style footer was not recognized.");
        Ensure(ClaudeCliUsageProbe.IsReadyPromptCapture("Welcome\n❯\n──────────────────────\n? for shortcuts\n", 1),
            "A decorative prompt border hid the ready footer.");
        Ensure(!ClaudeCliUsageProbe.IsReadyPromptCapture("Welcome\n❯ Yes\n? for shortcuts\n", 1),
            "A consent/menu choice was mistaken for the free-input prompt.");
        Ensure(!ClaudeCliUsageProbe.IsReadyPromptCapture("Welcome\n❯\nDo you trust this folder?\n", 1),
            "A trust dialog was mistaken for a ready prompt.");
        Ensure(!ClaudeCliUsageProbe.IsReadyPromptCapture("Welcome\n❯\n", 1),
            "A prompt without the known ready footer was accepted.");
    }

    private static void TrustMetadata()
    {
        using var temp = new TemporaryDirectory();
        string probe = Path.Combine(temp.Path, "ClaudeUsageProbe");
        Directory.CreateDirectory(probe);
        string metadata = Path.Combine(temp.Path, ".claude.json");
        File.WriteAllText(metadata, JsonSerializer.Serialize(new
        {
            projects = new Dictionary<string, object>
            {
                [Path.GetFullPath(probe)] = new { hasTrustDialogAccepted = true },
                [Path.Combine(temp.Path, "OtherProject")] = new { hasTrustDialogAccepted = false }
            }
        }));
        Ensure(ClaudeCliUsageProbe.HasExplicitTrustForProbeDirectory(metadata, probe),
            "Exact explicitly trusted probe folder was not accepted.");
        Ensure(!ClaudeCliUsageProbe.HasExplicitTrustForProbeDirectory(metadata, Path.Combine(temp.Path, "Other")),
            "Trust for another folder was broadened.");

        File.WriteAllText(metadata, "{\"projects\":{\"unrelated\":{\"hasTrustDialogAccepted\":true}}}");
        Ensure(!ClaudeCliUsageProbe.HasExplicitTrustForProbeDirectory(metadata, probe), "Unrelated trust metadata was accepted.");
        File.WriteAllText(metadata, "{malformed");
        Ensure(!ClaudeCliUsageProbe.HasExplicitTrustForProbeDirectory(metadata, probe), "Malformed trust metadata was accepted.");

        string customHome = Path.Combine(temp.Path, "custom-home");
        string userHome = Path.Combine(temp.Path, "user-home");
        string existingTrustedProject = Path.Combine(temp.Path, "existing-project");
        string defaultTrustedProject = Path.Combine(temp.Path, "default-project");
        string privateCandidate = Path.Combine(temp.Path, "Quotra", "ClaudeUsageProbe");
        Directory.CreateDirectory(customHome);
        Directory.CreateDirectory(userHome);
        Directory.CreateDirectory(existingTrustedProject);
        Directory.CreateDirectory(defaultTrustedProject);
        WriteTrustMetadata(Path.Combine(customHome, ".claude.json"), existingTrustedProject, trusted: true);
        WriteTrustMetadata(Path.Combine(userHome, ".claude.json"), defaultTrustedProject, trusted: true);
        Ensure(PathsEqual(ClaudeCliUsageProbe.FindTrustedWorkingDirectory(customHome, privateCandidate, userHome), existingTrustedProject),
            "An existing custom-home trusted workspace was not selected.");

        WriteTrustMetadata(Path.Combine(customHome, ".claude.json"), privateCandidate, trusted: true,
            additionalPath: existingTrustedProject);
        Directory.CreateDirectory(privateCandidate);
        Ensure(PathsEqual(ClaudeCliUsageProbe.FindTrustedWorkingDirectory(customHome, privateCandidate, userHome), privateCandidate),
            "An already-trusted Quotra private folder did not take precedence.");

        File.WriteAllText(Path.Combine(customHome, ".claude.json"), "{\"projects\":{}}");
        Ensure(PathsEqual(ClaudeCliUsageProbe.FindTrustedWorkingDirectory(customHome, privateCandidate, userHome), defaultTrustedProject),
            "The standard user-home metadata fallback was not read.");
        File.WriteAllText(Path.Combine(userHome, ".claude.json"), "{\"projects\":{}}");
        Ensure(ClaudeCliUsageProbe.FindTrustedWorkingDirectory(customHome, privateCandidate, userHome) is null,
            "An untrusted folder was selected or trust was auto-created.");
    }

    private static void WriteTrustMetadata(string path, string trustedPath, bool trusted, string? additionalPath = null)
    {
        var projects = new Dictionary<string, object>
        {
            [Path.GetFullPath(trustedPath)] = new { hasTrustDialogAccepted = trusted }
        };
        if (additionalPath is not null)
            projects[Path.GetFullPath(additionalPath)] = new { hasTrustDialogAccepted = false };
        File.WriteAllText(path, JsonSerializer.Serialize(new { projects }));
    }

    private static bool PathsEqual(string? left, string right) =>
        left is not null && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static async Task ConPtyOutputAsync()
    {
        RequireWindows();
        using var fixture = new TemporaryDirectory();
        const string marker = "QUOTRA-CONPTY-OUTPUT";
        using var terminal = ClaudePseudoConsole.Start(SystemExe("cmd.exe"), ["/d", "/c", "echo " + marker], fixture.Path, FixtureEnvironment());
        uint pid = terminal.ProcessId;
        var deadline = Stopwatch.StartNew();
        string screen = "";
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            screen = terminal.ReadAvailable();
            // Conhost may finish rendering just after the short-lived child exits.
            if (terminal.HasExited && screen.Contains(marker, StringComparison.Ordinal)) break;
            await Task.Delay(15).ConfigureAwait(false);
        }
        screen = terminal.ReadAvailable() + screen;
        Ensure(terminal.HasExited && screen.Contains(marker, StringComparison.Ordinal),
            "ConPTY fixture did not start and return output.");
        terminal.Dispose();
        Ensure(ProcessExited(pid, TimeSpan.FromSeconds(2)), "ConPTY output fixture remained running after disposal.");
    }

    private static async Task ConPtyOverflowAsync()
    {
        RequireWindows();
        using var fixture = new TemporaryDirectory();
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        Ensure(File.Exists(powershell), "Windows PowerShell fixture executable is unavailable.");
        // Several rendered frames are required: ConPTY may coalesce one huge write into one screen.
        string script = "while ($true) { [Console]::WriteLine('X' * 2048); [Threading.Thread]::Sleep(5) }";
        using var terminal = ClaudePseudoConsole.Start(powershell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script], fixture.Path, FixtureEnvironment());
        uint pid = terminal.ProcessId;
        var deadline = Stopwatch.StartNew();
        bool capped = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                _ = terminal.ReadAvailable();
                if (terminal.HasExited) break;
            }
            catch (InvalidOperationException ex) when (ex.Message == "Claude CLI output exceeded the capture limit.")
            {
                capped = true;
                break;
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
        Ensure(capped, "ConPTY output cap did not reject a >128 KiB fixture.");
        terminal.Dispose();
        Ensure(ProcessExited(pid, TimeSpan.FromSeconds(2)), "Noisy fixture survived output-cap cleanup.");
    }

    private static async Task ConPtyCancellationAsync()
    {
        RequireWindows();
        using var fixture = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(180));
        uint pid;
        bool cancelled = false;
        using (var terminal = ClaudePseudoConsole.Start(SystemExe("ping.exe"), ["-n", "60", "127.0.0.1"], fixture.Path, FixtureEnvironment()))
        {
            pid = terminal.ProcessId;
            try
            {
                while (true)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    _ = terminal.ReadAvailable();
                    await Task.Delay(15, cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                cancelled = true;
            }
        }
        Ensure(cancelled, "Cancellation fixture did not reach its cancellation path.");
        Ensure(ProcessExited(pid, TimeSpan.FromSeconds(2)), "Cancelled ConPTY fixture remained running.");
    }

    private static Dictionary<string, string> FixtureEnvironment()
    {
        string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string temp = Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath();
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot,
            ["WINDIR"] = systemRoot,
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? systemRoot + "\\System32",
            ["TEMP"] = temp,
            ["TMP"] = temp,
            ["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
    }

    private static string SystemExe(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), name);

    private static bool ProcessExited(uint processId, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                using Process process = Process.GetProcessById(checked((int)processId));
                if (process.HasExited) return true;
            }
            catch (ArgumentException) { return true; }
            Thread.Sleep(20);
        }
        return false;
    }

    private static void RequireWindows()
    {
        Ensure(OperatingSystem.IsWindows(), "ConPTY checks are Windows-only.");
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected the parser to reject invalid data.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new FixtureAssertionException(message);
    }

    private sealed class FixtureAssertionException(string message) : Exception(message);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Quotra-Claude-CLI-Test-" + Guid.NewGuid().ToString("N"));
        internal TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
