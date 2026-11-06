using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace PixelSnapper;

public sealed record ReleaseInfo(string VersionTag, Version Version, Uri DownloadUrl, Uri HashUrl, long Size);

public static class UpdateService
{
    public const string RepositoryUrl = "https://github.com/JIINNY-Hee/spritefusion-pixel-snapper";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    private const string LatestUrl = "https://api.github.com/repos/JIINNY-Hee/spritefusion-pixel-snapper/releases/latest";
    private const string AssetName = "PixelSnapper.exe";
    private const string HashName = "SHA256SUMS.txt";
    private const long MaxAssetBytes = 300L * 1024 * 1024;
    private static readonly Regex StableTag = new(@"\Av(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\z", RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);
    public static Version CurrentVersion { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));
    private static readonly HttpClient Client = CreateClient();
    public static string CurrentVersionTag => "v" + CurrentVersion.ToString(3);
    private static string UpdateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelSnapper", "updates");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PixelSnapper/" + CurrentVersion.ToString(3));
        return client;
    }

    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new InvalidOperationException("GitHub 연결 제한에 도달했습니다. 잠시 후 다시 확인해 주세요.");
            response.EnsureSuccessStatusCode();
            return ParseRelease(await ReadLimitedTextAsync(response.Content, 1024 * 1024, timeout.Token), CurrentVersion);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("업데이트 확인 시간이 초과되었습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요."); }
    }

    internal static ReleaseInfo? ParseRelease(string json, Version installedVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseStableVersion(tag, out var version) || version <= Normalize(installedVersion)) return null;
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var executable = FindAsset(assets, AssetName);
        var checksum = FindAsset(assets, HashName);
        var release = new ReleaseInfo(tag, version,
            new Uri(executable.GetProperty("browser_download_url").GetString() ?? "", UriKind.Absolute),
            new Uri(checksum.GetProperty("browser_download_url").GetString() ?? "", UriKind.Absolute),
            executable.GetProperty("size").GetInt64());
        ValidateRelease(release);
        return release;
    }

    private static JsonElement FindAsset(JsonElement[] assets, string name)
    {
        var matches = assets.Where(item => item.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"릴리스에 {name} 파일이 정확히 하나 있어야 합니다.");
        return matches[0];
    }

    internal static bool TryParseStableVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (!StableTag.IsMatch(tag) || !Version.TryParse(tag[1..], out var parsed) || parsed.Revision != -1) return false;
        version = parsed;
        return true;
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    internal static void ValidateRelease(ReleaseInfo release)
    {
        if (!TryParseStableVersion(release.VersionTag, out var parsed) || parsed != release.Version ||
            release.Size is <= 0 or > MaxAssetBytes ||
            !IsExpectedAssetUrl(release.DownloadUrl, release.VersionTag, AssetName) ||
            !IsExpectedAssetUrl(release.HashUrl, release.VersionTag, HashName))
            throw new InvalidDataException("이 저장소의 정식 Windows 업데이트 파일을 확인할 수 없습니다.");
    }

    internal static bool IsExpectedAssetUrl(Uri uri, string tag, string name) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath.Equals($"/JIINNY-Hee/spritefusion-pixel-snapper/releases/download/{tag}/{name}", StringComparison.Ordinal);

    public static Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress = null, CancellationToken token = default)
    {
        ValidateRelease(release);
        if (release.Version <= CurrentVersion) throw new InvalidOperationException("설치된 버전보다 새로운 업데이트만 설치할 수 있습니다.");
        return DownloadCoreAsync(release, Client, UpdateRoot, progress, token);
    }

    internal static async Task<string> DownloadCoreAsync(ReleaseInfo release, HttpClient client, string stagingRoot,
        IProgress<double>? progress, CancellationToken token)
    {
        ValidateRelease(release);
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        string? stage = null;
        try
        {
            using var checksumResponse = await GetAssetAsync(client, release.HashUrl, release.VersionTag, HashName, timeout.Token);
            var hash = ParseChecksum(await ReadLimitedTextAsync(checksumResponse.Content, 16 * 1024, timeout.Token));
            stage = Path.Combine(Path.GetFullPath(stagingRoot), release.VersionTag + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var stagedFile = Path.Combine(stage, AssetName);
            using var response = await GetAssetAsync(client, release.DownloadUrl, release.VersionTag, AssetName, timeout.Token);
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new InvalidDataException("업데이트 파일 크기가 릴리스 정보와 일치하지 않습니다.");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(stagedFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                progress?.Report(0);
                while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += count;
                    if (total > release.Size || total > MaxAssetBytes) throw new InvalidDataException("업데이트 파일이 예상 크기를 초과했습니다.");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    progress?.Report(100.0 * total / release.Size);
                }
                if (total != release.Size) throw new InvalidDataException("업데이트 파일 다운로드가 완료되지 않았습니다.");
                await output.FlushAsync(timeout.Token);
            }
            await VerifyHashAsync(stagedFile, hash, timeout.Token);
            ValidateExecutableVersion(stagedFile, release.Version);
            var receipt = new DownloadReceipt(release.VersionTag, hash);
            await File.WriteAllTextAsync(Path.Combine(stage, "verified.json"), JsonSerializer.Serialize(receipt), timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            progress?.Report(100);
            return stagedFile;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { DeleteOwnStage(stage, stagingRoot); throw new TimeoutException("업데이트 다운로드 시간이 초과되었습니다. 다시 시도해 주세요."); }
        catch
        { DeleteOwnStage(stage, stagingRoot); throw; }
    }

    internal static string ParseChecksum(string text)
    {
        string? found = null;
        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var match = Regex.Match(line, @"\A([0-9a-fA-F]{64}) [ *]PixelSnapper\.exe\z", RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            if (found != null) throw new InvalidDataException("업데이트 체크섬이 중복되어 있습니다.");
            found = match.Groups[1].Value.ToLowerInvariant();
        }
        return found ?? throw new InvalidDataException("SHA256SUMS.txt에서 PixelSnapper.exe의 SHA-256 체크섬을 찾을 수 없습니다.");
    }

    private static async Task<HttpResponseMessage> GetAssetAsync(HttpClient client, Uri uri, string tag, string name, CancellationToken token)
    {
        if (!IsExpectedAssetUrl(uri, tag, name)) throw new InvalidDataException("허용되지 않은 업데이트 주소입니다.");
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null || redirect == 5) throw new InvalidDataException("업데이트 다운로드 주소를 확인할 수 없습니다.");
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!IsAllowedRedirect(next, tag, name)) throw new InvalidDataException("허용되지 않은 다운로드 경로로 이동하려 했습니다.");
                uri = next;
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("업데이트 다운로드 주소를 확인할 수 없습니다.");
    }

    internal static bool IsAllowedRedirect(Uri uri, string tag, string name) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        (IsExpectedAssetUrl(uri, tag, name) || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static async Task<string> ReadLimitedTextAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("업데이트 응답이 너무 큽니다.");
        await using var stream = await content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + count > maximum) throw new InvalidDataException("업데이트 응답이 너무 큽니다.");
            buffer.Write(chunk, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task VerifyHashAsync(string path, string expected, CancellationToken token)
    {
        if (!DigestPattern.IsMatch(expected)) throw new InvalidDataException("올바르지 않은 SHA-256 체크섬입니다.");
        await using var stream = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(stream, token);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected)))
            throw new InvalidDataException("업데이트 파일의 SHA-256 검증에 실패했습니다. 기존 프로그램은 변경되지 않았습니다.");
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void ValidateExecutableVersion(string path, Version expected)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != expected)
            throw new InvalidDataException("다운로드한 실행파일의 버전이 릴리스 버전과 일치하지 않습니다.");
    }

    public static void StartInstall(string stagedPath)
    {
        stagedPath = Path.GetFullPath(stagedPath);
        var receipt = ValidateStagedPath(stagedPath);
        if (!TryParseStableVersion(receipt.VersionTag, out var version) || version <= CurrentVersion)
            throw new InvalidOperationException("설치된 버전보다 새로운 업데이트만 설치할 수 있습니다.");
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("현재 프로그램 경로를 확인할 수 없습니다.");
        ValidateTarget(target, stagedPath);
        using var parent = Process.GetCurrentProcess();
        var request = new InstallRequest(target, parent.Id, parent.StartTime.ToUniversalTime().Ticks,
            FileHash(target), receipt.Sha256, receipt.VersionTag, DateTimeOffset.UtcNow);
        var requestPath = Path.Combine(Path.GetDirectoryName(stagedPath)!, "install-" + Guid.NewGuid().ToString("N") + ".json");
        // A request is private to this verified staged executable; no command shell is involved.
        using (var output = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(output, request);
        try
        {
            var start = new ProcessStartInfo(stagedPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(stagedPath)! };
            start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(requestPath);
            using var helper = Process.Start(start) ?? throw new InvalidOperationException("업데이트 설치 프로그램을 실행하지 못했습니다.");
            var readyPath = requestPath + ".ready";
            var stopwatch = Stopwatch.StartNew();
            while (!File.Exists(readyPath) && !helper.HasExited && stopwatch.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(50);
            if (!File.Exists(readyPath))
            {
                if (!helper.HasExited) { helper.Kill(entireProcessTree: true); helper.WaitForExit(5000); }
                throw new IOException("업데이트 설치 준비를 완료하지 못했습니다. 기존 프로그램을 계속 사용할 수 있습니다.");
            }
            TryDeleteFile(readyPath);
        }
        catch { File.Delete(requestPath); throw; }
    }

    public static bool TryHandleHelper(string[] args)
    {
        if (args.Length == 0 || args[0] != "--apply-update") return false;
        try
        {
            if (args.Length != 2) throw new InvalidDataException("올바르지 않은 업데이트 실행 요청입니다.");
            RunHelper(args[1]);
        }
        catch (Exception ex)
        {
            MessageBox.Show("업데이트를 설치하지 못했습니다.\n\n" + ex.Message +
                "\n\n기존 실행파일을 다시 열어 주세요. 폴더에 쓰기 권한이 없다면 프로그램을 문서 폴더 등으로 옮긴 뒤 다시 시도해 주세요.",
                "픽셀 스냅퍼 업데이트", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return true;
    }

    internal static void RunHelper(string requestPath)
    {
        var staged = Environment.ProcessPath ?? throw new InvalidOperationException("업데이트 실행파일 경로를 확인할 수 없습니다.");
        var receipt = ValidateStagedPath(staged);
        requestPath = Path.GetFullPath(requestPath);
        if (!string.Equals(Path.GetDirectoryName(requestPath), Path.GetDirectoryName(staged), StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(requestPath), @"\Ainstall-[0-9a-f]{32}\.json\z") || new FileInfo(requestPath).Length > 16384)
            throw new InvalidDataException("올바르지 않은 업데이트 요청 파일입니다.");
        var request = JsonSerializer.Deserialize<InstallRequest>(File.ReadAllText(requestPath)) ?? throw new InvalidDataException("업데이트 요청을 읽을 수 없습니다.");
        if (request.CreatedUtc < DateTimeOffset.UtcNow.AddMinutes(-10) || request.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(1) ||
            request.VersionTag != receipt.VersionTag || !DigestPattern.IsMatch(request.OldSha256) ||
            !string.Equals(request.NewSha256, receipt.Sha256, StringComparison.OrdinalIgnoreCase) || request.ParentId <= 0)
            throw new InvalidDataException("업데이트 요청의 검증에 실패했습니다.");
        ValidateTarget(request.TargetPath, staged);
        File.WriteAllText(requestPath + ".ready", "ready");
        WaitForParent(request);
        ApplyVerifiedUpdate(staged, request.TargetPath, request.NewSha256, request.OldSha256, path =>
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
            using var launched = Process.Start(start) ?? throw new InvalidOperationException("업데이트한 프로그램을 실행하지 못했습니다.");
        });
        TryDeleteFile(requestPath);
    }

    private static DownloadReceipt ValidateStagedPath(string stagedPath)
    {
        if (!IsUnderDirectory(stagedPath, UpdateRoot) || Path.GetFileName(stagedPath) != AssetName ||
            (File.GetAttributes(stagedPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("검증된 업데이트 실행파일이 아닙니다.");
        var receiptPath = Path.Combine(Path.GetDirectoryName(stagedPath)!, "verified.json");
        if (new FileInfo(receiptPath).Length > 4096) throw new InvalidDataException("업데이트 검증 정보가 올바르지 않습니다.");
        var receipt = JsonSerializer.Deserialize<DownloadReceipt>(File.ReadAllText(receiptPath)) ?? throw new InvalidDataException("업데이트 검증 정보를 읽을 수 없습니다.");
        if (!TryParseStableVersion(receipt.VersionTag, out var version) || !DigestPattern.IsMatch(receipt.Sha256) ||
            !string.Equals(FileHash(stagedPath), receipt.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("업데이트 실행파일의 검증에 실패했습니다.");
        ValidateExecutableVersion(stagedPath, version);
        return receipt;
    }

    private static void ValidateTarget(string target, string staged)
    {
        if (!Path.IsPathFullyQualified(target) || !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(target) || IsUnderDirectory(target, UpdateRoot) ||
            string.Equals(Path.GetFullPath(target), Path.GetFullPath(staged), StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("업데이트할 기존 실행파일 경로가 올바르지 않습니다.");
    }

    private static void WaitForParent(InstallRequest request)
    {
        Process parent;
        try { parent = Process.GetProcessById(request.ParentId); }
        catch (ArgumentException) { return; } // The parent can exit before the helper finishes starting.
        using (parent)
        {
            try
            {
                if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks ||
                    !string.Equals(parent.MainModule?.FileName, request.TargetPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("업데이트 요청을 보낸 프로그램이 일치하지 않습니다.");
                if (!parent.WaitForExit(60000))
                    throw new TimeoutException("기존 프로그램이 닫히지 않았습니다. 픽셀 스냅퍼를 닫고 업데이트를 다시 시도해 주세요.");
            }
            catch (InvalidOperationException) when (parent.HasExited) { }
        }
    }

    internal static void ApplyVerifiedUpdate(string stagedPath, string targetPath, string expectedNewHash, string expectedOldHash, Action<string> launch)
    {
        if (!DigestPattern.IsMatch(expectedNewHash) || !DigestPattern.IsMatch(expectedOldHash) ||
            !string.Equals(FileHash(stagedPath), expectedNewHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(FileHash(targetPath), expectedOldHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("업데이트 중 파일이 변경되어 설치를 중단했습니다. 기존 실행파일은 변경되지 않았습니다.");
        var directory = Path.GetDirectoryName(targetPath)!;
        var id = Guid.NewGuid().ToString("N");
        var incoming = Path.Combine(directory, "." + Path.GetFileName(targetPath) + ".update-" + id + ".tmp");
        var backup = Path.Combine(directory, "." + Path.GetFileName(targetPath) + ".backup-" + id + ".exe");
        var replaced = false;
        try
        {
            File.Copy(stagedPath, incoming, overwrite: false);
            if (!string.Equals(FileHash(incoming), expectedNewHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("복사한 업데이트 파일 검증에 실패했습니다.");
            // Same-directory replacement keeps the original intact if the operation fails.
            File.Replace(incoming, targetPath, backup, ignoreMetadataErrors: true);
            replaced = true;
            try { launch(targetPath); }
            catch (Exception launchError)
            {
                try { File.Replace(backup, targetPath, null, ignoreMetadataErrors: true); replaced = false; }
                catch (Exception restoreError)
                {
                    throw new IOException($"새 버전을 실행하지 못했고 자동 복원에도 실패했습니다. 이전 버전 백업: {backup}\n{restoreError.Message}", launchError);
                }
                throw new IOException("새 버전을 실행하지 못해 기존 실행파일로 복원했습니다.\n" + launchError.Message, launchError);
            }
            TryDeleteFile(backup);
        }
        finally
        {
            TryDeleteFile(incoming);
            // Keep a backup whenever a completed replacement could not be rolled back.
            if (!replaced && File.Exists(targetPath) && FileHash(targetPath).Equals(expectedOldHash, StringComparison.OrdinalIgnoreCase)) TryDeleteFile(backup);
        }
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return relative != "." && !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void DeleteOwnStage(string? stage, string root)
    {
        if (stage == null || !IsUnderDirectory(stage, root)) return;
        try { Directory.Delete(stage, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record DownloadReceipt(string VersionTag, string Sha256);
    private sealed record InstallRequest(string TargetPath, int ParentId, long ParentStartTicks, string OldSha256, string NewSha256, string VersionTag, DateTimeOffset CreatedUtc);
}
