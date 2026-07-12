using System.Net;
using Core;

namespace Tests;

public sealed class UpdateDownloaderTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("update-download").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task DownloadAsync_WritesFileAfterSizeAndHashMatch()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create();
        using var http = Http(_ => Ok(fixture.PackageBytes));
        string destination = Path.Combine(_tempDir, "setup.exe");

        string result = await new UpdateDownloader(http, fixture.Policy)
            .DownloadAsync(fixture.Manifest, destination);

        Assert.Equal(destination, result);
        Assert.Equal(fixture.PackageBytes, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_FollowsAllowedRedirect()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create();
        int calls = 0;
        using var http = Http(request =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath == "/downloads/milestone_dialer_setup.exe")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri($"https://{UpdateTestData.Host}/release/setup.exe");
                return redirect;
            }
            return Ok(fixture.PackageBytes);
        });

        await new UpdateDownloader(http, fixture.Policy).DownloadAsync(
            fixture.Manifest, Path.Combine(_tempDir, "redirect.exe"));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DownloadAsync_RejectsRedirectToUntrustedHost()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create();
        using var http = Http(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://evil.example/setup.exe");
            return redirect;
        });
        string destination = Path.Combine(_tempDir, "redirect-evil.exe");

        UpdateSecurityException error = await Assert.ThrowsAsync<UpdateSecurityException>(() =>
            new UpdateDownloader(http, fixture.Policy)
                .DownloadAsync(fixture.Manifest, destination));

        Assert.Equal("DOWNLOAD_HOST_POLICY", error.Code);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_RejectsContentLengthMismatch()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create();
        using var http = Http(_ =>
        {
            HttpResponseMessage response = Ok(fixture.PackageBytes);
            response.Content.Headers.ContentLength = fixture.PackageBytes.Length + 1;
            return response;
        });

        UpdateDownloadException error = await Assert.ThrowsAsync<UpdateDownloadException>(() =>
            new UpdateDownloader(http, fixture.Policy).DownloadAsync(
                fixture.Manifest, Path.Combine(_tempDir, "length.exe")));

        Assert.Equal("DOWNLOAD_LENGTH_HEADER", error.Code);
    }

    [Fact]
    public async Task DownloadAsync_RejectsSha256MismatchAndDeletesPartialFile()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create();
        byte[] altered = fixture.PackageBytes.ToArray();
        altered[^1] ^= 0xff;
        using var http = Http(_ => Ok(altered));
        string destination = Path.Combine(_tempDir, "hash.exe");

        UpdateDownloadException error = await Assert.ThrowsAsync<UpdateDownloadException>(() =>
            new UpdateDownloader(http, fixture.Policy).DownloadAsync(fixture.Manifest, destination));

        Assert.Equal("DOWNLOAD_HASH_MISMATCH", error.Code);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_CleansUpAfterNetworkFailure()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create();
        using var http = new HttpClient(new DelegateHttpMessageHandler((_, _) =>
            throw new HttpRequestException("offline")));
        string destination = Path.Combine(_tempDir, "offline.exe");

        UpdateDownloadException error = await Assert.ThrowsAsync<UpdateDownloadException>(() =>
            new UpdateDownloader(http, fixture.Policy).DownloadAsync(fixture.Manifest, destination));

        Assert.Equal("DOWNLOAD_IO", error.Code);
        Assert.False(File.Exists(destination + ".part"));
    }

    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new DelegateHttpMessageHandler((request, _) => Task.FromResult(handler(request))));

    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };
}
