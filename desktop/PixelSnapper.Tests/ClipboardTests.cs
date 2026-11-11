using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelSnapper;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;

internal static class ClipboardTests
{
    public static async Task RunAsync(string root)
    {
        var source = Path.Combine(root, "원본 이미지.png");
        var image = await OnSta(() =>
        {
            var pngBytes = File.ReadAllBytes(source);
            var data = new DataObject();
            data.SetData("PNG", pngBytes, autoConvert: false);
            var png = ClipboardImageService.ReadImage(data)!;
            Check(png.IsFrozen && png.PixelWidth == 64 && png.PixelHeight == 64, "PNG clipboard data keeps dimensions and is detached from clipboard");
            var pixels = Pixels(png);
            Check(Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[i * 4 + 3] == 0), "PNG clipboard transparency is preserved");
            using var stream = new MemoryStream(pngBytes);
            stream.Position = 5;
            var streamData = new DataObject();
            streamData.SetData("image/png", stream, autoConvert: false);
            Check(Pixels(ClipboardImageService.ReadImage(streamData)!).SequenceEqual(pixels) && stream.Position == 5,
                "PNG stream clipboard data is read completely without changing stream position");
            var text = new DataObject(); text.SetText("not an image");
            Check(ClipboardImageService.ReadImage(text) == null, "text-only clipboard is not imported as an image");
            var rgbBytes = new byte[] { 10, 20, 30, 0, 40, 50, 60, 0 };
            var rgb = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, rgbBytes, 8);
            var bitmapData = new DataObject(); bitmapData.SetData(DataFormats.Bitmap, rgb);
            var opaque = Pixels(ClipboardImageService.ReadImage(bitmapData)!);
            Check(opaque[3] == 255 && opaque[7] == 255 && opaque[0] == 10 && opaque[4] == 40,
                "legacy bitmap clipboard with unused alpha remains visible");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rgb));
            using var transparentPng = new MemoryStream(); encoder.Save(transparentPng);
            bitmapData.SetData("PNG", transparentPng.ToArray(), autoConvert: false);
            Check(Pixels(ClipboardImageService.ReadImage(bitmapData)!).Where((_, i) => i % 4 == 3).All(a => a == 0),
                "explicit PNG takes precedence over bitmap and keeps fully transparent pixels");
            return png;
        });

        var store = new ClipboardImageStore();
        string first, second, saved;
        try
        {
            first = store.Add(image); second = store.Add(image);
            Check(first != second && File.Exists(first) && File.Exists(second), "repeated paste creates distinct snapshots");
            var entry = new ImageEntry { InputPath = first, IsClipboardImage = true };
            try { entry.GetOutputDirectory(null, null); throw new Exception("missing destination was accepted"); }
            catch (InvalidOperationException) { Check(true, "clipboard export cannot fall back to its temporary input folder"); }
            var exportDirectory = Path.Combine(root, "클립보드 저장 결과");
            Check(entry.GetOutputDirectory(null, exportDirectory) == exportDirectory
                && entry.GetOutputDirectory(exportDirectory, null) == exportDirectory,
                "clipboard export uses the chosen destination");
            Check(new ImageEntry { InputPath = source }.GetOutputDirectory(null, null) == null,
                "regular image keeps original-side destination behavior");
            var preview = await EngineService.PreviewAsync(first, 16, CancellationToken.None,
                outputSize: new OutputSizeOptions(32, 32, false));
            Check(preview.Width == 32 && Directory.GetFiles(Path.GetDirectoryName(first)!).Length == 2
                && !Directory.Exists(exportDirectory), "pasted image previews without exporting a result");
            saved = await EngineService.ConvertAsync(first, entry.GetOutputDirectory(null, exportDirectory), 16,
                CancellationToken.None, outputSize: new OutputSizeOptions(32, 32, false));
            Check(File.Exists(saved) && Path.GetDirectoryName(saved) == exportDirectory,
                "explicit clipboard conversion writes only to the chosen export directory");
            store.Release(first); store.Release(source);
            Check(!File.Exists(first) && File.Exists(second) && File.Exists(source) && File.Exists(saved),
                "removing a pasted entry deletes only its owned snapshot");
        }
        finally { store.Dispose(); }
        Check(!File.Exists(second) && !Directory.Exists(Path.GetDirectoryName(second)!) && File.Exists(saved),
            "closing removes clipboard session inputs and preserves saved results");
    }

    private static Task<T> OnSta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { completion.SetResult(action()); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAILED: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
