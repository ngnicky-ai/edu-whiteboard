using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhiteboardApp.Services;

public static class ExportService
{
    /// <summary>Saves exactly what is visible inside <paramref name="visual"/> as a PNG at screen resolution.</summary>
    public static void ExportToPng(FrameworkElement visual, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        var width = visual.ActualWidth;
        var height = visual.ActualHeight;

        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(width * dpi.DpiScaleX),
            (int)Math.Ceiling(height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        // An absolute Viewbox pins the brush to the element's own visible rectangle, so the huge
        // off-screen world content is not included (or shrunk to fit) in the output.
        var brush = new VisualBrush(visual)
        {
            Viewbox = new Rect(0, 0, width, height),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill
        };

        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }
        rtb.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using var fs = new FileStream(path, FileMode.Create);
        encoder.Save(fs);
    }
}
