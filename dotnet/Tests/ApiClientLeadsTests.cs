using Core;
using System.Text.Json;
using Xunit;

namespace Tests;

public class ApiClientLeadsTests
{
    private const string AttemptId = "2f1e8918-8dc3-4aef-ab97-a4513ca0f649";
    private const string CallbackKey = "10f03171-b9d4-4cab-b63c-cb00451ee959";
    private const string AppointmentKey = "a3776489-23ec-4edf-8201-abb0614d62a3";
    private const string NoAnswerKey = "f1e2c58c-92fe-4b55-a3db-81aac813b73e";

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
    public async Task QueueAll_FollowsPages_AndDeduplicatesItems()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("GET", "/api/v1/leads/queue", (_, path, _, _) =>
        {
            if (path.Contains("offset=2", StringComparison.Ordinal))
            {
                return (200, new
                {
                    serverTime = "2026-07-05T10:00:01+09:00",
                    nextOffset = (int?)null,
                    items = new[]
                    {
                        Lead,
                        new
                        {
                            id = "L2", name = "이영희", phoneMasked = "010-****-5678",
                            status = "ASSIGNED", nextCallAt = (string?)null,
                            memo = (string?)null, updatedAt = "2026-07-04T10:01:00+09:00",
                        },
                    },
                });
            }

            return (200, new
            {
                serverTime = "2026-07-05T10:00:00+09:00",
                nextOffset = (int?)2,
                items = new[] { Lead },
            });
        });

        var items = await client.QueueAllAsync(pageSize: 2);

        Assert.Equal(new[] { "L1", "L2" }, items.Select(item => item.Id));
        Assert.Equal(2, crm.Requests.Count(request =>
            request.Path.StartsWith("/api/v1/leads/queue", StringComparison.Ordinal)));
        Assert.Contains(crm.Requests, request => request.Path == "/api/v1/leads/queue?limit=2&offset=2");
    }

    [Fact]
    public async Task ResolveAssignedLead_SendsPhone_AndReturnsLead()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("POST", "/api/v1/leads/resolve-phone", 200, new { lead = Lead });

        LeadItem lead = await client.ResolveAssignedLeadAsync("01012341234");

        Assert.Equal("L1", lead.Id);
        Assert.Equal("01012341234", crm.Last.Body!.Value.GetProperty("phone").GetString());
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
    public async Task RevealLead_ReturnsFullAssignedContact()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("POST", "/api/v1/leads/L1/reveal", 200,
            new { name = "김철수", phone = "01012341234" });

        LeadReveal contact = await client.RevealLeadAsync("L1");

        Assert.Equal("김철수", contact.Name);
        Assert.Equal("01012341234", contact.Phone);
        Assert.Equal("담당 리드 연락처 확인",
            crm.Last.Body!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task StartCallAttempt_SendsDeviceAndAttemptId()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("POST", "/api/v1/call-attempts", 200, new
        {
            attemptId = AttemptId,
            leadId = "L1",
            phone = "01012341234",
            expiresAt = "2026-07-10T15:30:00+09:00",
        });

        CallAttemptResponse response = await client.StartCallAttemptAsync(
            "L1", "pc-abc", "R3CN123", AttemptId);

        Assert.Equal(AttemptId, response.AttemptId);
        Assert.Equal("01012341234", response.Phone);
        var (_, path, headers, body) = crm.Last;
        Assert.Equal("/api/v1/call-attempts", path);
        Assert.Equal(AttemptId, headers["Idempotency-Key"]);
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
        crm.Set("POST", $"/api/v1/call-attempts/{AttemptId}/cancel", 200,
            new { ok = true, attemptId = AttemptId });

        await client.CancelCallAttemptAsync(AttemptId);

        Assert.Equal($"/api/v1/call-attempts/{AttemptId}/cancel", crm.Last.Path);
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
            "2026-07-06T14:30:00+09:00", CallbackKey);
        Assert.True(res.Ok);
        Assert.Equal("CALLBACK", res.Lead.Status);
        var (_, _, headers, body) = crm.Last;
        Assert.Equal(CallbackKey, headers["Idempotency-Key"]);
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
        await client.LogCallAsync("L1", "APPOINTMENT", 90, null, null, AppointmentKey,
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
        crm.Set("POST", $"/api/v1/call-attempts/{AttemptId}/result", 200, new
        {
            ok = true,
            attemptId = AttemptId,
            lead = new { id = "L1", status = "NOANSWER", nextCallAt = (string?)null },
        });

        CallResponse response = await client.LogCallAttemptAsync(
            AttemptId, "NOANSWER", 18, "부재", null);

        Assert.True(response.Ok);
        Assert.Equal($"/api/v1/call-attempts/{AttemptId}/result", crm.Last.Path);
        Assert.False(crm.Last.Headers.ContainsKey("Idempotency-Key"));
        Assert.Equal(18, crm.Last.Body!.Value.GetProperty("talkSeconds").GetInt32());
    }

    [Theory]
    [InlineData("CALLBACK", "2026-08-04T14:30:00+09:00", null)]
    [InlineData("APPOINTMENT", null, "2026-08-04T14:30:00+09:00")]
    public async Task LogCallAttempt_SendsOnlyDateAllowedForResult(
        string resultCode,
        string? callbackAt,
        string? appointmentAt)
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("POST", $"/api/v1/call-attempts/{AttemptId}/result", 200, new
        {
            ok = true,
            attemptId = AttemptId,
            lead = new { id = "L1", status = resultCode, nextCallAt = (string?)null },
        });

        await client.LogCallAttemptAsync(
            AttemptId, resultCode, 60, null, callbackAt, appointmentAt);

        var body = crm.Last.Body!.Value;
        Assert.Equal(resultCode, body.GetProperty("resultCode").GetString());
        if (resultCode == "CALLBACK")
        {
            Assert.Equal(callbackAt, body.GetProperty("callbackAt").GetString());
            Assert.False(body.TryGetProperty(
                "appointmentAt", out JsonElement ignoredAppointment));
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty("callbackAt").ValueKind);
            Assert.Equal(appointmentAt, body.GetProperty("appointmentAt").GetString());
        }
    }

    [Fact]
    public async Task LogCallAttempt_InvalidConditionalDate_FailsBeforeHttpRequest()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        int requestsBefore = crm.Requests.Count;

        ApiException error = await Assert.ThrowsAsync<ApiException>(() =>
            client.LogCallAttemptAsync(AttemptId, "CALLBACK", 60, null, null));

        Assert.Equal("VALIDATION", error.Code);
        Assert.Equal(requestsBefore, crm.Requests.Count);
    }

    [Fact]
    public async Task LogCall_NightBlocked_Throws()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("POST", "/api/v1/leads/L1/call", 423,
            new { error = new { code = "NIGHT_BLOCKED", message = "야간에는 발신할 수 없습니다." } });
        await Assert.ThrowsAsync<NightBlockedException>(
            () => client.LogCallAsync("L1", "NOANSWER", 0, null, null, NoAnswerKey));
    }

    [Fact]
    public async Task StartCallAttempt_DncBlocked423_ThrowsDedicatedException()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("POST", "/api/v1/call-attempts", 423, new
        {
            error = new
            {
                code = "DNC_BLOCKED",
                message = "수신거부 등록 번호에는 발신할 수 없습니다.",
            },
        });

        DncBlockedException error = await Assert.ThrowsAsync<DncBlockedException>(() =>
            client.StartCallAttemptAsync("L1", "pc-abc", "R3CN123", AttemptId));

        Assert.Equal(423, error.HttpStatus);
        Assert.Equal("DNC_BLOCKED", error.Code);
        Assert.Equal("수신거부 고객 — 발신 불가, 큐를 새로고침하세요",
            DncBlockedException.UserMessage);
    }

    [Fact]
    public async Task UpdateLeadName_SendsPatch_WithNameBody()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("PATCH", "/api/v1/leads/L1/name", 200, new { ok = true });
        await client.UpdateLeadNameAsync("L1", "홍길동");
        var (method, path, _, body) = crm.Last;
        Assert.Equal("PATCH", method);
        Assert.Equal("/api/v1/leads/L1/name", path);
        Assert.Equal("홍길동", body!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task UpdateLeadName_NotOwnLead_Throws404()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("PATCH", "/api/v1/leads/L1/name", 404,
            new { error = new { code = "NOT_FOUND", message = "본인에게 배정된 리드가 아닙니다." } });
        var ex = await Assert.ThrowsAsync<ApiException>(
            () => client.UpdateLeadNameAsync("L1", "홍길동"));
        Assert.Equal(404, ex.HttpStatus);
        Assert.Equal("NOT_FOUND", ex.Code);
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
    public async Task CheckVersion_ParsesRemoteFeatureGate()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("GET", "/api/v1/version", 200,
            new
            {
                minVersion = "2.0.0",
                latestVersion = "2.8.1",
                downloadUrl = (string?)null,
                features = new Dictionary<string, bool>
                {
                    ["serverInsights"] = true,
                    ["somethingElse"] = false,
                },
            });
        var info = await client.CheckVersionAsync();
        Assert.True(info!.HasFeature(AppConfig.ServerInsightsFeatureName));
        Assert.False(info.HasFeature("somethingElse"));
        Assert.False(info.HasFeature("unknown"));
    }

    [Fact]
    public void VersionInfo_HasFeature_FalseWithoutFeaturesField()
    {
        var info = new VersionInfo("2.0.0", "2.8.1", null);
        Assert.False(info.HasFeature(AppConfig.ServerInsightsFeatureName));
    }

    [Fact]
    public async Task Heartbeat_SendsDeviceStatus()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("POST", "/api/v1/devices/heartbeat", 200, new
        {
            ok = true,
            serverTime = "2026-08-03T17:20:00+09:00",
        });
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
