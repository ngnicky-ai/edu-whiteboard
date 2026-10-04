using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhiteboardApp.Services;

/// <summary>
/// Screen captures kept as PNG files (Documents\교육용 판서\캡처 보관함) so they can be placed on
/// any board, any number of times, until the user deletes them.
/// </summary>
public static class CaptureTrayStore
{
    public static string Folder { get; } = Path.Combine(BoardLibrary.Folder, "캡처 보관함");

    /// <summary>Capture files, newest first.</summary>
    public static List<string> List()
    {
        Directory.CreateDirectory(Folder);
        return Directory.EnumerateFiles(Folder, "*.png")
            .OrderByDescending(File.GetLastWriteTime)
            .ToList();
    }

    public static string Save(BitmapSource image)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"캡처_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.png");

        // PNG keeps the capture's DPI, so the image reopens at the size it had on screen.
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = new FileStream(path, FileMode.CreateNew);
        encoder.Save(fs);
        return path;
    }

    public static BitmapSource Load(string path) => Decode(path, decodeHeight: 0);

    public static ImageSource LoadThumbnail(string path, int pixelHeight) => Decode(path, pixelHeight);

    public static void Delete(string path) => BoardLibrary.MoveToRecycleBin(path);

    // Read through a memory copy so the file isn't kept locked (it may be deleted later).
    private static BitmapSource Decode(string path, int decodeHeight)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(File.ReadAllBytes(path));
        if (decodeHeight > 0) bmp.DecodePixelHeight = decodeHeight;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
