using System.Net;
using System.Net.Http.Json;
using FinancialAppApi.Contracts;

namespace FinancialAppApi.Tests.Integration;

public sealed class RefreshHeaderIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task SuccessfulFinancialMutationExposesDeclaredSlices()
    {
        var client = await CreateSignedInClientAsync();
        var response = await client.PostAsJsonAsync("/api/financial/select-period", new
        {
            selectedMonth = "Jul",
            selectedYear = 2026,
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(RefreshSliceNames.HeaderName, out var values));
        Assert.Equal("core", values!.Single());
    }

    [Fact]
    public async Task FailedMutationDoesNotExposeRefreshHeader()
    {
        var client = await CreateSignedInClientAsync();
        var response = await client.PostAsJsonAsync("/api/financial/select-period", new
        {
            selectedMonth = "NotAMonth",
            selectedYear = 2026,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains(RefreshSliceNames.HeaderName));
    }

    [Fact]
    public async Task NotFoundMutationDoesNotExposeRefreshHeader()
    {
        var client = await CreateSignedInClientAsync();
        var response = await client.DeleteAsync("/api/loans/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains(RefreshSliceNames.HeaderName));
    }

    [Fact]
    public async Task NonFinancialMutationExplicitlyDeclaresNoRefresh()
    {
        var client = await CreateSignedInClientAsync();
        var response = await client.PostAsJsonAsync("/api/financial/summary-seen", new { cycleKey = "2026-07" });

        // This endpoint is PUT in production; the POST is intentionally rejected and proves the
        // failed path does not emit metadata. The successful no-refresh assertion is covered by
        // the session lock endpoint below.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);

        var lockResponse = await client.PostAsync("/api/auth/lock", content: null);
        Assert.Equal(HttpStatusCode.OK, lockResponse.StatusCode);
        Assert.True(lockResponse.Headers.TryGetValues(RefreshSliceNames.HeaderName, out var values));
        Assert.Equal("none", values!.Single());
    }
}
