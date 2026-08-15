using Core;
using Xunit;

namespace Tests;

/// <summary>실제 CRM(dev) 대상 통합 검증 — TM_ITEST_URL 환경변수가 있을 때만 실행.
/// 사용: TM_ITEST_URL=http://127.0.0.1:3005 dotnet test --filter RealCrm</summary>
public class RealCrmIntegrationTests
{
    [Fact]
    public async Task FullCycle_AgainstRealCrm()
    {
        string? baseUrl = Environment.GetEnvironmentVariable("TM_ITEST_URL");
        if (string.IsNullOrEmpty(baseUrl))
            return; // 통합 서버 미지정 — 단위 테스트 실행에서는 건너뜀

        var client = new ApiClient(baseUrl);

        var user = await client.LoginAsync("tm1", "test1234!");
        Assert.Contains(user.Roles, r => r is "TM" or "SALES");

        var me = await client.MeAsync();
        Assert.Equal("tm1", me.LoginId);

        var items = await client.QueueAsync();
        Assert.NotEmpty(items);
        var lead = items[0];
        Assert.Contains("****", lead.PhoneMasked);

        string phone = await client.RevealAsync(lead.Id);
        Assert.True(phone.Length >= 9 && phone.All(char.IsDigit));
        Assert.Equal(lead.PhoneMasked[^4..], phone[^4..]);

        // 콜 기록 + 멱등키 중복 재전송 무해
        string key = Guid.NewGuid().ToString();
        var first = await client.LogCallAsync(lead.Id, "INTERESTED", 42, "C# 통합테스트", null, key);
        Assert.True(first.Ok);
        Assert.Equal("INTERESTED", first.Lead.Status);
        var second = await client.LogCallAsync(lead.Id, "INTERESTED", 42, "C# 통합테스트", null, key);
        Assert.Equal("INTERESTED", second.Lead.Status);

        // 서버 인사이트 — 상담 이력·오늘 실적 (2026-08-14 지시서 검증)
        List<CallHistoryItem>? history = await client.HistoryAsync(lead.Id);
        Assert.NotNull(history);
        Assert.NotEmpty(history!);
        Assert.Equal("INTERESTED", history![0].ResultCode);
        Assert.Equal(42, history[0].TalkSeconds);

        TodayStats? today = await client.TodayAsync();
        Assert.NotNull(today);
        Assert.True(today!.Dials >= 1);
        Assert.True(today.TalkSeconds >= 42);
        Assert.NotNull(today.ByResult);
        Assert.True(today.ByResult!.GetValueOrDefault("INTERESTED") >= 1);

        // 타인·미존재 리드 이력은 404 존재 은닉 → 클라이언트는 null 폴백
        Assert.Null(await client.HistoryAsync("00000000-0000-4000-8000-000000000000"));

        // /version 원격 기능 게이트 (2026-08-15 지시서 검증)
        // — dev CRM을 DIALER_FEATURE_SERVER_INSIGHTS=1 로 띄운 상태여야 통과
        VersionInfo? version = await client.CheckVersionAsync();
        Assert.NotNull(version);
        Assert.True(version!.HasFeature("serverInsights"));
        Assert.False(version.HasFeature("nonexistentFeature"));

        // 임의 리드 접근 거부
        var denied = await Assert.ThrowsAnyAsync<ApiException>(
            () => client.RevealAsync("00000000-0000-4000-8000-000000000000"));
        Assert.True(denied.Code is "NOT_FOUND" or "VALIDATION");

        await client.LogoutAsync();
        await Assert.ThrowsAsync<AuthException>(() => client.MeAsync());
    }
}
