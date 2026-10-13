namespace Quotra.Providers;

/// <summary>Provider-owned login folders. Resolve at every refresh so an app restart is not required.</summary>
public static class ProviderAuthPaths
{
    public static string CodexHome => ResolveHome("CODEX_HOME", ".codex");
    public static string ClaudeHome => ResolveHome("CLAUDE_CONFIG_DIR", ".claude");

    public static string ResolveHome(string variable, string defaultFolder)
    {
        string? configured = Environment.GetEnvironmentVariable(variable);
        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(configured))
                configured = Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Machine);
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(configured)) return Path.Combine(profile, defaultFolder);
        configured = Environment.ExpandEnvironmentVariables(configured.Trim());
        if (configured.StartsWith("~/", StringComparison.Ordinal) || configured.StartsWith("~\\", StringComparison.Ordinal))
            configured = Path.Combine(profile, configured[2..]);
        try { return Path.GetFullPath(configured, profile); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"{variable}의 로그인 폴더 경로가 올바르지 않습니다.");
        }
    }
}
