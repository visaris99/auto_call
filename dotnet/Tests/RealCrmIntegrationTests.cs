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

        // 임의 리드 접근 거부
        var denied = await Assert.ThrowsAnyAsync<ApiException>(
            () => client.RevealAsync("00000000-0000-4000-8000-000000000000"));
        Assert.True(denied.Code is "NOT_FOUND" or "VALIDATION");

        await client.LogoutAsync();
        await Assert.ThrowsAsync<AuthException>(() => client.MeAsync());
    }
}
