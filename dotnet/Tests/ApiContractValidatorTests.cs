using Core;

namespace Tests;

public class ApiContractValidatorTests
{
    [Theory]
    [InlineData("2f1e8918-8dc3-4aef-ab97-a4513ca0f649")]
    [InlineData("2F1E8918-8DC3-4AEF-AB97-A4513CA0F649")]
    public void RequireUuid4_AcceptsUuidVersion4(string value)
    {
        Assert.Equal(value, ApiContractValidator.RequireUuid4(value, "attemptId"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("key-1")]
    [InlineData("2f1e8918-8dc3-1aef-ab97-a4513ca0f649")]
    [InlineData("2f1e8918-8dc3-4aef-7b97-a4513ca0f649")]
    public void RequireUuid4_RejectsNonUuid4Values(string value)
    {
        ApiException error = Assert.Throws<ApiException>(
            () => ApiContractValidator.RequireUuid4(value, "attemptId"));

        Assert.Equal("VALIDATION", error.Code);
        Assert.Equal(400, error.HttpStatus);
    }
}
