using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PixelSnapper;

public partial class MainWindow
{
    private readonly ClipboardImageStore clipboardImages = new();
    private bool pastingClipboard;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool paste = (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            || (e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift);
        if (!paste || Keyboard.FocusedElement is TextBoxBase or PasswordBox
            or ComboBox { IsEditable: true }) return;
        e.Handled = true;
        PasteClipboard_Click(sender, e);
    }

    private async void PasteClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (!initialized || busy || pastingClipboard || previewClosing) return;
        pastingClipboard = true;
        UpdateControls();
        try
        {
            IDataObject? data = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { data = Clipboard.GetDataObject(); break; }
                catch (ExternalException) when (attempt < 2) { await Task.Delay(80); }
            }
            if (previewClosing) return;
            if (data?.GetData(DataFormats.FileDrop) is string[] paths)
            {
                AddPaths(paths);
                FileList.Focus();
                return;
            }
            var image = data == null ? null : ClipboardImageService.ReadImage(data);
            if (image == null)
            {
                StatusLabel.Text = "클립보드에 이미지가 없습니다. 이미지를 복사한 뒤 Ctrl+V를 눌러 주세요.";
                return;
            }
            var path = clipboardImages.Add(image);
            var entry = new ImageEntry { InputPath = path, IsClipboardImage = true };
            entries.Add(entry);
            FileList.SelectedItem = entry;
            FileList.ScrollIntoView(entry);
            FileList.Focus();
            StatusLabel.Text = $"클립보드 이미지 추가됨 · 총 {entries.Count}개";
        }
        catch (ExternalException)
        { StatusLabel.Text = "다른 프로그램이 클립보드를 사용 중입니다. 잠시 후 다시 붙여넣어 주세요."; }
        catch (Exception ex)
        { StatusLabel.Text = "이미지를 붙여넣지 못했습니다. " + ex.Message.Split('\n')[0]; }
        finally { pastingClipboard = false; UpdateControls(); }
    }

    private async void ReleaseClipboardEntries(IEnumerable<ImageEntry> removed)
    {
        var paths = removed.Where(entry => entry.IsClipboardImage).Select(entry => entry.InputPath).ToArray();
        if (paths.Length == 0) return;
        // Selection changes cancel the old preview; wait until it releases its input file.
        await previewWork;
        foreach (var path in paths) clipboardImages.Release(path);
    }
}
