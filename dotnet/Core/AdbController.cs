// ADB 발신/종료/연결감지 — 배포 시 adb.exe는 프로그램 폴더의 adb\ 하위에 둔다.
using System.Diagnostics;

namespace Core;

public static class AdbController
{
    public static string AdbPath()
    {
        string? overridePath = Environment.GetEnvironmentVariable("TM_ADB");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;
        string bundled = Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe");
        return File.Exists(bundled) ? bundled : "adb";
    }

    private static (int ExitCode, string Stdout)? Run(string[] args, int timeoutMs = 10_000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = AdbPath(),
                UseShellExecute = false,
                CreateNoWindow = true,          // 콘솔창 깜빡임 방지
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args)
                psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi);
            if (proc == null)
                return null;
            string stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                proc.Kill(entireProcessTree: true);
                return null;
            }
            return (proc.ExitCode, stdout);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public static bool Call(string phone)
    {
        var result = Run(new[]
            { "shell", "am", "start", "-a", "android.intent.action.CALL", "-d", $"tel:{phone}" });
        return result is { ExitCode: 0 };
    }

    public static bool Hangup()
    {
        var result = Run(new[] { "shell", "input", "keyevent", "6" });
        return result is { ExitCode: 0 };
    }

    public static bool IsConnected()
    {
        var result = Run(new[] { "devices" }, timeoutMs: 5_000);
        if (result is not { ExitCode: 0 } r)
            return false;
        return r.Stdout.Split('\n').Skip(1)
            .Any(line => line.Trim().EndsWith("device", StringComparison.Ordinal));
    }
}
