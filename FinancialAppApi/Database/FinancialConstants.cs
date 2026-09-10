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

    /// <summary>
    /// How many cycles the app spreads an emergency-fund recovery over. Three rather than one
    /// because a single large withdrawal repaid in full next cycle can exceed what the other
    /// buckets have spare, and an offer that never fits is an offer nobody takes.
    /// <para>
    /// These three are the cycles *after* the one the money left in — see
    /// <c>StabilityRecoveryPlanner.GraceCycles</c>. The spending cycle asks for nothing.
    /// </para>
    /// <para>
    /// Deliberately a constant and not a <see cref="Models.FinancialSetting"/> column: a setting
    /// with no UI is an orphaned feature, and this one has no screen to live on.
    /// </para>
    /// </summary>
    public const int StabilityRecoveryCycles = 3;
}
