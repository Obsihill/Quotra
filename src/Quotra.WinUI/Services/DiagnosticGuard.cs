namespace Quotra.WinUI.Services;

/// <summary>
/// 진단 재정의(<c>--provider-url=&lt;id&gt;=&lt;주소&gt;</c>)가 켜졌을 때의 안전 규칙.
///
/// 진단 주소는 로컬 루프백(http/https)만 허용한다 — 실계정 토큰이 사용자가 지정한
/// 임의 호스트로 실려 나가는 일을 막는다(Claude 소스의 진단 가드와 같은 fail-closed 원칙).
/// </summary>
internal static class DiagnosticGuard
{
    /// <summary>주소가 로컬 루프백이 아니면 조회를 거부한다(거부 사유는 화면·로그에 남는다).</summary>
    public static void EnsureLoopback(string url, string label)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.IsLoopback && uri.Scheme is "http" or "https")
        {
            return;
        }

        throw new InvalidOperationException(
            $"{label} 진단 URL은 로컬 루프백 주소(http/https)만 허용합니다 — {url}");
    }
}
