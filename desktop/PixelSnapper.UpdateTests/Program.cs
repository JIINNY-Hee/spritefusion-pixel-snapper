using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PixelSnapper;

if (args.Length > 0 && args[0] == "--apply-update") { UpdateService.TryHandleHelper(args); return; }
if (args.Length == 3 && args[0] == "--test-parent")
{
    File.WriteAllText(args[1], "ready");
    var waiting = Stopwatch.StartNew();
    while (!File.Exists(args[2]) && waiting.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(20);
    return;
}
if (args.Length == 3 && args[0] == "--test-helper-no-ui")
{
    try { UpdateService.RunHelper(args[1]); }
    catch (Exception ex) { File.WriteAllText(args[2], ex.Message); Environment.ExitCode = 3; }
    return;
}
if (args.Length == 0 && File.Exists(Path.Combine(AppContext.BaseDirectory, ".test-update-target")))
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "restarted.txt"), "restarted");
    return;
}

var root = Path.Combine(AppContext.BaseDirectory, "test-results", "업데이트 검사 " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checkedCount = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); checkedCount++;
}
void Reject<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, name); return; }
    throw new Exception("FAIL: " + name);
}
async Task RejectAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, name); return; }
    throw new Exception("FAIL: " + name);
}

var installed = new Version(1, 1, 0);
Check(UpdateService.ParseRelease(Metadata("v1.2.3"), installed)?.Version == new Version(1, 2, 3), "new stable release is offered");
Check(UpdateService.ParseRelease(Metadata("v1.1.0"), installed) == null, "same release is ignored");
Check(UpdateService.ParseRelease(Metadata("v1.0.9"), installed) == null, "older release cannot downgrade");
Check(UpdateService.ParseRelease(Metadata("v2.0.0", prerelease: true), installed) == null, "prereleases are ignored");
Check(UpdateService.ParseRelease(Metadata("v2.0.0", draft: true), installed) == null, "drafts are ignored");
foreach (var tag in new[] { "1.2.3", "v1.2", "v1.2.3.0", "v01.2.3", "v1.2.3-beta", "v1.2.3+metadata", "v1.2.3\n", " v1.2.3", "v999999999999999999.2.3" })
    Check(!UpdateService.TryParseStableVersion(tag, out _), "invalid stable tag rejected: " + tag.TrimEnd());

var expected = AssetUrl("v2.0.0", "PixelSnapper.exe");
foreach (var hostile in new[]
{
    expected.Replace("https://", "http://"), expected.Replace("github.com/", "github.com.evil.invalid/"),
    expected.Replace("github.com/", "github.com@evil.invalid/"), expected.Replace("JIINNY-Hee", "SomeoneElse"),
    expected.Replace("v2.0.0/", "v1.0.0/"), expected + "?download=1", expected + "#fragment",
    expected.Replace("github.com/", "github.com:444/")
})
    Reject<InvalidDataException>(() => UpdateService.ParseRelease(Metadata("v2.0.0", exeUrl: hostile), installed), "hostile or mismatched asset URL rejected");
Reject<InvalidDataException>(() => UpdateService.ParseRelease(Metadata("v2.0.0", hashUrl: "https://example.org/SHA256SUMS.txt"), installed), "checksum URL must belong to exact repo/tag");
Reject<InvalidDataException>(() => UpdateService.ParseRelease(Metadata("v2.0.0", missingHash: true), installed), "checksum asset is mandatory");
Reject<InvalidDataException>(() => UpdateService.ParseRelease(Metadata("v2.0.0", duplicate: true), installed), "duplicate executable asset rejected");
Check(UpdateService.IsAllowedRedirect(new Uri("https://release-assets.githubusercontent.com/test?token=x"), "v2.0.0", "PixelSnapper.exe"), "GitHub HTTPS asset CDN accepted");
Check(!UpdateService.IsAllowedRedirect(new Uri("http://release-assets.githubusercontent.com/test"), "v2.0.0", "PixelSnapper.exe"), "HTTP CDN redirect rejected");
Check(!UpdateService.IsAllowedRedirect(new Uri("https://evil.invalid/test"), "v2.0.0", "PixelSnapper.exe"), "foreign redirect rejected");

var digest = new string('a', 64);
Check(UpdateService.ParseChecksum(digest.ToUpperInvariant() + "  PixelSnapper.exe\r\n") == digest, "SHA256SUMS normal Windows line parsed");
Check(UpdateService.ParseChecksum(digest + " *PixelSnapper.exe\n") == digest, "sha256sum binary marker accepted");
Reject<InvalidDataException>(() => UpdateService.ParseChecksum(digest + "  ../PixelSnapper.exe"), "checksum path traversal rejected");
Reject<InvalidDataException>(() => UpdateService.ParseChecksum(digest + "  PixelSnapper.exe\n" + digest + "  PixelSnapper.exe"), "duplicate checksums rejected");
Reject<InvalidDataException>(() => UpdateService.ParseChecksum("garbage"), "missing digest rejected");

var executable = Environment.ProcessPath!;
var binary = File.ReadAllBytes(executable);
var binaryInfo = FileVersionInfo.GetVersionInfo(executable);
var binaryVersion = new Version(binaryInfo.FileMajorPart, binaryInfo.FileMinorPart, binaryInfo.FileBuildPart);
var binaryTag = "v" + binaryVersion.ToString(3);
var binaryHash = Hash(binary);
var release = new ReleaseInfo(binaryTag, binaryVersion, new Uri(AssetUrl(binaryTag, "PixelSnapper.exe")), new Uri(AssetUrl(binaryTag, "SHA256SUMS.txt")), binary.Length);
var downloadRoot = Path.Combine(root, "다운로드");
var updatesProgress = new List<double>();
using (var client = FakeClient(binary, binaryHash))
{
    var result = await UpdateService.DownloadCoreAsync(release, client, downloadRoot, new InlineProgress(p => updatesProgress.Add(p)), CancellationToken.None);
    Check(File.Exists(result) && Hash(File.ReadAllBytes(result)) == binaryHash && File.Exists(Path.Combine(Path.GetDirectoryName(result)!, "verified.json")), "successful download validates digest/version and writes receipt");
    Check(updatesProgress.Count > 1 && updatesProgress[0] == 0 && updatesProgress[^1] == 100 && updatesProgress.All(p => p is >= 0 and <= 100), "progress range is 0 through 100");
}
var successfulDirectories = Directory.GetDirectories(downloadRoot).Length;
using (var client = FakeClient(binary, new string('0', 64)))
    await RejectAsync<InvalidDataException>(() => UpdateService.DownloadCoreAsync(release, client, downloadRoot, null, CancellationToken.None), "bad digest cannot produce installable update");
Check(Directory.GetDirectories(downloadRoot).Length == successfulDirectories, "bad digest cleans its staged folder");

using (var client = FakeClient(binary, binaryHash, delayExecutable: true))
using (var cancellation = new CancellationTokenSource(150))
    await RejectAsync<OperationCanceledException>(() => UpdateService.DownloadCoreAsync(release, client, downloadRoot, null, cancellation.Token), "download cancellation propagates");
Check(Directory.GetDirectories(downloadRoot).Length == successfulDirectories, "cancelled download cleans its staged folder");

using (var client = FakeClient(binary, binaryHash, redirect: "http://evil.invalid/PixelSnapper.exe"))
    await RejectAsync<InvalidDataException>(() => UpdateService.DownloadCoreAsync(release, client, downloadRoot, null, CancellationToken.None), "download refuses unsafe redirect");
Check(Directory.GetDirectories(downloadRoot).Length == successfulDirectories, "unsafe redirect cleans its staged folder");

using (var client = FakeClient(binary, binaryHash, redirect: "https://release-assets.githubusercontent.com/test/PixelSnapper.exe?token=x"))
{
    var result = await UpdateService.DownloadCoreAsync(release, client, downloadRoot, null, CancellationToken.None);
    Check(File.Exists(result), "legitimate GitHub asset redirect succeeds");
}
var beforeBadVersion = Directory.GetDirectories(downloadRoot).Length;
var mismatch = release with { VersionTag = "v9.9.9", Version = new Version(9, 9, 9), DownloadUrl = new Uri(AssetUrl("v9.9.9", "PixelSnapper.exe")), HashUrl = new Uri(AssetUrl("v9.9.9", "SHA256SUMS.txt")) };
using (var client = FakeClient(binary, binaryHash))
    await RejectAsync<InvalidDataException>(() => UpdateService.DownloadCoreAsync(mismatch, client, downloadRoot, null, CancellationToken.None), "wrong executable version rejected even with correct checksum");
Check(Directory.GetDirectories(downloadRoot).Length == beforeBadVersion, "version mismatch cleans its staged folder");

var installRoot = Path.Combine(root, "설치 경로"); Directory.CreateDirectory(installRoot);
var target = Path.Combine(installRoot, "픽셀 스냅퍼.exe");
var staged = Path.Combine(root, "staged.exe");
var oldBytes = Encoding.UTF8.GetBytes("old application stays intact");
var newBytes = Encoding.UTF8.GetBytes("new application bytes");
File.WriteAllBytes(target, oldBytes); File.WriteAllBytes(staged, newBytes);
Reject<InvalidDataException>(() => UpdateService.ApplyVerifiedUpdate(staged, target, new string('0', 64), Hash(oldBytes), _ => { }), "invalid staged digest blocks replacement");
Check(File.ReadAllBytes(target).SequenceEqual(oldBytes), "failed verification leaves target unchanged");
Reject<InvalidDataException>(() => UpdateService.ApplyVerifiedUpdate(staged, target, Hash(newBytes), new string('0', 64), _ => { }), "changed installed file blocks replacement");
Reject<IOException>(() => UpdateService.ApplyVerifiedUpdate(staged, target, Hash(newBytes), Hash(oldBytes), _ => throw new IOException("simulated launch failure")), "launch failure reports rollback");
Check(File.ReadAllBytes(target).SequenceEqual(oldBytes), "launch failure atomically restores old executable");
Check(!Directory.GetFiles(installRoot).Any(p => Path.GetFileName(p).StartsWith('.')), "rollback cleans incoming and backup artifacts");
var launchSawNewVersion = false;
UpdateService.ApplyVerifiedUpdate(staged, target, Hash(newBytes), Hash(oldBytes), path => launchSawNewVersion = File.ReadAllBytes(path).SequenceEqual(newBytes));
Check(launchSawNewVersion && File.ReadAllBytes(target).SequenceEqual(newBytes), "successful replacement launches verified new executable");
Check(!Directory.GetFiles(installRoot).Any(p => Path.GetFileName(p).StartsWith('.')), "successful replacement cleans backup artifacts");
Check(File.ReadAllBytes(staged).SequenceEqual(newBytes), "installer does not mutate the running staged helper");

// Use a real signed Windows executable in a controlled destination to check Process.Start after replacement.
var systemTool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
var realToolBytes = File.ReadAllBytes(systemTool);
File.WriteAllBytes(target, oldBytes); File.WriteAllBytes(staged, realToolBytes);
UpdateService.ApplyVerifiedUpdate(staged, target, Hash(realToolBytes), Hash(oldBytes), path =>
{
    using var process = Process.Start(new ProcessStartInfo(path, "where.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
    if (!process.WaitForExit(10000) || process.ExitCode != 0) throw new Exception("new executable did not launch correctly");
});
Check(File.ReadAllBytes(target).SequenceEqual(realToolBytes), "real Windows executable launches after atomic replacement");

await HelperIntegrationAsync(false);
await HelperIntegrationAsync(true);
Console.WriteLine($"ALL {checkedCount} UPDATE CHECKS PASSED");
Console.WriteLine("Artifacts: " + root);

async Task HelperIntegrationAsync(bool corruptOldHash)
{
    var isolated = Path.Combine(root, "helper-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(isolated);
    var updateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelSnapper", "updates");
    var helperStage = Path.Combine(updateRoot, binaryTag + "-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(helperStage);
    Process? parent = null, helper = null;
    try
    {
        foreach (var runtime in Directory.GetFiles(AppContext.BaseDirectory).Where(p => Path.GetExtension(p) is ".dll" or ".json"))
        {
            File.Copy(runtime, Path.Combine(isolated, Path.GetFileName(runtime)));
            File.Copy(runtime, Path.Combine(helperStage, Path.GetFileName(runtime)));
        }
        var isolatedTarget = Path.Combine(isolated, "PixelSnapper.exe");
        var helperExe = Path.Combine(helperStage, "PixelSnapper.exe");
        File.Copy(executable, isolatedTarget); File.Copy(executable, helperExe);
        // A harmless trailing marker distinguishes the old apphost from the new one.
        using (var append = new FileStream(isolatedTarget, FileMode.Append)) append.Write(Encoding.UTF8.GetBytes("old-apphost-test-marker"));
        var original = File.ReadAllBytes(isolatedTarget);
        File.WriteAllText(Path.Combine(isolated, ".test-update-target"), "controlled update target");
        File.WriteAllText(Path.Combine(helperStage, "verified.json"), JsonSerializer.Serialize(new { VersionTag = binaryTag, Sha256 = binaryHash }));
        var ready = Path.Combine(isolated, "parent-ready.txt");
        var exit = Path.Combine(isolated, "parent-exit.txt");
        var parentStart = new ProcessStartInfo(isolatedTarget) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = isolated };
        parentStart.ArgumentList.Add("--test-parent"); parentStart.ArgumentList.Add(ready); parentStart.ArgumentList.Add(exit);
        parent = Process.Start(parentStart)!;
        await WaitForFile(ready, parent);
        var requestPath = Path.Combine(helperStage, "install-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(new
        {
            TargetPath = isolatedTarget, ParentId = parent.Id, ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks,
            OldSha256 = corruptOldHash ? new string('0', 64) : Hash(original), NewSha256 = binaryHash,
            VersionTag = binaryTag, CreatedUtc = DateTimeOffset.UtcNow
        }));
        var failed = Path.Combine(isolated, "helper-failed.txt");
        var helperStart = new ProcessStartInfo(helperExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = helperStage };
        helperStart.ArgumentList.Add(corruptOldHash ? "--test-helper-no-ui" : "--apply-update");
        helperStart.ArgumentList.Add(requestPath);
        if (corruptOldHash) helperStart.ArgumentList.Add(failed);
        helper = Process.Start(helperStart)!;
        await WaitForFile(requestPath + ".ready", helper);
        Check(!parent.HasExited && File.ReadAllBytes(isolatedTarget).SequenceEqual(original), "helper signals readiness and waits while exact parent still runs");
        File.WriteAllText(exit, "exit now");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await helper.WaitForExitAsync(timeout.Token);
        if (corruptOldHash)
        {
            Check(helper.ExitCode == 3 && File.Exists(failed) && File.ReadAllBytes(isolatedTarget).SequenceEqual(original), "helper rejects changed current hash without replacing old executable");
            Check(!File.Exists(Path.Combine(isolated, "restarted.txt")), "failed helper never launches target");
        }
        else
        {
            await WaitForFile(Path.Combine(isolated, "restarted.txt"), null);
            Check(helper.ExitCode == 0 && File.ReadAllBytes(isolatedTarget).SequenceEqual(binary), "real --apply-update route replaces after parent exits and restarts target");
            Check(!File.Exists(requestPath), "successful helper consumes install request");
        }
    }
    finally
    {
        if (parent != null) { if (!parent.HasExited) { parent.Kill(true); parent.WaitForExit(5000); } parent.Dispose(); }
        if (helper != null) { if (!helper.HasExited) { helper.Kill(true); helper.WaitForExit(5000); } helper.Dispose(); }
        if (Path.GetFullPath(helperStage).StartsWith(Path.GetFullPath(updateRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(helperStage, recursive: true);
    }
}

static async Task WaitForFile(string path, Process? running)
{
    var watch = Stopwatch.StartNew();
    while (!File.Exists(path) && watch.Elapsed < TimeSpan.FromSeconds(10))
    {
        if (running?.HasExited == true) throw new IOException("Helper test process exited early: " + running.ExitCode);
        await Task.Delay(25);
    }
    if (!File.Exists(path)) throw new TimeoutException("Helper test did not create " + Path.GetFileName(path));
}

static string AssetUrl(string tag, string name) => UpdateService.RepositoryUrl + "/releases/download/" + tag + "/" + name;
static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
static string Metadata(string tag, bool prerelease = false, bool draft = false, string? exeUrl = null, string? hashUrl = null, bool missingHash = false, bool duplicate = false)
{
    var assets = new List<object> { new { name = "PixelSnapper.exe", size = 100, browser_download_url = exeUrl ?? AssetUrl(tag, "PixelSnapper.exe") } };
    if (duplicate) assets.Add(new { name = "PixelSnapper.exe", size = 100, browser_download_url = AssetUrl(tag, "PixelSnapper.exe") });
    if (!missingHash) assets.Add(new { name = "SHA256SUMS.txt", size = 82, browser_download_url = hashUrl ?? AssetUrl(tag, "SHA256SUMS.txt") });
    return JsonSerializer.Serialize(new { tag_name = tag, prerelease, draft, assets });
}
static HttpClient FakeClient(byte[] binary, string hash, bool delayExecutable = false, string? redirect = null) => new(new FakeHandler(async (request, token) =>
{
    if (request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt"))
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(hash + "  PixelSnapper.exe\n") };
    if (delayExecutable) await Task.Delay(10000, token);
    if (redirect != null && request.RequestUri.Host == "github.com")
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(redirect); return response;
    }
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(binary) };
}));

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
}
sealed class InlineProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
