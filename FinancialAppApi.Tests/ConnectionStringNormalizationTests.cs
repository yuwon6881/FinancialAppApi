using FinancialAppApi.Extensions;
using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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

    [Fact]
    public void AddPersistence_ResetsPooledSessionsForAdvisoryLockSafety()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {"ConnectionStrings:DefaultConnection", "Host=localhost;Database=test;Username=test;Password=test"}
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration, migrateOnly: false);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connectionString = context.Database.GetDbConnection().ConnectionString;

        Assert.False(new NpgsqlConnectionStringBuilder(connectionString).NoResetOnClose);
    }

    [Fact]
    public void AddPersistence_DefaultsKeepAliveToFifteenSeconds()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {"ConnectionStrings:DefaultConnection", "Host=localhost;Database=test;Username=test;Password=test"}
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration, migrateOnly: false);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var builder = new NpgsqlConnectionStringBuilder(context.Database.GetDbConnection().ConnectionString);

        Assert.Equal(15, builder.KeepAlive);
        Assert.True(builder.TcpKeepAlive);
        Assert.Equal(15, builder.TcpKeepAliveTime);
        Assert.Equal(5, builder.TcpKeepAliveInterval);
    }

    [Fact]
    public void AddPersistence_ConfiguresCustomKeepAliveAndTcpKeepAlive()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {"ConnectionStrings:DefaultConnection", "Host=localhost;Database=test;Username=test;Password=test"},
                {"Database:KeepAlive", "10"},
                {"Database:TcpKeepAlive", "true"},
                {"Database:TcpKeepAliveTime", "20"},
                {"Database:TcpKeepAliveInterval", "4"}
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration, migrateOnly: false);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var builder = new NpgsqlConnectionStringBuilder(context.Database.GetDbConnection().ConnectionString);

        Assert.Equal(10, builder.KeepAlive);
        Assert.True(builder.TcpKeepAlive);
        Assert.Equal(20, builder.TcpKeepAliveTime);
        Assert.Equal(4, builder.TcpKeepAliveInterval);
    }
}
