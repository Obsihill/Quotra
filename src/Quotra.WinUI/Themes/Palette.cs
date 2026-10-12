using Windows.UI;

namespace Quotra.WinUI.Themes;

/// <summary>등급(남은 한도 기준). 색은 사용률 임계값에서 파생된다.</summary>
public enum UsageSeverity
{
    Healthy,
    Watch,
    Critical,
}

/// <summary>
/// 테마 토큰의 단일 출처(코드 쪽).
///
/// WinUI 의 XAML 토큰(Theme.xaml)과 역할을 나눈다.
///   · 테마에 따라 바뀌는 표면/텍스트 색  → Theme.xaml 의 ThemeDictionaries
///   · 테마와 무관한 브랜드·등급·게이지 색 → 여기
/// 게이지는 XAML 도형 안에서 색을 조합해야 해서(그라데이션 정지점 등) 코드 토큰이 필요하다.
/// </summary>
public static class Palette
{
    public const string BrandHexDark = "#8B9CFF";
    public const string BrandHexLight = "#5B6FE0";

    /// <summary>다크 배경용 브랜드 색.</summary>
    public static readonly Color BrandDark = Parse(BrandHexDark);

    /// <summary>라이트 배경용 브랜드 색(대비 확보를 위해 더 진하다).</summary>
    public static readonly Color BrandLight = Parse(BrandHexLight);

    public static readonly Color Healthy = Parse("#6EE7B7");
    public static readonly Color Watch = Parse("#FFC86B");
    public static readonly Color Critical = Parse("#FF7A8A");

    // 게이지(스피어) 본체: 위에서 빛을 받는 유리 구체
    public static readonly Color BodyMidDark = Parse("#333745");
    public static readonly Color BodyMidLight = Parse("#F7F9FC");
    public static readonly Color BodyLowDark = Parse("#161820");
    public static readonly Color BodyLowLight = Parse("#DFE4EE");
    public static readonly Color BodyDeepDark = Parse("#0B0C11");
    public static readonly Color BodyDeepLight = Parse("#C3CAD9");

    /// <summary>유리 반사광(흰색 계열).</summary>
    public static readonly Color SpecularHigh = Color.FromArgb(96, 255, 255, 255);

    public static readonly Color SpecularLow = Color.FromArgb(0, 255, 255, 255);

    /// <summary>가운데 퍼센트 글자.</summary>
    public static readonly Color LabelDark = Color.FromArgb(244, 255, 255, 255);

    public static readonly Color LabelLight = Color.FromArgb(246, 0x1A, 0x1C, 0x22);

    /// <summary>트레이 아이콘 본체.</summary>
    public static readonly Color TrayBodyHigh = Parse("#3A3F52");

    public static readonly Color TrayBodyLow = Parse("#12131A");

    /// <summary>사용률 → 등급. 남은 양이 아니라 '쓴 양' 기준이다(0.9 이상 소진 = Critical).</summary>
    public static UsageSeverity SeverityOf(double used)
    {
        // 문자열이 아니라 게이지 아래쪽 색과 트레이 아이콘이 같은 규칙을 쓰도록 여기 한 곳에 둔다.
        UsageSeverity severity = used >= 0.9 ? UsageSeverity.Critical
            : used >= 0.7 ? UsageSeverity.Watch
            : UsageSeverity.Healthy;

        return severity;
    }

    public static Color ColorFor(UsageSeverity severity) => severity switch
    {
        UsageSeverity.Critical => Critical,
        UsageSeverity.Watch => Watch,
        _ => Healthy,
    };

    /// <summary>Matte gauge colors, with white text in light mode and dark text in dark mode.</summary>
    public static Color GaugeColorFor(UsageSeverity severity, bool lightTheme) => (severity, lightTheme) switch
    {
        (UsageSeverity.Critical, true) => Parse("#C42B1C"),
        (UsageSeverity.Critical, false) => Parse("#F28B82"),
        (UsageSeverity.Watch, true) => Parse("#946200"),
        (UsageSeverity.Watch, false) => Parse("#F0C168"),
        (_, true) => Parse("#16834D"),
        _ => Parse("#6CCB9A"),
    };

    /// <summary>#RRGGBB / #AARRGGBB 문자열. 잘못된 값이면 브랜드 색으로 떨어진다.</summary>
    public static Color ParseAccent(string? hex, bool lightTheme)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return lightTheme ? BrandLight : BrandDark;
        }

        try
        {
            return Parse(hex);
        }
        catch (FormatException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return lightTheme ? BrandLight : BrandDark;
        }
    }

    private static Color Parse(string hex)
    {
        string text = hex.TrimStart('#');
        byte a = 255;
        byte r;
        byte g;
        byte b;

        if (text.Length == 8)
        {
            a = Convert.ToByte(text[..2], 16);
            r = Convert.ToByte(text.Substring(2, 2), 16);
            g = Convert.ToByte(text.Substring(4, 2), 16);
            b = Convert.ToByte(text.Substring(6, 2), 16);
        }
        else if (text.Length == 6)
        {
            r = Convert.ToByte(text[..2], 16);
            g = Convert.ToByte(text.Substring(2, 2), 16);
            b = Convert.ToByte(text.Substring(4, 2), 16);
        }
        else
        {
            throw new FormatException($"색 문자열 형식이 아닙니다: {hex}");
        }

        return Color.FromArgb(a, r, g, b);
    }
}
