using Core;
using Xunit;

namespace Tests;

public class ApiClientTests
{
    private static readonly object User = new
    {
        id = "u1",
        loginId = "hong",
        name = "홍길동",
        orgName = "강남1팀",
        roles = new[] { "TM" },
        mustChangePassword = false,
    };

    internal static void SetLoginOk(MockCrm crm) =>
        crm.Set("POST", "/api/v1/auth/login", 200,
            new { token = "tok1", expiresAt = "2026-07-05T18:00:00+09:00", user = User });

    [Fact]
    public async Task Login_Success_SetsTokenAndUser()
    {
        using var crm = new MockCrm();
        SetLoginOk(crm);
        var client = new ApiClient(crm.Url);
        var user = await client.LoginAsync("hong", "pw");
        Assert.True(client.IsAuthenticated);
        Assert.Equal("홍길동", user.Name);
        Assert.Equal("강남1팀", client.User!.OrgName);
        var (method, path, _, body) = crm.Last;
        Assert.Equal(("POST", "/api/v1/auth/login"), (method, path));
        Assert.Equal("hong", body!.Value.GetProperty("loginId").GetString());
        Assert.Equal("pw", body.Value.GetProperty("password").GetString());
        Assert.False(body.Value.TryGetProperty("code", out _)); // code 없으면 미포함
    }

    [Fact]
    public async Task Login_WithMfaCode_IncludesCode()
    {
        using var crm = new MockCrm();
        SetLoginOk(crm);
        await new ApiClient(crm.Url).LoginAsync("hong", "pw", "123456");
        Assert.Equal("123456", crm.Last.Body!.Value.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Login_MfaRequired_Throws()
    {
        using var crm = new MockCrm();
        crm.Set("POST", "/api/v1/auth/login", 401,
            new { error = new { code = "MFA_REQUIRED", message = "인증 코드가 필요합니다." } });
        await Assert.ThrowsAsync<MfaRequiredException>(
            () => new ApiClient(crm.Url).LoginAsync("hong", "pw"));
    }

    [Fact]
    public async Task Login_InvalidCredentials_IsPlainApiException()
    {
        // INVALID_CREDENTIALS는 재로그인 유도(AuthException)가 아니라 일반 오류여야 한다.
        using var crm = new MockCrm();
        crm.Set("POST", "/api/v1/auth/login", 401,
            new { error = new { code = "INVALID_CREDENTIALS", message = "아이디 또는 비밀번호가 올바르지 않습니다." } });
        var ex = await Assert.ThrowsAsync<ApiException>(
            () => new ApiClient(crm.Url).LoginAsync("hong", "bad"));
        Assert.IsNotType<AuthException>(ex);
        Assert.Equal("INVALID_CREDENTIALS", ex.Code);
    }

    [Fact]
    public async Task AuthedRequest_SendsBearer()
    {
        using var crm = new MockCrm();
        SetLoginOk(crm);
        crm.Set("GET", "/api/v1/me", 200, new { user = User });
        var client = new ApiClient(crm.Url);
        await client.LoginAsync("hong", "pw");
        var me = await client.MeAsync();
        Assert.Equal("hong", me.LoginId);
        Assert.Equal("Bearer tok1", crm.Last.Headers["Authorization"]);
    }

    [Fact]
    public async Task ExpiredToken_ThrowsAuthException()
    {
        using var crm = new MockCrm();
        SetLoginOk(crm);
        crm.Set("GET", "/api/v1/me", 401,
            new { error = new { code = "UNAUTHENTICATED", message = "세션이 만료되었습니다." } });
        var client = new ApiClient(crm.Url);
        await client.LoginAsync("hong", "pw");
        await Assert.ThrowsAsync<AuthException>(() => client.MeAsync());
    }

    [Fact]
    public async Task RequestWithoutLogin_ThrowsAuthException()
    {
        using var crm = new MockCrm();
        await Assert.ThrowsAsync<AuthException>(() => new ApiClient(crm.Url).MeAsync());
    }

    [Fact]
    public async Task Logout_ClearsToken()
    {
        using var crm = new MockCrm();
        SetLoginOk(crm);
        crm.Set("POST", "/api/v1/auth/logout", 204, null);
        var client = new ApiClient(crm.Url);
        await client.LoginAsync("hong", "pw");
        await client.LogoutAsync();
        Assert.False(client.IsAuthenticated);
        Assert.Null(client.User);
    }

    [Fact]
    public async Task ConnectionRefused_ThrowsNetworkException()
    {
        var client = new ApiClient("http://127.0.0.1:1", TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<NetworkException>(() => client.LoginAsync("hong", "pw"));
    }
}
