namespace Core;

public enum CallSessionState
{
    Idle,
    Authorizing,
    Dialing,
    Active,
    Ending,
    Ended,
    Saving,
}

public sealed record CallSessionSnapshot(
    string LeadId,
    string DeviceSerial,
    string OperationId,
    CallSessionState State,
    int TalkSeconds);

/// <summary>
/// Owns the lifecycle of one CRM call. The lead, device and operation id stay fixed
/// until the result has been saved or the session is explicitly abandoned.
/// </summary>
public sealed class CallSessionCoordinator
{
    private readonly object _gate = new();
    private readonly Func<string> _operationIdFactory;
    private CallSessionSnapshot? _current;
    private CallSessionState _stateBeforeEnding;

    public CallSessionCoordinator(Func<string>? operationIdFactory = null)
    {
        _operationIdFactory = operationIdFactory ?? (() => Guid.NewGuid().ToString());
    }

    public CallSessionSnapshot? Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public CallSessionState State => Current?.State ?? CallSessionState.Idle;

    public bool LocksLeadSelection => State != CallSessionState.Idle;

    public bool TryBegin(
        string leadId,
        string deviceSerial,
        out CallSessionSnapshot? session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSerial);

        lock (_gate)
        {
            if (_current != null)
            {
                session = null;
                return false;
            }

            _current = new CallSessionSnapshot(
                leadId,
                deviceSerial,
                _operationIdFactory(),
                CallSessionState.Authorizing,
                0);
            session = _current;
            return true;
        }
    }

    public bool MarkDialing() => Transition(CallSessionState.Authorizing, CallSessionState.Dialing);

    public bool MarkActive()
    {
        lock (_gate)
        {
            if (_current?.State is not (CallSessionState.Dialing or CallSessionState.Active))
                return false;
            _current = _current with { State = CallSessionState.Active };
            return true;
        }
    }

    public bool TryBeginEnding()
    {
        lock (_gate)
        {
            if (_current?.State is not (CallSessionState.Dialing or CallSessionState.Active))
                return false;
            _stateBeforeEnding = _current.State;
            _current = _current with { State = CallSessionState.Ending };
            return true;
        }
    }

    public bool CancelEnding()
    {
        lock (_gate)
        {
            if (_current?.State != CallSessionState.Ending)
                return false;
            _current = _current with { State = _stateBeforeEnding };
            return true;
        }
    }

    public bool MarkEnded(int talkSeconds)
    {
        lock (_gate)
        {
            if (_current?.State is not (
                    CallSessionState.Dialing or
                    CallSessionState.Active or
                    CallSessionState.Ending))
                return false;
            _current = _current with
            {
                State = CallSessionState.Ended,
                TalkSeconds = Math.Max(0, talkSeconds),
            };
            return true;
        }
    }

    public bool TryBeginSaving(out CallSessionSnapshot? session)
    {
        lock (_gate)
        {
            if (_current?.State != CallSessionState.Ended)
            {
                session = null;
                return false;
            }
            _current = _current with { State = CallSessionState.Saving };
            session = _current;
            return true;
        }
    }

    public bool SaveFailed() => Transition(CallSessionState.Saving, CallSessionState.Ended);

    public CallSessionSnapshot? CompleteSaving()
    {
        lock (_gate)
        {
            if (_current?.State != CallSessionState.Saving)
                return null;
            CallSessionSnapshot completed = _current;
            _current = null;
            return completed;
        }
    }

    public bool FailStart()
    {
        lock (_gate)
        {
            if (_current?.State is not (CallSessionState.Authorizing or CallSessionState.Dialing))
                return false;
            _current = null;
            return true;
        }
    }

    public bool Abandon()
    {
        lock (_gate)
        {
            if (_current?.State == CallSessionState.Saving)
                return false;
            _current = null;
            return true;
        }
    }

    private bool Transition(CallSessionState from, CallSessionState to)
    {
        lock (_gate)
        {
            if (_current?.State != from)
                return false;
            _current = _current with { State = to };
            return true;
        }
    }
}
