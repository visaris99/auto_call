using Core;
using Xunit;

namespace Tests;

public class HistoryTodayTests
{
    private static async Task<(MockCrm Crm, ApiClient Client)> LoggedInAsync()
    {
        var crm = new MockCrm();
        ApiClientTests.SetLoginOk(crm);
        var client = new ApiClient(crm.Url);
        await client.LoginAsync("hong", "pw");
        return (crm, client);
    }

    [Fact]
    public async Task History_ReturnsItems()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("GET", "/api/v1/leads/L1/history", 200, new
        {
            items = new[]
            {
                new { resultCode = "CALLBACK", memo = "다음주 재상담", talkSeconds = 154,
                      calledAt = "2026-07-03T14:20:00+09:00",
                      callbackAt = "2026-07-06T14:30:00+09:00" },
            },
        });
        var items = await client.HistoryAsync("L1", 5);
        var item = Assert.Single(items!);
        Assert.Equal("CALLBACK", item.ResultCode);
        Assert.Equal(154, item.TalkSeconds);
        Assert.Equal("/api/v1/leads/L1/history?limit=5", crm.Last.Path);
    }

    [Fact]
    public async Task History_Returns_Null_When_Missing()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        Assert.Null(await client.HistoryAsync("L1")); // 404 → null (서버 미구현/타인 리드)
    }

    [Fact]
    public async Task Today_ReturnsStats()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        crm.Set("GET", "/api/v1/me/today", 200, new
        {
            date = "2026-07-05", dials = 47, talkSeconds = 5820,
            byResult = new Dictionary<string, int> { ["WON"] = 2, ["NOANSWER"] = 20 },
        });
        var stats = await client.TodayAsync();
        Assert.Equal(47, stats!.Dials);
        Assert.Equal(2, stats.ByResult!["WON"]);
    }

    [Fact]
    public async Task Today_Returns_Null_When_Missing()
    {
        var (crm, client) = await LoggedInAsync();
        using var _ = crm;
        Assert.Null(await client.TodayAsync());
    }
}
