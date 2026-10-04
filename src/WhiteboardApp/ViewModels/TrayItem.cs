using System.IO;
using System.Windows.Media;
using WhiteboardApp.Services;

namespace WhiteboardApp.ViewModels;

public class TrayItem
{
    public required string Path { get; init; }
    public required ImageSource Thumbnail { get; init; }
    public required string ToolTipText { get; init; }

    // Thumbnails are decoded small (the strip shows them ~80px tall; 2x for high-DPI screens).
    public static TrayItem From(string path) => new()
    {
        Path = path,
        Thumbnail = CaptureTrayStore.LoadThumbnail(path, 160),
        ToolTipText = $"캡처: {File.GetLastWriteTime(path):yyyy.MM.dd HH:mm} — 클릭하면 칠판에 붙습니다"
    };
}
