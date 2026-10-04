using System.IO;
using System.Runtime.InteropServices;

namespace WhiteboardApp.Services;

/// <summary>The folder of boards shown on the home screen (Documents\교육용 판서).</summary>
public static class BoardLibrary
{
    // EDU_WHITEBOARD_LIBRARY lets tests and screenshot runs use a separate folder instead of the user's boards.
    public static string Folder { get; } =
        Environment.GetEnvironmentVariable("EDU_WHITEBOARD_LIBRARY") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "교육용 판서");

    /// <summary>All readable boards, most recently edited first. Unreadable files are skipped.</summary>
    public static List<BoardInfo> List()
    {
        Directory.CreateDirectory(Folder);

        var boards = new List<BoardInfo>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*.wbd"))
        {
            try
            {
                boards.Add(FileService.ReadInfo(path));
            }
            catch (Exception)
            {
                // A corrupt or half-synced file shouldn't hide the rest of the library.
            }
        }
        return boards.OrderByDescending(b => b.Modified).ToList();
    }

    public static string NewBoardPath()
    {
        Directory.CreateDirectory(Folder);
        return Path.Combine(Folder, $"판서_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.wbd");
    }

    /// <summary>Copies an external .wbd into the library (so it appears on the home screen) and returns the copy's path.</summary>
    public static string Import(string sourcePath)
    {
        if (IsInLibrary(sourcePath)) return sourcePath;

        var target = NewBoardPath();
        File.Copy(sourcePath, target);
        return target;
    }

    public static bool IsInLibrary(string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves the file to the Recycle Bin so an accidental delete can be undone from Explorer.</summary>
    public static void MoveToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
        };
        var result = SHFileOperation(ref op);
        if (result != 0 || op.fAnyOperationsAborted)
        {
            throw new IOException($"휴지통으로 이동하지 못했습니다 (코드 {result}).");
        }
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
