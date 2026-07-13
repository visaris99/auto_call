// milestone-crm /api/v1 클라이언트 — 설계서 3장(API 계약) 구현.
using System.Text;
using System.Text.Json;

namespace Core;

public sealed class ApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private string? _token;

    public string BaseUrl { get; set; }
    public UserInfo? User { get; private set; }
    public bool IsAuthenticated => _token != null;

    public ApiClient(string baseUrl, TimeSpan? timeout = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
    }

    private async Task<JsonElement?> RequestAsync(HttpMethod method, string path,
        object? body = null, IDictionary<string, string>? headers = null, bool auth = true)
    {
        var req = new HttpRequestMessage(method, $"{BaseUrl}/api/v1{path}");
        if (auth)
        {
            if (_token is null)
                throw new AuthException("UNAUTHENTICATED", "로그인이 필요합니다.", 401);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_token}");
        }
        if (headers != null)
            foreach (var (key, value) in headers)
                req.Headers.TryAddWithoutValidation(key, value);
        if (body != null)
            req.Content = new StringContent(
                JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new NetworkException();
        }

        using (res)
        {
            if ((int)res.StatusCode == 204)
                return null;

            string text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonElement data;
            try
            {
                using var doc = JsonDocument.Parse(text);
                data = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new ApiException("INTERNAL",
                    $"서버 응답 오류(HTTP {(int)res.StatusCode})", (int)res.StatusCode);
            }

            if (res.IsSuccessStatusCode)
                return data;

            string code = "INTERNAL";
            string message = "알 수 없는 오류가 발생했습니다.";
            if (data.TryGetProperty("error", out var err))
            {
                if (err.TryGetProperty("code", out var c)) code = c.GetString() ?? code;
                if (err.TryGetProperty("message", out var m)) message = m.GetString() ?? message;
            }
            throw ErrorFor((int)res.StatusCode, code, message);
        }
    }

    private static ApiException ErrorFor(int status, string code, string message) => code switch
    {
        "MFA_REQUIRED" => new MfaRequiredException(code, message, status),
        "NIGHT_BLOCKED" => new NightBlockedException(code, message, status),
        "UNAUTHENTICATED" when status == 401 => new AuthException(code, message, status),
        _ => new ApiException(code, message, status),
    };

    // ---- 인증 ----

    public async Task<UserInfo> LoginAsync(string loginId, string password, string? code = null)
    {
        object body = code is null
            ? new { loginId, password }
            : new { loginId, password, code };
        var data = await RequestAsync(HttpMethod.Post, "/auth/login", body, auth: false)
            .ConfigureAwait(false);
        var login = data!.Value.Deserialize<LoginResponse>(Json)!;
        _token = login.Token;
        User = login.User;
        return login.User;
    }

    public async Task LogoutAsync()
    {
        if (_token != null)
        {
            try
            {
                await RequestAsync(HttpMethod.Post, "/auth/logout").ConfigureAwait(false);
            }
            catch (ApiException)
            {
                // 로그아웃 실패는 무시 — 로컬 토큰만 버리면 됨
            }
        }
        _token = null;
        User = null;
    }

    public async Task<UserInfo> MeAsync()
    {
        var data = await RequestAsync(HttpMethod.Get, "/me").ConfigureAwait(false);
        return data!.Value.GetProperty("user").Deserialize<UserInfo>(Json)!;
    }

    // ---- 리드/콜 ----

    public async Task<List<LeadItem>> QueueAsync(int limit = 50, IEnumerable<string>? statuses = null)
    {
        QueueResponse page = await QueuePageAsync(limit, statuses: statuses).ConfigureAwait(false);
        return page.Items;
    }

    public async Task<QueueResponse> QueuePageAsync(int limit = 50, int offset = 0,
        IEnumerable<string>? statuses = null)
    {
        string path = $"/leads/queue?limit={limit}";
        if (offset > 0)
            path += $"&offset={offset}";
        if (statuses != null)
        {
            foreach (string status in statuses.Where(s => !string.IsNullOrWhiteSpace(s)))
                path += $"&status={Uri.EscapeDataString(status.Trim())}";
        }

        var data = await RequestAsync(HttpMethod.Get, path)
            .ConfigureAwait(false);
        return data!.Value.Deserialize<QueueResponse>(Json)!;
    }

    public async Task<List<LeadItem>> QueueAllAsync(int pageSize = 500,
        IEnumerable<string>? statuses = null, int maxPages = 100)
    {
        string[]? statusValues = statuses?.ToArray();
        var items = new List<LeadItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int offset = 0;

        for (int pageNumber = 0; pageNumber < maxPages; pageNumber++)
        {
            QueueResponse page = await QueuePageAsync(pageSize, offset, statusValues)
                .ConfigureAwait(false);
            foreach (LeadItem item in page.Items)
            {
                if (seen.Add(item.Id))
                    items.Add(item);
            }

            if (page.NextOffset is null)
                return items;
            if (page.NextOffset <= offset)
                throw new ApiException("INVALID_RESPONSE", "CRM 큐 페이지 응답이 올바르지 않습니다.", 200);
            offset = page.NextOffset.Value;
        }

        throw new ApiException("QUEUE_TOO_LARGE", "CRM 큐 페이지 수가 안전 한도를 초과했습니다.", 200);
    }

    /// <summary>발신 직전 1건 복호화. 평문은 반환값으로만 다루고 저장하지 않는다.</summary>
    public async Task<string> RevealAsync(string leadId, string reason = "TM 발신")
    {
        LeadReveal contact = await RevealLeadAsync(leadId, reason).ConfigureAwait(false);
        return contact.Phone;
    }

    public async Task<LeadReveal> RevealLeadAsync(string leadId, string reason = "담당 리드 연락처 확인")
    {
        var data = await RequestAsync(HttpMethod.Post, $"/leads/{leadId}/reveal",
            new { reason }).ConfigureAwait(false);
        return data!.Value.Deserialize<LeadReveal>(Json)!;
    }

    public async Task<CallAttemptResponse> StartCallAttemptAsync(
        string leadId,
        string deviceCode,
        string deviceSerial,
        string attemptId)
    {
        var data = await RequestAsync(HttpMethod.Post, "/call-attempts",
            new { leadId, deviceCode, deviceSerial, channel = "ADB" },
            new Dictionary<string, string> { ["Idempotency-Key"] = attemptId })
            .ConfigureAwait(false);
        return data!.Value.Deserialize<CallAttemptResponse>(Json)!;
    }

    public async Task CancelCallAttemptAsync(string attemptId)
    {
        await RequestAsync(HttpMethod.Post,
            $"/call-attempts/{Uri.EscapeDataString(attemptId)}/cancel")
            .ConfigureAwait(false);
    }

    public async Task<CallResponse> LogCallAsync(string leadId, string resultCode,
        int talkSeconds, string? memo, string? callbackAt, string idempotencyKey,
        string? appointmentAt = null)
    {
        var body = CallResultBody(resultCode, talkSeconds, memo, callbackAt, appointmentAt);
        var data = await RequestAsync(HttpMethod.Post, $"/leads/{leadId}/call", body,
            new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey })
            .ConfigureAwait(false);
        return data!.Value.Deserialize<CallResponse>(Json)!;
    }

    public async Task<CallResponse> LogCallAttemptAsync(string attemptId, string resultCode,
        int talkSeconds, string? memo, string? callbackAt, string? appointmentAt = null)
    {
        var body = CallResultBody(resultCode, talkSeconds, memo, callbackAt, appointmentAt);
        var data = await RequestAsync(HttpMethod.Post,
            $"/call-attempts/{Uri.EscapeDataString(attemptId)}/result", body)
            .ConfigureAwait(false);
        return data!.Value.Deserialize<CallResponse>(Json)!;
    }

    private static Dictionary<string, object?> CallResultBody(string resultCode,
        int talkSeconds, string? memo, string? callbackAt, string? appointmentAt)
    {
        var body = new Dictionary<string, object?>
        {
            ["resultCode"] = resultCode,
            ["talkSeconds"] = talkSeconds,
            ["memo"] = memo,
            ["callbackAt"] = callbackAt,
        };
        if (appointmentAt != null || resultCode == "APPOINTMENT")
            body["appointmentAt"] = appointmentAt;
        return body;
    }

    public async Task HeartbeatAsync(string deviceCode, string clientVersion,
        bool adbConnected, string? lastError)
    {
        await RequestAsync(HttpMethod.Post, "/devices/heartbeat",
            new { deviceCode, clientVersion, adbConnected, lastError }).ConfigureAwait(false);
    }

    /// <summary>리드 상담 이력. 서버 미구현/본인 리드 아님(404)이면 null.</summary>
    public async Task<List<CallHistoryItem>?> HistoryAsync(string leadId, int limit = 10)
    {
        try
        {
            var data = await RequestAsync(HttpMethod.Get, $"/leads/{leadId}/history?limit={limit}")
                .ConfigureAwait(false);
            return data!.Value.GetProperty("items").Deserialize<List<CallHistoryItem>>(Json);
        }
        catch (ApiException ex) when (ex.HttpStatus == 404)
        {
            return null;
        }
    }

    /// <summary>오늘 내 실적 집계. 서버 미구현(404)이면 null — 세션 카운터로 폴백.</summary>
    public async Task<TodayStats?> TodayAsync()
    {
        try
        {
            var data = await RequestAsync(HttpMethod.Get, "/me/today").ConfigureAwait(false);
            return data!.Value.Deserialize<TodayStats>(Json);
        }
        catch (ApiException ex) when (ex.HttpStatus == 404)
        {
            return null;
        }
    }

    /// <summary>서버 미구현(404 포함) 등 어떤 오류든 null — 버전 안내는 선택 기능.</summary>
    public async Task<VersionInfo?> CheckVersionAsync()
    {
        try
        {
            var data = await RequestAsync(HttpMethod.Get, "/version", auth: false)
                .ConfigureAwait(false);
            return data!.Value.Deserialize<VersionInfo>(Json);
        }
        catch (Exception ex) when (ex is ApiException or JsonException)
        {
            return null;
        }
    }
}
