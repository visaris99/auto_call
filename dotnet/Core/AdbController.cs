using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Core;

public sealed record AdbDeviceInfo(string Serial, string State)
{
    public bool IsReady => State.Equals("device", StringComparison.OrdinalIgnoreCase);
}

// ADB 발신/종료/연결감지. 배포 시 adb.exe는 프로그램 폴더의 adb 하위에 둔다.
public static class AdbController
{
    private sealed record AdbResult(int ExitCode, string Stdout, string Stderr);

    public static string AdbPath()
    {
        string? overridePath = Environment.GetEnvironmentVariable("TM_ADB");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;
        string bundled = Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe");
        return File.Exists(bundled) ? bundled : "adb";
    }

    private static async Task<AdbResult?> RunAsync(
        IEnumerable<string> args,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = AdbPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException
                    or System.ComponentModel.Win32Exception)
                {
                    // The process exited between cancellation and Kill.
                }
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask)
                        .WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException)
                {
                    // Killing the process can close redirected pipes abruptly.
                }
                return null;
            }

            return new AdbResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task<IReadOnlyList<AdbDeviceInfo>> ListDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        AdbResult? result = await RunAsync(
            new[] { "devices" }, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (result is not { ExitCode: 0 })
            return Array.Empty<AdbDeviceInfo>();

        return ParseDevices(result.Stdout);
    }

    public static IReadOnlyList<AdbDeviceInfo> ParseDevices(string output)
    {
        var devices = new List<AdbDeviceInfo>();
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0
                || line.StartsWith("List of devices", StringComparison.Ordinal)
                || line.StartsWith('*'))
                continue;
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                devices.Add(new AdbDeviceInfo(parts[0], parts[1]));
        }
        return devices;
    }

    public static AdbDeviceInfo? ResolveReadyDevice(
        IEnumerable<AdbDeviceInfo> devices,
        string? preferredSerial = null)
    {
        AdbDeviceInfo[] ready = devices.Where(device => device.IsReady).ToArray();
        if (!string.IsNullOrWhiteSpace(preferredSerial))
        {
            AdbDeviceInfo? preferred = ready.FirstOrDefault(device =>
                device.Serial.Equals(preferredSerial, StringComparison.Ordinal));
            if (preferred != null)
                return preferred;
        }
        return ready.Length == 1 ? ready[0] : null;
    }

    public static async Task<bool> CallAsync(
        string serial,
        string phone,
        CancellationToken cancellationToken = default)
    {
        AdbResult? result = await RunAsync(
            DeviceArgs(serial, "shell", "am", "start", "-a", "android.intent.action.CALL", "-d", $"tel:{phone}"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 };
    }

    public static async Task<bool> HangupAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        AdbResult? result = await RunAsync(
            DeviceArgs(serial, "shell", "input", "keyevent", "6"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 };
    }

    public static async Task<int?> GetCallStateAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        AdbResult? result = await RunAsync(
            DeviceArgs(serial, "shell", "dumpsys", "telephony.registry"),
            TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (result is not { ExitCode: 0 })
            return null;

        int? best = null;
        foreach (Match match in Regex.Matches(result.Stdout, @"mCallState=(\d)"))
        {
            int value = match.Groups[1].Value[0] - '0';
            best = best is null ? value : Math.Max(best.Value, value);
        }
        return best;
    }

    public static async Task<bool> WaitForIdleAsync(
        string serial,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan maxWait = timeout ?? TimeSpan.FromSeconds(8);
        TimeSpan interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        while (watch.Elapsed < maxWait)
        {
            if (await GetCallStateAsync(serial, cancellationToken).ConfigureAwait(false) == 0)
                return true;
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    // Synchronous wrappers are retained for compatibility with existing callers/tests.
    public static bool Call(string phone, string? serial = null)
    {
        string? resolved = ResolveSerialAsync(serial).GetAwaiter().GetResult();
        return resolved != null && CallAsync(resolved, phone).GetAwaiter().GetResult();
    }

    public static bool Hangup(string? serial = null)
    {
        string? resolved = ResolveSerialAsync(serial).GetAwaiter().GetResult();
        return resolved != null && HangupAsync(resolved).GetAwaiter().GetResult();
    }

    public static bool IsConnected(string? serial = null)
    {
        return ResolveSerialAsync(serial).GetAwaiter().GetResult() != null;
    }

    public static int? GetCallState(string? serial = null)
    {
        string? resolved = ResolveSerialAsync(serial).GetAwaiter().GetResult();
        return resolved == null ? null : GetCallStateAsync(resolved).GetAwaiter().GetResult();
    }

    private static async Task<string?> ResolveSerialAsync(string? preferredSerial)
    {
        IReadOnlyList<AdbDeviceInfo> devices = await ListDevicesAsync().ConfigureAwait(false);
        return ResolveReadyDevice(devices, preferredSerial)?.Serial;
    }

    private static IEnumerable<string> DeviceArgs(string serial, params string[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        return new[] { "-s", serial }.Concat(args);
    }
}
