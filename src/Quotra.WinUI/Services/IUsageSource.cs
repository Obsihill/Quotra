namespace Quotra.WinUI.Services;

/// <summary>No usable login is registered; distinct from a temporary usage-query failure.</summary>
internal class ProviderNotConnectedException(string message) : InvalidOperationException(message);

/// <summary>한도 창 하나의 조회 결과.</summary>
/// <param name="Used">사용률 0~1(남은 양이 아니다).</param>
/// <param name="ResetsAt">이 창이 리셋되는 시각. 모르면 null.</param>
public readonly record struct WindowUsage(double Used, DateTimeOffset? ResetsAt);

/// <summary>
/// 프로바이더 한 곳의 조회 결과.
///
/// null 은 "값 없음"이다 — 0(0% 사용)과 반드시 구분한다.
/// 0 으로 채워 버리면 연동이 안 된 상태가 "한도가 가득 찼다"로 둔갑한다.
/// </summary>
public sealed record ProviderSnapshot(
    WindowUsage? Primary = null,
    WindowUsage? Secondary = null,
    string? Plan = null,
    string? Note = null,
    string? HistoryKey = null,
    string? PrimaryLabel = null,
    string? SecondaryLabel = null,
    DateTimeOffset? CapturedAt = null);

/// <summary>
/// **확장 지점** — 프로바이더 한 곳의 사용량을 실제로 가져오는 구현체.
///
/// 새 프로바이더를 붙이는 방법은 두 단계뿐이다.
///   1. <c>Data/providers.json</c> 에 정의를 추가한다(이름·색·한도 창 라벨).
///   2. 이 인터페이스를 구현하고 <see cref="UsageSourceAttribute"/> 로 id 를 표시한다 — 등록은 자동이다.
/// API 키 입력형은 <see cref="IKeyedUsageSource"/> 를 함께 구현하면 설정 창에 자동으로 나타난다.
///
/// 구현 규칙
///   · 네트워크/파일 IO 는 <see cref="Task"/> 로 돌려준다(앱이 UI 스레드에서 호출한다).
///   · 실패는 예외로 알린다 — 서비스가 그 프로바이더만 "조회 오류"로 표시하고 나머지는 계속 돈다.
///   · 확실하지 않은 값은 <c>null</c> 로 둔다. 추측해서 채우지 않는다.
/// </summary>
public interface IUsageSource
{
    /// <summary>로그에 쓰는 이름(예: "Codex 주간 한도").</summary>
    string DisplayName { get; }

    /// <summary>로그에 쓰는 짧은 이름(예: "Codex", "시뮬레이션").</summary>
    string ShortName { get; }

    /// <summary>현재 사용량을 가져온다.</summary>
    Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// **API 키 입력형** 프로바이더의 능력 계약.
///
/// 이 인터페이스를 구현하면 설정 창(우클릭 → 프로바이더 설정…)이 그 프로바이더를
/// 자동으로 목록에 올린다 — 화면 코드는 프로바이더 이름을 모른다.
/// 키는 자격 증명 보관함에만 저장하고, 로그·설정 파일·오류 문구에는 남기지 않는다.
/// </summary>
public interface IKeyedUsageSource : IUsageSource
{
    /// <summary>설정 창에 보여줄 제목(예: "OpenCode Go").</summary>
    string KeyLabel { get; }

    /// <summary>키 입력란 안내 문구.</summary>
    string KeyHint { get; }

    /// <summary>저장돼 있는 키(없으면 null).</summary>
    string? LoadKey();

    /// <summary>키를 저장한다(기존 키는 교체).</summary>
    void SaveKey(string key);

    /// <summary>저장된 키를 지운다(없으면 아무 일도 하지 않는다).</summary>
    void DeleteKey();

    /// <summary>키가 실제로 동작하는지 확인한다(성공하면 요약 문구, 실패는 예외).</summary>
    Task<string> VerifyKeyAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// 소스 클래스에 붙이는 등록 표시 — <c>App.OnLaunched</c> 가 리플렉션으로 스캔해
/// 카탈로그에 있는 id 만 자동 등록한다. 새 프로바이더는 클래스에 이 어트리뷰트만 붙이면 된다.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class UsageSourceAttribute(string providerId) : Attribute
{
    public string ProviderId { get; } = providerId;
}

/// <summary>
/// 소스가 자체 점검(네트워크 없음)을 제공하면 <c>--provider-test=&lt;id&gt;</c> 로 실행된다.
/// 실패 사유는 Log 로 남기고, 반환값은 통과 여부만 알린다.
/// </summary>
public interface IUsageSelfTest
{
    /// <summary>로그에 쓰는 이름(예: "Codex 파서").</summary>
    string SelfTestName { get; }

    /// <summary>합성 입력만으로 파서·변환을 점검한다.</summary>
    bool RunSelfTest();
}

/// <summary>
/// 소스가 진단 스위치용 재정의(주소·자격증명 경로)를 받을 수 있으면 구현한다.
/// 실패 경로를 실제로 재현할 때만 쓰는 검증용 구멍이다 — 사용자 흐름에서는 호출되지 않는다.
/// </summary>
public interface IUsageDiagnostics
{
    /// <summary>조회 주소·자격증명 파일을 강제한다(둘 다 null 이면 기본값).</summary>
    void ApplyDiagnostics(string? url, string? file);
}
