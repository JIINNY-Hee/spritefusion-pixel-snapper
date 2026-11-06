using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixelSnapper;

public static class OutputImageService
{
    public static (int Width, int Height) ReadDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    // The caller owns this temporary PNG and removes it if conversion or cancellation fails.
    public static void ResizePng(string path, int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        OutputSizeOptions.ValidateDimensions(width, height);
        BitmapSource source;
        using (var stream = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            source = decoder.Frames[0];
            if (source.PixelWidth == width && source.PixelHeight == height) return;
            if (source.Format != PixelFormats.Bgra32)
                source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            source.Freeze();
        }
        token.ThrowIfCancellationRequested();

        var sourceStride = checked(source.PixelWidth * 4);
        var sourcePixels = new byte[checked(sourceStride * source.PixelHeight)];
        source.CopyPixels(sourcePixels, sourceStride, 0);
        token.ThrowIfCancellationRequested();
        var targetStride = checked(width * 4);
        var targetPixels = new byte[checked(targetStride * height)];
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var sourceY = (int)(((2L * y + 1) * source.PixelHeight) / (2L * height));
            for (var x = 0; x < width; x++)
            {
                var sourceX = (int)(((2L * x + 1) * source.PixelWidth) / (2L * width));
                var from = sourceY * sourceStride + sourceX * 4;
                var to = y * targetStride + x * 4;
                targetPixels[to] = sourcePixels[from];
                targetPixels[to + 1] = sourcePixels[from + 1];
                targetPixels[to + 2] = sourcePixels[from + 2];
                targetPixels[to + 3] = sourcePixels[from + 3];
            }
        }

        token.ThrowIfCancellationRequested();
        var resized = BitmapSource.Create(width, height, source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null, targetPixels, targetStride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(resized));
        token.ThrowIfCancellationRequested();
        using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            encoder.Save(output);
        token.ThrowIfCancellationRequested();
    }
}
