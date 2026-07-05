// 테스트용 가짜 CRM 서버 — 파이썬 tests/conftest.py의 MockCRM과 동일 의미론.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Tests;

public sealed class MockCrm : IDisposable
{
    public delegate (int Status, object? Body) Handler(
        string method, string path, Dictionary<string, string> headers, JsonElement? body);

    private readonly HttpListener _listener;

    public string Url { get; }

    /// <summary>라우트: (METHOD, 쿼리스트링 제외 경로) → (status, body) 또는 Handler.</summary>
    public ConcurrentDictionary<(string Method, string Path), object> Routes { get; } = new();

    public ConcurrentQueue<(string Method, string Path, Dictionary<string, string> Headers, JsonElement? Body)>
        Requests { get; } = new();

    public MockCrm()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public void Set(string method, string path, int status, object? body) =>
        Routes[(method, path)] = (status, body);

    public void Set(string method, string path, Handler handler) =>
        Routes[(method, path)] = handler;

    public (string Method, string Path, Dictionary<string, string> Headers, JsonElement? Body) Last =>
        Requests.Last();

    private async Task LoopAsync()
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            try { Handle(ctx); }
            catch { /* 테스트 서버 — 개별 요청 실패 무시 */ }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        string method = ctx.Request.HttpMethod;
        string rawPath = ctx.Request.Url!.PathAndQuery;
        string path = rawPath.Split('?')[0];

        JsonElement? body = null;
        if (ctx.Request.HasEntityBody)
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            string raw = reader.ReadToEnd();
            if (raw.Length > 0)
                body = JsonDocument.Parse(raw).RootElement.Clone();
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? key in ctx.Request.Headers.AllKeys)
            if (key != null)
                headers[key] = ctx.Request.Headers[key]!;

        Requests.Enqueue((method, rawPath, headers, body));

        int status;
        object? payload;
        if (!Routes.TryGetValue((method, path), out var route))
        {
            status = 404;
            payload = new { error = new { code = "NOT_FOUND", message = "no route" } };
        }
        else if (route is Handler fn)
        {
            (status, payload) = fn(method, rawPath, headers, body);
        }
        else
        {
            (status, payload) = ((int, object?))route;
        }

        ctx.Response.StatusCode = status;
        if (payload != null)
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(payload);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data);
        }
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch { /* ignore */ }
    }
}
