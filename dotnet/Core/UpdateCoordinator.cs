using System.Diagnostics;

namespace Core;

public interface IUpdateInstaller
{
    void Start(string verifiedSetupPath);
}

public interface IUpdatePackageInspector
{
    void Verify(string setupPath, string expectedVersion);
}

public sealed class WindowsUpdatePackageInspector : IUpdatePackageInspector
{
    public void Verify(string setupPath, string expectedVersion)
    {
        using (var stream = File.OpenRead(setupPath))
        {
            if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
                throw new UpdateSecurityException(
                    "PACKAGE_NOT_PE", "업데이트 파일이 Windows 실행 파일이 아닙니다.");
        }

        if (!OperatingSystem.IsWindows()) return;

        FileVersionInfo info = FileVersionInfo.GetVersionInfo(setupPath);
        if (!string.Equals(info.ProductName, "Milestone Dialer", StringComparison.OrdinalIgnoreCase))
            throw new UpdateSecurityException(
                "PACKAGE_PRODUCT", "업데이트 파일의 제품명이 올바르지 않습니다.");
        if (!VersionsEqual(expectedVersion, info.ProductVersion))
            throw new UpdateSecurityException(
                "PACKAGE_VERSION", "업데이트 파일의 제품 버전이 manifest와 다릅니다.");
    }

    private static bool VersionsEqual(string expected, string? actual)
    {
        if (!Version.TryParse(expected, out var expectedVersion)
            || !Version.TryParse(actual, out var actualVersion))
            return false;
        return Part(expectedVersion.Major) == Part(actualVersion.Major)
               && Part(expectedVersion.Minor) == Part(actualVersion.Minor)
               && Part(expectedVersion.Build) == Part(actualVersion.Build)
               && Part(expectedVersion.Revision) == Part(actualVersion.Revision);
    }

    private static int Part(int value) => value < 0 ? 0 : value;
}

public enum UpdateRunStatus
{
    NoUpdate,
    Busy,
    InstallerStarted,
}

public sealed record UpdateRunResult(UpdateRunStatus Status, string? Version = null);

public sealed class UpdateCoordinator
{
    private static readonly SemaphoreSlim GlobalGate = new(1, 1);

    private readonly UpdateManifestVerifier _manifestVerifier;
    private readonly IUpdatePackageDownloader _downloader;
    private readonly IUpdatePackageInspector _packageInspector;
    private readonly IUpdateInstaller _installer;
    private readonly SemaphoreSlim _gate;

    public UpdateCoordinator(
        UpdateManifestVerifier manifestVerifier,
        IUpdatePackageDownloader downloader,
        IUpdatePackageInspector packageInspector,
        IUpdateInstaller installer,
        SemaphoreSlim? gate = null)
    {
        _manifestVerifier = manifestVerifier;
        _downloader = downloader;
        _packageInspector = packageInspector;
        _installer = installer;
        _gate = gate ?? GlobalGate;
    }

    public async Task<UpdateRunResult> RunAsync(
        VersionInfo info,
        string currentVersion,
        string downloadDirectory,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!UpdateCheck.IsNewer(currentVersion, info.LatestVersion))
            return new UpdateRunResult(UpdateRunStatus.NoUpdate);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new UpdateRunResult(UpdateRunStatus.Busy);

        string? setupPath = null;
        FileStream? processLock = null;
        try
        {
            Directory.CreateDirectory(downloadDirectory);
            processLock = TryAcquireProcessLock(downloadDirectory);
            if (processLock is null)
                return new UpdateRunResult(UpdateRunStatus.Busy);

            UpdateManifest manifest = UpdateManifest.FromVersionInfo(info);
            _manifestVerifier.Verify(manifest);
            setupPath = Path.Combine(
                downloadDirectory,
                $"milestone_dialer_setup_{manifest.Version}.exe");
            setupPath = await _downloader.DownloadAsync(
                    manifest, setupPath, progress, cancellationToken)
                .ConfigureAwait(false);
            _packageInspector.Verify(setupPath, manifest.Version);

            try
            {
                _installer.Start(setupPath);
            }
            catch (Exception ex) when (ex is not UpdateException)
            {
                throw new UpdateInstallException("검증된 업데이트 설치파일을 실행하지 못했습니다.", ex);
            }

            return new UpdateRunResult(UpdateRunStatus.InstallerStarted, manifest.Version);
        }
        catch
        {
            if (setupPath is not null) TryDelete(setupPath);
            throw;
        }
        finally
        {
            processLock?.Dispose();
            _gate.Release();
        }
    }

    private static FileStream? TryAcquireProcessLock(string directory)
    {
        try
        {
            return new FileStream(
                Path.Combine(directory, "milestone_dialer_update.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
