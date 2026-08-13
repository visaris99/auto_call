using Core;
using Xunit;

namespace Tests;

public class ErrorReportTests
{
    [Fact]
    public void 네트워크_예외는_CRM01로_매핑되고_엔드포인트를_보존한다()
    {
        var ex = new NetworkException() { Endpoint = "/calls/result" };
        ErrorReport report = ErrorCatalog.FromApi(ex);

        Assert.Equal("CRM-01", report.Code);
        Assert.Contains(report.Details, d => d.Key == "엔드포인트" && d.Value == "/calls/result");
        Assert.False(string.IsNullOrWhiteSpace(report.NextAction));
    }

    [Theory]
    [InlineData(500, "INTERNAL", "CRM-04")]
    [InlineData(400, "VALIDATION", "CRM-03")]
    [InlineData(200, "INVALID_RESPONSE", "CRM-05")]
    [InlineData(200, "QUEUE_TOO_LARGE", "CRM-05")]
    public void 상태와_코드에_따라_범주코드가_결정된다(int status, string code, string expected)
    {
        var ex = new ApiException(code, "메시지", status);
        Assert.Equal(expected, ErrorCatalog.FromApi(ex).Code);
    }

    [Fact]
    public void 야간차단과_수신거부는_전용코드다()
    {
        Assert.Equal("CRM-06", ErrorCatalog.FromApi(new NightBlockedException("NIGHT_BLOCKED", "야간 제한", 403)).Code);
        Assert.Equal("CRM-07", ErrorCatalog.FromApi(new DncBlockedException("DNC_BLOCKED", "수신거부", 403)).Code);
    }

    [Fact]
    public void 인증예외는_CRM02다()
    {
        Assert.Equal("CRM-02", ErrorCatalog.FromApi(new AuthException("UNAUTHENTICATED", "세션 만료", 401)).Code);
    }

    [Fact]
    public void 보고텍스트는_코드_시각_버전_상세를_포함한다()
    {
        var report = ErrorCatalog.Adb(ErrorCatalog.AdbHangupFailed,
            "종료 명령을 보내지 못했습니다.", "휴대폰에서 통화를 종료하세요.", "R3CN30ABCD");
        string text = report.ToReportText("2.6.0", "agent01",
            new DateTimeOffset(2026, 8, 13, 14, 30, 0, TimeSpan.FromHours(9)));

        Assert.Contains("ADB-04", text);
        Assert.Contains("2026-08-13 14:30:00", text);
        Assert.Contains("v2.6.0", text);
        Assert.Contains("agent01", text);
        Assert.Contains("R3CN30ABCD", text);
    }

    [Fact]
    public void 빈_상세값은_보고텍스트에서_생략된다()
    {
        var report = ErrorCatalog.Adb(ErrorCatalog.AdbNoDevice, "장치 없음", "USB 연결 확인", serial: null);
        string text = report.ToReportText("2.6.0", null, DateTimeOffset.Now);
        Assert.DoesNotContain("장치:", text);
        Assert.DoesNotContain("계정:", text);
    }
}
