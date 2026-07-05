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

    /// <summary>통화 상태: 0=대기, 1=수신중, 2=통화중(발신 포함). 확인 불가면 null.
    /// 멀티심 단말은 mCallState가 여러 줄 — 가장 큰 값을 취한다.</summary>
    public static int? GetCallState()
    {
        var result = Run(new[] { "shell", "dumpsys", "telephony.registry" }, timeoutMs: 5_000);
        if (result is not { ExitCode: 0 } r)
            return null;
        int? best = null;
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(r.Stdout, @"mCallState=(\d)"))
        {
            int value = m.Groups[1].Value[0] - '0';
            best = best is null ? value : Math.Max(best.Value, value);
        }
        return best;
    }
}
