using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Core;

namespace MilestoneDialer;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\MilestoneDialer.App.v1",
            createdNew: out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "마일스톤 다이얼러가 이미 실행 중입니다.",
                "다이얼러",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

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

    /// <summary>시작 시 서명된 manifest와 설치파일을 검증한 뒤 자동 업데이트한다.</summary>
    private async Task TryAutoUpdateAsync()
    {
        if (UpdateCheck.IsAutoUpdateDisabled())
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

            var updateWindow = new UpdateWindow();
            window = updateWindow;
            updateWindow.SetStatus($"새 버전 v{info.LatestVersion} 검증 및 다운로드 중…");
            updateWindow.Show();

            var policy = UpdatePolicy.OfficeProduction;
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            var coordinator = new UpdateCoordinator(
                new UpdateManifestVerifier(policy),
                new UpdateDownloader(http, policy),
                new WindowsUpdatePackageInspector(),
                new ShellUpdateInstaller());
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                double percent = value.TotalBytes > 0
                    ? value.DownloadedBytes * 100d / value.TotalBytes
                    : 0;
                updateWindow.SetProgress(
                    percent,
                    $"{value.DownloadedBytes / 1048576}MB / {value.TotalBytes / 1048576}MB");
            });

            UpdateRunResult result = await coordinator.RunAsync(
                info, Ui.Version, Path.GetTempPath(), progress);
            if (result.Status == UpdateRunStatus.InstallerStarted)
            {
                updateWindow.SetProgress(100, "검증 완료 — 설치를 시작합니다.");
                Shutdown();
            }
            else
            {
                updateWindow.Close();
            }
        }
        catch (UpdateException ex)
        {
            LogError($"AutoUpdate 실패 [{ex.Code}]: {ex.Message}");
            window?.Close();
            MessageBox.Show(
                "업데이트 검증 또는 다운로드에 실패했습니다. 기존 버전으로 계속 실행합니다.",
                "업데이트 확인",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException
            or IOException or TaskCanceledException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException)
        {
            LogError($"AutoUpdate 실패 [{ex.GetType().Name}]: {ex.Message}");
            window?.Close();
        }
    }

    private sealed class ShellUpdateInstaller : IUpdateInstaller
    {
        public void Start(string verifiedSetupPath)
        {
            Process? process = Process.Start(new ProcessStartInfo(
                verifiedSetupPath,
                "/VERYSILENT /NORESTART /FORCECLOSEAPPLICATIONS")
            {
                UseShellExecute = true,
            });
            if (process is null)
                throw new UpdateInstallException("업데이트 설치 프로세스를 시작하지 못했습니다.");
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
