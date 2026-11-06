using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PixelSnapper;
public partial class MainWindow : Window
{
    private readonly ObservableCollection<ImageEntry> entries = new();
    private readonly Settings settings = Settings.Load();
    private CancellationTokenSource? cancellation;
    private bool busy;
    private bool closeAfterCancel;
    private bool initialized;
    private string? lastOutput;

    public MainWindow(string[] arguments)
    {
        InitializeComponent();
        FileList.ItemsSource = entries;
        ColorsBox.Text = Math.Clamp(settings.Colors, 1, 256).ToString();
        bool validPixelSize = double.IsFinite(settings.PixelSize) && settings.PixelSize >= 1;
        PixelSizeBox.Text = (validPixelSize ? settings.PixelSize : 8).ToString(CultureInfo.CurrentCulture);
        ManualPixelSizeRadio.IsChecked = validPixelSize && settings.UseManualPixelSize;
        AutoPixelSizeRadio.IsChecked = ManualPixelSizeRadio.IsChecked != true;
        OutputBox.Text = settings.OutputDirectory;
        CustomRadio.IsChecked = settings.UseCustomDirectory;
        AlongsideRadio.IsChecked = !settings.UseCustomDirectory;
        InitializeOutputSize();
        initialized = true;
        VersionLabel.Text = UpdateService.CurrentVersionTag;
        AutoUpdateCheck.IsChecked = settings.CheckUpdatesOnStartup;
        UpdateDestination();
        UpdatePixelSize();
        UpdateOutputSizeControls();
        Loaded += async (_, _) => { AddPaths(arguments); ResizePreviews(); if (settings.CheckUpdatesOnStartup) await CheckForUpdatesAsync(false); };
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "변환할 이미지 선택", Filter = "이미지 파일|*.png;*.jpg;*.jpeg", Multiselect = true };
        if (Directory.Exists(settings.LastInputDirectory)) dialog.InitialDirectory = settings.LastInputDirectory;
        if (dialog.ShowDialog(this) == true)
        {
            settings.LastInputDirectory = Path.GetDirectoryName(dialog.FileNames[0]) ?? "";
            AddPaths(dialog.FileNames);
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "이미지가 들어 있는 폴더 선택 (하위 폴더 제외)" };
        if (Directory.Exists(settings.LastInputDirectory)) dialog.InitialDirectory = settings.LastInputDirectory;
        if (dialog.ShowDialog(this) == true) { settings.LastInputDirectory = dialog.FolderName; AddPaths([dialog.FolderName]); }
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        if (busy) return;
        int added = 0, skipped = 0;
        foreach (var path in paths)
        {
            try
            {
                var candidates = Directory.Exists(path)
                    ? Directory.GetFiles(path).Where(EngineService.IsSupported).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    : (IEnumerable<string>)[path];
                foreach (var candidate in candidates)
                {
                    var fullPath = Path.GetFullPath(candidate);
                    if (!File.Exists(fullPath) || !EngineService.IsSupported(fullPath)) { skipped++; continue; }
                    if (entries.Any(item => string.Equals(item.InputPath, fullPath, StringComparison.OrdinalIgnoreCase))) continue;
                    entries.Add(new ImageEntry { InputPath = fullPath }); added++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { skipped++; }
        }
        UpdateControls();
        if (FileList.SelectedItem == null && entries.Count > 0) FileList.SelectedIndex = 0;
        if (added > 0 || skipped > 0) StatusLabel.Text = $"{added}개 추가됨 · 총 {entries.Count}개" + (skipped > 0 ? $" · 추가할 수 없는 항목 {skipped}개" : "");
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!busy && e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
        e.Handled = true;
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is ImageEntry entry) entries.Remove(entry);
        if (entries.Count > 0 && FileList.SelectedItem == null) FileList.SelectedIndex = 0;
        UpdateControls();
        StatusLabel.Text = $"총 {entries.Count}개 이미지";
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        entries.Clear(); UpdateControls(); Progress.Value = 0;
        StatusLabel.Text = "이미지를 추가하면 시작할 수 있습니다.";
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (initialized) RefreshPreview(); }
    private void RefreshPreview()
    {
        RefreshOutputSizeSource();
        var entry = FileList.SelectedItem as ImageEntry;
        SetPreview(BeforeImage, BeforeSize, BeforeHint, entry?.InputPath, "이미지를 선택해 주세요");
        SetPreview(AfterImage, AfterSize, AfterHint, entry?.OutputPath, "변환 후 여기에 표시됩니다");
        OpenResultButton.IsEnabled = entry?.OutputPath != null && File.Exists(entry.OutputPath);
        RemoveButton.IsEnabled = !busy && entry != null;
        PreviewNote.Text = string.IsNullOrEmpty(entry?.Details) ? "픽셀을 선명하게 확대해 비교할 수 있습니다." : entry.Details;
        PreviewNote.ToolTip = PreviewNote.Text;
        ResizePreviews();
    }
    private static void SetPreview(Image image, TextBlock size, TextBlock hint, string? path, string emptyText)
    {
        image.Source = null; size.Text = ""; hint.Text = emptyText; hint.Visibility = Visibility.Visible;
        if (path == null) return;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
            int width = decoder.Frames[0].PixelWidth, height = decoder.Frames[0].PixelHeight;
            if (width > 10000 || height > 10000)
            {
                size.Text = $"{width} × {height}";
                hint.Text = "10,000px 이하의 이미지를 사용해 주세요";
                return;
            }
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream;
            if (Math.Max(width, height) > 2048) { if (width >= height) bitmap.DecodePixelWidth = 2048; else bitmap.DecodePixelHeight = 2048; }
            bitmap.EndInit(); bitmap.Freeze();
            image.Source = bitmap; size.Text = $"{width} × {height}"; hint.Visibility = Visibility.Collapsed;
            image.ToolTip = Math.Max(width, height) > 2048 ? "큰 이미지는 미리보기만 축소해서 표시합니다. 변환은 원본으로 진행합니다." : null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.IO.FileFormatException or UnauthorizedAccessException or ArgumentException)
        { hint.Text = "미리보기를 열 수 없습니다"; hint.ToolTip = ex.Message; }
    }
    private void Zoom_Changed(object sender, SelectionChangedEventArgs e) { if (initialized) ResizePreviews(); }
    private void Preview_SizeChanged(object sender, SizeChangedEventArgs e) { if (initialized) ResizePreviews(); }
    private void ResizePreviews()
    {
        SetPreviewScale(BeforeScroll, BeforeBox, BeforeImage);
        SetPreviewScale(AfterScroll, AfterBox, AfterImage);
    }
    private void SetPreviewScale(ScrollViewer scroll, Viewbox box, Image image)
    {
        if (ZoomBox.SelectedIndex == 0)
        {
            box.Width = Math.Max(1, scroll.ActualWidth - 28);
            box.Height = Math.Max(1, scroll.ActualHeight - 28);
        }
        else if (image.Source is BitmapSource bitmap)
        {
            double scale = Math.Pow(2, ZoomBox.SelectedIndex - 1);
            box.Width = bitmap.PixelWidth * scale; box.Height = bitmap.PixelHeight * scale;
        }
    }

    private void Destination_Changed(object sender, RoutedEventArgs e) { if (initialized) UpdateDestination(); }
    private void PixelSizeMode_Changed(object sender, RoutedEventArgs e) { if (initialized) UpdatePixelSize(); }
    private void UpdatePixelSize()
    {
        AutoPixelSizeRadio.IsEnabled = ManualPixelSizeRadio.IsEnabled = !busy;
        PixelSizeBox.IsEnabled = !busy && ManualPixelSizeRadio.IsChecked == true;
    }
    private bool TryGetPixelSize(out double pixelSize)
    {
        return (double.TryParse(PixelSizeBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out pixelSize)
            || double.TryParse(PixelSizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out pixelSize))
            && double.IsFinite(pixelSize) && pixelSize >= 1;
    }
    private void UpdateDestination()
    {
        OutputBox.IsEnabled = !busy && CustomRadio.IsChecked == true;
        BrowseOutputButton.IsEnabled = !busy && CustomRadio.IsChecked == true;
    }
    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "변환 결과 저장 폴더" };
        if (Directory.Exists(OutputBox.Text)) dialog.InitialDirectory = OutputBox.Text;
        if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FolderName;
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (busy || updating || entries.Count == 0) return;
        if (!int.TryParse(ColorsBox.Text, out int colors) || colors is < 1 or > 256)
        { MessageBox.Show(this, "색상 수는 1~256 사이의 정수로 입력해 주세요.", "색상 수 확인", MessageBoxButton.OK, MessageBoxImage.Information); ColorsBox.Focus(); ColorsBox.SelectAll(); return; }
        double? pixelSize = null;
        if (ManualPixelSizeRadio.IsChecked == true)
        {
            if (!TryGetPixelSize(out double enteredPixelSize))
            { MessageBox.Show(this, "원본 격자 간격은 1 이상의 숫자로 입력해 주세요.\n예: 4, 8, 2.5", "원본 격자 확인", MessageBoxButton.OK, MessageBoxImage.Information); PixelSizeBox.Focus(); PixelSizeBox.SelectAll(); return; }
            pixelSize = enteredPixelSize;
        }
        if (!TryGetOutputSize(out OutputSizeOptions? outputSize)) return;
        string? outputDirectory = CustomRadio.IsChecked == true ? OutputBox.Text : null;
        if (outputDirectory != null && string.IsNullOrWhiteSpace(outputDirectory))
        { MessageBox.Show(this, "찾아보기를 눌러 저장 폴더를 선택해 주세요.", "저장 위치 확인", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        SaveSettings(); busy = true; cancellation = new CancellationTokenSource(); UpdateControls();
        var batch = entries.ToArray(); int done = 0, failed = 0; bool cancelled = false;
        Progress.Maximum = batch.Length; Progress.Value = 0;
        foreach (var entry in batch) { entry.Status = "대기 중"; entry.Details = ""; entry.OutputPath = null; }
        RefreshPreview();
        try
        {
            foreach (var entry in batch)
            {
                if (cancellation.IsCancellationRequested) { cancelled = true; break; }
                entry.Status = "변환 중…"; StatusLabel.Text = $"{done + failed + 1}/{batch.Length} 변환 중 · {entry.FileName}";
                FileList.SelectedItem = entry; FileList.ScrollIntoView(entry);
                try
                {
                    entry.OutputPath = await EngineService.ConvertAsync(entry.InputPath, outputDirectory, colors, cancellation.Token, pixelSize, outputSize);
                    entry.Status = "완료"; entry.Details = Path.GetFileName(entry.OutputPath); lastOutput = entry.OutputPath; done++;
                }
                catch (OperationCanceledException) { entry.Status = "중지됨"; cancelled = true; break; }
                catch (Exception ex) { entry.Status = "실패"; entry.Details = ex.Message; failed++; }
                Progress.Value = done + failed;
                RefreshPreview();
            }
        }
        finally
        {
            busy = false; cancellation.Dispose(); cancellation = null;
            StatusLabel.Text = (cancelled ? "중지됨 · " : "변환 완료 · ") + $"성공 {done}개" + (failed > 0 ? $" · 실패 {failed}개 (목록에서 확인)" : "") + (cancelled ? $" · 남은 항목 {batch.Length - done - failed}개" : "");
            UpdateControls(); RefreshPreview();
            if (closeAfterCancel) Close();
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    { cancellation?.Cancel(); CancelButton.IsEnabled = false; StatusLabel.Text = "작업을 중지하고 있습니다…"; }

    private void UpdateControls()
    {
        CountLabel.Text = $"{entries.Count}개";
        EmptyHint.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddButton.IsEnabled = FolderButton.IsEnabled = ColorsBox.IsEnabled = AlongsideRadio.IsEnabled = CustomRadio.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy && entries.Count > 0; RemoveButton.IsEnabled = !busy && FileList.SelectedItem != null;
        ConvertButton.IsEnabled = !busy && entries.Count > 0;
        ConvertButton.Content = busy ? "변환 중…" : "변환 시작";
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; CancelButton.IsEnabled = busy;
        OpenFolderButton.IsEnabled = lastOutput != null && Directory.Exists(Path.GetDirectoryName(lastOutput));
        UpdateDestination();
        UpdatePixelSize();
        UpdateOutputSizeControls();
        UpdateButton.IsEnabled = !busy && !updating;
        if (updating) ConvertButton.IsEnabled = false;
    }
    private void OpenResult_Click(object sender, RoutedEventArgs e)
    { if (FileList.SelectedItem is ImageEntry { OutputPath: not null } entry) OpenPath(entry.OutputPath); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = (FileList.SelectedItem as ImageEntry)?.OutputPath ?? lastOutput;
        if (path != null) OpenPath(Path.GetDirectoryName(path)!);
    }
    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "열 수 없습니다.\n" + ex.Message, "픽셀 스냅퍼"); }
    }

    private void SaveSettings()
    {
        if (int.TryParse(ColorsBox.Text, out int colors) && colors is >= 1 and <= 256) settings.Colors = colors;
        settings.UseManualPixelSize = ManualPixelSizeRadio.IsChecked == true;
        if (TryGetPixelSize(out double pixelSize)) settings.PixelSize = pixelSize;
        SaveOutputSizeSettings();
        settings.UseCustomDirectory = CustomRadio.IsChecked == true; settings.OutputDirectory = OutputBox.Text; settings.Save();
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        updateCancellation?.Cancel();
        if (busy) { e.Cancel = true; closeAfterCancel = true; cancellation?.Cancel(); StatusLabel.Text = "작업을 중지한 뒤 창을 닫습니다…"; return; }
        SaveSettings();
    }
    private void Help_Click(object sender, RoutedEventArgs e)
    {
        string license = "";
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("SpriteFusion-LICENSE.txt", StringComparison.Ordinal));
        if (resource != null) { using var stream = assembly.GetManifestResourceStream(resource)!; using var reader = new StreamReader(stream); license = reader.ReadToEnd(); }
        var text = $"픽셀 스냅퍼 {UpdateService.CurrentVersionTag}\n\n시작 시 새 버전을 확인합니다. 업데이트 버튼으로 다운로드·설치할 수 있습니다.\n\n1. 파일 추가 또는 끌어놓기로 이미지를 넣습니다.\n   폴더 추가는 선택한 폴더 바로 안의 이미지를 불러옵니다.\n2. 원하는 결과 해상도가 있으면 출력 크기 지정을 켜고 가로·세로를 입력합니다.\n   체인 아이콘이 켜져 있으면 원본 비율에 맞춰 다른 값이 자동으로 바뀝니다.\n3. 색상 수, 원본 격자와 저장 위치를 정합니다. 기본값은 16색·자동입니다.\n4. 변환 시작을 누릅니다. 완료 후 원본과 결과를 비교할 수 있습니다.\n\n• PNG / JPG / JPEG를 지원하고 결과는 PNG로 저장합니다.\n• 출력 크기는 저장할 PNG의 가로·세로 픽셀 수입니다.\n• 체인을 끄면 가로·세로를 각각 정확히 지정합니다.\n• 체인을 켜면 마지막으로 입력한 가로 또는 세로를 기준으로 원본 비율을 유지합니다.\n• 여러 이미지는 각각의 원본 비율을 사용하며, 정수 픽셀로 반올림합니다.\n• 각 변 1~8192px, 전체 16,777,216픽셀 이하를 지원합니다.\n• 크기를 바꿀 때 픽셀 경계가 흐려지지 않도록 최근접 방식으로 조절합니다.\n• 원본 격자: 간격을 자동으로 감지하거나 직접 지정할 수 있습니다.\n• 원본 격자의 직접 지정은 원본에서 한 픽셀 칸의 크기입니다. 예: 8px.\n• 1 이상, 이미지의 짧은 변 길이의 절반 이하로 입력하세요. 소수도 가능합니다.\n• 원본 격자에 맞춰 경계를 보정한 뒤, 출력 크기를 지정했다면 그 크기로 저장합니다.\n• 여러 이미지는 같은 원본 격자 간격을 사용하며 이미지마다 유효 범위가 다릅니다.\n• 원본과 기존 결과는 덮어쓰지 않습니다. 이름이 겹치면 번호를 붙입니다.\n• 다시 변환하면 현재 목록 전체를 새 설정으로 변환합니다.\n• 이미지 처리는 이 컴퓨터에서 이루어집니다.\n• 실행파일에 변환 엔진과 실행 환경이 포함되어 있습니다.\n\n변환 엔진: SpriteFusion Pixel Snapper 1.0.0\nhttps://github.com/Hugo-Dz/spritefusion-pixel-snapper\n\n" + license;
        var help = new Window { Owner = this, Title = "사용 안내 · 픽셀 스냅퍼", Width = 650, Height = 670, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(26), LineHeight = 23 } } };
        help.ShowDialog();
    }
}
