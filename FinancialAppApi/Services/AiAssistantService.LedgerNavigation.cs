using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinancialAppApi.Database;

namespace FinancialAppApi.Services;

// Where an openLedger action lands. The Ledger shows one cycle unless told otherwise, so a filter
// sent without a period searched only the current cycle: "show me my haircuts" opened an empty
// list while the haircuts sat in earlier cycles. The server resolves the period from what the
// model can reliably name -- a transaction id, a cycle key, or nothing -- into the month/year
// fields the client reads, instead of trusting the model to spell out the app's view state.
public partial class AiAssistantService
{
    // Fields that narrow the list. With one of these and no period, the user is looking for
    // something, and the tools' own default for that is all history.
    private static readonly string[] LedgerNarrowingKeys =
    [
        "search", "category", "ledgerCategory", "txType", "minAmount", "maxAmount",
        "recurringOnly", "wishlistOnly", "date", "startDate", "endDate"
    ];

    private static readonly string[] LedgerPeriodKeys = ["month", "year", "allCycles", "range", "cycleKey"];

    private static bool IsLedgerNavigation(string type) =>
        type.Equals("openLedger", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("openLedgerExport", StringComparison.OrdinalIgnoreCase);

    // Rewrites the payload in place. Returns a rejection reason for the model, or null.
    internal static string? NormalizeLedgerNavigation(
        Dictionary<string, object?> payload,
        IReadOnlyList<AiTransactionRow> knownTransactions,
        int cycleDay)
    {
        if (ReadPayloadString(payload, "id") is { Length: > 0 } id)
        {
            var row = knownTransactions.FirstOrDefault(transaction => transaction.Id == id);
            if (row == null)
                return "That transaction id was not returned by a tool in this conversation. Look it up first and copy its exact id.";
            // An exact record opens in its own cycle and is highlighted there. Any other filter
            // could only hide it, so none is kept.
            var date = DateOnly.ParseExact(row.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(date, cycleDay);
            payload.Clear();
            payload["id"] = id;
            SetLedgerCycle(payload, year, monthIndex);
            return null;
        }
        payload.Remove("id");

        if (payload.ContainsKey("cycleKey") && IsPresent(payload["cycleKey"]))
        {
            var key = ReadPayloadString(payload, "cycleKey");
            var match = key == null ? null : Regex.Match(key, @"^((?:19|20)\d{2})-(0[1-9]|1[0-2])$");
            if (match is not { Success: true })
                return "cycleKey must be a cycle key such as 2026-08.";
            payload.Remove("cycleKey");
            payload.Remove("allCycles");
            payload.Remove("range");
            SetLedgerCycle(payload, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
            return null;
        }
        payload.Remove("cycleKey");

        var hasMonth = IsPresent(payload.GetValueOrDefault("month"));
        var hasYear = IsPresent(payload.GetValueOrDefault("year"));
        if (hasMonth != hasYear)
            return "Name a single cycle with cycleKey (for example 2026-08), or omit the period to search all history.";

        var multiCycleRange = ReadPayloadString(payload, "range") is "3month" or "6month" or "yearly";
        var hasPeriod = hasMonth || ReadPayloadBoolean(payload, "allCycles") == true || multiCycleRange;
        if (!hasPeriod && LedgerNarrowingKeys.Any(key => IsPresent(payload.GetValueOrDefault(key))))
            payload["allCycles"] = true;
        return null;
    }

    private static void SetLedgerCycle(Dictionary<string, object?> payload, int year, int monthIndex)
    {
        foreach (var key in LedgerPeriodKeys) payload.Remove(key);
        payload["month"] = FinancialConstants.MonthAbbreviations[monthIndex - 1];
        payload["year"] = year;
    }

    // A field the model sent as null, "", or false narrows nothing.
    private static bool IsPresent(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        string text => !string.IsNullOrWhiteSpace(text),
        JsonElement element => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
            _ => true
        },
        _ => true
    };
}
