using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Quotra.WinUI.Diagnostics;

/// <summary>Opt-in visual QA export of the real XAML tree; no desktop capture or user input.</summary>
internal static class WidgetCapture
{
    public static async Task SaveAsync(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        double scale = root.XamlRoot.RasterizationScale;
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);

        VerifyBounds(root, root);

        var buffer = await bitmap.GetPixelsAsync();
        byte[] pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(pixels);
        }

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, pixels);
        await encoder.FlushAsync();
        using var encodedReader = new DataReader(stream.GetInputStreamAt(0));
        await encodedReader.LoadAsync((uint)stream.Size);
        byte[] encoded = new byte[stream.Size];
        encodedReader.ReadBytes(encoded);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllBytesAsync(path, encoded);

        if (root is Microsoft.UI.Xaml.Controls.Grid grid && grid.Background is SolidColorBrush brush)
        {
            if (brush.Color.A is not (0 or 255))
            {
                throw new InvalidOperationException("The widget surface must be fully transparent or use the opaque fallback.");
            }

            string surface = brush.Color.A == 0 ? "transparent(Acrylic backdrop)" : "opaque(fallback)";
            Log.Write($"capture: {path} · {bitmap.PixelWidth}x{bitmap.PixelHeight}px · surface={surface} · scale={scale}");
        }
    }

    private static void VerifyBounds(DependencyObject node, FrameworkElement root)
    {
        if (node is Controls.ProviderCell or Controls.BatteryGauge)
        {
            var element = (FrameworkElement)node;
            var bounds = element.TransformToVisual(root).TransformBounds(
                new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            if (bounds.Left < -1 || bounds.Top < -1 ||
                bounds.Right > root.ActualWidth + 1 || bounds.Bottom > root.ActualHeight + 1)
            {
                throw new InvalidOperationException($"Clipped {element.GetType().Name}: {bounds}");
            }
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            VerifyBounds(VisualTreeHelper.GetChild(node, i), root);
        }
    }
}
