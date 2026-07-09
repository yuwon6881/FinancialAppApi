using FinancialAppApi.Database;
using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

public class DiLifetimeTests
{
    [Theory]
    [InlineData(typeof(TotpService))]
    [InlineData(typeof(SecretProtector))]
    [InlineData(typeof(ReceiptScanQueue))]
    public void SingletonServices_DoNotInjectAppDbContextDirectly(Type serviceType)
    {
        var constructors = serviceType.GetConstructors();

        Assert.DoesNotContain(constructors, constructor =>
            constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(AppDbContext)));
    }
}
