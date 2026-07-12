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
    public void Add_PersistsAttemptId()
    {
        var q = new PendingCallQueue(QueuePath);
        q.Add(new PendingCall("k1", "L1", "NOANSWER", 20, null, null,
            AttemptId: "attempt-1"));

        PendingCall item = Assert.Single(new PendingCallQueue(QueuePath).Items);
        Assert.Equal("attempt-1", item.AttemptId);
    }

    [Fact]
    public async Task Flush_UsesAttemptEndpoint_ForNewEntries()
    {
        using var crm = new MockCrm();
        ApiClientTests.SetLoginOk(crm);
        crm.Set("POST", "/api/v1/call-attempts/attempt-1/result", 200, new
        {
            ok = true,
            attemptId = "attempt-1",
            lead = new { id = "L1", status = "NOANSWER", nextCallAt = (string?)null },
        });
        var client = new ApiClient(crm.Url);
        await client.LoginAsync("hong", "pw");
        var q = new PendingCallQueue(QueuePath);
        q.Add(new PendingCall("k1", "L1", "NOANSWER", 20, null, null,
            AttemptId: "attempt-1"));

        Assert.Equal((1, 0), await q.FlushAsync(client));
        Assert.Equal("/api/v1/call-attempts/attempt-1/result", crm.Last.Path);
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

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Flush_TransientHttpError_StopsAndKeeps(int status)
    {
        var q = MakeQueue(2);
        var (sender, calls) = FakeSender(new ApiException("TEMPORARY", "일시 오류", status));

        Assert.Equal((0, 2), await q.FlushAsync(sender));
        Assert.Single(calls);
        Assert.Equal(2, q.Items.Count);
    }

    [Fact]
    public async Task Flush_ValidationError_RethrowsAndKeeps()
    {
        var q = MakeQueue(2);
        var (sender, calls) = FakeSender(new ApiException("VALIDATION", "잘못된 요청", 400));

        await Assert.ThrowsAsync<ApiException>(() => q.FlushAsync(sender));
        Assert.Single(calls);
        Assert.Equal(2, q.Items.Count);
    }

    [Fact]
    public void CorruptJson_IsPreservedBeforeStartingNewQueue()
    {
        const string corrupt = "{ not-json";
        File.WriteAllText(QueuePath, corrupt);

        var q = new PendingCallQueue(QueuePath);

        Assert.Empty(q.Items);
        Assert.NotNull(q.RecoveryFilePath);
        Assert.True(File.Exists(q.RecoveryFilePath));
        Assert.Equal(corrupt, File.ReadAllText(q.RecoveryFilePath!));
        Assert.False(File.Exists(QueuePath));

        q.Add(new PendingCall("new", "L1", "NOANSWER", 0, null, null));
        Assert.Single(new PendingCallQueue(QueuePath).Items);
    }

    [Fact]
    public async Task ConcurrentFlush_IsSerializedAndSendsEachEntryOnce()
    {
        var q = MakeQueue(1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        async Task Sender(PendingCall _)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
        }

        Task<(int Sent, int Remaining)> first = q.FlushAsync(Sender);
        await started.Task;
        Task<(int Sent, int Remaining)> second = q.FlushAsync(Sender);
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Empty(q.Items);
        Assert.Equal(1, results.Sum(result => result.Sent));
    }
}
