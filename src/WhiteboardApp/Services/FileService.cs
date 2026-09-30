using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Ink;
using WhiteboardApp.Models;

namespace WhiteboardApp.Services;

public class LoadedPage
{
    public int Version { get; set; }
    public string Title { get; set; } = WhiteboardDocument.DefaultTitle;
    public BackgroundTheme Background { get; set; }
    public StrokeCollection Strokes { get; set; } = new();
    public List<CanvasObjectModel> Objects { get; set; } = new();
    public Dictionary<string, byte[]> Images { get; set; } = new();
}

/// <summary>Lightweight summary used by the home screen (no strokes/images are decoded).</summary>
public class BoardInfo
{
    public required string Path { get; init; }
    public string Title { get; init; } = WhiteboardDocument.DefaultTitle;
    public DateTime Modified { get; init; }
    public byte[]? Thumbnail { get; init; }
}

public static class FileService
{
    private const string ManifestEntryName = "manifest.json";
    private const string StrokesEntryName = "pages/page1.isf";
    private const string ThumbnailEntryName = "thumbnail.png";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Save(
        string path,
        string title,
        BackgroundTheme background,
        StrokeCollection strokes,
        List<CanvasObjectModel> objects,
        Dictionary<string, byte[]> images,
        byte[]? thumbnail = null)
    {
        var tempPath = path + ".tmp";

        using (var fs = new FileStream(tempPath, FileMode.Create))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var doc = new WhiteboardDocument { Title = title };
            var page = new PageModel
            {
                Background = background,
                StrokesFileName = StrokesEntryName,
                Objects = objects
            };
            doc.Pages.Add(page);

            WriteManifest(archive, doc);

            var strokesEntry = archive.CreateEntry(page.StrokesFileName);
            using (var strokesStream = strokesEntry.Open())
            {
                strokes.Save(strokesStream);
            }

            foreach (var (fileName, bytes) in images)
            {
                WriteEntry(archive, $"images/{fileName}", bytes);
            }

            if (thumbnail != null)
            {
                WriteEntry(archive, ThumbnailEntryName, thumbnail);
            }
        }

        File.Copy(tempPath, path, overwrite: true);
        File.Delete(tempPath);
    }

    public static LoadedPage Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Read);

        var doc = ReadManifest(archive);
        var page = doc.Pages.FirstOrDefault()
            ?? throw new InvalidDataException("페이지 정보가 없습니다.");

        var strokes = new StrokeCollection();
        var strokesEntry = archive.GetEntry(page.StrokesFileName);
        if (strokesEntry != null)
        {
            using var ss = strokesEntry.Open();
            strokes = new StrokeCollection(ss);
        }

        var images = new Dictionary<string, byte[]>();
        foreach (var obj in page.Objects.Where(o => o.Type == CanvasObjectType.Image && o.ImageFileName != null))
        {
            var bytes = ReadEntry(archive, $"images/{obj.ImageFileName}");
            if (bytes != null) images[obj.ImageFileName!] = bytes;
        }

        return new LoadedPage
        {
            Version = doc.Version,
            Title = doc.Title,
            Background = page.Background,
            Strokes = strokes,
            Objects = page.Objects,
            Images = images
        };
    }

    public static BoardInfo ReadInfo(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Read);

        return new BoardInfo
        {
            Path = path,
            Title = ReadManifest(archive).Title,
            Modified = File.GetLastWriteTime(path),
            Thumbnail = ReadEntry(archive, ThumbnailEntryName)
        };
    }

    public static void Rename(string path, string newTitle)
    {
        var modified = File.GetLastWriteTime(path);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Update))
        {
            var doc = ReadManifest(archive);
            doc.Title = newTitle;
            archive.GetEntry(ManifestEntryName)?.Delete();
            WriteManifest(archive, doc);
        }

        // Renaming isn't editing the drawing, so keep its place in the "recently edited" order.
        File.SetLastWriteTime(path, modified);
    }

    private static WhiteboardDocument ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("올바른 판서 파일(.wbd)이 아닙니다.");
        using var reader = new StreamReader(entry.Open());
        return JsonSerializer.Deserialize<WhiteboardDocument>(reader.ReadToEnd())
            ?? throw new InvalidDataException("파일 내용을 읽을 수 없습니다.");
    }

    private static void WriteManifest(ZipArchive archive, WhiteboardDocument doc)
    {
        var entry = archive.CreateEntry(ManifestEntryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(JsonSerializer.Serialize(doc, JsonOptions));
    }

    private static byte[]? ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry == null) return null;

        using var es = entry.Open();
        using var ms = new MemoryStream();
        es.CopyTo(ms);
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        using var es = archive.CreateEntry(name).Open();
        es.Write(bytes, 0, bytes.Length);
    }
}
