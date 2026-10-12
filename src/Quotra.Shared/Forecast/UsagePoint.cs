namespace Quotra.Forecast;

/// <summary>어느 한도 창인지. 앱마다 표시 이름만 다르고(주간/세션) 계산은 같다.</summary>
public enum UsageWindow
{
    /// <summary>장기 한도(예: 주간).</summary>
    Primary = 0,

    /// <summary>단기 한도(예: 세션).</summary>
    Secondary = 1,
}

/// <summary>한 시점의 사용률 표본(0~1). 값은 "사용한 양"이다(남은 양이 아니다).</summary>
public readonly record struct UsagePoint(DateTimeOffset At, double Used);
