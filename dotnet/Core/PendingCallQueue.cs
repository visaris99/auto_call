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
    [property: JsonPropertyName("callback_at")] string? CallbackAt);

public sealed class PendingCallQueue
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();
    private List<PendingCall> _items;

    public PendingCallQueue(string? path = null)
    {
        _path = path ?? Path.Combine(AppConfig.ConfigDir(), "pending_calls.json");
        try
        {
            _items = JsonSerializer.Deserialize<List<PendingCall>>(File.ReadAllText(_path), Json)
                     ?? new List<PendingCall>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _items = new List<PendingCall>();
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
            _items.Add(item);
            SaveLocked();
        }
    }

    public void Remove(string idempotencyKey)
    {
        lock (_lock)
        {
            _items = _items.Where(x => x.IdempotencyKey != idempotencyKey).ToList();
            SaveLocked();
        }
    }

    private void SaveLocked() =>
        AppConfig.AtomicWrite(_path, JsonSerializer.Serialize(_items, Json));

    /// <summary>(성공 수, 잔여 수). Network/NightBlocked→중단(다음에 재시도),
    /// Auth→re-throw(재로그인 필요), 그 외 ApiException→해당 건 폐기(재시도 무의미).</summary>
    public async Task<(int Sent, int Remaining)> FlushAsync(Func<PendingCall, Task> sender)
    {
        int sent = 0;
        foreach (var entry in Items)
        {
            try
            {
                await sender(entry).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NetworkException or NightBlockedException)
            {
                break;
            }
            catch (AuthException)
            {
                throw;
            }
            catch (ApiException)
            {
                Remove(entry.IdempotencyKey);
                continue;
            }
            Remove(entry.IdempotencyKey);
            sent++;
        }
        return (sent, Items.Count);
    }

    /// <summary>ApiClient로 전송하는 편의 오버로드.</summary>
    public Task<(int Sent, int Remaining)> FlushAsync(ApiClient client) =>
        FlushAsync(entry => client.LogCallAsync(entry.LeadId, entry.ResultCode,
            entry.TalkSeconds, entry.Memo, entry.CallbackAt, entry.IdempotencyKey));
}
