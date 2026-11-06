using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
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

var gridSource = Path.Combine(root, "수동 픽셀 격자.png");
using (var image = new Bitmap(64, 64))
{
    for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            image.SetPixel(x, y, ((x / 4 + y / 4) % 2) == 0 ? Color.Coral : Color.Teal);
    image.Save(gridSource, ImageFormat.Png);
}
var gridHash = SHA256.HashData(File.ReadAllBytes(gridSource));
var manual4 = await EngineService.ConvertAsync(gridSource, customDirectory, 2, CancellationToken.None, 4);
var manual8 = await EngineService.ConvertAsync(gridSource, customDirectory, 2, CancellationToken.None, 8);
using (var image4 = new Bitmap(manual4))
using (var image8 = new Bitmap(manual8))
{
    Assert(image4.Width > image8.Width && image4.Height > image8.Height,
        "manual 4px and 8px produce different grid sizes on the same source");
    Console.WriteLine($"Manual grid outputs: 4px={image4.Width}x{image4.Height}, 8px={image8.Width}x{image8.Height}");
}
Assert(gridHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(gridSource))), "manual conversion preserves source");
var autoAgain = await EngineService.ConvertAsync(source, customDirectory, 16, CancellationToken.None);
Assert(firstHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(autoAgain))), "automatic conversion remains unchanged after manual requests");

var decimalInvariant = await EngineService.ConvertAsync(gridSource, customDirectory, 2, CancellationToken.None, 2.5);
var previousCulture = CultureInfo.CurrentCulture;
try
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
    var decimalComma = await EngineService.ConvertAsync(gridSource, customDirectory, 2, CancellationToken.None, 2.5);
    Assert(SHA256.HashData(File.ReadAllBytes(decimalInvariant)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(decimalComma))),
        "fractional pixel size is culture invariant with a comma decimal separator");
}
finally { CultureInfo.CurrentCulture = previousCulture; }

foreach (var invalidSize in new[] { 0.0, -1.0, 0.5, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    await MustFail<ArgumentOutOfRangeException>(() => EngineService.ConvertAsync(source, customDirectory, 16, CancellationToken.None, invalidSize),
        $"invalid manual pixel size rejected: {invalidSize.ToString(CultureInfo.InvariantCulture)}");
var filesBeforeOversize = Directory.GetFiles(customDirectory).Length;
await MustFail<InvalidOperationException>(() => EngineService.ConvertAsync(source, customDirectory, 16, CancellationToken.None, 33),
    "manual pixel size larger than half the shortest image side rejected");
Assert(Directory.GetFiles(customDirectory).Length == filesBeforeOversize, "oversized manual request creates no result or partial output");

var malformed = Path.Combine(root, "깨진 이미지.png");
File.WriteAllText(malformed, "this is not a PNG");
await MustFail<InvalidOperationException>(() => EngineService.ConvertAsync(malformed, customDirectory, 16, CancellationToken.None), "invalid input reports engine failure");
await MustFail<ArgumentOutOfRangeException>(() => EngineService.ConvertAsync(source, null, 0, CancellationToken.None), "invalid colors rejected");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    await MustFail<OperationCanceledException>(() => EngineService.ConvertAsync(source, null, 16, cancelled.Token, 4), "pre-cancelled manual request creates no output");
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
