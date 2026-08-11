namespace FinancialAppApi.Models;

/// <summary>Derived status for a marked Stability drawdown. It is not persisted on transactions.</summary>
public static class StabilityReloadStatus
{
    public const string Outstanding = "Outstanding";
    public const string PartlyRepaid = "PartlyRepaid";
    public const string Complete = "Complete";
    public const string NotRequired = "NotRequired";
}
