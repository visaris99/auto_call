using Core;
using Xunit;

namespace Tests;

// AdbController는 프로세스 환경변수(TM_ADB 등)를 쓰므로 병렬 실행 금지.
[Collection("adb-env")]
public class AdbControllerTests : IDisposable
{
    private const string FakeAdb = """
        #!/bin/sh
        echo "$@" >> "$TM_ADB_LOG"
        if [ "$1" = "devices" ]; then
          printf 'List of devices attached\n%s' "$TM_ADB_DEVICES"
        fi
        if [ "$2" = "dumpsys" ]; then
          printf '%s' "$TM_ADB_DUMPSYS"
        fi
        exit ${TM_ADB_EXIT:-0}
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("adbtest").FullName;
    private string LogPath => Path.Combine(_dir, "calls.log");

    public AdbControllerTests()
    {
        string script = Path.Combine(_dir, "fakeadb.sh");
        File.WriteAllText(script, FakeAdb);
        File.SetUnixFileMode(script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("TM_ADB", script);
        Environment.SetEnvironmentVariable("TM_ADB_LOG", LogPath);
        Environment.SetEnvironmentVariable("TM_ADB_DEVICES", "R3CN123\tdevice\n");
        Environment.SetEnvironmentVariable("TM_ADB_EXIT", null);
    }

    public void Dispose()
    {
        foreach (string name in new[]
                 { "TM_ADB", "TM_ADB_LOG", "TM_ADB_DEVICES", "TM_ADB_EXIT", "TM_ADB_DUMPSYS" })
            Environment.SetEnvironmentVariable(name, null);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Call_InvokesCallIntent()
    {
        Assert.True(AdbController.Call("01012341234"));
        Assert.Contains("shell am start -a android.intent.action.CALL -d tel:01012341234",
            File.ReadAllText(LogPath));
    }

    [Fact]
    public void Call_Failure_ReturnsFalse()
    {
        Environment.SetEnvironmentVariable("TM_ADB_EXIT", "1");
        Assert.False(AdbController.Call("01012341234"));
    }

    [Fact]
    public void Call_MissingBinary_ReturnsFalse()
    {
        Environment.SetEnvironmentVariable("TM_ADB", "/nonexistent/adb");
        Assert.False(AdbController.Call("01012341234"));
    }

    [Fact]
    public void Hangup_SendsKeyevent()
    {
        Assert.True(AdbController.Hangup());
        Assert.Contains("shell input keyevent 6", File.ReadAllText(LogPath));
    }

    [Fact]
    public void IsConnected_True()
    {
        Assert.True(AdbController.IsConnected());
    }

    [Fact]
    public void IsConnected_False_WhenNoDevices()
    {
        Environment.SetEnvironmentVariable("TM_ADB_DEVICES", "");
        Assert.False(AdbController.IsConnected());
    }

    [Fact]
    public void GetCallState_ReturnsMaxState_AcrossSims()
    {
        Environment.SetEnvironmentVariable("TM_ADB_DUMPSYS",
            "mCallState=0\nsomething\nmCallState=2\n");
        Assert.Equal(2, AdbController.GetCallState());
    }

    [Fact]
    public void GetCallState_Idle()
    {
        Environment.SetEnvironmentVariable("TM_ADB_DUMPSYS", "mCallState=0\n");
        Assert.Equal(0, AdbController.GetCallState());
    }

    [Fact]
    public void GetCallState_Null_WhenUnparsable()
    {
        Environment.SetEnvironmentVariable("TM_ADB_DUMPSYS", "no call state here");
        Assert.Null(AdbController.GetCallState());
    }
}
