using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
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

Assert(new OutputSizeOptions(96, 1).Resolve(128, 64) == (96, 48), "linked width follows original landscape ratio");
Assert(new OutputSizeOptions(1, 32, UseWidthAsAnchor: false).Resolve(128, 64) == (64, 32), "linked height follows original landscape ratio");
Assert(new OutputSizeOptions(96, 1).Resolve(64, 128) == (96, 192), "linked width follows original portrait ratio");
Assert(new OutputSizeOptions(1, 96, UseWidthAsAnchor: false).Resolve(64, 128) == (48, 96), "linked height follows original portrait ratio");
Assert(new OutputSizeOptions(80, 32, KeepAspectRatio: false).Resolve(128, 64) == (80, 32), "unlinked dimensions stay exact");
Assert(new OutputSizeOptions(3, 1).Resolve(2, 1) == (3, 2), "linked fractional dimension rounds halves away from zero");
Assert(new OutputSizeOptions(1, 1).Resolve(4096, 1) == (1, 1), "linked dimension never rounds to zero");
Assert(new OutputSizeOptions(4096, 4096, false).Resolve(64, 64) == (4096, 4096), "maximum output pixel count is accepted");
MustThrow<ArgumentOutOfRangeException>(() => new OutputSizeOptions(0, 32, false).Validate(), "zero output dimension rejected");
MustThrow<ArgumentOutOfRangeException>(() => new OutputSizeOptions(8193, 32, false).Validate(), "output side above limit rejected");
MustThrow<ArgumentOutOfRangeException>(() => new OutputSizeOptions(8192, 8192, false).Validate(), "output pixel count above limit rejected");
MustThrow<ArgumentOutOfRangeException>(() => new OutputSizeOptions(8192, 1).Resolve(1, 2), "linked derived dimension above limit rejected");
MustThrow<ArgumentOutOfRangeException>(() => new OutputSizeOptions(8192, 1).Resolve(2, 1), "linked derived pixel count above limit rejected");

var landscapeSource = Path.Combine(root, "가로 원본 128x64.png");
var portraitSource = Path.Combine(root, "세로 원본 64x128.png");
foreach (var (path, width, height) in new[] { (landscapeSource, 128, 64), (portraitSource, 64, 128) })
{
    using var image = new Bitmap(width, height);
    using var graphics = Graphics.FromImage(image);
    graphics.Clear(Color.Transparent);
    graphics.FillRectangle(Brushes.Coral, 8, 8, width / 2 - 8, height - 16);
    graphics.FillRectangle(Brushes.Teal, width / 2, 8, width / 2 - 8, height - 16);
    graphics.FillRectangle(Brushes.Gold, width / 2 - 8, height / 2 - 8, 16, 16);
    image.Save(path, ImageFormat.Png);
}
Assert(OutputImageService.ReadDimensions(landscapeSource) == (128, 64), "metadata reads actual original dimensions");
var landscapeHash = SHA256.HashData(File.ReadAllBytes(landscapeSource));
var exactOutput = await EngineService.ConvertAsync(landscapeSource, customDirectory, 16, CancellationToken.None,
    outputSize: new OutputSizeOptions(80, 32, KeepAspectRatio: false));
Assert(OutputImageService.ReadDimensions(exactOutput) == (80, 32), "unlinked conversion writes exact requested PNG dimensions");
var linkedOutputs = await Task.WhenAll(
    EngineService.ConvertAsync(landscapeSource, customDirectory, 16, CancellationToken.None, outputSize: new OutputSizeOptions(96, 1)),
    EngineService.ConvertAsync(portraitSource, customDirectory, 16, CancellationToken.None, outputSize: new OutputSizeOptions(96, 1)));
Assert(OutputImageService.ReadDimensions(linkedOutputs[0]) == (96, 48)
    && OutputImageService.ReadDimensions(linkedOutputs[1]) == (96, 192), "linked batch conversion uses each original image ratio");
var heightOutput = await EngineService.ConvertAsync(landscapeSource, customDirectory, 16, CancellationToken.None,
    outputSize: new OutputSizeOptions(1, 32, UseWidthAsAnchor: false));
Assert(OutputImageService.ReadDimensions(heightOutput) == (64, 32), "height anchored conversion writes exact dimensions");
Assert(landscapeHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(landscapeSource))), "output resizing preserves original image");
var autoAfterResize = await EngineService.ConvertAsync(source, customDirectory, 16, CancellationToken.None);
Assert(firstHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(autoAfterResize))), "automatic output remains unchanged after size overrides");

var alphaSource = Path.Combine(root, "알파 리사이즈.png");
var alphaColors = new[] { Color.FromArgb(0, 10, 20, 30), Color.FromArgb(1, 230, 40, 50), Color.FromArgb(128, 60, 170, 80), Color.FromArgb(255, 90, 100, 210) };
using (var image = new Bitmap(2, 2))
{
    for (var y = 0; y < 2; y++)
        for (var x = 0; x < 2; x++) image.SetPixel(x, y, alphaColors[y * 2 + x]);
    image.Save(alphaSource, ImageFormat.Png);
}
OutputImageService.ResizePng(alphaSource, 6, 4, CancellationToken.None);
using (var image = new Bitmap(alphaSource))
{
    var exactColors = image.Width == 6 && image.Height == 4;
    for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            exactColors &= image.GetPixel(x, y).ToArgb() == alphaColors[(y / 2) * 2 + x / 3].ToArgb();
    Assert(exactColors, "nearest neighbor preserves exact BGRA values including transparent and partial alpha pixels");
}
var beforeCancelledResize = SHA256.HashData(File.ReadAllBytes(alphaSource));
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    MustThrow<OperationCanceledException>(() => OutputImageService.ResizePng(alphaSource, 12, 8, cancelled.Token),
        "pre-cancelled resize stops before writing");
}
Assert(beforeCancelledResize.SequenceEqual(SHA256.HashData(File.ReadAllBytes(alphaSource))), "pre-cancelled resize leaves PNG unchanged");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.CancelAfter(1);
    MustThrow<OperationCanceledException>(() => OutputImageService.ResizePng(alphaSource, 8192, 2048, cancelled.Token),
        "in-flight nearest neighbor resizing observes cancellation");
}
Assert(beforeCancelledResize.SequenceEqual(SHA256.HashData(File.ReadAllBytes(alphaSource))), "cancelled large resize leaves its PNG unchanged before save");

var malformed = Path.Combine(root, "깨진 이미지.png");
File.WriteAllText(malformed, "this is not a PNG");
await MustFail<InvalidOperationException>(() => EngineService.ConvertAsync(malformed, customDirectory, 16, CancellationToken.None), "invalid input reports engine failure");
await MustFail<ArgumentOutOfRangeException>(() => EngineService.ConvertAsync(source, null, 0, CancellationToken.None), "invalid colors rejected");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    await MustFail<OperationCanceledException>(() => EngineService.ConvertAsync(source, null, 16, cancelled.Token, 4,
        new OutputSizeOptions(96, 48)), "pre-cancelled manual and output-size request creates no output");
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
    await MustFail<OperationCanceledException>(() => EngineService.ConvertAsync(large, customDirectory, 256, cancelled.Token,
        outputSize: new OutputSizeOptions(4096, 4096)), "in-flight cancellation terminates conversion with output sizing");
}
Assert(!Directory.EnumerateFiles(root, ".pixelsnapper-*", SearchOption.AllDirectories).Any(), "no partial output after success, failure, or cancellation");
Assert(!Directory.EnumerateFiles(customDirectory, "취소 테스트_snapped*").Any(), "cancelled conversion commits no final result");

// Preview is a rendering-only operation: it must not consume export filenames or leave files behind.
var previewDirectory = Path.Combine(Path.GetTempPath(), "PixelSnapper", "previews");
var previewFilesBefore = Directory.Exists(previewDirectory)
    ? Directory.GetFiles(previewDirectory).OrderBy(path => path).ToArray() : Array.Empty<string>();
var previewOnlyDirectory = Path.Combine(root, "미리보기 전용 입력 폴더");
Directory.CreateDirectory(previewOnlyDirectory);
var previewOnlySource = Path.Combine(previewOnlyDirectory, "저장 전 이미지.png");
File.Copy(landscapeSource, previewOnlySource);
var filesBeforePreviews = Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path).ToArray();
var automaticPreview = await EngineService.PreviewAsync(source, 16, CancellationToken.None);
Assert(automaticPreview.Bitmap.IsFrozen, "preview bitmap can safely cross worker and UI threads");
Assert((automaticPreview.Width, automaticPreview.Height) == OutputImageService.ReadDimensions(output1),
    "automatic preview uses the same native result dimensions as export");
Assert(BitmapPixels(automaticPreview.Bitmap).SequenceEqual(PngPixels(output1)),
    "preview pixels and transparency exactly match regular export");
var exactPreview = await EngineService.PreviewAsync(previewOnlySource, 16, CancellationToken.None,
    outputSize: new OutputSizeOptions(80, 32, false));
Assert((exactPreview.Width, exactPreview.Height) == (80, 32)
    && BitmapPixels(exactPreview.Bitmap).SequenceEqual(PngPixels(exactOutput)),
    "preview applies exact output dimensions with the same pixels as export");
var linkedPreview = await EngineService.PreviewAsync(portraitSource, 16, CancellationToken.None,
    outputSize: new OutputSizeOptions(96, 1));
Assert((linkedPreview.Width, linkedPreview.Height) == (96, 192),
    "preview uses original image ratio for linked output size");
var preview4 = await EngineService.PreviewAsync(gridSource, 2, CancellationToken.None, 4);
var preview8 = await EngineService.PreviewAsync(gridSource, 2, CancellationToken.None, 8);
Assert(preview4.Width > preview8.Width && preview4.Height > preview8.Height,
    "changing detail options changes preview result");
var twoColorPreview = await EngineService.PreviewAsync(source, 2, CancellationToken.None);
Assert(OpaqueColors(twoColorPreview.Bitmap) <= 2 && OpaqueColors(automaticPreview.Bitmap) > 2,
    "changing color count changes preview palette");
var largePreview = await EngineService.PreviewAsync(previewOnlySource, 16, CancellationToken.None,
    outputSize: new OutputSizeOptions(3072, 1024, false));
Assert((largePreview.Width, largePreview.Height) == (3072, 1024)
    && Math.Max(largePreview.Bitmap.PixelWidth, largePreview.Bitmap.PixelHeight) == 2048,
    "large preview reports actual export dimensions while bounding display memory");
await MustFail<InvalidOperationException>(() => EngineService.PreviewAsync(malformed, 16, CancellationToken.None),
    "malformed preview reports an error without exporting");
await MustFail<InvalidOperationException>(() => EngineService.PreviewAsync(source, 16, CancellationToken.None, 33),
    "out-of-range detail preview reports an error without exporting");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    await MustFail<OperationCanceledException>(() => EngineService.PreviewAsync(source, 16, cancelled.Token),
        "pre-cancelled preview creates no result");
}
using (var cancelled = new CancellationTokenSource())
{
    cancelled.CancelAfter(200);
    await MustFail<OperationCanceledException>(() => EngineService.PreviewAsync(large, 256, cancelled.Token,
        outputSize: new OutputSizeOptions(4096, 4096)), "in-flight preview cancellation terminates rendering");
}
Assert(filesBeforePreviews.SequenceEqual(Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path)),
    "preview successes, option changes, errors and cancellation create no user-visible output files");
Assert(previewFilesBefore.SequenceEqual(Directory.GetFiles(previewDirectory).OrderBy(path => path)),
    "all preview temporary files are removed after success, failure and cancellation");
Assert(landscapeHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(previewOnlySource))),
    "preview leaves original image bytes unchanged");
var savedAfterPreviews = await EngineService.ConvertAsync(previewOnlySource, null, 16, CancellationToken.None,
    outputSize: new OutputSizeOptions(80, 32, false));
Assert(Path.GetFileName(savedAfterPreviews) == "저장 전 이미지_snapped.png"
    && Directory.GetFiles(previewOnlyDirectory).Length == 2,
    "explicit export is the first and only saved result after multiple previews");
Assert(BitmapPixels(exactPreview.Bitmap).SequenceEqual(PngPixels(savedAfterPreviews)),
    "saving after preview preserves the displayed result");

await ClipboardTests.RunAsync(root);
Console.WriteLine("ALL CHECKS PASSED");
Console.WriteLine("Artifacts: " + root);

static void Assert(bool condition, string label)
{
    if (!condition) throw new Exception("FAILED: " + label);
    Console.WriteLine("PASS: " + label);
}

static async Task MustFail<T>(Func<Task> action, string label) where T : Exception
{
    try { await action(); }
    catch (T) { Console.WriteLine("PASS: " + label); return; }
    throw new Exception("FAILED: " + label);
}

static void MustThrow<T>(Action action, string label) where T : Exception
{
    try { action(); }
    catch (T) { Console.WriteLine("PASS: " + label); return; }
    throw new Exception("FAILED: " + label);
}

static byte[] BitmapPixels(System.Windows.Media.Imaging.BitmapSource bitmap)
{
    if (bitmap.Format != System.Windows.Media.PixelFormats.Bgra32)
        bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap,
            System.Windows.Media.PixelFormats.Bgra32, null, 0);
    var stride = bitmap.PixelWidth * 4;
    var pixels = new byte[stride * bitmap.PixelHeight];
    bitmap.CopyPixels(pixels, stride, 0);
    return pixels;
}

static byte[] PngPixels(string path)
{
    using var stream = File.OpenRead(path);
    var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
    return BitmapPixels(decoder.Frames[0]);
}

static int OpaqueColors(System.Windows.Media.Imaging.BitmapSource bitmap)
{
    var pixels = BitmapPixels(bitmap);
    var colors = new HashSet<int>();
    for (var i = 0; i < pixels.Length; i += 4)
        if (pixels[i + 3] > 0) colors.Add(pixels[i] | (pixels[i + 1] << 8) | (pixels[i + 2] << 16));
    return colors.Count;
}
