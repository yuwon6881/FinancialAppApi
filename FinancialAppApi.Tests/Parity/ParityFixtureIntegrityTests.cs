using System.Security.Cryptography;

namespace FinancialAppApi.Tests.Parity;

public sealed class ParityFixtureIntegrityTests
{
    [Fact]
    public void BucketAttributionFixtureMatchesManifest()
    {
        var fixture = File.ReadAllBytes(FixturePath);
        var hash = Convert.ToHexString(SHA256.HashData(fixture)).ToLowerInvariant();
        var manifest = File.ReadAllText(ManifestPath);

        Assert.Contains($"{hash}  bucket-attribution.cases.json", manifest);
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Parity",
        "Fixtures",
        "bucket-attribution.cases.json");

    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "Parity", "manifest.sha256");
}
