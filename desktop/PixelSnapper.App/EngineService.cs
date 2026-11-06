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
        int sampleValue, CancellationToken token, double? pixelSize = null)
    {
        token.ThrowIfCancellationRequested();
        if (sampleValue is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(sampleValue), "색상 수는 1~256 사이로 입력해 주세요.");
        if (pixelSize is { } size && (!double.IsFinite(size) || size < 1))
            throw new ArgumentOutOfRangeException(nameof(pixelSize), "픽셀 크기는 1px 이상의 숫자로 입력해 주세요.");

        inputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("원본 이미지 파일을 찾을 수 없습니다.", inputPath);
        if (!IsSupported(inputPath))
            throw new ArgumentException("PNG, JPG, JPEG 이미지만 사용할 수 있습니다.", nameof(inputPath));

        var destination = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.GetDirectoryName(inputPath)!
            : Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(destination);
        var engine = await ExtractEngineAsync(token);
        // Keep the temporary output on the destination volume, so the final move is atomic.
        var temporaryOutput = Path.Combine(destination, $".pixelsnapper-{Guid.NewGuid():N}.tmp.png");
        try
        {
            var start = new ProcessStartInfo(engine)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(engine)!
            };
            start.ArgumentList.Add(inputPath);
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
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                // Wait for the child to release its file before deleting our temporary output.
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdoutTask, stderrTask);
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                if (pixelSize.HasValue && detail.Contains("pixel_size_override", StringComparison.Ordinal)
                    && detail.Contains("out of valid range", StringComparison.Ordinal))
                    throw new InvalidOperationException("픽셀 크기는 1px 이상, 원본 이미지의 짧은 변 길이의 절반 이하로 지정해 주세요.");
                if (detail.Length > 2000) detail = detail[..2000];
                throw new InvalidOperationException($"이미지 변환에 실패했습니다.\n{detail}");
            }
            if (!File.Exists(temporaryOutput) || new FileInfo(temporaryOutput).Length < 8)
                throw new InvalidOperationException("변환 결과 파일이 생성되지 않았습니다.");

            var stem = Path.GetFileNameWithoutExtension(inputPath) + "_snapped";
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
        finally
        {
            try { File.Delete(temporaryOutput); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
