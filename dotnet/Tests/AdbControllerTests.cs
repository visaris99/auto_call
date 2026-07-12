using Core;
using Xunit;

namespace Tests;

// AdbController는 프로세스 환경변수(TM_ADB 등)를 쓰므로 병렬 실행 금지.
[Collection("adb-env")]
public class AdbControllerTests : IDisposable
{
    private const string FakeAdbSh = """
        #!/bin/sh
        echo "$@" >> "$TM_ADB_LOG"
        if [ "$1" = "devices" ]; then
          printf 'List of devices attached\n'
          cat "$TM_ADB_DEVICES_FILE"
        fi
        if [ "$4" = "dumpsys" ]; then
          cat "$TM_ADB_DUMPSYS_FILE"
        fi
        exit ${TM_ADB_EXIT:-0}
        """;

    private const string FakeAdbCmd = """
        @echo off
        >> "%TM_ADB_LOG%" echo %*
        if "%1"=="devices" (
          echo List of devices attached
          if exist "%TM_ADB_DEVICES_FILE%" type "%TM_ADB_DEVICES_FILE%"
        )
        if "%4"=="dumpsys" (
          if exist "%TM_ADB_DUMPSYS_FILE%" type "%TM_ADB_DUMPSYS_FILE%"
        )
        if defined TM_ADB_EXIT exit /b %TM_ADB_EXIT%
        exit /b 0
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("adbtest").FullName;
    private string LogPath => Path.Combine(_dir, "calls.log");
    private string DevicesPath => Path.Combine(_dir, "devices.txt");
    private string DumpsysPath => Path.Combine(_dir, "dumpsys.txt");

    public AdbControllerTests()
    {
        string script;
        if (OperatingSystem.IsWindows())
        {
            script = Path.Combine(_dir, "fakeadb.cmd");
            File.WriteAllText(script, FakeAdbCmd);
        }
        else
        {
            script = Path.Combine(_dir, "fakeadb.sh");
            File.WriteAllText(script, FakeAdbSh);
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Environment.SetEnvironmentVariable("TM_ADB", script);
        Environment.SetEnvironmentVariable("TM_ADB_LOG", LogPath);
        Environment.SetEnvironmentVariable("TM_ADB_DEVICES_FILE", DevicesPath);
        Environment.SetEnvironmentVariable("TM_ADB_DUMPSYS_FILE", DumpsysPath);
        SetDevices("R3CN123\tdevice\n");
        SetDumpsys("");
        Environment.SetEnvironmentVariable("TM_ADB_EXIT", null);
    }

    public void Dispose()
    {
        foreach (string name in new[]
                 { "TM_ADB", "TM_ADB_LOG", "TM_ADB_DEVICES", "TM_ADB_DEVICES_FILE", "TM_ADB_EXIT", "TM_ADB_DUMPSYS", "TM_ADB_DUMPSYS_FILE" })
            Environment.SetEnvironmentVariable(name, null);
        Directory.Delete(_dir, recursive: true);
    }

    private void SetDevices(string value)
    {
        Environment.SetEnvironmentVariable("TM_ADB_DEVICES", value);
        File.WriteAllText(DevicesPath, value);
    }

    private void SetDumpsys(string value)
    {
        Environment.SetEnvironmentVariable("TM_ADB_DUMPSYS", value);
        File.WriteAllText(DumpsysPath, value);
    }

    [Fact]
    public void Call_InvokesCallIntent()
    {
        Assert.True(AdbController.Call("01012341234"));
        Assert.Contains("-s R3CN123 shell am start -a android.intent.action.CALL -d tel:01012341234",
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
        Assert.Contains("-s R3CN123 shell input keyevent 6", File.ReadAllText(LogPath));
    }

    [Fact]
    public void IsConnected_True()
    {
        Assert.True(AdbController.IsConnected());
    }

    [Fact]
    public void IsConnected_False_WhenNoDevices()
    {
        SetDevices("");
        Assert.False(AdbController.IsConnected());
    }

    [Fact]
    public void GetCallState_ReturnsMaxState_AcrossSims()
    {
        SetDumpsys("mCallState=0\nsomething\nmCallState=2\n");
        Assert.Equal(2, AdbController.GetCallState());
    }

    [Fact]
    public void GetCallState_Idle()
    {
        SetDumpsys("mCallState=0\n");
        Assert.Equal(0, AdbController.GetCallState());
    }

    [Fact]
    public void GetCallState_Null_WhenUnparsable()
    {
        SetDumpsys("no call state here");
        Assert.Null(AdbController.GetCallState());
    }

    [Fact]
    public void ParseDevices_PreservesNonReadyStates()
    {
        IReadOnlyList<AdbDeviceInfo> devices = AdbController.ParseDevices(
            "* daemon started successfully\nList of devices attached\n" +
            "READY\tdevice product:x\nLOCKED\tunauthorized\nOFF\toffline\n");

        Assert.Collection(devices,
            device =>
            {
                Assert.Equal("READY", device.Serial);
                Assert.True(device.IsReady);
            },
            device =>
            {
                Assert.Equal("LOCKED", device.Serial);
                Assert.Equal("unauthorized", device.State);
                Assert.False(device.IsReady);
            },
            device => Assert.Equal("offline", device.State));
    }

    [Fact]
    public void ResolveReadyDevice_RequiresSelection_WhenMultipleReady()
    {
        AdbDeviceInfo[] devices =
        {
            new("FIRST", "device"),
            new("SECOND", "device"),
        };

        Assert.Null(AdbController.ResolveReadyDevice(devices));
        Assert.Equal("SECOND", AdbController.ResolveReadyDevice(devices, "SECOND")?.Serial);
    }

    [Fact]
    public async Task AsyncCommands_AlwaysTargetProvidedSerial()
    {
        Assert.True(await AdbController.CallAsync("SECOND", "01055556666"));
        Assert.True(await AdbController.HangupAsync("SECOND"));
        SetDumpsys("mCallState=2\n");
        Assert.Equal(2, await AdbController.GetCallStateAsync("SECOND"));

        string log = File.ReadAllText(LogPath);
        Assert.Contains("-s SECOND shell am start", log);
        Assert.Contains("-s SECOND shell input keyevent 6", log);
        Assert.Contains("-s SECOND shell dumpsys telephony.registry", log);
    }

    [Fact]
    public void LegacyCall_RejectsAmbiguousMultipleDevices()
    {
        SetDevices("FIRST\tdevice\nSECOND\tdevice\n");

        Assert.False(AdbController.Call("01012341234"));
    }
}
