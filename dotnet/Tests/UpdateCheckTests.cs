using Core;
using Xunit;

namespace Tests;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("2.2.2", "2.3.0", true)]
    [InlineData("2.2.2", "2.2.3", true)]
    [InlineData("2.2.2", "10.0.0", true)]
    [InlineData("2.2.2", "2.2.2", false)]
    [InlineData("2.2.2", "2.2.1", false)]
    [InlineData("2.2.2", "1.9.9", false)]
    [InlineData("2.2.2", null, false)]
    [InlineData("2.2.2", "", false)]
    [InlineData("2.2.2", "abc", false)]
    [InlineData("2.2.2", " 2.3.0 ", true)]
    public void IsNewer(string current, string? candidate, bool expected) =>
        Assert.Equal(expected, UpdateCheck.IsNewer(current, candidate));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", true)]
    [InlineData("1", true)]
    public void IsAutoUpdateDisabled_UsesKillSwitch(string? value, bool expected) =>
        Assert.Equal(expected, UpdateCheck.IsAutoUpdateDisabled(_ => value));
}
