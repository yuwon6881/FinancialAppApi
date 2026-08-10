namespace FinancialAppApi.Models;

// Stored as a string with a database check constraint rather than a CLR enum, matching the
// other user-editable wire values in this project. Unknown or missing input is deliberately
// fail-safe: an unanswered drawdown must still remain part of the recovery obligation.
public static class StabilityReloadIntent
{
    public const string Unanswered = "Unanswered";
    public const string Required = "Required";
    public const string NotRequired = "NotRequired";

    public static string Normalize(string? value) => value switch
    {
        Required => Required,
        NotRequired => NotRequired,
        _ => Unanswered
    };
}
