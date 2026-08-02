namespace Core;

public enum CallbackNotificationKind
{
    StartupSummary,
    Due,
    Reminder,
}

public sealed record CallbackNotification(
    CallbackNotificationKind Kind,
    LeadItem Target,
    int Count = 1);

public sealed class CallbackNotificationTracker
{
    private readonly TimeSpan _reminderInterval;
    private readonly Dictionary<string, DateTimeOffset> _lastNotified = new(StringComparer.Ordinal);
    private bool _initialized;

    public CallbackNotificationTracker(TimeSpan? reminderInterval = null)
    {
        _reminderInterval = reminderInterval ?? TimeSpan.FromMinutes(3);
    }

    public IReadOnlyList<CallbackNotification> Check(
        IEnumerable<LeadItem> leads,
        DateTimeOffset now)
    {
        List<LeadItem> due = leads
            .Where(item => QueueLogic.IsCallbackDue(item, now))
            .OrderBy(item => QueueLogic.ParseIso(item.NextCallAt))
            .ToList();
        var dueIds = due.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string id in _lastNotified.Keys.Where(id => !dueIds.Contains(id)).ToArray())
            _lastNotified.Remove(id);

        if (!_initialized)
        {
            _initialized = true;
            foreach (LeadItem item in due)
                _lastNotified[item.Id] = now;
            return due.Count == 0
                ? Array.Empty<CallbackNotification>()
                : new[]
                {
                    new CallbackNotification(
                        CallbackNotificationKind.StartupSummary,
                        due[0],
                        due.Count),
                };
        }

        var notifications = new List<CallbackNotification>();
        foreach (LeadItem item in due)
        {
            if (!_lastNotified.TryGetValue(item.Id, out DateTimeOffset last))
            {
                _lastNotified[item.Id] = now;
                notifications.Add(new CallbackNotification(
                    CallbackNotificationKind.Due, item));
            }
            else if (now - last >= _reminderInterval)
            {
                _lastNotified[item.Id] = now;
                notifications.Add(new CallbackNotification(
                    CallbackNotificationKind.Reminder, item));
            }
        }
        return notifications;
    }
}
