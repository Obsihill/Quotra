using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Quotra.WinUI.Themes;
using Windows.UI.ViewManagement;

namespace Quotra.WinUI.Controls;

/// <summary>A compact provider header; hover never changes its measured size.</summary>
public sealed partial class ProviderCell : UserControl
{
    public ProviderCell() => InitializeComponent();

    public BatteryGauge Battery => Gauge;

    public void SetIdentity(string name, string windowLabel, string accent)
    {
        NameLabel.Text = name;
        WindowLabel.Text = windowLabel;
        BrandTint.Background = new SolidColorBrush(Palette.ParseAccent(accent, ActualTheme == ElementTheme.Light));
        BrandTint.Visibility = new AccessibilitySettings().HighContrast ? Visibility.Collapsed : Visibility.Visible;
        Monogram.Text = string.IsNullOrWhiteSpace(name)
            ? "Q"
            : StringInfo.GetNextTextElement(name.Trim()).ToUpperInvariant();
        AutomationProperties.SetName(this, $"{name} · {windowLabel}");
    }

    public void SetHovered(bool hovered) =>
        VisualStateManager.GoToState(this, hovered ? "Hovered" : "Rest", useTransitions: true);
}
