using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Quotra.WinUI.Services;

internal sealed class ClaudeAccountChangedException()
    : ProviderNotConnectedException("Claude 로그인 계정이 조회 중 변경되었거나 서로 다릅니다. 새로고침하세요.");

/// <summary>Only a server profile establishes ownership for cross-source fallback.</summary>
internal sealed record ClaudeAccountIdentity(string AccountUuid, string OrganizationUuid, string? Email = null)
{
    internal string HistoryId => Hash(AccountUuid + ":" + OrganizationUuid)[..16];

    internal bool Matches(ClaudeAccountIdentity other) =>
        string.Equals(AccountUuid, other.AccountUuid, StringComparison.OrdinalIgnoreCase)
        && string.Equals(OrganizationUuid, other.OrganizationUuid, StringComparison.OrdinalIgnoreCase);

    internal static ClaudeAccountIdentity? FromOAuth(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("account", out JsonElement account)
            || !root.TryGetProperty("organization", out JsonElement organization)) return null;
        return Create(Text(account, "uuid"), Text(organization, "uuid"), Text(account, "email"));
    }

    internal static ClaudeAccountIdentity? FromDesktop(JsonElement root, string organization)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("account", out JsonElement account)) root = account;
        return Create(Text(root, "uuid"), organization, Text(root, "email"));
    }

    internal static ClaudeAccountIdentity? Create(string? account, string? organization, string? email = null) =>
        Guid.TryParse(account, out Guid accountId) && Guid.TryParse(organization, out Guid organizationId)
            ? new(accountId.ToString(), organizationId.ToString(), email) : null;

    internal static string? Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>Cache verified account IDs against a token hash. Never stores a token or cookie.</summary>
internal sealed class ClaudeIdentityCache(string directory)
{
    private sealed record Entry(string TokenHash, string AccountUuid, string OrganizationUuid);

    internal ClaudeAccountIdentity? Read(string profile, string tokenHash)
    {
        try
        {
            string path = PathFor(profile);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            Entry? entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
            return entry?.TokenHash == tokenHash ? ClaudeAccountIdentity.Create(entry.AccountUuid, entry.OrganizationUuid) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal void Write(string profile, string tokenHash, ClaudeAccountIdentity identity)
    {
        string temporary = PathFor(profile) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Entry(tokenHash, identity.AccountUuid, identity.OrganizationUuid)));
            File.Move(temporary, PathFor(profile), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    private string PathFor(string profile) => Path.Combine(directory, ClaudeAccountIdentity.Hash(Path.GetFullPath(profile).ToUpperInvariant()) + ".json");
}
