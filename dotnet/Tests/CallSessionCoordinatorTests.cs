using Core;

namespace Tests;

public class CallSessionCoordinatorTests
{
    private const string OperationId = "2f1e8918-8dc3-4aef-ab97-a4513ca0f649";

    [Fact]
    public void HappyPath_LocksLeadDeviceAndOperationUntilSaveCompletes()
    {
        var coordinator = new CallSessionCoordinator(() => OperationId);

        Assert.True(coordinator.TryBegin("lead-1", "device-1", out CallSessionSnapshot? started));
        Assert.Equal(CallSessionState.Authorizing, started?.State);
        Assert.True(coordinator.LocksLeadSelection);
        Assert.True(coordinator.MarkDialing());
        Assert.True(coordinator.MarkActive());
        Assert.True(coordinator.MarkEnded(37));
        Assert.True(coordinator.TryBeginSaving(out CallSessionSnapshot? saving));
        Assert.Equal("lead-1", saving?.LeadId);
        Assert.Equal("device-1", saving?.DeviceSerial);
        Assert.Equal(OperationId, saving?.OperationId);
        Assert.Equal(37, saving?.TalkSeconds);

        CallSessionSnapshot? completed = coordinator.CompleteSaving();
        Assert.Equal(OperationId, completed?.OperationId);
        Assert.Equal(CallSessionState.Idle, coordinator.State);
        Assert.False(coordinator.LocksLeadSelection);
    }

    [Fact]
    public void DuplicateStart_IsRejected()
    {
        var coordinator = new CallSessionCoordinator(() => OperationId);

        Assert.True(coordinator.TryBegin("lead-1", "device-1", out _));
        Assert.False(coordinator.TryBegin("lead-2", "device-2", out _));
        Assert.Equal("lead-1", coordinator.Current?.LeadId);
    }

    [Fact]
    public void DuplicateEndAndSave_AreRejected()
    {
        var coordinator = new CallSessionCoordinator(() => OperationId);
        coordinator.TryBegin("lead-1", "device-1", out _);
        coordinator.MarkDialing();

        Assert.True(coordinator.TryBeginEnding());
        Assert.False(coordinator.TryBeginEnding());
        Assert.True(coordinator.MarkEnded(5));
        Assert.False(coordinator.MarkEnded(6));
        Assert.True(coordinator.TryBeginSaving(out _));
        Assert.False(coordinator.TryBeginSaving(out _));
    }

    [Fact]
    public void SaveFailure_ReusesOriginalOperationId()
    {
        var coordinator = new CallSessionCoordinator(() => OperationId);
        coordinator.TryBegin("lead-1", "device-1", out _);
        coordinator.MarkDialing();
        coordinator.MarkEnded(12);
        coordinator.TryBeginSaving(out CallSessionSnapshot? firstAttempt);

        Assert.True(coordinator.SaveFailed());
        Assert.True(coordinator.TryBeginSaving(out CallSessionSnapshot? secondAttempt));
        Assert.Equal(firstAttempt?.OperationId, secondAttempt?.OperationId);
        Assert.Equal(firstAttempt?.TalkSeconds, secondAttempt?.TalkSeconds);
    }

    [Fact]
    public void FailedHangup_ReturnsToPreviousCallState()
    {
        var coordinator = new CallSessionCoordinator(() => OperationId);
        coordinator.TryBegin("lead-1", "device-1", out _);
        coordinator.MarkDialing();
        coordinator.MarkActive();

        Assert.True(coordinator.TryBeginEnding());
        Assert.True(coordinator.CancelEnding());
        Assert.Equal(CallSessionState.Active, coordinator.State);
    }

    [Fact]
    public async Task ConcurrentStarts_AllowExactlyOneSession()
    {
        var coordinator = new CallSessionCoordinator(() => OperationId);
        Task<bool>[] starts = Enumerable.Range(0, 20)
            .Select(index => Task.Run(() =>
                coordinator.TryBegin($"lead-{index}", "device-1", out _)))
            .ToArray();

        bool[] results = await Task.WhenAll(starts);
        Assert.Single(results.Where(result => result));
    }

    [Fact]
    public void SavingSession_CannotBeAbandoned()
    {
        var coordinator = new CallSessionCoordinator(() => OperationId);
        coordinator.TryBegin("lead-1", "device-1", out _);
        coordinator.MarkDialing();
        coordinator.MarkEnded(1);
        coordinator.TryBeginSaving(out _);

        Assert.False(coordinator.Abandon());
        Assert.Equal(CallSessionState.Saving, coordinator.State);
    }
}
