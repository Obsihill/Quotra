using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quotra.Providers;

namespace Quotra.WinUI.Services;

/// <summary>Read numbers delivered by Claude Code, bound to the login observed at session start.</summary>
internal static class ClaudeStatusLineBridge
{
    private static string Command(string home) => "\"" + Path.Combine(AppContext.BaseDirectory, "QuotraWinUI.exe").Replace('\\', '/')
        + "\" --claude-statusline";

    internal static bool IsInstalled => IsManaged(ReadSettings(ProviderAuthPaths.ClaudeHome), ProviderAuthPaths.ClaudeHome);

    internal static void Install(string? home = null)
    {
        home ??= ProviderAuthPaths.ClaudeHome;
        JsonObject settings = ReadSettings(home);
        string before = settings.ToJsonString();
        if (settings["statusLine"] is not null && !IsManaged(settings, home))
            throw new InvalidOperationException("기존 Claude 상태줄 설정이 있어 자동 교체하지 않았습니다. 기존 상태줄에서 Quotra 수집기를 함께 호출하도록 설정할 수 있습니다.");
        Directory.CreateDirectory(home);
        if (IsManaged(settings, home)) return;
        settings["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = Command(home) };
        SaveSettings(home, settings, before);
    }

    internal static void Remove(string? home = null)
    {
        home ??= ProviderAuthPaths.ClaudeHome;
        JsonObject settings = ReadSettings(home);
        string before = settings.ToJsonString();
        if (!IsManaged(settings, home)) return;
        settings.Remove("statusLine");
        SaveSettings(home, settings, before);
    }

    private static bool IsManaged(JsonObject settings, string home) =>
        settings["statusLine"] is JsonObject line && line["command"] is JsonValue command
            && command.TryGetValue<string>(out string? text) && text == Command(home);

    private static JsonObject ReadSettings(string home)
    {
        string path = Path.Combine(home, "settings.json");
        if (!File.Exists(path)) return new JsonObject();
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("Claude 설정 파일 형식을 읽을 수 없습니다. 기존 설정은 유지했습니다."); }
    }

    private static void SaveSettings(string home, JsonObject settings, string before)
    {
        string path = Path.Combine(home, "settings.json");
        string temporary = Path.Combine(home, ".quotra-settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Claude 설정 파일이 링크여서 변경하지 않았습니다.");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            if (File.Exists(path)) new FileInfo(temporary).SetAccessControl(new FileInfo(path).GetAccessControl());
            File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (ReadSettings(home).ToJsonString() != before)
                throw new InvalidOperationException("Claude 설정이 변경되어 덮어쓰지 않았습니다. 다시 시도하세요.");
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string Fingerprint(string credentials)
    {
        using var input = File.OpenRead(credentials);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    // Called before WinUI initialization. No logging: stdin may contain private session data.
    internal static void Collect(string input)
    {
        try
        {
            using JsonDocument data = JsonDocument.Parse(input);
            JsonElement root = data.RootElement;
            string home = ProviderAuthPaths.ClaudeHome;
            string credentials = Path.Combine(home, ".credentials.json");
            if (root.TryGetProperty("session_id", out JsonElement session) && session.ValueKind == JsonValueKind.String
                && session.GetString() is { Length: > 0 and <= 100 } id && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                && File.Exists(credentials))
            {
                string directory = Path.Combine(home, "quotra");
                Directory.CreateDirectory(directory);
                string fingerprint = Fingerprint(credentials);
                string binding = Path.Combine(directory, "session-" + id + ".txt");
                try
                {
                    using var file = new FileStream(binding, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var writer = new StreamWriter(file);
                    writer.Write(fingerprint);
                }
                catch (IOException) when (File.Exists(binding)) { }
                if (File.ReadAllText(binding) == fingerprint)
                {
                    var sample = new JsonObject { ["captured_at"] = DateTimeOffset.UtcNow.ToString("O"), ["credential_fingerprint"] = fingerprint };
                    if (root.TryGetProperty("rate_limits", out JsonElement limits) && limits.ValueKind == JsonValueKind.Object)
                        foreach (string name in new[] { "five_hour", "seven_day" })
                            if (limits.TryGetProperty(name, out JsonElement window) && window.ValueKind == JsonValueKind.Object
                                && window.TryGetProperty("used_percentage", out JsonElement used) && used.ValueKind == JsonValueKind.Number
                                && window.TryGetProperty("resets_at", out JsonElement resets) && resets.ValueKind == JsonValueKind.Number)
                                sample[name] = new JsonObject { ["used_percentage"] = JsonNode.Parse(used.GetRawText()), ["resets_at"] = JsonNode.Parse(resets.GetRawText()) };
                    string temporary = Path.Combine(directory, "sample-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        File.WriteAllText(temporary, sample.ToJsonString());
                        // Do not publish a sample across a concurrent login change.
                        if (Fingerprint(credentials) == fingerprint) File.Move(temporary, Path.Combine(directory, "statusline-usage.json"), overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
            List<string> parts = [];
            if (root.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.Object
                && model.TryGetProperty("display_name", out JsonElement display) && display.ValueKind == JsonValueKind.String)
                parts.Add(display.GetString()!);
            if (root.TryGetProperty("rate_limits", out JsonElement rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
                foreach ((string key, string label) in new[] { ("five_hour", "5h"), ("seven_day", "7d") })
                    if (rateLimits.TryGetProperty(key, out JsonElement window) && window.ValueKind == JsonValueKind.Object
                        && window.TryGetProperty("used_percentage", out JsonElement used) && used.TryGetDouble(out double percent))
                        parts.Add($"{label}: {percent:0.#}%");
            Console.WriteLine(string.Join(" | ", parts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or ArgumentException) { }
    }

    internal static ProviderSnapshot? Read(string credentials, DateTimeOffset? utcNow = null)
    {
        string home = Path.GetDirectoryName(credentials)!;
        string sample = Path.Combine(home, "quotra", "statusline-usage.json");
        DateTimeOffset now = utcNow ?? DateTimeOffset.UtcNow;
        try
        {
            if (!File.Exists(sample) || new FileInfo(sample).Length > 16_384) return null;
            using JsonDocument data = JsonDocument.Parse(File.ReadAllText(sample));
            JsonElement root = data.RootElement;
            string fingerprint = Fingerprint(credentials);
            if (root.GetProperty("credential_fingerprint").GetString() != fingerprint) return null;
            DateTimeOffset captured = root.GetProperty("captured_at").GetDateTimeOffset();
            if (captured > now.AddSeconds(5) || now - captured > TimeSpan.FromMinutes(2)) return null;
            var normalized = new JsonObject();
            foreach (string windowName in new[] { "five_hour", "seven_day" })
            {
                if (!root.TryGetProperty(windowName, out JsonElement window) || window.ValueKind != JsonValueKind.Object) continue;
                if (!window.TryGetProperty("used_percentage", out JsonElement used) || !used.TryGetDouble(out double percent)
                    || !double.IsFinite(percent) || percent < 0 || percent > 100) continue;
                if (!window.TryGetProperty("resets_at", out JsonElement reset) || !reset.TryGetInt64(out long seconds)
                    || seconds <= 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) continue;
                DateTimeOffset resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (resetsAt <= now) continue;
                normalized[windowName] = new JsonObject { ["utilization"] = percent, ["resets_at"] = resetsAt.ToString("O") };
            }
            if (normalized.Count == 0) return null;
            if (Fingerprint(credentials) != fingerprint) return null;
            using JsonDocument usage = JsonDocument.Parse(normalized.ToJsonString());
            return ClaudeUsageSource.ParseUsage(usage.RootElement, null) with
            {
                CapturedAt = captured,
                Note = $"Claude 상태줄에서 받은 한도 · 수집 {captured.ToLocalTime():HH:mm:ss}",
                HistoryKey = "claude-statusline-" + fingerprint[..16] + "-" + (normalized.ContainsKey("seven_day") ? "weekly" : "five-hour"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException) { return null; }
    }
}
