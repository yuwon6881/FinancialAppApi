using FinancialAppApi.Services;
using Xunit;

namespace FinancialAppApi.Tests;

public class RecoveryCodeServiceTests
{
    [Fact]
    public async Task RegenerateAsync_ReturnsTenUniqueCodes()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var service = new RecoveryCodeService(context);

        var codes = await service.RegenerateAsync("alice");

        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct().Count());
    }

    [Fact]
    public async Task TryConsumeAsync_CodeIsSingleUse()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var service = new RecoveryCodeService(context);
        var codes = await service.RegenerateAsync("alice");
        var code = codes[0];

        Assert.True(await service.TryConsumeAsync("alice", code));
        Assert.False(await service.TryConsumeAsync("alice", code));
    }

    [Fact]
    public async Task TryConsumeAsync_RejectsUnknownCode()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var service = new RecoveryCodeService(context);
        await service.RegenerateAsync("alice");

        Assert.False(await service.TryConsumeAsync("alice", "AAAA-BBBB-CCCC"));
    }

    [Fact]
    public async Task RegenerateAsync_InvalidatesPreviousCodes()
    {
        using var context = TestHelpers.NewInMemoryContext();
        var service = new RecoveryCodeService(context);
        var firstBatch = await service.RegenerateAsync("alice");

        await service.RegenerateAsync("alice");

        Assert.False(await service.TryConsumeAsync("alice", firstBatch[0]));
    }
}
