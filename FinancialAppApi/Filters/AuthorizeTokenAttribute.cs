using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using FinancialAppApi.Database;
using FinancialAppApi.Extensions;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AuthorizeTokenAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var dbContext = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();

        var tokenResult = context.HttpContext.Request.TryGetBearerToken(out var token);
        if (tokenResult == BearerTokenResult.Missing)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Authorization header is missing" });
            return;
        }
        if (tokenResult == BearerTokenResult.Malformed)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Invalid Authorization format. Use Bearer <token>" });
            return;
        }

        // Check token in database
        var session = await dbContext.UserSessions
            .FirstOrDefaultAsync(s => s.Token == token);

        if (session == null)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Invalid token" });
            return;
        }

        if (session.ExpiresAt < DateTime.UtcNow)
        {
            // Clean up expired session
            dbContext.UserSessions.Remove(session);
            try
            {
                await dbContext.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request pruned the same expired session first.
                dbContext.Entry(session).State = EntityState.Detached;
            }

            context.Result = new UnauthorizedObjectResult(new { message = "Token has expired" });
            return;
        }

        if (session.IsLocked)
        {
            var path = context.HttpContext.Request.Path.Value ?? "";
            if (!path.Contains("/auth/verify-password", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("/auth/logout", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("/auth/webauthn/assert/", StringComparison.OrdinalIgnoreCase))
            {
                context.Result = new ObjectResult(new { message = "Session is locked" }) { StatusCode = 423 };
                return;
            }
        }

        // Store username in context items for reference
        context.HttpContext.Items["Username"] = session.Username;

        await next();
    }
}
