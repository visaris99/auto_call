using Core;
using Xunit;

namespace Tests;

public class AppConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cfgtest").FullName;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TM_SERVER_URL", null);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Roundtrip()
    {
        Environment.SetEnvironmentVariable("TM_SERVER_URL", null);
        string path = Path.Combine(_dir, "config.json");
        new AppConfig
        {
            ServerUrl = "http://crm:3002",
            LastLoginId = "hong",
            AdbSerial = "R3CN123",
        }.Save(path);
        var loaded = AppConfig.Load(path);
        Assert.Equal("http://crm:3002", loaded.ServerUrl);
        Assert.Equal("hong", loaded.LastLoginId);
        Assert.Equal("R3CN123", loaded.AdbSerial);
        Assert.False(string.IsNullOrWhiteSpace(loaded.DeviceCode));
    }

    [Fact]
    public void Load_Missing_ReturnsDefaults()
    {
        Environment.SetEnvironmentVariable("TM_SERVER_URL", null);
        var loaded = AppConfig.Load(Path.Combine(_dir, "none.json"));
        Assert.Equal(AppConfig.DefaultServerUrl, loaded.ServerUrl);
        Assert.Equal("", loaded.LastLoginId);
        Assert.StartsWith("pc-", loaded.DeviceCode);
    }

    [Fact]
    public void EnvOverridesServerUrl()
    {
        string path = Path.Combine(_dir, "config.json");
        new AppConfig { ServerUrl = "http://saved:1" }.Save(path);
        Environment.SetEnvironmentVariable("TM_SERVER_URL", "http://env:2");
        Assert.Equal("http://env:2", AppConfig.Load(path).ServerUrl);
        Environment.SetEnvironmentVariable("TM_SERVER_URL", null);
        Assert.Equal("http://saved:1", AppConfig.Load(path).ServerUrl);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        Environment.SetEnvironmentVariable("TM_SERVER_URL", null);
        string path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{broken json");
        Assert.Equal(AppConfig.DefaultServerUrl, AppConfig.Load(path).ServerUrl);
    }

    [Fact]
    public void ConfigDir_CreatesDirectory()
    {
        string dir = AppConfig.ConfigDir();
        Assert.True(Directory.Exists(dir));
        Assert.EndsWith("MilestoneDialer", dir);
    }
}
