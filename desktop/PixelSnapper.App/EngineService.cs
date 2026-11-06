using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;

namespace PixelSnapper;

public static class EngineService
{
    public static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg"];
    public static int DefaultSampleValue = 16;
    private static readonly SemaphoreSlim ExtractionGate = new(1, 1);
    private const string ResourceName = "PixelSnapper.Engine.exe";

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static async Task<string> ConvertAsync(string inputPath, string? outputDirectory,
        int sampleValue, CancellationToken token, double? pixelSize = null, OutputSizeOptions? outputSize = null)
    {
        var request = await PrepareRequestAsync(inputPath, sampleValue, token, pixelSize, outputSize).ConfigureAwait(false);
        var destination = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.GetDirectoryName(request.InputPath)!
            : Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(destination);
        // Keep the temporary output on the destination volume, so the final move is atomic.
        var temporaryOutput = Path.Combine(destination, $".pixelsnapper-{Guid.NewGuid():N}.tmp.png");
        try
        {
            await RenderAsync(request, temporaryOutput, sampleValue, pixelSize, token).ConfigureAwait(false);
            var stem = Path.GetFileNameWithoutExtension(request.InputPath) + "_snapped";
            for (var index = 0; ; index++)
            {
                token.ThrowIfCancellationRequested();
                var suffix = index == 0 ? "" : "_" + index.ToString(CultureInfo.InvariantCulture);
                var candidate = Path.Combine(destination, stem + suffix + ".png");
                try
                {
                    // No overwrite: a simultaneous conversion or an existing result gets a new name.
                    File.Move(temporaryOutput, candidate, overwrite: false);
                    return candidate;
                }
                catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
            }
        }
        finally { DeleteTemporaryOutput(temporaryOutput); }
    }

    public static async Task<PreviewImage> PreviewAsync(string inputPath, int sampleValue,
        CancellationToken token, double? pixelSize = null, OutputSizeOptions? outputSize = null)
    {
        var request = await PrepareRequestAsync(inputPath, sampleValue, token, pixelSize, outputSize).ConfigureAwait(false);
        // Preview requests never receive or create a user-selected export destination.
        var directory = Path.Combine(Path.GetTempPath(), "PixelSnapper", "previews");
        Directory.CreateDirectory(directory);
        var temporaryOutput = Path.Combine(directory, $"preview-{Guid.NewGuid():N}.png");
        try
        {
            await RenderAsync(request, temporaryOutput, sampleValue, pixelSize, token).ConfigureAwait(false);
            return await Task.Run(() => PreviewImage.Load(temporaryOutput, token), token).ConfigureAwait(false);
        }
        finally { DeleteTemporaryOutput(temporaryOutput); }
    }

    private sealed record RenderRequest(string InputPath, (int Width, int Height)? OutputDimensions);

    private static async Task<RenderRequest> PrepareRequestAsync(string inputPath, int sampleValue,
        CancellationToken token, double? pixelSize, OutputSizeOptions? outputSize)
    {
        token.ThrowIfCancellationRequested();
        if (sampleValue is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(sampleValue), "색상 수는 1~256 사이로 입력해 주세요.");
        if (pixelSize is { } size && (!double.IsFinite(size) || size < 1))
            throw new ArgumentOutOfRangeException(nameof(pixelSize), "세밀도는 1px 이상의 숫자로 입력해 주세요.");
        outputSize?.Validate();

        inputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("원본 이미지 파일을 찾을 수 없습니다.", inputPath);
        if (!IsSupported(inputPath))
            throw new ArgumentException("PNG, JPG, JPEG 이미지만 사용할 수 있습니다.", nameof(inputPath));

        (int Width, int Height)? outputDimensions = null;
        if (outputSize != null)
        {
            outputDimensions = await Task.Run(() =>
            {
                var original = OutputImageService.ReadDimensions(inputPath);
                return outputSize.Resolve(original.Width, original.Height);
            }, token).ConfigureAwait(false);
        }
        return new RenderRequest(inputPath, outputDimensions);
    }

    private static async Task RenderAsync(RenderRequest request, string temporaryOutput,
        int sampleValue, double? pixelSize, CancellationToken token)
    {
        var engine = await ExtractEngineAsync(token).ConfigureAwait(false);
        var start = new ProcessStartInfo(engine)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(engine)!
        };
        start.ArgumentList.Add(request.InputPath);
        start.ArgumentList.Add(temporaryOutput);
        start.ArgumentList.Add(sampleValue.ToString(CultureInfo.InvariantCulture));
        if (pixelSize is { } manualSize)
        {
            start.ArgumentList.Add("--pixel-size");
            start.ArgumentList.Add(manualSize.ToString("R", CultureInfo.InvariantCulture));
        }

        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new InvalidOperationException("이미지 변환 엔진을 실행하지 못했습니다.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            // Wait for the child to release its file before deleting our temporary output.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            if (pixelSize.HasValue && detail.Contains("pixel_size_override", StringComparison.Ordinal)
                && detail.Contains("out of valid range", StringComparison.Ordinal))
                throw new InvalidOperationException("세밀도는 1px 이상, 원본 이미지의 짧은 변 길이의 절반 이하로 지정해 주세요.");
            if (detail.Length > 2000) detail = detail[..2000];
            throw new InvalidOperationException($"이미지 변환에 실패했습니다.\n{detail}");
        }
        if (!File.Exists(temporaryOutput) || new FileInfo(temporaryOutput).Length < 8)
            throw new InvalidOperationException("변환 결과 파일이 생성되지 않았습니다.");

        if (request.OutputDimensions is { } dimensions)
            await Task.Run(() => OutputImageService.ResizePng(temporaryOutput, dimensions.Width, dimensions.Height, token), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private static void DeleteTemporaryOutput(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<string> ExtractEngineAsync(CancellationToken token)
    {
        await ExtractionGate.WaitAsync(token);
        string? temporaryEngine = null;
        try
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("실행파일에 이미지 변환 엔진이 포함되어 있지 않습니다.");
            using var memory = new MemoryStream();
            await resource.CopyToAsync(memory, token);
            var bytes = memory.ToArray();
            var expectedHash = Convert.ToHexString(SHA256.HashData(bytes));
            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PixelSnapper", "engine", expectedHash.ToLowerInvariant());
            Directory.CreateDirectory(cacheDirectory);
            var enginePath = Path.Combine(cacheDirectory, "spritefusion-pixel-snapper.exe");

            if (!File.Exists(enginePath))
            {
                temporaryEngine = Path.Combine(cacheDirectory, $"engine-{Guid.NewGuid():N}.tmp");
                await File.WriteAllBytesAsync(temporaryEngine, bytes, token);
                try { File.Move(temporaryEngine, enginePath, overwrite: false); }
                catch (IOException) when (File.Exists(enginePath)) { }
            }

            // Validate an existing cache too; never execute a partial or modified cached binary.
            await using var cached = File.OpenRead(enginePath);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(cached, token));
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                throw new InvalidOperationException("변환 엔진 캐시가 손상되었습니다. 다음 폴더를 삭제한 후 다시 실행해 주세요:\n" + cacheDirectory);
            return enginePath;
        }
        finally
        {
            if (temporaryEngine != null)
            {
                try { File.Delete(temporaryEngine); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            ExtractionGate.Release();
        }
    }
}
