using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Quotra.WinUI.Themes;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Quotra.WinUI.Controls;

/// <summary>
/// 가로 배터리 게이지 — 남은 한도를 왼쪽부터 채운다(배터리 표기 관례).
///
/// 값이 없으면(<see cref="HasValue"/>=false) 채우지 않는다. 0 으로 채우면
/// "한도가 바닥"으로 읽히므로, 모르는 상태는 빈 트랙으로 둔다.
/// </summary>
public sealed partial class BatteryGauge : UserControl
{
    private readonly AccessibilitySettings _accessibility = new();
    /// <summary>남은 비율 0~1.</summary>
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(double),
        typeof(BatteryGauge),
        new PropertyMetadata(1.0, OnVisualChanged));

    /// <summary>값이 있는지(false 면 빈 트랙).</summary>
    public static readonly DependencyProperty HasValueProperty = DependencyProperty.Register(
        nameof(HasValue),
        typeof(bool),
        typeof(BatteryGauge),
        new PropertyMetadata(true, OnVisualChanged));

    /// <summary>채움 색(등급 색 — Palette.ColorFor 가 정한다).</summary>
    public static readonly DependencyProperty FillColorProperty = DependencyProperty.Register(
        nameof(FillColor),
        typeof(Color),
        typeof(BatteryGauge),
        new PropertyMetadata(Palette.ColorFor(UsageSeverity.Healthy), OnVisualChanged));

    /// <summary>몸통 안에 넣을 문구(예측 감소량 또는 호버 문구).</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(BatteryGauge),
        new PropertyMetadata(string.Empty, OnVisualChanged));

    public BatteryGauge()
    {
        InitializeComponent();

        Loaded += (_, _) => Redraw();

        // 채움 경계가 글자를 가로지르면 한 색으로는 읽히지 않는다 —
        // 두 벌(채움 위용/트랙 위용)을 겹쳐 두고 경계에서 잘라, 어느 쪽이든 읽히게 한다.
        Body.SizeChanged += (_, _) => UpdateTextClips();
        Fill.SizeChanged += (_, _) => UpdateTextClips();
        GaugeTextOnFill.SizeChanged += (_, _) => UpdateTextClips();

        ActualThemeChanged += (_, _) => Redraw();
    }

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool HasValue
    {
        get => (bool)GetValue(HasValueProperty);
        set => SetValue(HasValueProperty, value);
    }

    public Color FillColor
    {
        get => (Color)GetValue(FillColorProperty);
        set => SetValue(FillColorProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((BatteryGauge)d).Redraw();

    private void Redraw()
    {
        double level = HasValue && double.IsFinite(Level) ? Math.Clamp(Level, 0, 1) : 0;

        // 픽셀 폭을 직접 만지지 않고 비율만 넘긴다(레이아웃이 알아서 맞춘다).
        FillColumn.Width = new GridLength(level, GridUnitType.Star);
        RestColumn.Width = new GridLength(1 - level, GridUnitType.Star);

        Fill.Visibility = level > 0 ? Visibility.Visible : Visibility.Collapsed;
        SeverityFill.Background = new SolidColorBrush(FillColor);
        SeverityFill.Visibility = _accessibility.HighContrast ? Visibility.Collapsed : Visibility.Visible;

        // 문구는 가운데 정렬이라 채움 경계가 글자를 가로지를 수 있다.
        // 두 벌을 모두 띄워 두고, 경계에서 잘라 각자 자기 배경 위에만 그려지게 한다.
        GaugeTextOnFill.Text = Text;
        GaugeTextOnTrack.Text = Text;
        GaugeTextOnFill.Visibility = Visibility.Visible;
        GaugeTextOnTrack.Visibility = Visibility.Visible;
        UpdateTextClips();
    }

    /// <summary>채움 오른쪽 경계를 글자 좌표계에서 구해, 두 벌의 클립을 그 지점에서 자른다.</summary>
    private void UpdateTextClips()
    {
        double width = GaugeTextOnFill.ActualWidth;
        double height = Math.Max(GaugeTextOnFill.ActualHeight, GaugeTextOnTrack.ActualHeight);

        if (width <= 0 || height <= 0)
        {
            return;
        }

        // 채움이 없으면 경계는 트랙 안쪽 왼쪽 끝 → 트랙용 글자만 보인다.
        double boundary = HasValue && double.IsFinite(Level) && Level > 0
            ? Fill.TransformToVisual(GaugeTextOnFill).TransformPoint(new Point(Fill.ActualWidth, 0)).X
            : -1;

        boundary = Math.Clamp(boundary, 0, width);

        GaugeTextOnFill.Clip = new RectangleGeometry { Rect = new Rect(0, 0, boundary, height) };
        GaugeTextOnTrack.Clip = new RectangleGeometry { Rect = new Rect(boundary, 0, width - boundary, height) };
    }
}
