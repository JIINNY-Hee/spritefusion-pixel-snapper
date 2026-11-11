using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PixelSnapper;
public sealed class ImageEntry : INotifyPropertyChanged
{
    public required string InputPath { get; init; }
    public bool IsClipboardImage { get; init; }
    public string FileName => Path.GetFileName(InputPath);
    public string? OutputPath { get; set; }
    public string? GetOutputDirectory(string? selectedDirectory, string? clipboardDirectory)
    {
        if (!string.IsNullOrWhiteSpace(selectedDirectory)) return selectedDirectory;
        if (!IsClipboardImage) return null;
        if (string.IsNullOrWhiteSpace(clipboardDirectory))
            throw new InvalidOperationException("클립보드 이미지의 저장 폴더를 선택해 주세요.");
        return clipboardDirectory;
    }
    private string status = "대기 중";
    public string Status { get => status; set { status = value; Changed(); } }
    private string details = "";
    public string Details { get => details; set { details = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public sealed class Settings
{
    public int Colors { get; set; } = 16;
    public bool UseManualPixelSize { get; set; }
    public double PixelSize { get; set; } = 8;
    public bool UseCustomOutputSize { get; set; }
    public int OutputWidth { get; set; }
    public int OutputHeight { get; set; }
    public bool KeepOutputAspectRatio { get; set; } = true;
    public bool OutputWidthIsAnchor { get; set; } = true;
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool UseCustomDirectory { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string LastInputDirectory { get; set; } = "";
    public string ClipboardOutputDirectory { get; set; } = "";
    public static string StoreDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelSnapper");
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(StoreDirectory, "settings.json"))) ?? new(); } catch { return new(); }
    }
    public void Save()
    {
        try { Directory.CreateDirectory(StoreDirectory); File.WriteAllText(Path.Combine(StoreDirectory, "settings.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
}
