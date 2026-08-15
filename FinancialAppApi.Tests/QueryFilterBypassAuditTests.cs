using System.Text.RegularExpressions;

namespace FinancialAppApi.Tests;

public class QueryFilterBypassAuditTests
{
    private static readonly HashSet<string> AuditedBypassFiles =
    [
        "FinancialAppApi/Database/DbSeeder.cs",
        "FinancialAppApi/Services/Accounts/LedgerAccountService.Reconciliation.cs",
        "FinancialAppApi/Services/Investments/MarketDataCutoverService.cs",
        "FinancialAppApi/Services/Push/PushDispatchService.cs",
        "FinancialAppApi/Services/Push/PushDispatchService.CategoryAlerts.cs",
    ];

    [Fact]
    public void EveryIgnoreQueryFiltersCallSiteIsAnExplicitlyAuditedCrossTenantPath()
    {
        var sourceRoot = FindProductionSourceRoot();
        var repositoryRoot = sourceRoot.Parent!;
        var callSites = Directory.EnumerateFiles(sourceRoot.FullName, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"\.IgnoreQueryFilters\s*\(")
                .Select(_ => Normalize(Path.GetRelativePath(repositoryRoot.FullName, path))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(callSites);
        Assert.True(
            callSites.All(AuditedBypassFiles.Contains),
            $"Unreviewed IgnoreQueryFilters call sites: {string.Join(", ", callSites.Where(path => !AuditedBypassFiles.Contains(path)))}");
    }

    private static DirectoryInfo FindProductionSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var projectRoot = Path.Combine(directory.FullName, "FinancialAppApi");
            if (File.Exists(Path.Combine(projectRoot, "FinancialAppApi.csproj")))
            {
                return new DirectoryInfo(projectRoot);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the FinancialAppApi production source root.");
    }

    private static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
}
