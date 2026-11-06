using System.IO;
using System.Windows.Media.Imaging;

namespace PixelSnapper;

public sealed record PreviewImage(BitmapSource Bitmap, int Width, int Height)
{
    internal static PreviewImage Load(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnDemand);
        int width = decoder.Frames[0].PixelWidth, height = decoder.Frames[0].PixelHeight;
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (Math.Max(width, height) > 2048)
        {
            if (width >= height) bitmap.DecodePixelWidth = 2048;
            else bitmap.DecodePixelHeight = 2048;
        }
        bitmap.EndInit();
        bitmap.Freeze();
        token.ThrowIfCancellationRequested();
        return new PreviewImage(bitmap, width, height);
    }
}
