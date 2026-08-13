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
            ErrorDialog.Show(Current?.MainWindow, Core.ErrorCatalog.App(
                Core.ErrorCatalog.AppUnhandled,
                $"예기치 못한 오류가 발생했습니다: {args.Exception.Message}",
                "작업을 다시 시도하세요. 반복되면 [관리자 보고 복사]로 관리자에게 전달하세요.",
                args.Exception.GetType().Name));
            args.Handled = true;
        };
        var login = new LoginWindow();
        MainWindow = login;
        login.Show();
        _ = RunVerifiedUpdateAsync(userInitiated: false);
    }

    /// <summary>
    /// 자동 확인과 수동 링크가 공유하는 서명·해시·패키지 검증 업데이트 경로.
    /// 명시적인 수동 요청은 자동 업데이트 비활성화 환경변수의 영향을 받지 않는다.
    /// </summary>
    internal async Task RunVerifiedUpdateAsync(bool userInitiated)
    {
        if (!userInitiated && UpdateCheck.IsAutoUpdateDisabled())
            return;
        UpdateWindow? window = null;
        try
        {
            var config = AppConfig.Load();
            var client = new ApiClient(config.ServerUrl);
            var info = await client.CheckVersionAsync();
            if (info == null)
            {
                if (userInitiated)
                {
                    ErrorDialog.Show(Current?.MainWindow, Core.ErrorCatalog.Update(
                        Core.ErrorCatalog.UpdateCheckFailed,
                        $"업데이트 정보를 가져오지 못했습니다. (서버: {config.ServerUrl})",
                        "잠시 후 다시 시도하세요. 반복되면 관리자에게 아래 코드를 전달하세요."));
                }
                return;
            }
            if (!UpdateCheck.IsNewer(Ui.Version, info.LatestVersion))
            {
                if (userInitiated)
                {
                    MessageBox.Show(
                        $"현재 최신 버전(v{Ui.Version})을 사용 중입니다.",
                        "업데이트",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                return;
            }
            if (string.IsNullOrWhiteSpace(info.DownloadUrl))
            {
                if (userInitiated)
                {
                    ErrorDialog.Show(Current?.MainWindow, Core.ErrorCatalog.Update(
                        Core.ErrorCatalog.UpdateNotConfigured,
                        "검증 가능한 업데이트 설치파일이 서버에 등록되지 않았습니다.",
                        "관리자에게 아래 코드를 전달하세요. 업데이트 서버 등록 상태 점검이 필요합니다.",
                        targetVersion: info.LatestVersion));
                }
                return;
            }

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
                new ShellUpdateInstaller(),
                installGate: new ActiveCallUpdateInstallGate(updateWindow));
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
                if (userInitiated && result.Status == UpdateRunStatus.Busy)
                {
                    MessageBox.Show(
                        "업데이트 확인이 이미 진행 중입니다.",
                        "업데이트",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
        }
        catch (UpdateException ex)
        {
            LogError($"VerifiedUpdate 실패 [{ex.Code}]: {ex.Message}");
            window?.Close();
            ErrorDialog.Show(Current?.MainWindow, Core.ErrorCatalog.Update(
                Core.ErrorCatalog.UpdateInstallFailed,
                "업데이트 검증 또는 설치에 실패했습니다. 기존 버전으로 계속 실행합니다.",
                "[관리자 보고 복사]를 눌러 관리자에게 전달하세요. 세부코드가 원인 판별에 필요합니다.",
                detailCode: ex.Code));
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException
            or IOException or TaskCanceledException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException)
        {
            LogError($"VerifiedUpdate 실패 [{ex.GetType().Name}]: {ex.Message}");
            window?.Close();
            if (userInitiated)
            {
                ErrorDialog.Show(Current?.MainWindow, Core.ErrorCatalog.Update(
                    Core.ErrorCatalog.UpdateCheckFailed,
                    ex is UnauthorizedAccessException or IOException
                        ? $"업데이트 파일 처리에 실패했습니다: {ex.Message}"
                        : "업데이트 서버에 연결하지 못했습니다.",
                    "네트워크 연결을 확인한 뒤 다시 시도하세요. 반복되면 관리자에게 아래 코드를 전달하세요.",
                    detailCode: ex.GetType().Name));
            }
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

    private sealed class ActiveCallUpdateInstallGate : IUpdateInstallGate
    {
        private readonly UpdateWindow _progressWindow;

        public ActiveCallUpdateInstallGate(UpdateWindow progressWindow)
        {
            _progressWindow = progressWindow;
        }

        public async Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
        {
            var dispatcher = Application.Current.Dispatcher;
            MainWindow? main = await dispatcher.InvokeAsync(
                () => Application.Current.MainWindow as MainWindow);
            if (main == null || main.IsCallSessionIdle)
                return;

            bool installNow = await dispatcher.InvokeAsync(() =>
            {
                _progressWindow.Hide();
                if (main.WindowState == WindowState.Minimized)
                    main.WindowState = WindowState.Normal;
                main.Show();
                main.Activate();
                var prompt = new UpdateInstallPromptWindow { Owner = main };
                bool? result = prompt.ShowDialog();
                if (result == true && prompt.InstallNow)
                {
                    _progressWindow.Show();
                    return true;
                }
                main.ShowDeferredUpdateNotice();
                return false;
            });
            if (installNow)
                return;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                bool ready = await dispatcher.InvokeAsync(() =>
                    Application.Current.MainWindow is not MainWindow workspace
                    || workspace.IsCallSessionIdle);
                if (!ready)
                    continue;
                await dispatcher.InvokeAsync(() =>
                {
                    _progressWindow.SetStatus("통화 종료 확인 — 설치를 시작합니다.");
                    _progressWindow.Show();
                });
                return;
            }
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
