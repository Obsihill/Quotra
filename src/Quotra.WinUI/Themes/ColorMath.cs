using Microsoft.UI;
using Windows.UI;

namespace Quotra.WinUI.Themes;

/// <summary>
/// 색 연산 단일 구현.
/// 색을 조금 밝게/어둡게 하거나 알파를 바꾸는 코드가 여러 곳에 흩어지지 않도록 여기에만 둔다.
/// </summary>
public static class ColorMath
{
    /// <summary>두 색을 t(0~1) 만큼 선형 보간.</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        double k = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(a.A + ((b.A - a.A) * k)),
            (byte)(a.R + ((b.R - a.R) * k)),
            (byte)(a.G + ((b.G - a.G) * k)),
            (byte)(a.B + ((b.B - a.B) * k)));
    }

    /// <summary>흰색 쪽으로 t 만큼 이동.</summary>
    public static Color Lighten(Color color, double t) => Mix(color, Colors.White, t);

    /// <summary>검은색 쪽으로 t 만큼 이동.</summary>
    public static Color Darken(Color color, double t) => Mix(color, Colors.Black, t);

    /// <summary>알파만 교체.</summary>
    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>불투명도를 0~1 로 지정(1 = 원래 색의 알파).</summary>
    public static Color WithOpacity(Color color, double opacity)
    {
        double k = Math.Clamp(opacity, 0, 1);
        return Color.FromArgb((byte)(color.A * k), color.R, color.G, color.B);
    }
}
