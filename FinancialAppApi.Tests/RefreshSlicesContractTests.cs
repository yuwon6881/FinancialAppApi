using System.Reflection;
using FinancialAppApi.Controllers;
using FinancialAppApi.Contracts;
using FinancialAppApi.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace FinancialAppApi.Tests;

public sealed class RefreshSlicesContractTests
{
    [Fact]
    public void SliceSetContainsOnlyTheCanonicalValues()
    {
        Assert.Equal(
            new[] { "core", "recurring", "categories", "wishlist", "savingsGoals", "loans", "investments", "documents" },
            RefreshSliceNames.All);
        Assert.Equal("X-FinancialApp-Refresh-Slices", RefreshSliceNames.HeaderName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("core,core")]
    [InlineData("core,unknown")]
    [InlineData("core,")]
    public void PartialRefreshParserRejectsInvalidRequests(string? value)
    {
        Assert.False(RefreshSliceNames.TryParseRequest(value, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void EveryMutationActionDeclaresRefreshMetadata()
    {
        var controllers = typeof(BootstrapController).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(BootstrapController).Namespace)
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type));

        var missing = new List<string>();
        foreach (var controller in controllers)
        {
            var classHasMetadata = controller.GetCustomAttribute<RefreshSlicesAttribute>(inherit: true) is not null
                || controller.GetCustomAttribute<NoFinancialRefreshAttribute>(inherit: true) is not null;
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var isMutation = method.GetCustomAttributes(inherit: true)
                    .OfType<HttpMethodAttribute>()
                    .Any(attribute => attribute.HttpMethods.Any(methodName =>
                        methodName is "POST" or "PUT" or "PATCH" or "DELETE"));
                if (!isMutation) continue;

                var hasMetadata = classHasMetadata
                    || method.GetCustomAttribute<RefreshSlicesAttribute>(inherit: true) is not null
                    || method.GetCustomAttribute<NoFinancialRefreshAttribute>(inherit: true) is not null;
                if (!hasMetadata) missing.Add($"{controller.Name}.{method.Name}");
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void LoanPreviewIsExplicitlyExcludedFromFinancialRefreshes()
    {
        var method = typeof(LoansController).GetMethod(nameof(LoansController.PreviewRepayment));

        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<NoFinancialRefreshAttribute>(inherit: true));
        Assert.Null(method.GetCustomAttribute<RefreshSlicesAttribute>(inherit: true));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(500)]
    public async Task ResultFilterChecksTheFinalResponseStatusBeforeEmittingMetadata(int statusCode)
    {
        var httpContext = new DefaultHttpContext();
        var descriptor = new ControllerActionDescriptor
        {
            ActionName = nameof(FilterProbeController.Mutate),
            ControllerName = nameof(FilterProbeController),
            ControllerTypeInfo = typeof(FilterProbeController).GetTypeInfo(),
            MethodInfo = typeof(FilterProbeController).GetMethod(nameof(FilterProbeController.Mutate))!,
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        var filters = new List<IFilterMetadata>();
        var executing = new ResultExecutingContext(actionContext, filters, new StatusCodeResult(statusCode), new FilterProbeController());
        var filter = new RefreshSlicesResultFilter();

        await filter.OnResultExecutionAsync(executing, () =>
        {
            httpContext.Response.StatusCode = statusCode;
            return Task.FromResult(new ResultExecutedContext(actionContext, filters, executing.Result, new FilterProbeController()));
        });
        await httpContext.Response.StartAsync();

        Assert.False(httpContext.Response.Headers.ContainsKey(RefreshSliceNames.HeaderName));
    }

    private sealed class FilterProbeController : ControllerBase
    {
        [HttpPost]
        [RefreshSlices(RefreshSliceNames.Core)]
        public IActionResult Mutate() => new StatusCodeResult(StatusCodes.Status200OK);
    }
}
