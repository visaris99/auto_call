using System.IO;
using System.Windows;
using Core;

namespace MilestoneDialer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception.ToString());
            MessageBox.Show($"예기치 못한 오류가 발생했습니다.\n{args.Exception.Message}",
                "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    public static void LogError(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppConfig.ConfigDir(), "error_log.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch (IOException)
        {
            // 로그 실패는 무시
        }
    }
}
