using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhiteboardApp.Services;

// Reads images from the clipboard without relying on Clipboard.GetImage(), which mangles the
// 32-bit DIBs that screenshot tools (Snagit, Greenshot, Win+Shift+S, ...) publish: their alpha
// bytes are usually all zero, so WPF renders the pasted image fully transparent (an empty box).
public static class ClipboardImageReader
{
    private const string DibV5Format = "Format17";
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    /// <summary>Returns the clipboard image re-stamped at the given DPI, or null if there is none.</summary>
    public static BitmapSource? TryRead(double dpiX, double dpiY)
    {
        var data = GetDataObjectWithRetry();
        if (data == null) return null;

        var image = TryPng(data)
                    ?? TryDib(data, DataFormats.Dib)
                    ?? TryDib(data, DibV5Format)
                    ?? TryImageFile(data)
                    ?? TryLegacy();

        return image == null ? null : ToBgra32(image, dpiX, dpiY);
    }

    // Capture tools often still hold the clipboard open for a moment right after copying.
    private static IDataObject? GetDataObjectWithRetry()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return Clipboard.GetDataObject();
            }
            catch (COMException)
            {
                Thread.Sleep(60);
            }
        }
        return null;
    }

    private static BitmapSource? TryPng(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent("PNG") || data.GetData("PNG") is not Stream stream) return null;
            stream.Position = 0;
            return new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static BitmapSource? TryDib(IDataObject data, string format)
    {
        try
        {
            if (!data.GetDataPresent(format) || data.GetData(format) is not MemoryStream stream) return null;
            return DecodeDib(stream.ToArray());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static BitmapSource? TryImageFile(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files) return null;
            var path = files.FirstOrDefault(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            if (path == null) return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static BitmapSource? TryLegacy()
    {
        try
        {
            return Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Decodes a packed DIB (BITMAPINFOHEADER / V4 / V5 followed by optional masks, palette and pixels).</summary>
    public static BitmapSource? DecodeDib(byte[] dib)
    {
        if (dib.Length < 40) return null;

        var headerSize = BitConverter.ToInt32(dib, 0);
        var width = BitConverter.ToInt32(dib, 4);
        var rawHeight = BitConverter.ToInt32(dib, 8);
        var bitCount = BitConverter.ToInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        var colorsUsed = BitConverter.ToInt32(dib, 32);

        const int BI_RGB = 0, BI_BITFIELDS = 3;
        var height = Math.Abs(rawHeight);
        var topDown = rawHeight < 0;
        if (width <= 0 || height == 0) return null;

        // With a plain 40-byte header, BI_BITFIELDS masks follow the header; V4/V5 headers embed them.
        var maskBytes = compression == BI_BITFIELDS && headerSize == 40 ? 12 : 0;
        var paletteEntries = bitCount <= 8 ? (colorsUsed == 0 ? 1 << bitCount : colorsUsed) : colorsUsed;
        var pixelOffset = headerSize + maskBytes + paletteEntries * 4;

        var isSimple = (bitCount == 24 && compression == BI_RGB)
                       || (bitCount == 32 && (compression == BI_RGB || (compression == BI_BITFIELDS && HasStandardMasks(dib))));
        if (!isSimple)
        {
            return DecodeAsBmpFile(dib, pixelOffset);
        }

        var srcStride = ((width * bitCount + 31) / 32) * 4;
        if (pixelOffset + (long)srcStride * height > dib.Length) return null;

        var dstStride = width * 4;
        var pixels = new byte[dstStride * height];
        var anyAlpha = false;

        for (var y = 0; y < height; y++)
        {
            var srcRow = pixelOffset + (topDown ? y : height - 1 - y) * srcStride;
            var dstRow = y * dstStride;
            for (var x = 0; x < width; x++)
            {
                var s = srcRow + x * (bitCount / 8);
                var d = dstRow + x * 4;
                pixels[d] = dib[s];
                pixels[d + 1] = dib[s + 1];
                pixels[d + 2] = dib[s + 2];
                if (bitCount == 32)
                {
                    pixels[d + 3] = dib[s + 3];
                    anyAlpha |= dib[s + 3] != 0;
                }
                else
                {
                    pixels[d + 3] = 255;
                }
            }
        }

        // An all-zero alpha channel means "no alpha" (the normal case for screenshots), not "invisible".
        if (bitCount == 32 && !anyAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, dstStride);
    }

    private static bool HasStandardMasks(byte[] dib)
    {
        if (dib.Length < 52) return false;
        return BitConverter.ToUInt32(dib, 40) == 0x00FF0000
               && BitConverter.ToUInt32(dib, 44) == 0x0000FF00
               && BitConverter.ToUInt32(dib, 48) == 0x000000FF;
    }

    // Fallback for palettized / compressed DIBs: prepend a BITMAPFILEHEADER and let WIC decode it.
    private static BitmapSource? DecodeAsBmpFile(byte[] dib, int pixelOffset)
    {
        try
        {
            var file = new byte[14 + dib.Length];
            file[0] = (byte)'B';
            file[1] = (byte)'M';
            BitConverter.GetBytes(file.Length).CopyTo(file, 2);
            BitConverter.GetBytes(14 + pixelOffset).CopyTo(file, 10);
            dib.CopyTo(file, 14);

            using var ms = new MemoryStream(file);
            return new BmpBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Normalizes to Bgra32 and stamps the monitor DPI so one image pixel = one screen pixel,
    // i.e. a pasted screenshot shows at the same size it had on screen.
    private static BitmapSource ToBgra32(BitmapSource source, double dpiX, double dpiY)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var anyAlpha = false;
        for (var i = 3; i < pixels.Length && !anyAlpha; i += 4) anyAlpha = pixels[i] != 0;
        if (!anyAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
