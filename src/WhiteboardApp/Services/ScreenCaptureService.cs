using System.Windows;
using System.Windows.Media.Imaging;
using WhiteboardApp.Controls;

namespace WhiteboardApp.Services;

public static class ScreenCaptureService
{
    /// <summary>
    /// Hides the given owner window, lets the user drag-select a screen region,
    /// then restores the owner window and returns the captured image (or null if cancelled).
    /// </summary>
    public static BitmapSource? CaptureRegion(Window owner)
    {
        var previousState = owner.WindowState;
        owner.Hide();

        // Give the compositor a moment to actually remove our window from the screen
        // before we take the screenshot, otherwise it can still be captured.
        System.Threading.Thread.Sleep(150);

        BitmapSource? result = null;
        try
        {
            var overlay = new CaptureOverlayWindow();
            var ok = overlay.ShowDialog();
            if (ok == true)
            {
                result = overlay.CapturedImage;
            }
        }
        finally
        {
            owner.Show();
            owner.WindowState = previousState;
            owner.Activate();
        }

        return result;
    }
}
