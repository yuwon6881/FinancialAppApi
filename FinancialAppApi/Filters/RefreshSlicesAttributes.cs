using FinancialAppApi.Contracts;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Controllers;
using System.Reflection;

namespace FinancialAppApi.Filters;

/// <summary>Declares which client stores must be reconciled after a successful mutation.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RefreshSlicesAttribute(params string[] slices) : Attribute
{
    public IReadOnlyList<string> Slices { get; } = Validate(slices);

    private static IReadOnlyList<string> Validate(IEnumerable<string> values)
    {
        var normalized = values.Select(value => value.Trim()).ToArray();
        if (normalized.Length == 0 || normalized.Any(value => !RefreshSliceNames.IsKnown(value)) ||
            normalized.Length != normalized.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ArgumentException("Refresh metadata contains an unknown or duplicate slice.");
        }
        return normalized;
    }
}

/// <summary>Marks successful mutations that do not change financial read models.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class NoFinancialRefreshAttribute : Attribute;

/// <summary>
/// Emits refresh metadata after MVC has selected a successful mutation result. Failed responses
/// never receive the header, so an old or new frontend cannot mistake a rejected write for data
/// that needs reconciliation.
/// </summary>
public sealed class RefreshSlicesResultFilter : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(
        ResultExecutingContext context,
        ResultExecutionDelegate next)
    {
        // IActionResult.StatusCode is only the result's initial hint. An executor, middleware, or
        // streaming failure can change the actual response status after this filter starts, so
        // eligibility must be checked in OnStarting against the final HTTP status.
        if (!context.HttpContext.Response.HasStarted
            && IsUnsafe(context.HttpContext.Request.Method))
        {
            var (slices, hasMetadata) = FindMetadata(context.ActionDescriptor);
            var value = hasMetadata
                ? slices
                : "all"; // Conservative default for a newly-added mutation.
            context.HttpContext.Response.OnStarting(state =>
            {
                var (httpContext, headerValue) = ((HttpContext Context, string Value))state;
                if (httpContext.Response.StatusCode is >= StatusCodes.Status200OK
                    and < StatusCodes.Status300MultipleChoices)
                {
                    httpContext.Response.Headers[RefreshSliceNames.HeaderName] = headerValue;
                }
                return Task.CompletedTask;
            }, (context.HttpContext, value));
        }

        await next();
    }

    private static (string Value, bool HasMetadata) FindMetadata(ActionDescriptor descriptor)
    {
        if (descriptor is not ControllerActionDescriptor controller)
            return ("all", false);

        var method = controller.MethodInfo;
        var methodRefresh = method.GetCustomAttribute<RefreshSlicesAttribute>(inherit: true);
        if (methodRefresh is not null)
            return (string.Join(',', methodRefresh.Slices), true);
        if (method.GetCustomAttribute<NoFinancialRefreshAttribute>(inherit: true) is not null)
            return ("none", true);

        var controllerType = controller.ControllerTypeInfo;
        var controllerRefresh = controllerType.GetCustomAttribute<RefreshSlicesAttribute>(inherit: true);
        if (controllerRefresh is not null)
            return (string.Join(',', controllerRefresh.Slices), true);
        if (controllerType.GetCustomAttribute<NoFinancialRefreshAttribute>(inherit: true) is not null)
            return ("none", true);

        return ("all", false);
    }

    private static bool IsUnsafe(string method) => method is "POST" or "PUT" or "PATCH" or "DELETE";

}
