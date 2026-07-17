using FinancialAppApi.Database;
using FinancialAppApi.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace FinancialAppApi.Tests;

public static class TestHelpers
{
    public const string DefaultUserId = "test-user";

    public static AppDbContext NewInMemoryContext(string? currentUserId = DefaultUserId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var context = new AppDbContext(options);
        if (currentUserId != null)
        {
            context.SetCurrentUser(currentUserId);
        }
        return context;
    }

    public static IConfiguration NewConfiguration(params (string Key, string Value)[] overrides)
    {
        var defaults = new Dictionary<string, string?>
        {
            ["Auth:MaxFailedLoginAttempts"] = "5",
            ["Auth:LockoutMinutes"] = "15",
            ["Auth:MaxPasswordVerificationAttempts"] = "5",
            ["Auth:PasswordVerificationLockoutMinutes"] = "15"
        };
        foreach (var (key, value) in overrides)
        {
            defaults[key] = value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(defaults).Build();
    }

    public static Controllers.AuthController NewAuthController(
        AppDbContext context,
        IConfiguration? configuration = null)
    {
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());
        var authSessionService = new AuthSessionService(context);
        var controller = new Controllers.AuthController(
            new AuthAccountService(
                context,
                configuration ?? NewConfiguration(),
                new TotpService(),
                protector,
                new RecoveryCodeService(context),
                authSessionService),
            authSessionService,
            new AuthCookieService());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }

    public static void SetBearerToken(Controllers.AuthController controller, string token)
    {
        controller.ControllerContext.HttpContext.Request.Headers["Authorization"] = $"Bearer {token}";
    }

    public static void SetUsername(Controllers.AuthController controller, string username)
    {
        controller.ControllerContext.HttpContext.Items["Username"] = username;
    }
}
