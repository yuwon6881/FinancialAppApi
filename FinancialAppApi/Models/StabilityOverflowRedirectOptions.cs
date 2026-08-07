namespace FinancialAppApi.Models;

/// <summary>
/// The six values <see cref="FinancialSetting.StabilityOverflowRedirect"/> can hold, matching the
/// select in the settings screen exactly. A <c>static class</c> of <c>const string</c> rather than
/// a CLR enum, following <c>SavingsGoalStatus</c> and <c>RecurringPaymentMode</c> -- the column
/// stays a plain string and no <c>HasConversion</c> infrastructure is introduced for one field.
/// <para>
/// The column is deliberately NOT constrained to these values. It predates this list and older
/// rows may hold anything; <c>IncomeSplitPlanner.ResolveRedirectTargets</c> parses the general
/// shape and falls back rather than rejecting, so an unrecognised string degrades instead of
/// failing a salary the user cannot re-enter.
/// </para>
/// </summary>
public static class StabilityOverflowRedirectOptions
{
    public const string EssentialsOnly = "Essentials 100%";
    public const string GrowthOnly = "Growth 100%";
    public const string RewardsOnly = "Rewards 100%";
    public const string EssentialsGrowth = "Split: Essentials 50%, Growth 50%";
    public const string EssentialsRewards = "Split: Essentials 50%, Rewards 50%";
    public const string GrowthRewards = "Split: Growth 50%, Rewards 50%";

    public static readonly string[] All =
    {
        EssentialsOnly, GrowthOnly, RewardsOnly, EssentialsGrowth, EssentialsRewards, GrowthRewards
    };
}
