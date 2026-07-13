using Core;
using Xunit;

namespace Tests;

public class QueueLogicTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 5, 10, 0, 0, TimeSpan.FromHours(9));

    private static LeadItem Lead(string id, string? nextCallAt = null) =>
        new(id, null, "010-****-0000", "ASSIGNED", nextCallAt, null, null);

    [Fact]
    public void ParseIso_HandlesNullAndValid()
    {
        Assert.Null(QueueLogic.ParseIso(null));
        Assert.Null(QueueLogic.ParseIso(""));
        Assert.Null(QueueLogic.ParseIso("abc"));
        var dt = QueueLogic.ParseIso("2026-07-05T14:30:00+09:00");
        Assert.Equal(14, dt!.Value.Hour);
    }

    [Fact]
    public void IsCallbackDue()
    {
        Assert.True(QueueLogic.IsCallbackDue(Lead("a", "2026-07-05T09:59:00+09:00"), Now));
        Assert.False(QueueLogic.IsCallbackDue(Lead("b", "2026-07-05T10:01:00+09:00"), Now));
        Assert.False(QueueLogic.IsCallbackDue(Lead("c"), Now));
    }

    [Fact]
    public void SortQueue_DueCallbacksFirst_OldestFirst()
    {
        var items = new[]
        {
            Lead("a"),
            Lead("b", "2026-07-05T09:30:00+09:00"),
            Lead("c", "2026-07-05T14:00:00+09:00"),
            Lead("d", "2026-07-05T09:00:00+09:00"),
        };
        Assert.Equal(new[] { "d", "b", "a", "c" },
            QueueLogic.SortQueue(items, Now).Select(x => x.Id).ToArray());
    }

    [Fact]
    public void SortQueue_KeepsServerOrderForRest()
    {
        var items = new[] { Lead("a"), Lead("b"), Lead("c") };
        Assert.Equal(new[] { "a", "b", "c" },
            QueueLogic.SortQueue(items, Now).Select(x => x.Id).ToArray());
    }

    [Fact]
    public void FormatSeconds()
    {
        Assert.Equal("00:00", QueueLogic.FormatSeconds(0));
        Assert.Equal("01:15", QueueLogic.FormatSeconds(75));
        Assert.Equal("1:01:40", QueueLogic.FormatSeconds(3700));
        Assert.Equal("00:00", QueueLogic.FormatSeconds(-5));
    }

    [Fact]
    public void CallbackIso_TodayAndTomorrow()
    {
        Assert.Equal("2026-07-05T14:30:00+09:00", QueueLogic.CallbackIso("14:30", Now));
        Assert.Equal("2026-07-06T09:00:00+09:00", QueueLogic.CallbackIso("09:00", Now)); // 지난 시각 → 내일
        Assert.Equal("2026-07-05T14:30:00+09:00", QueueLogic.LocalTimeIso("14:30", Now));
    }

    [Fact]
    public void CallbackIso_Invalid()
    {
        Assert.Null(QueueLogic.CallbackIso("25:00", Now));
        Assert.Null(QueueLogic.CallbackIso("abc", Now));
        Assert.Null(QueueLogic.CallbackIso("", Now));
    }

    [Fact]
    public void AsciiOnly_StripsHangulAndControls()
    {
        Assert.Equal("abc123!@#", QueueLogic.AsciiOnly("abc123!@#"));
        Assert.Equal("pass123", QueueLogic.AsciiOnly("pass워드123"));
        Assert.Equal("", QueueLogic.AsciiOnly("한글만"));
        Assert.Equal("tabhere", QueueLogic.AsciiOnly("tab\there\n"));
    }

    [Theory]
    [InlineData("010-1234-5678", "01012345678")]
    [InlineData("전화: 010 9999 0000", "01099990000")]
    [InlineData("+82 10-1234-5678", "01012345678")]
    [InlineData("0082-10-1234-5678", "01012345678")]
    [InlineData("02-123-4567", "021234567")]
    public void PhoneDigits_NormalizesPastedPhoneNumbers(string raw, string expected)
    {
        Assert.Equal(expected, QueueLogic.PhoneDigits(raw));
    }

    [Theory]
    [InlineData("01012345678", "010-1234-5678")]
    [InlineData("0212345678", "02-1234-5678")]
    [InlineData("0311234567", "031-123-4567")]
    [InlineData("02-123-4567", "02-123-4567")]
    public void FormatPhone_DisplaysKoreanPhoneNumbers(string raw, string expected)
    {
        Assert.Equal(expected, QueueLogic.FormatPhone(raw));
    }

    [Theory]
    [InlineData("NEW", true, true)]
    [InlineData("ASSIGNED", true, true)]
    [InlineData("NOANSWER", true, true)]
    [InlineData("CALLBACK", true, true)]
    [InlineData("INTERESTED", true, true)]
    [InlineData("CONSULT", true, true)]
    [InlineData("NOANSWER", false, false)]
    [InlineData("APPOINTMENT", true, false)]
    [InlineData("HANDOFF", true, false)]
    [InlineData("RISK", true, false)]
    [InlineData("WON", true, false)]
    [InlineData("REJECT", true, false)]
    [InlineData("DNC", true, false)]
    public void CanRedialAfterSavedStatus_RequiresPersistedCallableLead(
        string leadStatus, bool persisted, bool expected)
    {
        Assert.Equal(expected, QueueLogic.CanRedialAfterSavedStatus(leadStatus, persisted));
    }
}
