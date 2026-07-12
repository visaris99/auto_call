// 전송 실패한 콜 기록의 재전송 큐 — 파이썬 state.PendingCallQueue와 동일 의미론.
// ★평문 전화번호는 절대 저장하지 않는다.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Core;

public sealed record PendingCall(
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("lead_id")] string LeadId,
    [property: JsonPropertyName("result_code")] string ResultCode,
    [property: JsonPropertyName("talk_seconds")] int TalkSeconds,
    [property: JsonPropertyName("memo")] string? Memo,
    [property: JsonPropertyName("callback_at")] string? CallbackAt,
    [property: JsonPropertyName("appointment_at")] string? AppointmentAt = null,
    [property: JsonPropertyName("attempt_id")] string? AttemptId = null);

public sealed class PendingCallQueue
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private List<PendingCall> _items;

    public string? RecoveryFilePath { get; private set; }
    public string? LoadError { get; private set; }

    public PendingCallQueue(string? path = null)
    {
        _path = path ?? Path.Combine(AppConfig.ConfigDir(), "pending_calls.json");
        if (!File.Exists(_path))
        {
            _items = new List<PendingCall>();
            return;
        }

        try
        {
            _items = JsonSerializer.Deserialize<List<PendingCall>>(File.ReadAllText(_path), Json)
                     ?? new List<PendingCall>();
        }
        catch (JsonException)
        {
            _items = new List<PendingCall>();
            try
            {
                RecoveryFilePath = RecoveryPath(_path);
                File.Move(_path, RecoveryFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecoveryFilePath = null;
                LoadError = $"손상된 전송 대기열을 보존하지 못했습니다: {ex.Message}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _items = new List<PendingCall>();
            LoadError = $"전송 대기열을 읽지 못했습니다: {ex.Message}";
        }
    }

    public IReadOnlyList<PendingCall> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public void Add(PendingCall item)
    {
        lock (_lock)
        {
            EnsureWritableLocked();
            _items.Add(item);
            SaveLocked();
        }
    }

    public void Remove(string idempotencyKey)
    {
        lock (_lock)
        {
            EnsureWritableLocked();
            _items = _items.Where(x => x.IdempotencyKey != idempotencyKey).ToList();
            SaveLocked();
        }
    }

    private void EnsureWritableLocked()
    {
        if (LoadError != null)
            throw new IOException(LoadError);
    }

    private void SaveLocked() =>
        AppConfig.AtomicWrite(_path, JsonSerializer.Serialize(_items, Json));

    private static string RecoveryPath(string path)
    {
        string prefix = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        string candidate = prefix;
        int suffix = 1;
        while (File.Exists(candidate))
            candidate = $"{prefix}-{suffix++}";
        return candidate;
    }

    public static bool IsRetryable(ApiException error) =>
        error is NetworkException or NightBlockedException
        || error.HttpStatus is 408 or 429
        || error.HttpStatus >= 500;

    /// <summary>(성공 수, 잔여 수). 일시 오류는 중단 후 재시도하고,
    /// 영구 오류도 자동 삭제하지 않고 호출자에게 전달해 수동 확인을 기다린다.</summary>
    public async Task<(int Sent, int Remaining)> FlushAsync(Func<PendingCall, Task> sender)
    {
        await _flushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            int sent = 0;
            foreach (var entry in Items)
            {
                try
                {
                    await sender(entry).ConfigureAwait(false);
                }
                catch (ApiException ex) when (IsRetryable(ex))
                {
                    break;
                }
                catch (AuthException)
                {
                    throw;
                }
                catch (ApiException)
                {
                    throw;
                }
                Remove(entry.IdempotencyKey);
                sent++;
            }
            return (sent, Items.Count);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>ApiClient로 전송하는 편의 오버로드.</summary>
    public Task<(int Sent, int Remaining)> FlushAsync(ApiClient client) =>
        FlushAsync(entry => string.IsNullOrWhiteSpace(entry.AttemptId)
            ? client.LogCallAsync(entry.LeadId, entry.ResultCode,
                entry.TalkSeconds, entry.Memo, entry.CallbackAt, entry.IdempotencyKey,
                entry.AppointmentAt)
            : client.LogCallAttemptAsync(entry.AttemptId, entry.ResultCode,
                entry.TalkSeconds, entry.Memo, entry.CallbackAt, entry.AppointmentAt));
}
