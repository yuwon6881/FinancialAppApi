namespace FinancialAppApi.Models;

// An explicit empty configuration stops fallback to an earlier year's categories.
public sealed class TaxReliefEmptyYear : IUserOwnedEntity
{
    public string UserId { get; set; } = string.Empty;
    public int TaxYear { get; set; }
}
