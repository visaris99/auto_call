using Core;
using Xunit;

namespace Tests;

public class PendingCallQueueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pending").FullName;
    private string QueuePath => Path.Combine(_dir, "pending.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private PendingCallQueue MakeQueue(int n = 0)
    {
        var q = new PendingCallQueue(QueuePath);
        for (int i = 0; i < n; i++)
            q.Add(new PendingCall($"k{i}", $"L{i}", "NOANSWER", 0, null, null));
        return q;
    }

    /// <summary>결과를 순서대로 돌려주는 가짜 전송자. 예외 인스턴스면 throw.</summary>
    private static (Func<PendingCall, Task> Sender, List<PendingCall> Calls) FakeSender(
        params object?[] results)
    {
        var queue = new Queue<object?>(results);
        var calls = new List<PendingCall>();
        return (item =>
        {
            calls.Add(item);
            object? r = queue.Dequeue();
            if (r is Exception ex) throw ex;
            return Task.CompletedTask;
        }, calls);
    }

    [Fact]
    public void Add_PersistsToDisk()
    {
        MakeQueue(1);
        var reloaded = new PendingCallQueue(QueuePath);
        var item = Assert.Single(reloaded.Items);
        Assert.Equal("k0", item.IdempotencyKey);
    }

    [Fact]
    public void Add_PersistsAppointmentAt()
    {
        var q = new PendingCallQueue(QueuePath);
        q.Add(new PendingCall("k-appt", "L1", "APPOINTMENT", 30, null, null,
            "2026-07-06T11:00:00+09:00"));
        var item = Assert.Single(new PendingCallQueue(QueuePath).Items);
        Assert.Equal("APPOINTMENT", item.ResultCode);
        Assert.Equal("2026-07-06T11:00:00+09:00", item.AppointmentAt);
    }

    [Fact]
    public async Task Flush_Success_RemovesAndCounts()
    {
        var q = MakeQueue(2);
        var (sender, calls) = FakeSender(null, null);
        Assert.Equal((2, 0), await q.FlushAsync(sender));
        Assert.Empty(q.Items);
        Assert.Equal("L0", calls[0].LeadId);
        Assert.Equal("k0", calls[0].IdempotencyKey);
        Assert.Equal("NOANSWER", calls[0].ResultCode);
    }

    [Fact]
    public async Task Flush_NetworkError_StopsAndKeeps()
    {
        var q = MakeQueue(3);
        var (sender, calls) = FakeSender(null, new NetworkException());
        Assert.Equal((1, 2), await q.FlushAsync(sender));
        Assert.Equal(2, calls.Count); // 3번째는 시도 안 함
    }

    [Fact]
    public async Task Flush_AuthError_Rethrows_AndKeeps()
    {
        var q = MakeQueue(1);
        var (sender, _) = FakeSender(new AuthException("UNAUTHENTICATED", "만료", 401));
        await Assert.ThrowsAsync<AuthException>(() => q.FlushAsync(sender));
        Assert.Single(q.Items); // 재로그인 후 재시도되어야 하므로 유지
    }

    [Fact]
    public async Task Flush_ValidationError_DropsPoisonEntry()
    {
        var q = MakeQueue(2);
        var (sender, _) = FakeSender(new ApiException("VALIDATION", "잘못된 요청", 400), null);
        Assert.Equal((1, 0), await q.FlushAsync(sender)); // 1건 버리고 1건 성공
    }
}
