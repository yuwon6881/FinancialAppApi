using System.Security.Cryptography;

namespace FinancialAppApi.Tests.Parity;

public sealed class ParityFixtureIntegrityTests
{
    [Fact]
    public void AllFixturesMatchManifestExactly()
    {
        var fixtureNames = Directory.GetFiles(FixturesDirectory, "*.cases.json")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .OrderBy(name => name)
            .ToArray();
        var expectedManifest = fixtureNames
            .Select(name =>
            {
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(FixturesDirectory, name))))
                    .ToLowerInvariant();
                return $"{hash}  {name}";
            })
            .OrderBy(line => line)
            .ToArray();
        var actualManifest = File.ReadAllLines(ManifestPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .OrderBy(line => line)
            .ToArray();

        Assert.Equal(expectedManifest, actualManifest);
    }

    private static string FixturesDirectory => Path.Combine(
        AppContext.BaseDirectory,
        "Parity", "Fixtures");

    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "Parity", "manifest.sha256");
}
