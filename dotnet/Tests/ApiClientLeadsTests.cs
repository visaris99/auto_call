using Core;
using Xunit;

namespace Tests;

public class ApiClientLeadsTests
{
    private static readonly object Lead = new
    {
        id = "L1",
        name = "김철수",
        phoneMasked = "010-****-1234",
        status = "INTERESTED",
        nextCallAt = (string?)null,
        memo = "5시 이후 선호",
        updatedAt = "2026-07-04T10:00:00+09:00",
    };

    private static async Task<(MockCrm Crm, ApiClient Client)> LoggedInAsync()
    {
        var crm = new MockCrm();
        ApiClientTests.SetLoginOk(crm);
        var client = new ApiClient(crm.Url);
        await client.LoginAsync("hong", "pw");
        return (crm, client);
    }

    [Fact]
    public async Task Queue_ReturnsItems_AndSendsLimit()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("GET", "/api/v1/leads/queue", 200,
            new { serverTime = "2026-07-05T10:00:00+09:00", items = new[] { Lead } });
        var items = await client.QueueAsync(limit: 20);
        var item = Assert.Single(items);
        Assert.Equal("김철수", item.Name);
        Assert.Equal("010-****-1234", item.PhoneMasked);
        Assert.Equal("INTERESTED", item.Status);
        Assert.Null(item.NextCallAt);
        var (_, path, headers, _) = crm.Last;
        Assert.Equal("/api/v1/leads/queue?limit=20", path);
        Assert.Equal("Bearer tok1", headers["Authorization"]);
    }

    [Fact]
    public async Task Queue_SendsStatusFilters()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("GET", "/api/v1/leads/queue", 200,
            new { serverTime = "2026-07-05T10:00:00+09:00", items = Array.Empty<object>() });

        await client.QueueAsync(limit: 500, statuses: new[] { "NEW", "ASSIGNED" });

        var (_, path, _, _) = crm.Last;
        Assert.Equal("/api/v1/leads/queue?limit=500&status=NEW&status=ASSIGNED", path);
    }

    [Fact]
    public async Task Reveal_SendsReason_ReturnsPhone()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("POST", "/api/v1/leads/L1/reveal", 200, new { phone = "01012341234" });
        Assert.Equal("01012341234", await client.RevealAsync("L1"));
        Assert.Equal("TM 발신", crm.Last.Body!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task StartCallAttempt_SendsDeviceAndAttemptId()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        const string attemptId = "2f1e8918-8dc3-4aef-ab97-a4513ca0f649";
        crm.Set("POST", "/api/v1/call-attempts", 200, new
        {
            attemptId,
            leadId = "L1",
            phone = "01012341234",
            expiresAt = "2026-07-10T15:30:00+09:00",
        });

        CallAttemptResponse response = await client.StartCallAttemptAsync(
            "L1", "pc-abc", "R3CN123", attemptId);

        Assert.Equal(attemptId, response.AttemptId);
        Assert.Equal("01012341234", response.Phone);
        var (_, path, headers, body) = crm.Last;
        Assert.Equal("/api/v1/call-attempts", path);
        Assert.Equal(attemptId, headers["Idempotency-Key"]);
        Assert.Equal("L1", body!.Value.GetProperty("leadId").GetString());
        Assert.Equal("pc-abc", body.Value.GetProperty("deviceCode").GetString());
        Assert.Equal("R3CN123", body.Value.GetProperty("deviceSerial").GetString());
        Assert.Equal("ADB", body.Value.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task CancelCallAttempt_UsesAttemptResource()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("POST", "/api/v1/call-attempts/A1/cancel", 200,
            new { ok = true, attemptId = "A1" });

        await client.CancelCallAttemptAsync("A1");

        Assert.Equal("/api/v1/call-attempts/A1/cancel", crm.Last.Path);
    }

    [Fact]
    public async Task LogCall_SendsIdempotencyKey_AndCamelCaseBody()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("POST", "/api/v1/leads/L1/call", 200, new
        {
            ok = true,
            lead = new { id = "L1", status = "CALLBACK", nextCallAt = "2026-07-06T14:30:00+09:00" },
        });
        var res = await client.LogCallAsync("L1", "CALLBACK", 154, "재상담 원함",
            "2026-07-06T14:30:00+09:00", "key-1");
        Assert.True(res.Ok);
        Assert.Equal("CALLBACK", res.Lead.Status);
        var (_, _, headers, body) = crm.Last;
        Assert.Equal("key-1", headers["Idempotency-Key"]);
        Assert.Equal("CALLBACK", body!.Value.GetProperty("resultCode").GetString());
        Assert.Equal(154, body.Value.GetProperty("talkSeconds").GetInt32());
        Assert.Equal("재상담 원함", body.Value.GetProperty("memo").GetString());
        Assert.Equal("2026-07-06T14:30:00+09:00", body.Value.GetProperty("callbackAt").GetString());
        Assert.False(body.Value.TryGetProperty("appointmentAt", out var ignored));
    }

    [Fact]
    public async Task LogCall_SendsAppointmentAt()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("POST", "/api/v1/leads/L1/call", 200, new
        {
            ok = true,
            lead = new { id = "L1", status = "APPOINTMENT", nextCallAt = (string?)null },
        });
        await client.LogCallAsync("L1", "APPOINTMENT", 90, null, null, "key-appt",
            "2026-07-06T11:00:00+09:00");
        var body = crm.Last.Body!.Value;
        Assert.Equal("APPOINTMENT", body.GetProperty("resultCode").GetString());
        Assert.Equal("2026-07-06T11:00:00+09:00", body.GetProperty("appointmentAt").GetString());
    }

    [Fact]
    public async Task LogCallAttempt_UsesAttemptResultEndpoint()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("POST", "/api/v1/call-attempts/A1/result", 200, new
        {
            ok = true,
            attemptId = "A1",
            lead = new { id = "L1", status = "NOANSWER", nextCallAt = (string?)null },
        });

        CallResponse response = await client.LogCallAttemptAsync(
            "A1", "NOANSWER", 18, "부재", null);

        Assert.True(response.Ok);
        Assert.Equal("/api/v1/call-attempts/A1/result", crm.Last.Path);
        Assert.False(crm.Last.Headers.ContainsKey("Idempotency-Key"));
        Assert.Equal(18, crm.Last.Body!.Value.GetProperty("talkSeconds").GetInt32());
    }

    [Fact]
    public async Task LogCall_NightBlocked_Throws()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("POST", "/api/v1/leads/L1/call", 423,
            new { error = new { code = "NIGHT_BLOCKED", message = "야간에는 발신할 수 없습니다." } });
        await Assert.ThrowsAsync<NightBlockedException>(
            () => client.LogCallAsync("L1", "NOANSWER", 0, null, null, "key-2"));
    }

    [Fact]
    public async Task CheckVersion_ReturnsNull_WhenMissing()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        Assert.Null(await client.CheckVersionAsync()); // 라우트 없음 → 404 → null
    }

    [Fact]
    public async Task CheckVersion_ReturnsPayload()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("GET", "/api/v1/version", 200,
            new
            {
                minVersion = "2.0.0",
                latestVersion = "2.4.1",
                downloadUrl = "https://crm.milestone-sales.xyz/downloads/setup.exe",
                sha256 = new string('a', 64),
                size = 1234,
                publishedAt = "2026-07-11T00:00:00Z",
                keyId = "0123456789abcdef",
                signature = "signed",
            });
        var info = await client.CheckVersionAsync();
        Assert.Equal("2.4.1", info!.LatestVersion);
        Assert.Equal(1234, info.Size);
        Assert.Equal("0123456789abcdef", info.KeyId);
        Assert.Equal("signed", info.Signature);
    }

    [Fact]
    public async Task CheckVersion_ReturnsNull_ForMalformedPayload()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("GET", "/api/v1/version", 200, "not-a-version-object");

        Assert.Null(await client.CheckVersionAsync());
    }

    [Fact]
    public async Task Heartbeat_SendsDeviceStatus()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("POST", "/api/v1/devices/heartbeat", 204, null);
        await client.HeartbeatAsync("pc-abc", "2.2.0", adbConnected: true, lastError: "last");
        var (_, path, headers, body) = crm.Last;
        Assert.Equal("/api/v1/devices/heartbeat", path);
        Assert.Equal("Bearer tok1", headers["Authorization"]);
        Assert.Equal("pc-abc", body!.Value.GetProperty("deviceCode").GetString());
        Assert.Equal("2.2.0", body.Value.GetProperty("clientVersion").GetString());
        Assert.True(body.Value.GetProperty("adbConnected").GetBoolean());
        Assert.Equal("last", body.Value.GetProperty("lastError").GetString());
    }
}
