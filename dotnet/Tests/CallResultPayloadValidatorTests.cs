using Core;

namespace Tests;

public class CallResultPayloadValidatorTests
{
    private const string ScheduledAt = "2026-08-04T14:30:00+09:00";

    [Theory]
    [InlineData("NOANSWER", null, null)]
    [InlineData("CALLBACK", ScheduledAt, null)]
    [InlineData("APPOINTMENT", null, ScheduledAt)]
    public void Validate_AcceptsMatchingConditionalDates(
        string resultCode,
        string? callbackAt,
        string? appointmentAt)
    {
        CallResultPayloadValidator.Validate(
            resultCode, 60, "메모", callbackAt, appointmentAt);
    }

    [Theory]
    [InlineData("CALLBACK", null, null)]
    [InlineData("APPOINTMENT", null, null)]
    [InlineData("NOANSWER", ScheduledAt, null)]
    [InlineData("NOANSWER", null, ScheduledAt)]
    [InlineData("CALLBACK", ScheduledAt, ScheduledAt)]
    [InlineData("APPOINTMENT", ScheduledAt, ScheduledAt)]
    public void Validate_RejectsMissingOrMismatchedConditionalDates(
        string resultCode,
        string? callbackAt,
        string? appointmentAt)
    {
        ApiException error = Assert.Throws<ApiException>(() =>
            CallResultPayloadValidator.Validate(
                resultCode, 60, null, callbackAt, appointmentAt));

        Assert.Equal("VALIDATION", error.Code);
        Assert.Equal(400, error.HttpStatus);
    }

    [Theory]
    [InlineData("CALLBACK", "not-a-date", null)]
    [InlineData("APPOINTMENT", null, "not-a-date")]
    public void Validate_RejectsMalformedConditionalDates(
        string resultCode,
        string? callbackAt,
        string? appointmentAt)
    {
        Assert.Throws<ApiException>(() => CallResultPayloadValidator.Validate(
            resultCode, 60, null, callbackAt, appointmentAt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(86_400)]
    public void Validate_AcceptsTalkSecondsBoundaries(int talkSeconds)
    {
        CallResultPayloadValidator.Validate("NOANSWER", talkSeconds, null, null, null);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86_401)]
    public void Validate_RejectsTalkSecondsOutsideContract(int talkSeconds)
    {
        Assert.Throws<ApiException>(() => CallResultPayloadValidator.Validate(
            "NOANSWER", talkSeconds, null, null, null));
    }

    [Fact]
    public void Validate_EnforcesMemoMaxLength()
    {
        CallResultPayloadValidator.Validate(
            "NOANSWER", 0, new string('가', 2000), null, null);

        Assert.Throws<ApiException>(() => CallResultPayloadValidator.Validate(
            "NOANSWER", 0, new string('가', 2001), null, null));
    }
}
