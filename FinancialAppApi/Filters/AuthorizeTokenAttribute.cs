using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AuthorizeTokenAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var dbContext = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();

        // Get Authorization header
        if (!context.HttpContext.Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Authorization header is missing" });
            return;
        }

        var authHeader = authHeaderValues.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Invalid Authorization format. Use Bearer <token>" });
            return;
        }

        var token = authHeader.Substring("Bearer ".Length).Trim();

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
            await dbContext.SaveChangesAsync();

            context.Result = new UnauthorizedObjectResult(new { message = "Token has expired" });
            return;
        }

        // Store username in context items for reference
        context.HttpContext.Items["Username"] = session.Username;

        await next();
    }
}
