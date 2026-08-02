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
    public void CallbackIso_UsesTodayAndRejectsPastTime()
    {
        Assert.Equal("2026-07-05T14:30:00+09:00", QueueLogic.CallbackIso("14:30", Now));
        Assert.Null(QueueLogic.CallbackIso("09:00", Now));
        Assert.Equal("2026-07-05T14:30:00+09:00", QueueLogic.LocalTimeIso("14:30", Now));
    }

    [Fact]
    public void ScheduledLocalTime_IncludesExplicitDate()
    {
        ScheduledTimeResult tomorrow = QueueLogic.ScheduledLocalTime(
            new DateOnly(2026, 7, 6), "09:00", Now);
        ScheduledTimeResult later = QueueLogic.ScheduledLocalTime(
            new DateOnly(2026, 7, 31), "10:00", Now);

        Assert.Equal("2026-07-06T09:00:00+09:00", tomorrow.Iso);
        Assert.Equal("2026-07-31T10:00:00+09:00", later.Iso);
        Assert.True(tomorrow.IsValid);
        Assert.True(later.IsValid);
    }

    [Fact]
    public void ScheduledLocalTime_DistinguishesMissingInvalidAndPast()
    {
        Assert.Equal(ScheduledTimeError.MissingDate,
            QueueLogic.ScheduledLocalTime(null, "14:30", Now).Error);
        Assert.Equal(ScheduledTimeError.InvalidTime,
            QueueLogic.ScheduledLocalTime(new DateOnly(2026, 7, 5), "25:00", Now).Error);
        Assert.Equal(ScheduledTimeError.NotFuture,
            QueueLogic.ScheduledLocalTime(new DateOnly(2026, 7, 5), "09:00", Now).Error);
        Assert.Equal(ScheduledTimeError.NotFuture,
            QueueLogic.ScheduledLocalTime(new DateOnly(2026, 7, 5), "10:00", Now).Error);
    }

    [Theory]
    [InlineData("2026-07-05T14:30:00+09:00", "오늘 14:30")]
    [InlineData("2026-07-06T09:00:00+09:00", "내일 09:00")]
    [InlineData("2026-07-31T10:00:00+09:00", "07/31 10:00")]
    public void FormatCallbackTime_UsesRelativeDateLabels(string iso, string expected)
    {
        Assert.Equal(expected, QueueLogic.FormatCallbackTime(iso, Now));
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

    [Fact]
    public void FirstSelectableLead_SkipsCompletedLead()
    {
        LeadItem[] items = { Lead("saved"), Lead("next") };
        Assert.Equal("next",
            QueueLogic.FirstSelectableLead(items, new HashSet<string> { "saved" })?.Id);
    }

    [Fact]
    public void FirstSelectableLead_SingleCompletedLeadDoesNotReselect()
    {
        LeadItem[] items = { Lead("saved") };
        Assert.Null(QueueLogic.FirstSelectableLead(
            items, new HashSet<string> { "saved" }));
    }

    [Fact]
    public void FirstSelectableLead_AllowsExplicitlyClearedCompletion()
    {
        LeadItem[] items = { Lead("saved") };
        var completed = new HashSet<string> { "saved" };
        completed.Remove("saved");
        Assert.Equal("saved", QueueLogic.FirstSelectableLead(items, completed)?.Id);
    }

    [Theory]
    [InlineData("홍길동", "홍*동")]
    [InlineData("김철수상담", "김***담")]
    [InlineData("김민수", "김*수")]
    [InlineData("김", "*")]
    [InlineData("", "(이름없음)")]
    public void MaskName(string name, string expected)
    {
        Assert.Equal(expected, QueueLogic.MaskName(name));
    }
}
