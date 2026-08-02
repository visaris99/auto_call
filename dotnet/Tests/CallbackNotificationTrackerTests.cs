using Core;

namespace Tests;

public class CallbackNotificationTrackerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 5, 10, 0, 0, TimeSpan.FromHours(9));

    private static LeadItem Lead(string id, string nextCallAt) =>
        new(id, id, "010-****-0000", "CALLBACK", nextCallAt, null, null);

    [Fact]
    public void FirstCheck_AggregatesAllPastCallbacksOnce()
    {
        var tracker = new CallbackNotificationTracker();
        LeadItem[] leads =
        {
            Lead("later", "2026-07-05T09:30:00+09:00"),
            Lead("oldest", "2026-07-05T08:00:00+09:00"),
        };

        CallbackNotification notification = Assert.Single(tracker.Check(leads, Now));
        Assert.Equal(CallbackNotificationKind.StartupSummary, notification.Kind);
        Assert.Equal(2, notification.Count);
        Assert.Equal("oldest", notification.Target.Id);
        Assert.Empty(tracker.Check(leads, Now.AddSeconds(1)));
    }

    [Fact]
    public void NewlyDueCallback_NotifiesImmediately()
    {
        var tracker = new CallbackNotificationTracker();
        LeadItem lead = Lead("new", "2026-07-05T10:00:01+09:00");

        Assert.Empty(tracker.Check(new[] { lead }, Now));
        CallbackNotification notification = Assert.Single(
            tracker.Check(new[] { lead }, Now.AddSeconds(1)));

        Assert.Equal(CallbackNotificationKind.Due, notification.Kind);
        Assert.Equal("new", notification.Target.Id);
    }

    [Fact]
    public void UnhandledCallback_RemindsEveryThreeMinutes()
    {
        var tracker = new CallbackNotificationTracker();
        LeadItem lead = Lead("past", "2026-07-05T09:00:00+09:00");
        tracker.Check(new[] { lead }, Now);

        Assert.Empty(tracker.Check(new[] { lead }, Now.AddMinutes(2).AddSeconds(59)));
        CallbackNotification reminder = Assert.Single(
            tracker.Check(new[] { lead }, Now.AddMinutes(3)));
        Assert.Equal(CallbackNotificationKind.Reminder, reminder.Kind);
        Assert.Empty(tracker.Check(new[] { lead }, Now.AddMinutes(3).AddSeconds(1)));
    }

    [Fact]
    public void RemovedCallback_CanNotifyAsNewIfItReturns()
    {
        var tracker = new CallbackNotificationTracker();
        LeadItem lead = Lead("past", "2026-07-05T09:00:00+09:00");
        tracker.Check(new[] { lead }, Now);
        tracker.Check(Array.Empty<LeadItem>(), Now.AddMinutes(1));

        CallbackNotification notification = Assert.Single(
            tracker.Check(new[] { lead }, Now.AddMinutes(2)));
        Assert.Equal(CallbackNotificationKind.Due, notification.Kind);
    }
}
