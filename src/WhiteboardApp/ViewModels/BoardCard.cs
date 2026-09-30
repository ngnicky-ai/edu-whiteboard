using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteboardApp.Services;

namespace WhiteboardApp.ViewModels;

public class BoardCard
{
    private static readonly CultureInfo Korean = new("ko-KR");

    public required string Path { get; init; }
    public required string Title { get; init; }
    public required string EditedText { get; init; }
    public ImageSource? Thumbnail { get; init; }
    public bool HasThumbnail => Thumbnail != null;

    public static BoardCard From(BoardInfo info) => new()
    {
        Path = info.Path,
        Title = info.Title,
        EditedText = "편집됨: " + info.Modified.ToString("yyyy.MM.dd. tt h:mm", Korean),
        Thumbnail = DecodeThumbnail(info.Thumbnail)
    };

    private static ImageSource? DecodeThumbnail(byte[]? png)
    {
        if (png == null) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
