using FinancialAppApi.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinancialAppApi.Tests;

public class ConnectionStringNormalizationTests
{
    [Fact]
    public void AddPersistence_SupportsPostgresUriFormat()
    {
        var uriConnectionString = "postgresql://test_user:test_password@localhost:6543/test_database";
        var inMemorySettings = new Dictionary<string, string?>
        {
            {"ConnectionStrings:DefaultConnection", uriConnectionString}
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();

        var exception = Record.Exception(() => services.AddPersistence(configuration, migrateOnly: true));
        Assert.Null(exception);
    }
}
