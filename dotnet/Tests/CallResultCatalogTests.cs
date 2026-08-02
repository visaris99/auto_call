using Core;

namespace Tests;

public class CallResultCatalogTests
{
    [Fact]
    public void ShortcutMapping_RemainsOneThroughNineThenZero()
    {
        Assert.Equal(
            new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
            CallResultCatalog.Results.Select(result => result.Key));
        Assert.Equal(
            new[]
            {
                "NOANSWER", "CALLBACK", "INTERESTED", "CONSULT", "WON",
                "REJECT", "DNC", "APPOINTMENT", "HANDOFF", "RISK",
            },
            CallResultCatalog.Results.Select(result => result.Code));
    }

    [Fact]
    public void ResultGroups_AreSixPrimaryAndFourSpecialIncludingDnc()
    {
        Assert.Equal(6,
            CallResultCatalog.Results.Count(result =>
                !CallResultCatalog.IsSpecial(result.Code)));
        Assert.Equal(4,
            CallResultCatalog.Results.Count(result =>
                CallResultCatalog.IsSpecial(result.Code)));
        Assert.True(CallResultCatalog.IsSpecial("DNC"));
    }

    [Fact]
    public void CrmTerms_AreUsedForSpecialResults()
    {
        Dictionary<string, string> labels = CallResultCatalog.Results
            .ToDictionary(result => result.Code, result => result.Label);
        Assert.Equal("상담예약", labels["APPOINTMENT"]);
        Assert.Equal("영업이관", labels["HANDOFF"]);
        Assert.Equal("민원위험", labels["RISK"]);
    }
}
