using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Windows.Security.Credentials;

namespace Quotra.WinUI.Services;

/// <summary>
/// OpenCode Go(Zen) 사용량 — API 키 입력형 소스. 월간 한도만 주 배터리에 표시한다.
/// 키는 Windows 자격 증명 보관함에만 저장하며 로그·설정 파일에는 남기지 않는다.
/// </summary>
[UsageSource("opencode-go")]
public sealed class OpenCodeUsageSource : IKeyedUsageSource, IUsageSelfTest
{
    private const string Resource = "Quotra.OpenCodeGo";
    private const string Account = "api-key";

    public string DisplayName => "OpenCode Go 사용량";

    public string ShortName => "OpenCode Go";

    public string KeyLabel => "OpenCode Go";

    public string KeyHint => "OpenCode Go API 키";

    public string SelfTestName => "OpenCode Go 파서";

    public string? LoadKey()
    {
        try
        {
            PasswordCredential credential = new PasswordVault().Retrieve(Resource, Account);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public void SaveKey(string key)
    {
        DeleteKey();
        new PasswordVault().Add(new PasswordCredential(Resource, Account, key.Trim()));
    }

    public void DeleteKey()
    {
        PasswordVault vault = new();
        try
        {
            vault.Remove(vault.Retrieve(Resource, Account));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }

    public Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        string? key = LoadKey();
        return string.IsNullOrWhiteSpace(key)
            ? Task.FromResult(new ProviderSnapshot())
            : FetchWithKeyAsync(key, cancellationToken);
    }

    public async Task<string> VerifyKeyAsync(string key, CancellationToken cancellationToken)
    {
        ProviderSnapshot snapshot = await FetchWithKeyAsync(key, cancellationToken).ConfigureAwait(false);
        return snapshot.Note ?? "사용량 조회에 성공했습니다.";
    }

    public bool RunSelfTest()
    {
        try
        {
            VerifyParser();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write(ex, "OpenCode Go 파서 점검 실패");
            return false;
        }
    }

    public static async Task<ProviderSnapshot> FetchWithKeyAsync(string key, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://opencode.ai/zen/go/v1/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException((int)response.StatusCode is 401 or 403
                ? "API 키가 유효하지 않거나 사용량 조회 권한이 없습니다."
                : $"OpenCode Go 조회 실패 (HTTP {(int)response.StatusCode}). 잠시 후 다시 시도하세요.");
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var usage = json.RootElement.GetProperty("usage");
            var monthly = ParseWindow(usage, "monthly");
            if (monthly is null) throw new InvalidOperationException("월간 사용량을 확인할 수 없습니다.");
            return new ProviderSnapshot(monthly, null, "OpenCode Go", $"월간 잔여 {(1 - monthly.Value.Used) * 100:0.#}%", HistoryKey: "opencode-go-monthly");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new InvalidOperationException("OpenCode Go 사용량 응답 형식을 확인할 수 없습니다.");
        }
    }

    internal static WindowUsage? ParseWindow(JsonElement usage, string name)
    {
        if (!usage.TryGetProperty(name, out var row) || row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("percent", out var percent) || percent.ValueKind != JsonValueKind.Number || !percent.TryGetDouble(out var value) || !double.IsFinite(value)) return null;
        DateTimeOffset? reset = null;
        if (row.TryGetProperty("resetsAt", out var timestamp) && timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetDouble(out var epoch) && double.IsFinite(epoch) && epoch > 0)
            reset = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(epoch > 1e10 ? epoch : epoch * 1000)));
        return new WindowUsage(Math.Clamp(value / 100, 0, 1), reset);
    }

    internal static void VerifyParser()
    {
        using var json = JsonDocument.Parse("""{"weekly":{"percent":25,"resetsAt":1800000000},"rolling":{"percent":0,"resetsAt":1800000000000},"monthly":{"percent":150},"bad":{"percent":null}}""");
        var root = json.RootElement;
        var weekly = ParseWindow(root, "weekly");
        var rolling = ParseWindow(root, "rolling");
        if (weekly?.Used != .25 || rolling?.Used != 0 || weekly?.ResetsAt != rolling?.ResetsAt ||
            ParseWindow(root, "monthly")?.Used != 1 || ParseWindow(root, "bad") is not null || ParseWindow(root, "missing") is not null)
            throw new InvalidOperationException("OpenCode Go 파서 검증 실패");
        Log.Write("OpenCode Go 파서 검증 통과: 사용률·0%·초/ms 리셋·범위 제한·없는 값");
    }
}
