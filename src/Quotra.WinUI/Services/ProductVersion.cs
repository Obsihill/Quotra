using System.Reflection;

namespace Quotra.WinUI.Services;

/// <summary>
/// 앱 버전의 단일 출처 — 어셈블리 메타데이터(<c>Quotra.WinUI.csproj</c> 의 <c>Version</c>)에서 읽는다.
/// User-Agent·프로토콜 초기화처럼 버전을 문자열로 보내는 곳은 여기서만 가져간다(하드코딩 금지).
/// </summary>
internal static class ProductVersion
{
    /// <summary>예: "1.0.2". 메타데이터가 없으면 "0.0.0".</summary>
    public static string Value { get; } = Read();

    private static string Read()
    {
        string? informational = typeof(ProductVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // 소스 리비전 접미사("1.0.2+abc1234")는 버전이 아니다 — 앞부분만 쓴다.
        string value = informational?.Split('+')[0].Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(value) ? "0.0.0" : value;
    }
}
