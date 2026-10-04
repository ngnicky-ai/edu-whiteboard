using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;

namespace WhiteboardApp.Controls;

public partial class CaptureOverlayWindow : Window
{
    private System.Windows.Point? _startPoint;
    private bool _dragging;
    private bool _capturing;
    private readonly RectangleGeometry _hole = new(Rect.Empty);

    public BitmapSource? CapturedImage { get; private set; }

    public CaptureOverlayWindow()
    {
        InitializeComponent();

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // Dim layer = whole screen minus the selection, so the selected area shows its original colors.
        DimOverlay.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, Width, Height)), _hole);
    }

    private void UpdateSelection(Rect rect)
    {
        Canvas.SetLeft(SelectionRect, rect.X);
        Canvas.SetTop(SelectionRect, rect.Y);
        SelectionRect.Width = rect.Width;
        SelectionRect.Height = rect.Height;
        _hole.Rect = rect;
    }

    private Rect SelectionFrom(Point current) => new(
        Math.Min(current.X, _startPoint!.Value.X),
        Math.Min(current.Y, _startPoint.Value.Y),
        Math.Abs(current.X - _startPoint.Value.X),
        Math.Abs(current.Y - _startPoint.Value.Y));

    private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_capturing) return;

        _startPoint = e.GetPosition(RootCanvas);
        _dragging = true;
        UpdateSelection(new Rect(_startPoint.Value.X, _startPoint.Value.Y, 0, 0));
        SelectionRect.Visibility = Visibility.Visible;
        HintBox.Visibility = Visibility.Collapsed;
        RootCanvas.CaptureMouse();
    }

    private void RootCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _startPoint is null) return;
        UpdateSelection(SelectionFrom(e.GetPosition(RootCanvas)));
    }

    private async void RootCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || _startPoint is null) return;

        _dragging = false;
        RootCanvas.ReleaseMouseCapture();

        var selection = SelectionFrom(e.GetPosition(RootCanvas));
        var x = selection.X;
        var y = selection.Y;
        var w = selection.Width;
        var h = selection.Height;

        if (w < 3 || h < 3)
        {
            DialogResult = false;
            Close();
            return;
        }

        // WPF coordinates (Left/Top/x/y/w/h) are device-independent pixels (96 DPI units).
        // GDI's CopyFromScreen needs physical device pixels, so on any monitor with DPI scaling
        // other than 100% we must convert, or the wrong region (and a mis-scaled bitmap) is captured.
        var dpiScale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
            ?? new System.Windows.Media.Matrix(1, 0, 0, 1, 0, 0);

        var physX = (int)Math.Round((Left + x) * dpiScale.M11);
        var physY = (int)Math.Round((Top + y) * dpiScale.M22);
        var physW = (int)Math.Round(w * dpiScale.M11);
        var physH = (int)Math.Round(h * dpiScale.M22);

        // The overlay (dim layer, blue border) is still on screen at this point; grabbing the pixels now
        // would bake it into the picture. Make the overlay fully invisible and give the compositor a
        // moment to repaint the screen without it before copying.
        _capturing = true;
        Opacity = 0;
        await Task.Delay(150);

        CapturedImage = CaptureScreenRegion(physX, physY, physW, physH, 96.0 * dpiScale.M11, 96.0 * dpiScale.M22);

        DialogResult = true;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_capturing)
        {
            DialogResult = false;
            Close();
        }
    }

    private static BitmapSource CaptureScreenRegion(int screenX, int screenY, int width, int height, double dpiX, double dpiY)
    {
        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(screenX, screenY, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
        }

        // Read pixels directly via LockBits instead of GetHbitmap()+CreateBitmapSourceFromHBitmap:
        // GetHbitmap() converts to a device-dependent bitmap that does not carry a real alpha
        // channel, and the pixel bytes that end up in the "alpha" position typically come back as
        // 0 (fully transparent) — so the captured image would exist but be completely invisible
        // (just an empty box) once placed on the canvas. Screen captures have no real transparency
        // anyway, so we force alpha to fully opaque explicitly.
        var rect = new System.Drawing.Rectangle(0, 0, width, height);
        var bmpData = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var stride = bmpData.Stride;
            var pixels = new byte[stride * height];
            System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, pixels, 0, pixels.Length);

            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

            var source = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(bmpData);
        }
    }
}
