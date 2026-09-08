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

    [Fact]
    public void AddPersistence_AcceptsAProviderUriUsingLibpqKeywordSpelling()
    {
        // Neon's dashboard hands out exactly this. `channel_binding` is a real Npgsql setting under
        // a different name ("Channel Binding"), so pasting the URL verbatim used to throw
        // "Keyword not supported" and take the app down at startup.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {
                    "ConnectionStrings:DefaultConnection",
                    "postgresql://owner:secret@ep-test-123.ap-southeast-1.aws.neon.tech/neondb" +
                    "?sslmode=require&channel_binding=require"
                }
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration, migrateOnly: false);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var builder = new NpgsqlConnectionStringBuilder(context.Database.GetDbConnection().ConnectionString);

        Assert.Equal("ep-test-123.ap-southeast-1.aws.neon.tech", builder.Host);
        Assert.Equal("neondb", builder.Database);
        Assert.Equal("owner", builder.Username);
        Assert.Equal(SslMode.Require, builder.SslMode);
        Assert.Equal("Require", builder["Channel Binding"]?.ToString());
    }

    [Fact]
    public void AddPersistence_DropsQueryParametersNpgsqlHasNoEquivalentFor()
    {
        // fallback_application_name is a real libpq parameter with no Npgsql counterpart under any
        // spelling, unlike channel_binding or gssencmode which are only spelled differently.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {
                    "ConnectionStrings:DefaultConnection",
                    "postgresql://owner:secret@ep-test-123.aws.neon.tech/neondb" +
                    "?sslmode=require&fallback_application_name=probe"
                }
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();

        // Dropping the unusable parameter keeps a pasted dashboard URL working, and must not cost
        // the parameters either side of it.
        var exception = Record.Exception(() => services.AddPersistence(configuration, migrateOnly: false));
        Assert.Null(exception);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var builder = new NpgsqlConnectionStringBuilder(context.Database.GetDbConnection().ConnectionString);

        Assert.Equal("ep-test-123.aws.neon.tech", builder.Host);
        Assert.Equal("neondb", builder.Database);
        Assert.Equal(SslMode.Require, builder.SslMode);
    }

    [Fact]
    public void AddPersistence_DisablesGssEncryptionNegotiation()
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

        // Left at its default, Npgsql attempts GSS encryption on every new physical connection and
        // has to load libgssapi to do it -- absent from the chiseled runtime image, so each attempt
        // logs a library-load failure. SCRAM over TLS means the negotiation can never succeed.
        Assert.Equal(GssEncryptionMode.Disable, builder.GssEncryptionMode);
    }

    [Fact]
    public void AddPersistence_DisablesGssEncryptionEvenWhenTheUrlAsksForIt()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {
                    "ConnectionStrings:DefaultConnection",
                    "postgresql://owner:secret@ep-test-123.aws.neon.tech/neondb?gssencmode=require"
                }
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration, migrateOnly: false);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var builder = new NpgsqlConnectionStringBuilder(context.Database.GetDbConnection().ConnectionString);

        Assert.Equal(GssEncryptionMode.Disable, builder.GssEncryptionMode);
    }

    [Theory]
    [InlineData("postgresql://owner:secret@ep-test-123-pooler.ap-southeast-1.aws.neon.tech/neondb")]
    [InlineData("Host=ep-test-123-pooler.ap-southeast-1.aws.neon.tech;Database=neondb;Username=owner;Password=secret")]
    public void AddPersistence_UsesTheDirectEndpointForSchemaMaintenance(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {"ConnectionStrings:DefaultConnection", connectionString}
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration, migrateOnly: true);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var builder = new NpgsqlConnectionStringBuilder(context.Database.GetDbConnection().ConnectionString);

        // Migrations hold session state across statements, which a transaction pooler is free to
        // serve from a different backend. Neon separates the endpoints by host label, not by port.
        Assert.Equal("ep-test-123.ap-southeast-1.aws.neon.tech", builder.Host);
    }

    [Fact]
    public void AddPersistence_LeavesThePooledEndpointAloneForRequestTraffic()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                {
                    "ConnectionStrings:DefaultConnection",
                    "postgresql://owner:secret@ep-test-123-pooler.ap-southeast-1.aws.neon.tech/neondb"
                }
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration, migrateOnly: false);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var builder = new NpgsqlConnectionStringBuilder(context.Database.GetDbConnection().ConnectionString);

        Assert.Equal("ep-test-123-pooler.ap-southeast-1.aws.neon.tech", builder.Host);
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
