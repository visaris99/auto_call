using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
        var login = new LoginWindow();
        MainWindow = login;
        login.Show();
        _ = TryAutoUpdateAsync();
    }

    /// <summary>시작 시 자동 업데이트: 새 버전이면 setup.exe 받아 무음 설치 후 종료.
    /// 어떤 실패든 조용히 무시 — 메인 화면의 '새 버전 받기' 링크가 폴백.</summary>
    private async Task TryAutoUpdateAsync()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TM_NO_AUTOUPDATE")))
            return;
        UpdateWindow? window = null;
        try
        {
            var config = AppConfig.Load();
            var client = new ApiClient(config.ServerUrl);
            var info = await client.CheckVersionAsync();
            if (info == null
                || !UpdateCheck.IsNewer(Ui.Version, info.LatestVersion)
                || string.IsNullOrWhiteSpace(info.DownloadUrl))
                return;

            window = new UpdateWindow();
            window.SetStatus($"새 버전 v{info.LatestVersion} 다운로드 중…");
            window.Show();

            string path = Path.Combine(Path.GetTempPath(),
                $"milestone_dialer_setup_{info.LatestVersion}.exe");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var res = await http.GetAsync(info.DownloadUrl,
                       HttpCompletionOption.ResponseHeadersRead))
            {
                res.EnsureSuccessStatusCode();
                long total = res.Content.Headers.ContentLength ?? -1;
                await using var source = await res.Content.ReadAsStreamAsync();
                await using var target = File.Create(path);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0)
                        window.SetProgress(done * 100d / total,
                            $"{done / 1048576}MB / {total / 1048576}MB");
                }
            }

            window.SetProgress(100, "설치를 시작합니다 — 잠시 후 자동으로 다시 실행됩니다.");
            // 무음 설치: 실행 중인 앱은 강제 종료·교체되고, installer.iss의 [Run]이 재실행한다
            Process.Start(new ProcessStartInfo(path,
                "/VERYSILENT /NORESTART /FORCECLOSEAPPLICATIONS")
            { UseShellExecute = true });
            Shutdown();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException
            or IOException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            LogError($"AutoUpdate 실패(무시됨): {ex.Message}");
            window?.Close();
        }
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
