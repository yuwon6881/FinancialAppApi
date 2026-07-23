using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using FinancialAppApi.Extensions;
using FinancialAppApi.Services.Push;

namespace FinancialAppApi.Filters;

// Guards the push dispatch endpoint, which Cloud Scheduler calls directly (no user session
// exists). Only a request carrying a Google-signed OIDC identity token — issued to one of the
// explicitly allowlisted service accounts — is let through.
[AttributeUsage(AttributeTargets.Method)]
public class AuthorizeGoogleOidcAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var tokenResult = context.HttpContext.Request.TryGetBearerToken(out var idToken);
        if (tokenResult != BearerTokenResult.Ok)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "A Google-signed identity token is required." });
            return;
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<IGoogleOidcTokenValidator>();
        var validation = await validator.ValidateAsync(idToken, context.HttpContext.RequestAborted);
        if (!validation.IsValid || validation.ServiceAccountEmail == null)
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Invalid identity token." });
            return;
        }

        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var allowlist = configuration.GetSection("Push:AllowlistedServiceAccounts").Get<string[]>() ?? [];
        var isAllowlisted = allowlist.Any(email =>
            string.Equals(email, validation.ServiceAccountEmail, StringComparison.OrdinalIgnoreCase));
        if (!isAllowlisted)
        {
            context.Result = new ObjectResult(new { message = "Service account is not allowlisted." })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await next();
    }
}
