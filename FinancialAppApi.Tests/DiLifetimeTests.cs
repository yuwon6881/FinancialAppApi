using FinancialAppApi.Database;
using FinancialAppApi.Services;
using FinancialAppApi.Services.Documents;

namespace FinancialAppApi.Tests;

public class DiLifetimeTests
{
    [Theory]
    [InlineData(typeof(TotpService))]
    [InlineData(typeof(SecretProtector))]
    [InlineData(typeof(ReceiptScanQueue))]
    [InlineData(typeof(GcsDocumentVaultStore))]
    public void SingletonServices_DoNotInjectAppDbContextDirectly(Type serviceType)
    {
        var constructors = serviceType.GetConstructors();

        Assert.DoesNotContain(constructors, constructor =>
            constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(AppDbContext)));
    }
}
