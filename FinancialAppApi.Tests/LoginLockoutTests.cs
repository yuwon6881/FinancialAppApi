using FinancialAppApi.Controllers;
using FinancialAppApi.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace FinancialAppApi.Tests;

public class LoginLockoutTests
{
    private static AppUser SeedUser(FinancialAppApi.Database.AppDbContext context, string username, string password)
    {
        var hasher = new PasswordHasher<string>();
        var user = new AppUser { Id = Guid.NewGuid().ToString(), Username = username };
        user.PasswordHash = hasher.HashPassword(user.Username, password);
        context.AppUsers.Add(user);
        context.SaveChanges();
        return user;
    }

    [Fact]
    public async Task RepeatedFailedLogins_LockAccountAfterThreshold()
    {
        using var context = TestHelpers.NewInMemoryContext();
        SeedUser(context, "alice", "correct-password");
        var config = TestHelpers.NewConfiguration(("Auth:MaxFailedLoginAttempts", "5"), ("Auth:LockoutMinutes", "15"));
        var controller = TestHelpers.NewAuthController(context, config);

        for (var i = 0; i < 5; i++)
        {
            var result = await controller.Login(new LoginRequest { Username = "alice", Password = "wrong" });
            Assert.IsType<UnauthorizedObjectResult>(result);
        }

        var lockedResult = await controller.Login(new LoginRequest { Username = "alice", Password = "correct-password" });
        var statusResult = Assert.IsType<ObjectResult>(lockedResult);
        Assert.Equal(429, statusResult.StatusCode);
    }

    [Fact]
    public async Task SuccessfulLogin_ResetsFailedAttemptCounter()
    {
        using var context = TestHelpers.NewInMemoryContext();
        SeedUser(context, "bob", "correct-password");
        var controller = TestHelpers.NewAuthController(context);

        await controller.Login(new LoginRequest { Username = "bob", Password = "wrong" });
        await controller.Login(new LoginRequest { Username = "bob", Password = "wrong" });

        var okResult = await controller.Login(new LoginRequest { Username = "bob", Password = "correct-password", DeviceId = "device-1" });
        Assert.IsType<OkObjectResult>(okResult);

        var user = context.AppUsers.First(u => u.Username == "bob");
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockedUntil);
    }

    [Fact]
    public async Task ExpiredLockout_AllowsLoginAgain()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var user = SeedUser(context, "carol", "correct-password");
        user.LockedUntil = DateTime.UtcNow.AddMinutes(-1); // lockout window already elapsed
        context.SaveChanges();

        var controller = TestHelpers.NewAuthController(context);
        var result = await controller.Login(new LoginRequest { Username = "carol", Password = "correct-password", DeviceId = "device-1" });

        Assert.IsType<OkObjectResult>(result);
    }
}
