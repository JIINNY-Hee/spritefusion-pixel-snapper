using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IDataObject = System.Windows.IDataObject;
using DataFormats = System.Windows.DataFormats;

namespace PixelSnapper;

public static class ClipboardImageService
{
    public static BitmapSource? ReadImage(IDataObject data)
    {
        // Prefer explicit PNG data: it preserves transparency from browsers and image editors.
        foreach (var format in new[] { "PNG", "image/png" })
        {
            if (!data.GetDataPresent(format, autoConvert: false)) continue;
            var value = data.GetData(format, autoConvert: false);
            if (value is byte[] bytes)
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return DecodePng(stream);
            }
            if (value is Stream streamValue)
            {
                var position = streamValue.CanSeek ? streamValue.Position : 0;
                try
                {
                    if (streamValue.CanSeek) streamValue.Position = 0;
                    return DecodePng(streamValue);
                }
                finally { if (streamValue.CanSeek) streamValue.Position = position; }
            }
        }
        if (data.GetData(DataFormats.Bitmap, autoConvert: true) is not BitmapSource bitmap) return null;
        return Snapshot(bitmap, repairLegacyAlpha: true);
    }

    private static BitmapSource Snapshot(BitmapSource bitmap, bool repairLegacyAlpha)
    {
        ValidateDimensions(bitmap);
        // Legacy Windows DIB copies often contain RGB with unused, all-zero alpha bytes.
        // PNG above is kept exact, including genuinely fully transparent PNG images.
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) hasAlpha |= pixels[i] != 0;
        if (repairLegacyAlpha && !hasAlpha)
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var result = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static BitmapSource DecodePng(Stream stream)
    {
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var image = decoder.Frames[0];
        ValidateDimensions(image);
        return Snapshot(image, repairLegacyAlpha: false);
    }

    internal static void ValidateDimensions(BitmapSource image)
    {
        if (image.PixelWidth < 1 || image.PixelHeight < 1 || image.PixelWidth > 10000
            || image.PixelHeight > 10000 || (long)image.PixelWidth * image.PixelHeight > 40_000_000)
            throw new ArgumentException("클립보드 이미지는 한 변 10,000px, 전체 4천만 픽셀 이하로 사용해 주세요.");
    }
}

// Own only clipboard snapshots from this app session; never delete user files or saved exports.
public sealed class ClipboardImageStore : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PixelSnapper", "clipboard", Guid.NewGuid().ToString("N"));
    private readonly HashSet<string> ownedPaths = new(StringComparer.OrdinalIgnoreCase);
    private int sequence;

    public string Add(BitmapSource image)
    {
        ClipboardImageService.ValidateDimensions(image);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"클립보드_{DateTime.Now:yyyyMMdd_HHmmss}_{++sequence}.png");
        ownedPaths.Add(path);
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            encoder.Save(output);
            return path;
        }
        catch { Release(path); throw; }
    }

    public void Release(string path)
    {
        if (!ownedPaths.Contains(path)) return;
        try { File.Delete(path); ownedPaths.Remove(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        foreach (var path in ownedPaths.ToArray()) Release(path);
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
