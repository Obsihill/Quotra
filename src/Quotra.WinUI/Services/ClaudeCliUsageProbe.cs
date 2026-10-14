using System.Diagnostics;
using System.Globalization;
using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Quotra.WinUI.Interop;

namespace Quotra.WinUI.Services;

internal sealed record ClaudeCliUsageResult(
    ProviderSnapshot Snapshot,
    string? AccountUuid = null,
    string? OrganizationUuid = null,
    string? Email = null);

internal interface IClaudeCliUsageProbe
{
    Task<ClaudeCliUsageResult?> TryFetchAsync(string claudeHome, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the signed-in subscription's existing CLI /usage screen. It never invokes a model prompt,
/// changes Claude settings, performs login, or executes command-shell wrappers.
/// </summary>
internal sealed class ClaudeCliUsageProbe : IClaudeCliUsageProbe
{
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(20);
    private const int MaximumCaptureBytes = 128 * 1024;
    private const int MaximumAuthJsonBytes = 32 * 1024;
    private static readonly Regex PercentRegex = new(
        @"(?<![\d.+-])(?<value>\d{1,3}(?:\.\d{1,2})?)\s*%\s*(?<kind>used|remaining)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex IsoResetRegex = new(
        @"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<ClaudeCliUsageResult?> TryFetchAsync(string claudeHome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claudeHome);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Claude CLI usage probing is available only on Windows.");

        string home;
        try { home = Path.GetFullPath(claudeHome); }
        catch { throw new InvalidOperationException("Claude CLI profile path is invalid."); }
        if (!Directory.Exists(home)) return null;

        CliCommand? command = DiscoverCli();
        if (command is null) return null;

        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalTimeout.CancelAfter(MaximumDuration);
        string? cwd = null;
        string? privateProbeDirectory = null;
        string sessionId = Guid.NewGuid().ToString("D");
        try
        {
            privateProbeDirectory = GetProbeDirectoryPath();
            string authCwd = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (string.IsNullOrWhiteSpace(authCwd) || !Directory.Exists(authCwd))
                throw new InvalidOperationException("Claude CLI authentication status could not be read safely.");
            IReadOnlyDictionary<string, string> environment = BuildProbeEnvironment(home);
            AuthStatus auth = await ReadAuthStatusAsync(command, home, authCwd, environment, totalTimeout.Token).ConfigureAwait(false);
            if (!auth.IsSubscriptionLogin) return null;
            if (!HasStableIdentity(auth))
                throw new InvalidOperationException("Claude CLI account identity is insufficient to bind usage safely.");
            cwd = FindTrustedWorkingDirectory(home, privateProbeDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (cwd is null)
                throw new InvalidOperationException("Claude Code 사용량을 자동 조회하려면 먼저 Claude CLI에서 사용할 폴더를 한 번 열고 신뢰를 승인해 주세요. Quotra는 신뢰·로그인·동의 화면에 대신 응답하지 않습니다.");

            ProviderSnapshot? snapshot = await ReadUsageAsync(command, home, cwd, sessionId, environment, totalTimeout.Token)
                .ConfigureAwait(false);
            if (snapshot is null) return null;

            AuthStatus after = await ReadAuthStatusAsync(command, home, authCwd, environment, totalTimeout.Token).ConfigureAwait(false);
            if (!after.IsSubscriptionLogin || !SameStableIdentity(auth, after))
                throw new InvalidOperationException("Claude CLI account identity changed during usage capture.");

            return new ClaudeCliUsageResult(snapshot, after.AccountUuid, after.OrganizationUuid, after.Email);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("Claude CLI usage probe timed out; no usage was published.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException("Claude CLI usage probe failed; no usage was published.");
        }
        finally
        {
            TryDeleteProbeTranscript(home, sessionId);
        }
    }

    private async Task<AuthStatus> ReadAuthStatusAsync(CliCommand command, string home, string cwd,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateProcessStartInfo(command, ["auth", "status"], cwd, environment)
        };
        if (!process.Start()) throw new InvalidOperationException("Claude CLI authentication status could not be read.");

        ClaudeProcessJob job;
        try { job = ClaudeProcessJob.CreateAndAssign(process.Handle); }
        catch
        {
            TryKill(process);
            throw new InvalidOperationException("Claude CLI process cleanup could not be secured.");
        }

        using (job)
        {
            var budget = new OutputBudget(MaximumAuthJsonBytes);
            Task<string> stdout = ReadBoundedAsync(process.StandardOutput, budget, cancellationToken);
            Task<string> stderr = ReadBoundedAsync(process.StandardError, budget, cancellationToken);
            Task wait = process.WaitForExitAsync(cancellationToken);
            try
            {
                var pending = new HashSet<Task> { stdout, stderr, wait };
                while (!wait.IsCompleted)
                {
                    Task completed = await Task.WhenAny(pending).ConfigureAwait(false);
                    if (completed == wait) break;
                    if (completed.IsFaulted || completed.IsCanceled) await completed.ConfigureAwait(false);
                    pending.Remove(completed);
                    if (pending.Count == 0) break;
                }
                await wait.ConfigureAwait(false);
                string json = await stdout.ConfigureAwait(false);
                _ = await stderr.ConfigureAwait(false);
                AuthStatus auth = ParseAuthStatus(json, home);
                if (process.ExitCode != 0 && auth.LoggedIn)
                    throw new InvalidOperationException("Claude CLI authentication status returned an inconsistent result.");
                return auth;
            }
            catch
            {
                job.Terminate();
                TryKill(process);
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch { }
                throw;
            }
        }
    }

    private async Task<ProviderSnapshot?> ReadUsageAsync(CliCommand command, string home, string cwd, string sessionId,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "--safe-mode",
            "--no-chrome",
            "--tools", "",
            "--disallowedTools", "mcp__*",
            "--setting-sources", "user",
            "--settings", "{\"disableAllHooks\":true,\"remoteControlAtStartup\":false}",
            "--session-id", sessionId
        };
        using ClaudePseudoConsole terminal = ClaudePseudoConsole.Start(command.ExecutablePath,
            command.BuildArguments(arguments).ToArray(), cwd, environment);

        var stopwatch = Stopwatch.StartNew();
        bool commandSent = false;
        while (stopwatch.Elapsed < MaximumDuration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string screen = terminal.ReadAvailable();
            if (ContainsUnattendedDialog(screen))
                throw new InvalidOperationException("Claude CLI displayed a login, consent, trust, or policy dialog; no input was sent.");
            if (!commandSent)
            {
                if (terminal.HasExited) throw new InvalidOperationException("Claude CLI exited before its usage screen was available.");
                if (IsReadyPromptCapture(screen, terminal.CursorRow))
                {
                    terminal.WriteInput("/usage\r");
                    commandSent = true;
                }
                else if (stopwatch.Elapsed > TimeSpan.FromSeconds(7))
                    throw new InvalidOperationException("Claude CLI did not reach a safe input prompt.");
            }
            else
            {
                if (TryParseUsageCapture(screen, DateTimeOffset.UtcNow, out ProviderSnapshot? snapshot)) return snapshot;
                if (terminal.HasExited) throw new InvalidOperationException("Claude CLI usage view was incomplete.");
            }

            await Task.Delay(35, cancellationToken).ConfigureAwait(false);
        }

        throw new OperationCanceledException("Probe deadline reached.", cancellationToken);
    }

    private static ProcessStartInfo CreateProcessStartInfo(CliCommand command, IEnumerable<string> args, string cwd,
        IReadOnlyDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (string arg in command.BuildArguments(args)) info.ArgumentList.Add(arg);
        info.Environment.Clear();
        foreach ((string key, string value) in environment) info.Environment[key] = value;
        return info;
    }

    private static IReadOnlyDictionary<string, string> BuildProbeEnvironment(string claudeHome)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || entry.Value is not string value || key.StartsWith('=')) continue;
            if (key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("CLAUDE_REMOTE_CONTROL_", StringComparison.OrdinalIgnoreCase)
                || key.Equals("CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase)
                || key.Equals("DISABLE_AUTOUPDATER", StringComparison.OrdinalIgnoreCase)
                || key.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase)
                || key.Equals("NODE_PATH", StringComparison.OrdinalIgnoreCase)) continue;
            values[key] = value;
        }

        values["CLAUDE_CONFIG_DIR"] = claudeHome;
        values["CLAUDE_CODE_SKIP_PROMPT_HISTORY"] = "1";
        values["CLAUDE_CODE_SAFE_MODE"] = "1";
        values["DISABLE_AUTOUPDATER"] = "1";
        return values;
    }

    private static string GetProbeDirectoryPath()
    {
        string basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Quotra", "ClaudeUsageProbe");
        string fullPath = Path.GetFullPath(basePath);
        if (!IsSafeLocalPath(fullPath, requireExists: false))
            throw new InvalidOperationException("Claude CLI usage-probe folder is not a safe local path.");
        return fullPath;
    }

    internal static string? FindTrustedWorkingDirectory(string claudeHome, string privateProbeDirectory, string userProfile)
    {
        try
        {
            string privatePath = Path.GetFullPath(privateProbeDirectory);
            string[] metadataPaths =
            [
                Path.Combine(Path.GetFullPath(claudeHome), ".claude.json"),
                Path.Combine(Path.GetFullPath(userProfile), ".claude.json")
            ];
            var trustedPaths = new List<string>();
            foreach (string metadataPath in metadataPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                trustedPaths.AddRange(ReadTrustedDirectoryEntries(metadataPath));

            if (trustedPaths.Any(path => SamePath(path, privatePath)) && IsSafeLocalPath(privatePath, requireExists: false))
            {
                Directory.CreateDirectory(privatePath);
                if (IsSafeLocalDirectory(privatePath)) return privatePath;
                return null;
            }

            foreach (string candidate in trustedPaths)
            {
                if (SamePath(candidate, privatePath) || !IsSafeLocalDirectory(candidate)) continue;
                return Path.GetFullPath(candidate);
            }
        }
        catch { }
        return null;
    }

    internal static bool HasExplicitTrustForProbeDirectory(string metadataPath, string probeDirectory)
    {
        try
        {
            string fullPath = Path.GetFullPath(probeDirectory);
            return ReadTrustedDirectoryEntries(metadataPath).Any(path => SamePath(path, fullPath))
                && IsSafeLocalDirectory(fullPath);
        }
        catch { return false; }
    }

    // Stream only the `projects` trust flags. Other ~/.claude.json fields (including auth state) are skipped.
    private static IReadOnlyList<string> ReadTrustedDirectoryEntries(string metadataPath)
    {
        try
        {
            var file = new FileInfo(metadataPath);
            if (!file.Exists || file.Length is <= 0 or > 2 * 1024 * 1024
                || (file.Attributes & FileAttributes.ReparsePoint) != 0) return [];
            byte[] bytes = File.ReadAllBytes(metadataPath);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return [];
            var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return [];
                string? property = reader.GetString();
                if (!reader.Read()) return [];
                if (!string.Equals(property, "projects", StringComparison.Ordinal))
                {
                    reader.Skip();
                    continue;
                }
                if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) return [];
                    string? projectPath = reader.GetString();
                    if (!reader.Read()) return [];
                    bool trusted = false;
                    bool validEntry = reader.TokenType == JsonTokenType.StartObject;
                    if (validEntry)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType != JsonTokenType.PropertyName) return [];
                            string? field = reader.GetString();
                            if (!reader.Read()) return [];
                            if (string.Equals(field, "hasTrustDialogAccepted", StringComparison.Ordinal))
                            {
                                if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False)) validEntry = false;
                                else trusted = reader.GetBoolean();
                            }
                            else reader.Skip();
                        }
                    }
                    else reader.Skip();

                    if (string.IsNullOrWhiteSpace(projectPath) || !validEntry) continue;
                    if (seen.TryGetValue(projectPath, out bool previous) && previous != trusted)
                        seen[projectPath] = false;
                    else seen[projectPath] = trusted;
                }
            }
            return seen.Where(pair => pair.Value).Select(pair => pair.Key).ToArray();
        }
        catch { }
        return [];
    }

    private static bool IsSafeLocalDirectory(string path) => Directory.Exists(path) && IsSafeLocalPath(path, requireExists: true);

    private static bool IsSafeLocalPath(string path, bool requireExists)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath) ?? "";
            if (root.Length < 3 || root.StartsWith("\\\\", StringComparison.Ordinal)) return false;
            if (new DriveInfo(root).DriveType != DriveType.Fixed) return false;
            string current = root;
            foreach (string part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (!Directory.Exists(current) && !File.Exists(current))
                {
                    if (requireExists) return false;
                    continue;
                }
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return !requireExists || Directory.Exists(fullPath);
        }
        catch { return false; }
    }

    private static void TryDeleteProbeTranscript(string home, string sessionId)
    {
        try
        {
            string projects = Path.Combine(home, "projects");
            if (!Directory.Exists(projects) || (File.GetAttributes(projects) & FileAttributes.ReparsePoint) != 0) return;
            foreach (string folder in Directory.EnumerateDirectories(projects))
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (string name in new[] { sessionId + ".jsonl", Guid.Parse(sessionId).ToString("N") + ".jsonl" })
                {
                    string file = Path.Combine(folder, name);
                    if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                        File.Delete(file);
                }
            }
        }
        catch { }
    }

    internal static bool IsReadyPromptCapture(string screen, int cursorRow)
    {
        string[] lines = screen.Split('\n');
        if (cursorRow < 0 || cursorRow >= lines.Length) return false;
        string prompt = lines[cursorRow].Trim();
        if (prompt is not ("❯" or ">")) return false;
        for (int index = cursorRow + 1; index < lines.Length; index++)
        {
            string footer = NormalizeLine(lines[index]);
            if (footer.Length == 0 || footer.All(c => c == ' ' || c is >= '\u2500' and <= '\u257f')) continue;
            return footer.StartsWith("? for shortcuts", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool ContainsUnattendedDialog(string screen)
    {
        string lower = screen.ToLowerInvariant();
        string[] markers =
        [
            "do you trust", "trust this folder", "trust the files", "press enter to continue",
            "sign in", "log in", "login required", "consent", "accept the terms", "terms of service",
            "policy approval", "managed policy", "allow access", "approve access", "permission required",
            "authentication required", "choose an account", "select an organization"
        ];
        return markers.Any(lower.Contains);
    }

    internal static AuthStatus ParseAuthStatus(string json, string expectedHome)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumAuthJsonBytes)
            throw new InvalidOperationException("Claude CLI authentication response exceeded the capture limit.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("loggedIn", out JsonElement loggedIn)
                || loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("Claude CLI authentication response was incomplete.");
            if (!loggedIn.GetBoolean()) return new AuthStatus(false, null, null, null, null, null);

            string? authMethod = ReadString(root, "authMethod");
            string? subscriptionType = ReadString(root, "subscriptionType");
            string? apiProvider = ReadString(root, "apiProvider");
            string? configDirectory = ReadString(root, "configDirectory");
            if (configDirectory is not null && !SamePath(configDirectory, expectedHome))
                throw new InvalidOperationException("Claude CLI is using a different configuration directory.");

            string? accountUuid = ReadUuid(root, "accountUuid", "account_uuid", "userUuid", "user_uuid");
            string? organizationUuid = ReadUuid(root, "organizationUuid", "organization_uuid", "orgId", "orgUuid");
            string? email = ReadEmail(root);
            bool firstParty = apiProvider is null || apiProvider.Equals("firstParty", StringComparison.OrdinalIgnoreCase)
                || apiProvider.Equals("claude.ai", StringComparison.OrdinalIgnoreCase);
            string authMethodNormalized = authMethod?.ToLowerInvariant() ?? "";
            string subscriptionNormalized = subscriptionType?.ToLowerInvariant() ?? "";
            bool oauthLogin = authMethodNormalized is "claude.ai" or "oauth_token";
            bool subscription = subscriptionNormalized is "free" or "pro" or "max" or "team" or "enterprise";
            bool eligible = oauthLogin && firstParty && subscription;
            return new AuthStatus(true, eligible ? subscriptionNormalized : null, eligible ? accountUuid : null,
                eligible ? organizationUuid : null, eligible ? email : null, configDirectory);
        }
        catch (InvalidOperationException) { throw; }
        catch { throw new InvalidOperationException("Claude CLI authentication response was malformed."); }
    }

    internal static bool HasStableIdentity(AuthStatus auth) =>
        !string.IsNullOrWhiteSpace(auth.AccountUuid)
        || (!string.IsNullOrWhiteSpace(auth.OrganizationUuid) && !string.IsNullOrWhiteSpace(auth.Email));

    internal static bool SameStableIdentity(AuthStatus before, AuthStatus after) =>
        HasStableIdentity(before) && HasStableIdentity(after)
        && string.Equals(before.AccountUuid, after.AccountUuid, StringComparison.OrdinalIgnoreCase)
        && string.Equals(before.OrganizationUuid, after.OrganizationUuid, StringComparison.OrdinalIgnoreCase)
        && string.Equals(before.Email, after.Email, StringComparison.OrdinalIgnoreCase);

    internal static bool TryParseUsageCapture(string capture, DateTimeOffset capturedAt, out ProviderSnapshot? snapshot)
    {
        snapshot = null;
        if (HasIncompleteAnsi(capture)) return false;
        string text = StripAnsi(capture);
        string[] lines = text.Split('\n');
        var sessionSections = FindSections(lines, IsSessionHeader);
        var weeklySections = FindSections(lines, IsAllModelsHeader);
        if (sessionSections.Count != 1 || weeklySections.Count != 1 || sessionSections[0] >= weeklySections[0]) return false;

        if (!TryParseSection(lines, sessionSections[0], out WindowUsage? session)
            || !TryParseSection(lines, weeklySections[0], out WindowUsage? weekly)) return false;

        snapshot = new ProviderSnapshot(
            Primary: weekly,
            Secondary: session,
            Plan: "Claude Code 구독",
            Note: "Claude Code CLI /usage에서 조회",
            HistoryKey: "claude-cli",
            PrimaryLabel: "주간",
            SecondaryLabel: "5시간",
            CapturedAt: capturedAt);
        return true;
    }

    private static bool HasIncompleteAnsi(string input)
    {
        for (int index = 0; index < input.Length; index++)
        {
            if (input[index] != '\x1b') continue;
            if (index + 1 >= input.Length) return true;
            char kind = input[++index];
            if (kind == '[')
            {
                while (index + 1 < input.Length && !(input[index + 1] >= '@' && input[index + 1] <= '~')) index++;
                if (index + 1 >= input.Length) return true;
                index++;
            }
            else if (kind == ']')
            {
                while (index + 1 < input.Length && input[index + 1] != '\a'
                    && !(input[index + 1] == '\x1b' && index + 2 < input.Length && input[index + 2] == '\\')) index++;
                if (index + 1 >= input.Length) return true;
                if (input[index + 1] == '\x1b') index += 2;
                else index++;
            }
        }
        return false;
    }

    private static List<int> FindSections(string[] lines, Func<string, bool> matches)
    {
        var result = new List<int>();
        for (int i = 0; i < lines.Length; i++)
            if (matches(NormalizeLine(lines[i]))) result.Add(i);
        return result;
    }

    private static bool TryParseSection(string[] lines, int start, out WindowUsage? usage)
    {
        usage = null;
        var percentages = new List<(double Value, string Kind)>();
        var resets = new HashSet<DateTimeOffset>();
        for (int index = start + 1; index < lines.Length && index <= start + 18; index++)
        {
            string line = NormalizeLine(lines[index]);
            if (IsAnyUsageHeader(line)) break;
            MatchCollection matches = PercentRegex.Matches(line);
            if (line.Count(c => c == '%') != matches.Count) return false;
            foreach (Match match in matches)
            {
                if (!double.TryParse(match.Groups["value"].Value, NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value is < 0 or > 100)
                    return false;
                percentages.Add((value, match.Groups["kind"].Value));
            }
            MatchCollection resetMatches = IsoResetRegex.Matches(line);
            foreach (Match match in resetMatches)
                if (DateTimeOffset.TryParse(match.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset reset))
                    resets.Add(reset.ToUniversalTime());
        }

        if (percentages.Count != 1) return false;
        (double percent, string kind) = percentages[0];
        double used = kind.Equals("remaining", StringComparison.OrdinalIgnoreCase) ? 100 - percent : percent;
        DateTimeOffset? resetAt = resets.Count == 1 ? resets.Single() : null;
        usage = new WindowUsage(used / 100d, resetAt);
        return true;
    }

    private static bool IsSessionHeader(string line) => line.Equals("Current session", StringComparison.OrdinalIgnoreCase);
    private static bool IsAllModelsHeader(string line) =>
        line.Equals("Current week (all models)", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnyUsageHeader(string line) =>
        IsSessionHeader(line) || line.StartsWith("Current week", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Current month", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Weekly limits", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Extra usage", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Usage credits", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Usage insights", StringComparison.OrdinalIgnoreCase)
        || line.Equals("Insights", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("What's contributing", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("What’s contributing", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Usage by model", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeLine(string line) => Regex.Replace(line, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static string StripAnsi(string input)
    {
        var result = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            if (ch == '\x1b')
            {
                if (++i >= input.Length) break;
                if (input[i] == '[')
                {
                    while (i + 1 < input.Length && !(input[i + 1] >= '@' && input[i + 1] <= '~')) i++;
                    if (i + 1 < input.Length) i++;
                }
                else if (input[i] == ']')
                {
                    while (i + 1 < input.Length && input[i + 1] != '\a' && input[i + 1] != '\x1b') i++;
                    if (i + 1 < input.Length && input[i + 1] == '\x1b' && i + 2 < input.Length && input[i + 2] == '\\') i += 2;
                    else if (i + 1 < input.Length) i++;
                }
                continue;
            }
            if (ch == '\r')
            {
                result.Append('\n');
                if (i + 1 < input.Length && input[i + 1] == '\n') i++;
                continue;
            }
            if (ch == '\n' || ch == '\t' || ch >= ' ') result.Append(ch);
        }
        return result.ToString();
    }

    private static string? ReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement element)) return null;
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw new InvalidOperationException("Claude CLI authentication fields were malformed.");
        string? value = element.GetString()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? ReadUuid(JsonElement root, params string[] properties)
    {
        string? value = null;
        foreach (string property in properties)
        {
            if (!root.TryGetProperty(property, out JsonElement element) || element.ValueKind == JsonValueKind.Null) continue;
            if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
                throw new InvalidOperationException("Claude CLI identity fields were malformed.");
            if (!Guid.TryParse(element.GetString(), out Guid parsed))
                throw new InvalidOperationException("Claude CLI identity fields were ambiguous.");
            string current = parsed.ToString("D");
            if (value is not null && !value.Equals(current, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Claude CLI identity fields were inconsistent.");
            value = current;
        }
        return value;
    }

    private static string? ReadEmail(JsonElement root)
    {
        string? email = ReadString(root, "email");
        if (email is null) return null;
        if (email.Length > 254 || email.Any(char.IsControl) || !email.Contains('@'))
            throw new InvalidOperationException("Claude CLI identity fields were malformed.");
        return email;
    }

    private static bool SamePath(string left, string right)
    {
        try { return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static CliCommand? DiscoverCli()
    {
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localNative = Path.Combine(userHome, ".local", "bin", "claude.exe");
        if (File.Exists(localNative)) return new CliCommand(localNative, null);

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string path = directory.Trim().Trim('"');
            if (path.Length == 0) continue;
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(path, "claude.exe")); }
            catch { continue; }
            if (File.Exists(candidate)) return new CliCommand(candidate, null);
        }

        string[] trustedNodeDirectories = GetTrustedNodeDirectories();
        string? node = trustedNodeDirectories.Select(dir => Path.Combine(dir, "node.exe")).FirstOrDefault(File.Exists);
        if (node is null) return null;

        var packageRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        packageRoots.Add(Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code"));
        packageRoots.Add(Path.Combine(localAppData, "npm", "node_modules", "@anthropic-ai", "claude-code"));
        foreach (string directory in trustedNodeDirectories)
            packageRoots.Add(Path.Combine(directory, "node_modules", "@anthropic-ai", "claude-code"));

        foreach (string root in packageRoots)
            if (VerifyInstalledNpmCli(root, out string? cliPath)) return new CliCommand(node, cliPath);
        return null;
    }

    private static string[] GetTrustedNodeDirectories()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs");
        foreach (string path in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = path.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(candidate) && directories.Contains(Normalize(candidate))) continue;
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string node = Path.Combine(candidate, "node.exe");
            if (!File.Exists(node)) continue;
            string full = Path.GetFullPath(node);
            string dir = Path.GetDirectoryName(full)!;
            if (IsUnderKnownNodeRoot(dir)) directories.Add(dir);
        }
        return directories.Where(Directory.Exists).ToArray();

        void Add(params string[] parts)
        {
            string first = parts[0];
            if (string.IsNullOrWhiteSpace(first)) return;
            string dir = Path.Combine(parts);
            directories.Add(Normalize(dir));
        }
    }

    private static bool IsUnderKnownNodeRoot(string directory)
    {
        string[] roots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs")
        ];
        return roots.Any(root => !string.IsNullOrWhiteSpace(root) && SamePath(directory, root));
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path; }
    }

    private static bool VerifyInstalledNpmCli(string packageRoot, out string? cliPath)
    {
        cliPath = null;
        try
        {
            string root = Path.GetFullPath(packageRoot);
            string manifest = Path.Combine(root, "package.json");
            string entry = Path.Combine(root, "cli.js");
            if (!Directory.Exists(root) || !File.Exists(manifest) || !File.Exists(entry)) return false;
            if ((File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0
                || new FileInfo(manifest).Length > 64 * 1024 || new FileInfo(entry).Length > 64 * 1024 * 1024) return false;

            using JsonDocument package = JsonDocument.Parse(File.ReadAllBytes(manifest), new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement data = package.RootElement;
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("name", out JsonElement name)
                || name.ValueKind != JsonValueKind.String || name.GetString() != "@anthropic-ai/claude-code"
                || !data.TryGetProperty("bin", out JsonElement bin)) return false;
            bool entryVerified = bin.ValueKind == JsonValueKind.String && bin.GetString() == "cli.js";
            if (bin.ValueKind == JsonValueKind.Object && bin.TryGetProperty("claude", out JsonElement claudeBin))
                entryVerified = claudeBin.ValueKind == JsonValueKind.String && claudeBin.GetString() == "cli.js";
            if (!entryVerified) return false;
            cliPath = entry;
            return true;
        }
        catch { return false; }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, OutputBudget budget, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        char[] buffer = new char[2048];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToString();
            budget.Add(buffer.AsSpan(0, read));
            output.Append(buffer, 0, read);
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    private sealed class OutputBudget(int maximumBytes)
    {
        private int _written;
        internal void Add(ReadOnlySpan<char> chars)
        {
            int next = Interlocked.Add(ref _written, Encoding.UTF8.GetByteCount(chars));
            if (next > maximumBytes) throw new InvalidOperationException("Claude CLI output exceeded the capture limit.");
        }
    }

    internal sealed record AuthStatus(bool LoggedIn, string? SubscriptionType, string? AccountUuid,
        string? OrganizationUuid, string? Email, string? ConfigDirectory)
    {
        internal bool IsSubscriptionLogin => LoggedIn && SubscriptionType is not null;
    }

    private sealed record CliCommand(string ExecutablePath, string? ScriptPath)
    {
        internal IEnumerable<string> BuildArguments(IEnumerable<string> args) => ScriptPath is null
            ? args
            : new[] { ScriptPath }.Concat(args);
    }
}
