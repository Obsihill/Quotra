using System.Text.Json.Serialization;

namespace Quotra.WinUI.Models;

/// <summary>
/// 프로바이더 한 곳의 상태 = 카탈로그 정의 + 마지막 조회 결과.
///
/// 값이 <c>null</c> 이면 "연동되지 않았거나 아직 받지 못했다"는 뜻이다.
/// 0(0% 사용)과 반드시 구분한다 — 0 으로 채우면 미연동이 "한도가 가득 참"으로 둔갑한다.
/// 모델은 테마 계층을 참조하지 않는다(색 변환은 화면 계층에서).
/// </summary>
public sealed class ProviderUsage
{
    // ---------------------------------------------------------- 카탈로그 정의

    public string Id { get; set; } = "unknown";

    /// <summary>카드 제목(예: ChatGPT).</summary>
    public string Name { get; set; } = "Provider";

    /// <summary>제조사/서브 라벨(예: OpenAI).</summary>
    public string Vendor { get; set; } = string.Empty;

    /// <summary>플랜 이름(예: Plus, Max 20x). 카탈로그에 적어 두거나 소스가 알려준다.</summary>
    public string Plan { get; set; } = string.Empty;

    /// <summary>한도 창 라벨(예: 주간 / 5시간).</summary>
    public string PrimaryLabel { get; set; } = "주간";

    public string SecondaryLabel { get; set; } = "세션";

    /// <summary>브랜드 컬러(#RRGGBB). 비면 테마 브랜드 색을 쓴다.</summary>
    public string Accent { get; set; } = string.Empty;

    /// <summary>카탈로그에 적어 두는 설명(소스가 값을 주면 그쪽 문구로 대체된다).</summary>
    public string Note { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    // ---------------------------------------------------------- 조회 결과

    /// <summary>장기(주간) 한도 사용률 0~1. 화면에는 남은 양(1−이 값)을 보여준다.</summary>
    public double? Primary { get; set; }

    /// <summary>단기(세션) 한도 사용률 0~1. 스피어 바깥 링으로 표시.</summary>
    public double? Secondary { get; set; }

    /// <summary>장기 한도가 리셋되는 시각(모르면 null).</summary>
    public DateTimeOffset? PrimaryResetsAt { get; set; }

    public DateTimeOffset? SecondaryResetsAt { get; set; }

    /// <summary>마지막으로 값을 받은 시각.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>마지막 조회 실패 사유(정상이면 null).</summary>
    public string? Error { get; set; }

    /// <summary>값을 가져올 소스가 등록돼 있는지(카탈로그가 아니라 <see cref="Services.UsageService"/> 가 정한다).</summary>
    [JsonIgnore]
    public bool IsLinked { get; set; }

    /// <summary>
    /// 예측 이력 시리즈 키(Primary 창). 창 구성이 바뀌어 예전 기록과 섞이면 안 될 때 소스가 지정한다.
    /// 비어 있으면 <see cref="Id"/> 를 쓴다.
    /// </summary>
    [JsonIgnore]
    public string? HistoryKey { get; set; }

    /// <summary>이력 시리즈 키(Primary). HistoryKey 가 없으면 Id.</summary>
    [JsonIgnore]
    public string PrimaryHistoryKey => string.IsNullOrWhiteSpace(HistoryKey) ? Id : HistoryKey!;

    // ---------------------------------------------------------- 표시용 파생값

    [JsonIgnore]
    public bool HasPrimary => Primary is not null;

    [JsonIgnore]
    public bool HasAnyValue => Primary is not null || Secondary is not null;

    /// <summary>남은 장기 한도 0~1(값이 없으면 null).</summary>
    [JsonIgnore]
    public double? Remaining => Primary is { } used ? Math.Clamp(1 - used, 0, 1) : null;

    [JsonIgnore]
    public double? RemainingSession => Secondary is { } used ? Math.Clamp(1 - used, 0, 1) : null;

    /// <summary>게이지에 넣을 값(값이 없으면 0 — 화면은 HasValue 로 구분해 "—" 를 그린다).</summary>
    [JsonIgnore]
    public double RemainingOrZero => Remaining ?? 0;

    /// <summary>"58%" 또는 "—".</summary>
    [JsonIgnore]
    public string RemainingText => Format(Remaining);

    [JsonIgnore]
    public string RemainingSessionText => Format(RemainingSession);

    /// <summary>미연동 → 조회 오류 → 값 순으로 상태를 한 줄로.</summary>
    [JsonIgnore]
    public string StatusText => Error is not null ? "조회 오류" : !IsLinked ? "미연동" : "값 없음";

    /// <summary>"3일 4시간 후 리셋" 같은 문구. 리셋 시각을 모르면 안내 문구.</summary>
    [JsonIgnore]
    public string ResetText => ResetTextAt(DateTimeOffset.Now);

    public string ResetTextAt(DateTimeOffset now)
    {
        if (PrimaryResetsAt is not { } resetsAt)
        {
            return "리셋 정보 없음";
        }

        TimeSpan left = resetsAt - now;
        if (left <= TimeSpan.Zero)
        {
            return "곧 리셋";
        }

        if (left.TotalDays >= 1)
        {
            return $"{(int)left.TotalDays}일 {left.Hours}시간 후 리셋";
        }

        return left.TotalHours >= 1
            ? $"{(int)left.TotalHours}시간 {left.Minutes}분 후 리셋"
            : $"{Math.Max(1, left.Minutes)}분 후 리셋";
    }

    /// <summary>초기화까지 남은 시간만 짧게("6일 4시간") — 호버 줄에 쓰려고 앞뒤 설명을 뺀 형태.</summary>
    public string ResetRemainingShortAt(DateTimeOffset now)
    {
        if (PrimaryResetsAt is not { } resetsAt)
        {
            return "정보 없음";
        }

        TimeSpan left = resetsAt - now;
        if (left <= TimeSpan.Zero)
        {
            return "곧 리셋";
        }

        if (left.TotalDays >= 1)
        {
            return $"{(int)left.TotalDays}일 {left.Hours}시간";
        }

        return left.TotalHours >= 1
            ? $"{(int)left.TotalHours}시간 {left.Minutes}분"
            : $"{Math.Max(1, left.Minutes)}분";
    }

    private static string Format(double? remaining) =>
        remaining is { } value ? $"{(int)Math.Round(value * 100)}%" : "—";
}
