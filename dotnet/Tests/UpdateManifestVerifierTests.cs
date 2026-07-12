using Core;

namespace Tests;

public class UpdateManifestVerifierTests
{
    [Fact]
    public void Verify_AcceptsValidSignedManifest()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create();

        new UpdateManifestVerifier(fixture.Policy, fixture.TrustedKeys).Verify(fixture.Manifest);
    }

    [Fact]
    public void Verify_AcceptsProductionCrossLanguageFixture()
    {
        const string signature =
            "T0QUyT1FgL1jK7ey3dZSg7zsE9QNcu83uRNCv/PeREGaZPdlSHpt6+HQ46XEd6y3oRcaXud5EJkPwjK81QvstKpUJYdi0SY1O33KI86TxXEJqzh9yVx89PeL5JNZl7onQVStaqHAdg79WX03PKJVaySUzU+km/KOV3wTFdHWMZ8rHWk9loz+avrG7jmLCilZf3Kxp/Ysv4BG86oHcEa2yKrlXpzWfwJIs64oe/wh8g16jmWZhVGIGqqUu0fup6uWtTrI4wt9du38/8IeVitAZvMRjQyOWc+qTPSV39qiNWFHfA9SgEH9+0t+BCcraNPViHnDe4TFmwzLquA4WpyItox7qD/rAh/u2FdYvvfeEER0bc9Uj4Y/vFrH0NP0lT7H1cpsIZ8qxFcGk7aQN8tKUtXS1in7DRR6yCv2TChtnLf58ds651nmNzpZ66tNOP02VXGL5y5GkGhXpuIsa4YcK++VccXQ8262w15Tl2zWhLyNSOXcn03gEJpqAPx7w5ZQ";
        var info = new VersionInfo(
            "2.0.0",
            "2.4.1",
            "https://crm.milestone-sales.xyz/downloads/milestone_dialer_setup.exe",
            "79fcd3395b67f293876393e034cf92d0f00f2a491a5e198a78cdbcc384f3666e",
            12,
            "2026-07-11T00:00:00.000Z",
            UpdateTrust.ActiveKeyId,
            signature);
        UpdateManifest manifest = UpdateManifest.FromVersionInfo(info);

        new UpdateManifestVerifier(
            UpdatePolicy.OfficeProduction,
            utcNow: () => new DateTimeOffset(2026, 7, 11, 1, 0, 0, TimeSpan.Zero))
            .Verify(manifest);
    }

    [Fact]
    public void Verify_RejectsTamperedHash()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create();
        UpdateManifest tampered = fixture.Manifest with { Sha256 = new string('0', 64) };

        UpdateSecurityException error = Assert.Throws<UpdateSecurityException>(() =>
            new UpdateManifestVerifier(fixture.Policy, fixture.TrustedKeys).Verify(tampered));

        Assert.Equal("MANIFEST_SIGNATURE_INVALID", error.Code);
    }

    [Fact]
    public void Verify_RejectsHttpUrlBeforeSignatureCheck()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create(
            downloadUrl: $"http://{UpdateTestData.Host}/setup.exe");

        UpdateSecurityException error = Assert.Throws<UpdateSecurityException>(() =>
            new UpdateManifestVerifier(fixture.Policy, fixture.TrustedKeys).Verify(fixture.Manifest));

        Assert.Equal("DOWNLOAD_URL_POLICY", error.Code);
    }

    [Fact]
    public void Verify_RejectsHostOutsideAllowlist()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create(
            downloadUrl: "https://untrusted.example/setup.exe");

        UpdateSecurityException error = Assert.Throws<UpdateSecurityException>(() =>
            new UpdateManifestVerifier(fixture.Policy, fixture.TrustedKeys).Verify(fixture.Manifest));

        Assert.Equal("DOWNLOAD_HOST_POLICY", error.Code);
    }

    [Fact]
    public void Verify_RejectsUnknownSigningKey()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create();

        UpdateSecurityException error = Assert.Throws<UpdateSecurityException>(() =>
            new UpdateManifestVerifier(fixture.Policy, new Dictionary<string, string>())
                .Verify(fixture.Manifest));

        Assert.Equal("MANIFEST_UNKNOWN_KEY", error.Code);
    }

    [Fact]
    public void Verify_RejectsOversizedManifest()
    {
        SignedUpdateFixture fixture = UpdateTestData.Create(declaredSize: 2 * 1024 * 1024);

        UpdateSecurityException error = Assert.Throws<UpdateSecurityException>(() =>
            new UpdateManifestVerifier(fixture.Policy, fixture.TrustedKeys).Verify(fixture.Manifest));

        Assert.Equal("MANIFEST_SIZE_POLICY", error.Code);
    }
}
