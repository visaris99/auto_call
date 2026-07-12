using Core;

namespace Tests;

public sealed class UpdateCoordinatorTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("update-coordinator").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task RunAsync_DoesNothingForCurrentOrOlderVersion()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create(version: "2.4.0");
        var downloader = new RecordingDownloader(fixture.PackageBytes);
        var installer = new RecordingInstaller();
        UpdateCoordinator coordinator = Coordinator(fixture, downloader, installer);

        UpdateRunResult result = await coordinator.RunAsync(
            fixture.Info, "2.4.0", _tempDir);

        Assert.Equal(UpdateRunStatus.NoUpdate, result.Status);
        Assert.Equal(0, downloader.Calls);
        Assert.Equal(0, installer.Calls);
    }

    [Fact]
    public async Task RunAsync_AllowsOnlyOneConcurrentUpdate()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create();
        var downloader = new BlockingDownloader(fixture.PackageBytes);
        var installer = new RecordingInstaller();
        var gate = new SemaphoreSlim(1, 1);
        UpdateCoordinator coordinator = Coordinator(fixture, downloader, installer, gate);

        Task<UpdateRunResult> first = coordinator.RunAsync(
            fixture.Info, "2.4.0", _tempDir);
        await downloader.Started.Task;
        UpdateRunResult second = await coordinator.RunAsync(
            fixture.Info, "2.4.0", _tempDir);
        downloader.Release.SetResult();
        UpdateRunResult completed = await first;

        Assert.Equal(UpdateRunStatus.Busy, second.Status);
        Assert.Equal(UpdateRunStatus.InstallerStarted, completed.Status);
        Assert.Equal(1, installer.Calls);
    }

    [Fact]
    public async Task RunAsync_AllowsOnlyOneUpdateAcrossCoordinatorInstances()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create();
        var blockingDownloader = new BlockingDownloader(fixture.PackageBytes);
        var firstInstaller = new RecordingInstaller();
        var secondDownloader = new RecordingDownloader(fixture.PackageBytes);
        var secondInstaller = new RecordingInstaller();
        UpdateCoordinator firstCoordinator = Coordinator(
            fixture, blockingDownloader, firstInstaller, new SemaphoreSlim(1, 1));
        UpdateCoordinator secondCoordinator = Coordinator(
            fixture, secondDownloader, secondInstaller, new SemaphoreSlim(1, 1));

        Task<UpdateRunResult> first = firstCoordinator.RunAsync(
            fixture.Info, "2.4.0", _tempDir);
        await blockingDownloader.Started.Task;
        UpdateRunResult second = await secondCoordinator.RunAsync(
            fixture.Info, "2.4.0", _tempDir);
        blockingDownloader.Release.SetResult();
        UpdateRunResult completed = await first;

        Assert.Equal(UpdateRunStatus.Busy, second.Status);
        Assert.Equal(UpdateRunStatus.InstallerStarted, completed.Status);
        Assert.Equal(0, secondDownloader.Calls);
        Assert.Equal(0, secondInstaller.Calls);
    }

    [Fact]
    public async Task RunAsync_DeletesPackageWhenInstallerCannotStart()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create();
        var downloader = new RecordingDownloader(fixture.PackageBytes);
        var installer = new RecordingInstaller { Failure = new InvalidOperationException("blocked") };
        UpdateCoordinator coordinator = Coordinator(fixture, downloader, installer);

        UpdateInstallException error = await Assert.ThrowsAsync<UpdateInstallException>(() =>
            coordinator.RunAsync(fixture.Info, "2.4.0", _tempDir));

        Assert.Equal("INSTALL_START", error.Code);
        Assert.False(File.Exists(Path.Combine(_tempDir, "milestone_dialer_setup_2.4.1.exe")));
    }

    [Fact]
    public void WindowsInspector_RejectsNonPeFile()
    {
        string path = Path.Combine(_tempDir, "not-pe.exe");
        File.WriteAllText(path, "not an executable");

        UpdateSecurityException error = Assert.Throws<UpdateSecurityException>(() =>
            new WindowsUpdatePackageInspector().Verify(path, "2.4.1"));

        Assert.Equal("PACKAGE_NOT_PE", error.Code);
    }

    private static UpdateCoordinator Coordinator(
        SignedUpdateFixture fixture,
        IUpdatePackageDownloader downloader,
        IUpdateInstaller installer,
        SemaphoreSlim? gate = null) => new(
        new UpdateManifestVerifier(fixture.Policy, fixture.TrustedKeys),
        downloader,
        new NoOpInspector(),
        installer,
        gate ?? new SemaphoreSlim(1, 1));

    private sealed class NoOpInspector : IUpdatePackageInspector
    {
        public void Verify(string setupPath, string expectedVersion)
        {
        }
    }

    private class RecordingDownloader : IUpdatePackageDownloader
    {
        private readonly byte[] _bytes;
        internal int Calls { get; private set; }

        internal RecordingDownloader(byte[] bytes) => _bytes = bytes;

        public virtual async Task<string> DownloadAsync(
            UpdateManifest manifest,
            string destinationPath,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, _bytes, cancellationToken);
            return destinationPath;
        }
    }

    private sealed class BlockingDownloader : RecordingDownloader
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal BlockingDownloader(byte[] bytes) : base(bytes)
        {
        }

        public override async Task<string> DownloadAsync(
            UpdateManifest manifest,
            string destinationPath,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.DownloadAsync(
                manifest, destinationPath, progress, cancellationToken);
        }
    }

    private sealed class RecordingInstaller : IUpdateInstaller
    {
        internal int Calls { get; private set; }
        internal Exception? Failure { get; init; }

        public void Start(string verifiedSetupPath)
        {
            Calls++;
            if (Failure is not null) throw Failure;
        }
    }
}
