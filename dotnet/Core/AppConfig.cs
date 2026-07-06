// 로컬 설정 — %APPDATA%\MilestoneDialer\config.json (파이썬 state.Config와 동일 의미론).
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Core;

public sealed class AppConfig
{
    public const string DefaultServerUrl = "https://crm.milestone-sales.xyz";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,  // 파이썬 config.json과 호환
        WriteIndented = true,
    };

    [JsonPropertyName("server_url")]
    public string ServerUrl { get; set; } = DefaultServerUrl;

    [JsonPropertyName("last_login_id")]
    public string LastLoginId { get; set; } = "";

    [JsonPropertyName("auto_dial")]
    public bool AutoDial { get; set; }

    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = "";

    public static string ConfigDir()
    {
        string baseDir = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        string dir = Path.Combine(baseDir, "MilestoneDialer");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static AppConfig Load(string? path = null)
    {
        path ??= Path.Combine(ConfigDir(), "config.json");
        AppConfig config;
        try
        {
            config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json)
                     ?? new AppConfig();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            config = new AppConfig();
        }
        if (string.IsNullOrWhiteSpace(config.DeviceCode))
        {
            config.DeviceCode = $"pc-{Guid.NewGuid():N}";
            try
            {
                AtomicWrite(path, JsonSerializer.Serialize(config, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 설정 파일 저장 실패는 치명적이지 않다. 현재 세션에서는 생성값을 사용한다.
            }
        }
        string? env = Environment.GetEnvironmentVariable("TM_SERVER_URL");
        if (!string.IsNullOrEmpty(env))
            config.ServerUrl = env;
        return config;
    }

    public void Save(string? path = null)
    {
        path ??= Path.Combine(ConfigDir(), "config.json");
        AtomicWrite(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>tmp 파일에 쓰고 rename — 저장 중 크래시로 파일이 깨지지 않게.</summary>
    internal static void AtomicWrite(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
