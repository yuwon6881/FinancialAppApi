namespace FinancialAppApi.Database;

public static class FinancialConstants
{
    public static readonly string[] MonthAbbreviations = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    public static readonly string[] BudgetCategories = { "Essentials", "Growth", "Stability", "Rewards" };

    /// <summary>
    /// Cycle start day assumed when no <see cref="Models.FinancialSetting"/> row exists yet. Must
    /// match <c>FinancialSetting.CycleDay</c>'s own default: services that disagree here derive
    /// different cycle boundaries, and for recurring payments that means a settled occurrence date
    /// no other service recognises.
    /// </summary>
    public const int DefaultCycleDay = 28;
}
