namespace Core;

public static class CallWorkflowPolicy
{
    public static bool NeedsScheduledTime(string? resultCode) =>
        resultCode is "CALLBACK" or "APPOINTMENT";

    public static bool CanSave(
        CallSessionState state,
        string? resultCode,
        bool scheduledTimeValid)
    {
        if (state != CallSessionState.Ended || string.IsNullOrWhiteSpace(resultCode))
            return false;
        return !NeedsScheduledTime(resultCode) || scheduledTimeValid;
    }

    public static bool CanOfferManualEndConfirmation(int? observedCallState) =>
        observedCallState == null;
}
