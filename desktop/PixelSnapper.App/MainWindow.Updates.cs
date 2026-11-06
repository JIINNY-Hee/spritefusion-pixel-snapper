using System.Windows;
using System.Windows.Controls;

namespace PixelSnapper;

public partial class MainWindow
{
    private ReleaseInfo? availableRelease;
    private bool updating;
    private CancellationTokenSource? updateCancellation;

    private void AutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        settings.CheckUpdatesOnStartup = AutoUpdateCheck.IsChecked == true;
        settings.Save();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (busy || updating) return;
        if (availableRelease == null) { await CheckForUpdatesAsync(true); return; }
        if (MessageBox.Show(this, $"{availableRelease.VersionTag} 버전을 다운로드하고 다시 시작할까요?\n이미지와 설정은 보존됩니다.",
                "픽셀 스냅퍼 업데이트", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        updating = true; UpdateControls();
        using var cancellationSource = new CancellationTokenSource();
        updateCancellation = cancellationSource;
        var label = new TextBlock { Text = "새 버전을 다운로드하고 있습니다…", Margin = new Thickness(0,0,0,16) };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 10, IsIndeterminate = true };
        var cancel = new Button { Content = "취소", Margin = new Thickness(0,20,0,0), HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(label); panel.Children.Add(progress); panel.Children.Add(cancel);
        var dialog = new Window { Owner = this, Title = "업데이트 다운로드", Width = 450, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        string? staged = null; Exception? failure = null;
        cancel.Click += (_, _) => dialog.Close();
        dialog.Closing += (_, _) => cancellationSource.Cancel();
        Task downloadTask = Task.CompletedTask;
        async Task DownloadIntoStageAsync()
        {
            try
            {
                staged = await UpdateService.DownloadAsync(availableRelease, new Progress<double>(value =>
                { progress.IsIndeterminate = false; progress.Value = value; label.Text = $"다운로드 및 검증 중… {value:0}%"; }), cancellationSource.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { failure = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }
        dialog.Loaded += (_, _) => downloadTask = DownloadIntoStageAsync();
        try
        {
            dialog.ShowDialog();
            await downloadTask;
            if (failure != null) MessageBox.Show(this, "업데이트를 설치하지 못했습니다. 기존 버전을 계속 사용할 수 있습니다.\n\n" + failure.Message, "업데이트", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (staged != null) { SaveSettings(); await Task.Run(() => UpdateService.StartInstall(staged)); Application.Current.Shutdown(); }
        }
        catch (Exception ex) { MessageBox.Show(this, "업데이트를 시작하지 못했습니다.\n" + ex.Message, "업데이트", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { updateCancellation = null; updating = false; UpdateControls(); }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (updating) return;
        updating = true; UpdateControls(); UpdateButton.Content = "확인 중…";
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        updateCancellation = source;
        try
        {
            availableRelease = await UpdateService.CheckAsync(source.Token);
            if (manual && IsVisible)
                MessageBox.Show(this, availableRelease == null ? "설치할 새 버전이 없습니다." : $"{availableRelease.VersionTag} 버전이 있습니다.\n상단 업데이트 버튼을 누르면 설치할 수 있습니다.", "업데이트 확인", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateButton.ToolTip = availableRelease == null ? "새 버전이 나오면 알려드립니다." : "클릭해서 새 버전을 다운로드하고 다시 시작합니다.";
        }
        catch (Exception ex)
        {
            UpdateButton.ToolTip = "업데이트를 확인할 수 없습니다. 나중에 다시 시도하세요.\n" + ex.Message;
            if (manual && IsVisible && !source.IsCancellationRequested)
                MessageBox.Show(this, "업데이트를 확인하지 못했습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요.\n\n" + ex.Message, "업데이트 확인", MessageBoxButton.OK, MessageBoxImage.Information);
            else if (manual && IsVisible) MessageBox.Show(this, "업데이트 확인 시간이 초과되었습니다. 나중에 다시 시도해 주세요.", "업데이트 확인");
        }
        finally
        {
            updateCancellation = null; updating = false;
            UpdateButton.Content = availableRelease == null ? "업데이트 확인" : $"{availableRelease.VersionTag} 설치";
            UpdateControls();
        }
    }
}
