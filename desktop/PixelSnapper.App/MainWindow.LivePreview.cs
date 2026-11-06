using System.Windows;
using System.Windows.Controls;

namespace PixelSnapper;

public partial class MainWindow
{
    private CancellationTokenSource? previewCancellation;
    private Task previewWork = Task.CompletedTask;
    private long previewRevision;
    private bool previewClosing;

    private void PreviewOption_Changed(object sender, TextChangedEventArgs e)
    {
        if (initialized) RequestLivePreview();
    }

    private void CancelLivePreview()
    {
        previewRevision++;
        previewCancellation?.Cancel();
        previewCancellation = null;
    }

    private void SetPreviewNote(string text)
    {
        PreviewNote.Text = text;
        PreviewNote.ToolTip = text;
    }

    private void SetLivePreviewHint(string text, string? detail = null)
    {
        AfterImage.Source = null;
        AfterImage.ToolTip = null;
        AfterSize.Text = "";
        AfterHint.Text = text;
        AfterHint.ToolTip = detail;
        AfterHint.Visibility = Visibility.Visible;
    }

    private void RequestLivePreview(bool immediate = false)
    {
        if (!initialized || busy || previewClosing) return;
        CancelLivePreview();
        SetPreviewNote("미리보기는 자동 갱신됩니다. 파일을 저장하려면 변환 시작을 누르세요.");
        if (FileList.SelectedItem is not ImageEntry entry)
        {
            SetLivePreviewHint("이미지를 추가하면 자동으로 미리 봅니다");
            return;
        }
        if (!int.TryParse(ColorsBox.Text, out int colors) || colors is < 1 or > 256)
        {
            SetLivePreviewHint("색상 수를 1~256 사이의 정수로 입력해 주세요");
            return;
        }
        double? pixelSize = null;
        if (ManualPixelSizeRadio.IsChecked == true)
        {
            if (!TryGetPixelSize(out double enteredSize))
            {
                SetLivePreviewHint("세밀도를 1 이상의 숫자로 입력해 주세요");
                return;
            }
            pixelSize = enteredSize;
        }
        OutputSizeOptions? outputSize = null;
        try
        {
            if (CustomOutputSizeCheck.IsChecked == true) outputSize = ReadOutputSizeOptions();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            SetLivePreviewHint("출력 가로·세로 값을 확인해 주세요", ex.Message);
            return;
        }

        SetLivePreviewHint("미리보기 준비 중…");
        var source = new CancellationTokenSource();
        previewCancellation = source;
        // Each request waits for its predecessor to release the engine and temporary files.
        // Cancellation plus a revision check prevents stale successes and errors from reaching the UI.
        previewWork = GenerateLivePreviewAsync(previewWork, entry, colors, pixelSize,
            outputSize, source, previewRevision, immediate);
    }

    private async Task GenerateLivePreviewAsync(Task previous, ImageEntry entry, int colors,
        double? pixelSize, OutputSizeOptions? outputSize, CancellationTokenSource source,
        long revision, bool immediate)
    {
        try
        {
            await previous;
            source.Token.ThrowIfCancellationRequested();
            if (!immediate) await Task.Delay(300, source.Token);
            var preview = await EngineService.PreviewAsync(entry.InputPath, colors, source.Token,
                pixelSize, outputSize);
            if (!IsCurrentPreview()) return;
            AfterImage.Source = preview.Bitmap;
            AfterImage.ToolTip = "현재 옵션의 미리보기입니다. 결과 파일은 변환 시작을 누를 때 저장됩니다.";
            AfterSize.Text = $"{preview.Width} × {preview.Height}";
            AfterHint.Visibility = Visibility.Collapsed;
            SetPreviewNote("미리보기 완료 · 아직 저장하지 않았습니다. 저장하려면 변환 시작을 누르세요.");
            ResizePreviews();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrentPreview())
            {
                SetLivePreviewHint("미리보기를 만들 수 없습니다\n" + ex.Message.Split('\n')[0], ex.Message);
                SetPreviewNote("이미지와 옵션을 확인해 주세요. 결과 파일은 저장하지 않았습니다.");
            }
        }
        finally
        {
            if (ReferenceEquals(previewCancellation, source)) previewCancellation = null;
            source.Dispose();
        }

        bool IsCurrentPreview() => !source.IsCancellationRequested && !previewClosing && !busy
            && revision == previewRevision && ReferenceEquals(FileList.SelectedItem, entry);
    }
}
