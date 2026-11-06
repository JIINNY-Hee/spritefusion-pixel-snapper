using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PixelSnapper;

public partial class MainWindow
{
    private bool syncingOutputSize;
    private bool outputSizeInitialized;
    private bool outputWidthIsAnchor = true;
    private string? outputSizeSourcePath;
    private int originalWidth;
    private int originalHeight;

    private void InitializeOutputSize()
    {
        outputWidthIsAnchor = settings.OutputWidthIsAnchor;
        try
        {
            new OutputSizeOptions(settings.OutputWidth, settings.OutputHeight,
                settings.KeepOutputAspectRatio, outputWidthIsAnchor).Validate();
            outputSizeInitialized = true;
        }
        catch (ArgumentOutOfRangeException) { outputSizeInitialized = false; }
        OutputWidthBox.Text = outputSizeInitialized ? settings.OutputWidth.ToString(CultureInfo.CurrentCulture) : "";
        OutputHeightBox.Text = outputSizeInitialized ? settings.OutputHeight.ToString(CultureInfo.CurrentCulture) : "";
        AspectRatioToggle.IsChecked = settings.KeepOutputAspectRatio;
        CustomOutputSizeCheck.IsChecked = outputSizeInitialized && settings.UseCustomOutputSize;
    }

    private void RefreshOutputSizeSource()
    {
        var path = (FileList.SelectedItem as ImageEntry)?.InputPath;
        if (!string.Equals(path, outputSizeSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            outputSizeSourcePath = path;
            originalWidth = originalHeight = 0;
            if (path != null)
            {
                try { (originalWidth, originalHeight) = OutputImageService.ReadDimensions(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or NotSupportedException or FileFormatException or ArgumentException)
                { /* The preview/conversion reports unreadable input separately. */ }
            }
        }
        if (CustomOutputSizeCheck.IsChecked == true)
        {
            SeedOutputSize();
            SynchronizeLinkedSize();
        }
        UpdateOutputSizeControls();
    }

    private void SeedOutputSize()
    {
        if (outputSizeInitialized || originalWidth <= 0 || originalHeight <= 0) return;
        syncingOutputSize = true;
        try
        {
            OutputWidthBox.Text = originalWidth.ToString(CultureInfo.CurrentCulture);
            OutputHeightBox.Text = originalHeight.ToString(CultureInfo.CurrentCulture);
            outputSizeInitialized = true;
            outputWidthIsAnchor = true;
        }
        finally { syncingOutputSize = false; }
    }

    private void OutputSizeMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        if (CustomOutputSizeCheck.IsChecked == true) { SeedOutputSize(); SynchronizeLinkedSize(); }
        UpdateOutputSizeControls();
    }

    private void AspectRatio_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        SynchronizeLinkedSize();
        UpdateOutputSizeControls();
    }

    private void OutputWidth_Changed(object sender, TextChangedEventArgs e) => OutputDimensionChanged(true);
    private void OutputHeight_Changed(object sender, TextChangedEventArgs e) => OutputDimensionChanged(false);

    private void OutputDimensionChanged(bool widthChanged)
    {
        if (!initialized || syncingOutputSize) return;
        outputWidthIsAnchor = widthChanged;
        outputSizeInitialized = true;
        SynchronizeLinkedSize();
        UpdateOutputSizeControls();
    }

    private void SynchronizeLinkedSize()
    {
        if (CustomOutputSizeCheck.IsChecked != true || AspectRatioToggle.IsChecked != true
            || originalWidth <= 0 || originalHeight <= 0) return;
        var anchorText = outputWidthIsAnchor ? OutputWidthBox.Text : OutputHeightBox.Text;
        if (!int.TryParse(anchorText, out int anchor)) return;
        try
        {
            var options = new OutputSizeOptions(outputWidthIsAnchor ? anchor : 1,
                outputWidthIsAnchor ? 1 : anchor, true, outputWidthIsAnchor);
            var size = options.Resolve(originalWidth, originalHeight);
            syncingOutputSize = true;
            if (outputWidthIsAnchor) OutputHeightBox.Text = size.Height.ToString(CultureInfo.CurrentCulture);
            else OutputWidthBox.Text = size.Width.ToString(CultureInfo.CurrentCulture);
        }
        catch (ArgumentOutOfRangeException) { /* Keep the typed anchor; show its error below. */ }
        finally { syncingOutputSize = false; }
    }

    private OutputSizeOptions ReadOutputSizeOptions()
    {
        if (!int.TryParse(OutputWidthBox.Text, out int width) || !int.TryParse(OutputHeightBox.Text, out int height))
            throw new ArgumentOutOfRangeException("출력 크기", "가로와 세로는 1~8192 사이의 정수로 입력해 주세요.");
        var options = new OutputSizeOptions(width, height, AspectRatioToggle.IsChecked == true, outputWidthIsAnchor);
        options.Validate();
        return options;
    }

    private bool TryGetOutputSize(out OutputSizeOptions? options)
    {
        options = null;
        if (CustomOutputSizeCheck.IsChecked != true) return true;
        try { options = ReadOutputSizeOptions(); return true; }
        catch (ArgumentOutOfRangeException ex)
        {
            MessageBox.Show(this, ex.Message, "출력 크기 확인", MessageBoxButton.OK, MessageBoxImage.Information);
            var input = outputWidthIsAnchor ? OutputWidthBox : OutputHeightBox;
            input.Focus(); input.SelectAll();
            return false;
        }
    }

    private void UpdateOutputSizeControls()
    {
        bool enabled = !busy && CustomOutputSizeCheck.IsChecked == true;
        CustomOutputSizeCheck.IsEnabled = !busy;
        OutputWidthBox.IsEnabled = OutputHeightBox.IsEnabled = AspectRatioToggle.IsEnabled = enabled;
        bool linked = AspectRatioToggle.IsChecked == true;
        AspectRatioState.Text = linked ? "원본 비율 유지" : "가로·세로 각각 지정";
        AspectRatioState.Foreground = (Brush)FindResource(linked ? "Accent" : "Muted");
        AspectRatioToggle.ToolTip = linked
            ? "원본 비율 유지 · 끄면 가로와 세로를 각각 지정합니다."
            : "비율 연결 해제 · 켜면 마지막으로 바꾼 값을 기준으로 원본 비율을 맞춥니다.";
        OutputSizeHint.Foreground = (Brush)FindResource("Muted");
        if (CustomOutputSizeCheck.IsChecked != true)
        {
            OutputSizeHint.Text = "지정하지 않으면 격자 변환 결과 크기로 저장합니다.";
            return;
        }
        if (!outputSizeInitialized || (linked && (originalWidth <= 0 || originalHeight <= 0)))
        {
            OutputSizeHint.Text = "이미지를 선택하면 원본 크기와 비율을 불러옵니다. 가로 또는 세로를 바꿔 주세요.";
            return;
        }
        try
        {
            var options = ReadOutputSizeOptions();
            var size = linked ? options.Resolve(originalWidth, originalHeight) : (options.Width, options.Height);
            OutputSizeHint.Text = linked
                ? $"원본 {originalWidth} × {originalHeight} → 출력 {size.Item1} × {size.Item2}px · {(outputWidthIsAnchor ? "가로" : "세로")} 기준 · 여러 이미지는 각 원본 비율을 유지합니다."
                : $"출력 {size.Item1} × {size.Item2}px · 모든 이미지를 이 크기로 저장합니다. 원본 비율과 다르면 이미지가 늘어나거나 줄어듭니다.";
        }
        catch (ArgumentOutOfRangeException ex)
        {
            OutputSizeHint.Text = ex.Message.Split('\n')[0];
            OutputSizeHint.Foreground = Brushes.SandyBrown;
        }
    }

    private void SaveOutputSizeSettings()
    {
        settings.KeepOutputAspectRatio = AspectRatioToggle.IsChecked == true;
        settings.OutputWidthIsAnchor = outputWidthIsAnchor;
        settings.UseCustomOutputSize = false;
        try
        {
            var options = ReadOutputSizeOptions();
            settings.OutputWidth = options.Width;
            settings.OutputHeight = options.Height;
            settings.UseCustomOutputSize = CustomOutputSizeCheck.IsChecked == true;
        }
        catch (ArgumentOutOfRangeException) { }
    }
}
