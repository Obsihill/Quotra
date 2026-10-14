using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Quotra.Providers;

namespace Quotra.WinUI.Services;

/// <summary>Ask the installed Codex client to resolve its own file/keyring login. No tokens cross RPC.</summary>
internal static class CodexAppServerClient
{
    public static async Task<CodexFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        string? executable = FindExecutable();
        if (executable is null)
            return CodexFetchResult.Fail(CodexFetchStatus.NoCredential, "설치된 Codex 조회 클라이언트를 찾지 못했습니다.");
        Log.Write($"Codex 조회 클라이언트: {executable}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            },
        };
        process.StartInfo.Arguments = "app-server";
        process.StartInfo.Environment["CODEX_HOME"] = ProviderAuthPaths.CodexHome;
        Task? drain = null;
        string stage = "실행";
        try
        {
            process.Start();
            // Never retain or log the child process's diagnostic stream.
            drain = DrainAsync(process.StandardError, timeout.Token);
            stage = "초기화";
            // "$"를 4개 쓰는 이유: 본문에 닫는 중괄호가 3개 연속으로 나오므로 보간 구분자(중괄호)가 그보다 길어야 한다(CS9007).
            await SendAsync(process, $$$$"""{"id":1,"method":"initialize","params":{"clientInfo":{"name":"quotra","title":"Quotra","version":"{{{{ProductVersion.Value}}}}"}}}""", timeout.Token).ConfigureAwait(false);
            await ReadReplyAsync(process, 1, timeout.Token).ConfigureAwait(false);
            await SendAsync(process, """{"method":"initialized","params":{}}""", timeout.Token).ConfigureAwait(false);
            stage = "계정";
            await SendAsync(process, """{"id":2,"method":"account/read","params":{"refreshToken":false}}""", timeout.Token).ConfigureAwait(false);
            JsonElement accountResult = await ReadReplyAsync(process, 2, timeout.Token).ConfigureAwait(false);
            if (!accountResult.TryGetProperty("account", out JsonElement account) || account.ValueKind == JsonValueKind.Null)
                return CodexFetchResult.Fail(CodexFetchStatus.NoCredential, "Codex 클라이언트에 저장된 로그인이 없습니다. Codex에서 ChatGPT 계정으로 로그인하세요.");
            if (account.TryGetProperty("type", out JsonElement type) && type.GetString() == "apiKey")
                return CodexFetchResult.Fail(CodexFetchStatus.NoCredential, "Codex가 API 키로 로그인되어 있습니다. 구독 한도 조회에는 ChatGPT 계정 로그인이 필요합니다.");

            stage = "사용량";
            await SendAsync(process, """{"id":3,"method":"account/rateLimits/read"}""", timeout.Token).ConfigureAwait(false);
            JsonElement result = await ReadReplyAsync(process, 3, timeout.Token).ConfigureAwait(false);
            JsonElement bucket = SelectBucket(result);
            if (!CodexUsageParser.TryParse(bucket.GetRawText(), out CodexUsage usage, out _))
                return CodexFetchResult.Fail(CodexFetchStatus.SchemaChanged, "Codex가 숫자로 된 사용 한도를 반환하지 않았습니다.");
            if (account.TryGetProperty("planType", out JsonElement plan) && plan.ValueKind == JsonValueKind.String)
                usage = usage with { Plan = plan.GetString() ?? usage.Plan };
            return new CodexFetchResult(CodexFetchStatus.Ok, usage, "Codex 클라이언트 로그인으로 조회", "codex:account/rateLimits/read");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return CodexFetchResult.Fail(CodexFetchStatus.NetworkError, "Codex 클라이언트 조회 시간이 초과되었습니다(20초).");
        }
        catch (InvalidOperationException)
        {
            return CodexFetchResult.Fail(CodexFetchStatus.NoCredential, "Codex 클라이언트가 계정 조회를 거부했습니다. Codex에서 로그인을 갱신하고 새로고침하세요.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            int exitCode = -1;
            try { if (process.HasExited) exitCode = process.ExitCode; }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            Log.Write($"Codex 클라이언트 조회 실패: {stage} · {ex.GetType().Name} · HResult={ex.HResult:X8} · 종료={exitCode}");
            return CodexFetchResult.Fail(CodexFetchStatus.NoCredential, "설치된 Codex 클라이언트를 실행하거나 응답을 읽을 수 없습니다.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            timeout.Cancel();
            if (drain is not null) try { await drain.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    internal static JsonElement SelectBucket(JsonElement result)
    {
        if (result.TryGetProperty("rateLimitsByLimitId", out JsonElement buckets) && buckets.ValueKind == JsonValueKind.Object)
        {
            if (buckets.TryGetProperty("codex", out JsonElement codex)) return codex;
            // A different metered product must never become the Codex gauge.
            if (buckets.EnumerateObject().Any()) throw new JsonException("Codex bucket missing.");
        }
        if (result.TryGetProperty("rateLimits", out JsonElement legacy) && legacy.ValueKind == JsonValueKind.Object)
        {
            if (legacy.TryGetProperty("limitId", out JsonElement id) && id.ValueKind == JsonValueKind.String && id.GetString() != "codex")
                throw new JsonException("Different quota bucket.");
            return legacy;
        }
        throw new JsonException("Rate limits missing.");
    }

    private static async Task SendAsync(Process process, string message, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(message.AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadReplyAsync(Process process, int expectedId, CancellationToken token)
    {
        while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            // Ignore unrelated notifications, including auth refresh requests. Never echo their contents.
            if (line.Length > 4 * 1024 * 1024) throw new JsonException("RPC reply too large.");
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int value) || value != expectedId) continue;
            if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("Codex RPC failed.");
            if (!root.TryGetProperty("result", out JsonElement result)) throw new JsonException("RPC result missing.");
            return result.Clone();
        }
        throw new IOException("Codex RPC ended.");
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        char[] buffer = new char[2048];
        try { while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) > 0) { } }
        catch (IOException) { }
    }

    internal static string? FindExecutable()
    {
        // The desktop bundle contains the native CLI even when npm/CLI isn't separately installed.
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string physicalLocal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        try
        {
            foreach (string bundleLocal in new[] { local, physicalLocal }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string bundled = Path.Combine(bundleLocal, "OpenAI", "Codex", "bin");
                if (!Directory.Exists(bundled)) continue;
                string? found = Directory.EnumerateDirectories(bundled)
                    .Select(dir => Path.Combine(dir, "codex.exe"))
                    .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (found is not null) return found;
            }
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                // WindowsApps aliases can start the graphical desktop app instead of the native RPC server.
                if (dir.TrimEnd(Path.DirectorySeparatorChar).EndsWith("Microsoft\\WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
                string candidate = Path.Combine(dir.Trim('"'), "codex.exe");
                if (File.Exists(candidate)) return candidate;
            }
            // Microsoft Store desktop installs keep the CLI under their package's resources.
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                if (!package.Id.Name.Contains("Codex", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string relative in new[] { "app/resources/codex.exe", "resources/codex.exe", "app/resources/bin/codex.exe", "bin/codex.exe" })
                {
                    string candidate = Path.Combine(package.InstalledLocation.Path, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        return null;
    }
}
