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
    public void AddPersistence_SkipsPerCommandSessionResetForTransactionPoolerPerformance()
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

        Assert.True(new NpgsqlConnectionStringBuilder(connectionString).NoResetOnClose);
    }

    [Fact]
    public void AddPersistence_DefaultsKeepAliveAndConnectionLifetime()
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

        Assert.True(builder.Pooling);
        Assert.Equal(60, builder.ConnectionLifetime);
        Assert.Equal(30, builder.ConnectionIdleLifetime);
        Assert.Equal(5, builder.ConnectionPruningInterval);
        Assert.Equal(15, builder.CommandTimeout);
        Assert.Equal(5, builder.Timeout);
        Assert.Equal(0, builder.KeepAlive);
        Assert.False(builder.TcpKeepAlive);
        Assert.Equal(15, builder.TcpKeepAliveTime);
        Assert.Equal(5, builder.TcpKeepAliveInterval);
    }

    [Fact]
    public void AddPersistence_ConfiguresCustomPoolAndTimeoutSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {"ConnectionStrings:DefaultConnection", "Host=localhost;Database=test;Username=test;Password=test"},
                {"Database:Pooling", "false"},
                {"Database:ConnectionLifetime", "30"},
                {"Database:ConnectionIdleLifetime", "20"},
                {"Database:ConnectionPruningInterval", "5"},
                {"Database:CommandTimeout", "10"},
                {"Database:Timeout", "8"},
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

        Assert.False(builder.Pooling);
        Assert.Equal(30, builder.ConnectionLifetime);
        Assert.Equal(20, builder.ConnectionIdleLifetime);
        Assert.Equal(5, builder.ConnectionPruningInterval);
        Assert.Equal(10, builder.CommandTimeout);
        Assert.Equal(8, builder.Timeout);
        Assert.Equal(10, builder.KeepAlive);
        Assert.True(builder.TcpKeepAlive);
        Assert.Equal(20, builder.TcpKeepAliveTime);
        Assert.Equal(4, builder.TcpKeepAliveInterval);
    }

    [Fact]
    public void AddPersistence_BoundsTheTransientRetryBudget()
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
        var strategy = context.Database.CreateExecutionStrategy();

        // A wedged socket burns a full command timeout per attempt, so the retry budget -- not the
        // per-command timeout -- is what actually bounds how long one request can hang.
        Assert.True(strategy.RetriesOnFailure);
        Assert.Equal(3, GetProtected<int>(strategy, "MaxRetryCount"));
        Assert.Equal(TimeSpan.FromSeconds(1), GetProtected<TimeSpan>(strategy, "MaxRetryDelay"));
    }

    [Fact]
    public void UseSchemaMaintenanceCommandTimeout_OutlivesTheRequestPathTimeout()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {"ConnectionStrings:DefaultConnection", "Host=localhost;Database=test;Username=test;Password=test"}
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration, migrateOnly: true);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Migrations and seeding share the request-path context; without this the deploy's migrate
        // step inherits the short request timeout and a single index build aborts it.
        context.UseSchemaMaintenanceCommandTimeout(configuration);

        Assert.Equal(600, context.Database.GetCommandTimeout());
    }

    [Fact]
    public void UseSchemaMaintenanceCommandTimeout_HonoursConfiguredOverride()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {"ConnectionStrings:DefaultConnection", "Host=localhost;Database=test;Username=test;Password=test"},
                {"Database:MaintenanceCommandTimeout", "45"}
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration, migrateOnly: true);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        context.UseSchemaMaintenanceCommandTimeout(configuration);

        Assert.Equal(45, context.Database.GetCommandTimeout());
    }

    private static T GetProtected<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
        Assert.NotNull(property);
        return (T)property!.GetValue(instance)!;
    }
}
