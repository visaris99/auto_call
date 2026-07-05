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
}
