namespace WhiteboardApp.Models;

public enum BackgroundTheme
{
    White,
    Blackboard,
    GreenBoard
}

public enum CanvasObjectType
{
    Image,
    Text
}

public class CanvasObjectModel
{
    public CanvasObjectType Type { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    // Image objects: file name of the PNG stored inside the .wbd package (images/xxx.png)
    public string? ImageFileName { get; set; }

    // Text objects
    public string? Text { get; set; }
    public double FontSize { get; set; } = 24;
    public string? FontColor { get; set; } = "#FF000000";
}

public class PageModel
{
    public BackgroundTheme Background { get; set; } = BackgroundTheme.White;

    // File name of the serialized InkCanvas strokes inside the package (pages/pageN.isf)
    public string StrokesFileName { get; set; } = string.Empty;

    public List<CanvasObjectModel> Objects { get; set; } = new();
}

public class WhiteboardDocument
{
    // 1 = fixed 1600x900 page coordinates; 2 = infinite-canvas world coordinates.
    public int Version { get; set; } = 2;
    public string Title { get; set; } = WhiteboardDocument.DefaultTitle;
    public List<PageModel> Pages { get; set; } = new();

    public const string DefaultTitle = "제목 없음";
}
