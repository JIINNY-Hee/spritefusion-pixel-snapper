using System.IO;
using System.Windows;

namespace PixelSnapper;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (UpdateService.TryHandleHelper(e.Args)) { Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            try { Directory.CreateDirectory(Settings.StoreDirectory); File.AppendAllText(Path.Combine(Settings.StoreDirectory, "error.log"), $"{DateTime.Now:O} {args.Exception}\n"); } catch { }
            MessageBox.Show("작업을 완료하지 못했습니다.\n" + args.Exception.Message, "픽셀 스냅퍼", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        MainWindow = new MainWindow(e.Args);
        MainWindow.Show();
    }
}
