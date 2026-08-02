using Core;

namespace Tests;

public class CallWorkflowPolicyTests
{
    [Theory]
    [InlineData(CallSessionState.Dialing)]
    [InlineData(CallSessionState.Active)]
    [InlineData(CallSessionState.Ending)]
    [InlineData(CallSessionState.Idle)]
    public void CanSave_RejectsAnyStateExceptEnded(CallSessionState state)
    {
        Assert.False(CallWorkflowPolicy.CanSave(state, "NOANSWER", scheduledTimeValid: true));
    }

    [Fact]
    public void CanSave_RequiresResultAndValidScheduledTime()
    {
        Assert.False(CallWorkflowPolicy.CanSave(
            CallSessionState.Ended, null, scheduledTimeValid: true));
        Assert.False(CallWorkflowPolicy.CanSave(
            CallSessionState.Ended, "CALLBACK", scheduledTimeValid: false));
        Assert.False(CallWorkflowPolicy.CanSave(
            CallSessionState.Ended, "APPOINTMENT", scheduledTimeValid: false));
        Assert.True(CallWorkflowPolicy.CanSave(
            CallSessionState.Ended, "CALLBACK", scheduledTimeValid: true));
        Assert.True(CallWorkflowPolicy.CanSave(
            CallSessionState.Ended, "NOANSWER", scheduledTimeValid: false));
    }

    [Fact]
    public void ManualEndConfirmation_IsOnlyOfferedWhenAdbStateIsUnavailable()
    {
        Assert.True(CallWorkflowPolicy.CanOfferManualEndConfirmation(null));
        Assert.False(CallWorkflowPolicy.CanOfferManualEndConfirmation(0));
        Assert.False(CallWorkflowPolicy.CanOfferManualEndConfirmation(2));
    }
}
