using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using PixelSnapper;

var root = Path.Combine(AppContext.BaseDirectory, "test-results", "한글 경로 " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var source = Path.Combine(root, "원본 이미지.png");
using (var image = new Bitmap(64, 64))
{
    using var graphics = Graphics.FromImage(image);
    graphics.Clear(Color.Transparent);
    graphics.FillRectangle(Brushes.Coral, 8, 8, 24, 24);
    graphics.FillRectangle(Brushes.Teal, 32, 8, 24, 24);
    graphics.FillRectangle(Brushes.Gold, 8, 32, 24, 24);
    image.Save(source, ImageFormat.Png);
}
var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
var output1 = await EngineService.ConvertAsync(source, null, 16, CancellationToken.None);
Assert(File.Exists(output1), "real PNG conversion and Unicode input/output paths");
var firstHash = SHA256.HashData(File.ReadAllBytes(output1));
var output2 = await EngineService.ConvertAsync(source, null, 16, CancellationToken.None);
Assert(output1 != output2 && firstHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(output1))), "existing output stays unchanged");
Assert(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "source stays unchanged");
using (var image = new Bitmap(output1))
{
    Assert(image.Width > 0 && image.Height > 0, "valid output image");
    var transparent = false;
    for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            transparent |= image.GetPixel(x, y).A == 0;
    Assert(transparent, "transparent background survives conversion");
    Console.WriteLine($"Output image: {image.Width}x{image.Height}");
}
var sameStemJpeg = Path.Combine(root, "원본 이미지.JPG");
using (var image = new Bitmap(source)) image.Save(sameStemJpeg, ImageFormat.Jpeg);
var customDirectory = Path.Combine(root, "선택한 출력 폴더");
var results = await Task.WhenAll(
    EngineService.ConvertAsync(source, customDirectory, 8, CancellationToken.None),
    EngineService.ConvertAsync(sameStemJpeg, customDirectory, 8, CancellationToken.None));
Assert(results.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 && results.All(File.Exists), "simultaneous PNG/JPEG with identical stems get separate files");

var malformed = Path.Combine(root, "깨진 이미지.png");
File.WriteAllText(malformed, "this is not a PNG");
await MustFail<InvalidOperationException>(() => EngineService.ConvertAsync(malformed, customDirectory, 16, CancellationToken.None), "invalid input reports engine failure");
await MustFail<ArgumentOutOfRangeException>(() => EngineService.ConvertAsync(source, null, 0, CancellationToken.None), "invalid colors rejected");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    await MustFail<OperationCanceledException>(() => EngineService.ConvertAsync(source, null, 16, cancelled.Token), "pre-cancelled request creates no output");
}
var large = Path.Combine(root, "취소 테스트.png");
using (var image = new Bitmap(1024, 1024))
{
    var random = new Random(42);
    for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            image.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
    image.Save(large, ImageFormat.Png);
}
using (var cancelled = new CancellationTokenSource())
{
    cancelled.CancelAfter(200);
    await MustFail<OperationCanceledException>(() => EngineService.ConvertAsync(large, customDirectory, 256, cancelled.Token), "in-flight cancellation terminates conversion");
}
Assert(!Directory.EnumerateFiles(root, ".pixelsnapper-*", SearchOption.AllDirectories).Any(), "no partial output after success, failure, or cancellation");
Assert(!Directory.EnumerateFiles(customDirectory, "취소 테스트_snapped*").Any(), "cancelled conversion commits no final result");
Console.WriteLine("ALL CHECKS PASSED");
Console.WriteLine("Artifacts: " + root);

static void Assert(bool condition, string label)
{
    if (!condition) throw new Exception("FAILED: " + label);
    Console.WriteLine("PASS: " + label);
}

static async Task MustFail<T>(Func<Task<string>> action, string label) where T : Exception
{
    try { await action(); }
    catch (T) { Console.WriteLine("PASS: " + label); return; }
    throw new Exception("FAILED: " + label);
}
