using System.Globalization;
using System.Text.RegularExpressions;
using FinancialAppApi.Database;

namespace FinancialAppApi.Services;

// Fills three gaps where the app plainly holds the data a valid question needs but no server-side
// metric produced it, so the model was left to eyeball a bounded (possibly truncated) sample or
// guess arithmetic:
//   * amount-threshold transaction filters ("which transaction exceeded 250", "purchases over
//     100", "anything between 50 and 200") -> a deterministic, exact filter over the loaded rows;
//   * a generic ledger-balance target forecast ("how long until my Growth reaches 50000") -> the
//     same savings-rate projection the Wishlist page uses, but for any ledger + any target amount;
//   * a recurring cost summary ("how much do I spend on subscriptions a month") -> active recurring
//     charges normalized across Weekly/Monthly/Annually into monthly and annual totals.
// All three mirror the existing derived-metric conventions: pure/static, transfer-aware, and
// amount-bearing outputs are suppressed in sensitive mode by the callers.
public partial class AiAssistantService
{
    // ---------- amount threshold ----------

    internal enum AmountComparator { GreaterThan, GreaterOrEqual, LessThan, LessOrEqual, Between }

    internal sealed record AmountThreshold(AmountComparator Comparator, decimal Low, decimal? High);

    private static bool TryParseThresholdDecimal(string raw, out decimal value) =>
        decimal.TryParse(raw.Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    // Parses an amount comparison from free text. Deliberately narrow: it only fires on an explicit
    // comparison word immediately followed by a number, so a bare "250" (e.g. a cycle year or a
    // record id) never becomes a phantom filter.
    internal static AmountThreshold? TryParseAmountThreshold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var between = Regex.Match(text,
            @"\bbetween\s+[\p{Sc}$]?(?<low>\d[\d,]*(?:\.\d+)?)\s+(?:and|to|-)\s+[\p{Sc}$]?(?<high>\d[\d,]*(?:\.\d+)?)",
            RegexOptions.IgnoreCase);
        if (between.Success &&
            TryParseThresholdDecimal(between.Groups["low"].Value, out var low) &&
            TryParseThresholdDecimal(between.Groups["high"].Value, out var high))
        {
            return new AmountThreshold(AmountComparator.Between, Math.Min(low, high), Math.Max(low, high));
        }

        var greater = Regex.Match(text,
            @"\b(?<op>at least|>=|>|over|above|exceed(?:s|ed|ing)?|more than|greater than|bigger than|larger than|higher than)\s+[\p{Sc}$]?(?<v>\d[\d,]*(?:\.\d+)?)",
            RegexOptions.IgnoreCase);
        if (greater.Success && TryParseThresholdDecimal(greater.Groups["v"].Value, out var g))
        {
            var op = greater.Groups["op"].Value.ToLowerInvariant();
            var orEqual = op is "at least" or ">=";
            return new AmountThreshold(orEqual ? AmountComparator.GreaterOrEqual : AmountComparator.GreaterThan, g, null);
        }

        var less = Regex.Match(text,
            @"\b(?<op>at most|<=|<|under|below|less than|smaller than|cheaper than|lower than)\s+[\p{Sc}$]?(?<v>\d[\d,]*(?:\.\d+)?)",
            RegexOptions.IgnoreCase);
        if (less.Success && TryParseThresholdDecimal(less.Groups["v"].Value, out var l))
        {
            var op = less.Groups["op"].Value.ToLowerInvariant();
            var orEqual = op is "at most" or "<=";
            return new AmountThreshold(orEqual ? AmountComparator.LessOrEqual : AmountComparator.LessThan, l, null);
        }

        return null;
    }

    // Canonical wire form of a parsed threshold, the inverse of TryParseAmountThreshold. Stored in
    // the conversation frame and re-appended verbatim to a later follow-up's query text, so it MUST
    // round-trip: every string produced here is re-parsed by TryParseAmountThreshold to the same
    // comparator/values. Trailing ".0" is trimmed so "over 100" stays "over 100", not "over 100.0".
    internal static string FormatAmountThreshold(AmountThreshold threshold)
    {
        static string N(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);
        return threshold.Comparator switch
        {
            AmountComparator.GreaterThan => $"over {N(threshold.Low)}",
            AmountComparator.GreaterOrEqual => $"at least {N(threshold.Low)}",
            AmountComparator.LessThan => $"under {N(threshold.Low)}",
            AmountComparator.LessOrEqual => $"at most {N(threshold.Low)}",
            AmountComparator.Between => $"between {N(threshold.Low)} and {N(threshold.High ?? threshold.Low)}",
            _ => $"over {N(threshold.Low)}"
        };
    }

    // Wire (AiAmountThreshold, on the conversation frame) <-> internal (AmountThreshold). Validates
    // the comparator name and amount ranges; returns null on anything malformed so untrusted client
    // state can never inject a bad comparison.
    internal static AmountThreshold? ToInternalThreshold(AiAmountThreshold? wire)
    {
        if (wire == null) return null;
        if (!Enum.TryParse<AmountComparator>(wire.Comparator, ignoreCase: true, out var comparator)) return null;
        if (wire.Low is < 0m or > 1_000_000_000m) return null;
        if (wire.High is < 0m or > 1_000_000_000m) return null;
        if (comparator == AmountComparator.Between && wire.High == null) return null;
        return new AmountThreshold(comparator, wire.Low, wire.High);
    }

    internal static AiAmountThreshold ToWireThreshold(AmountThreshold threshold) =>
        new(threshold.Comparator.ToString(), threshold.Low, threshold.High);

    // Canonical display text from the typed wire threshold (used only when expanding a follow-up).
    internal static string? FormatAmountThreshold(AiAmountThreshold? wire) =>
        ToInternalThreshold(wire) is { } threshold ? FormatAmountThreshold(threshold) : null;

    private static bool MatchesThreshold(decimal magnitude, AmountThreshold threshold) => threshold.Comparator switch
    {
        AmountComparator.GreaterThan => magnitude > threshold.Low,
        AmountComparator.GreaterOrEqual => magnitude >= threshold.Low,
        AmountComparator.LessThan => magnitude < threshold.Low,
        AmountComparator.LessOrEqual => magnitude <= threshold.Low,
        AmountComparator.Between => magnitude >= threshold.Low && magnitude <= (threshold.High ?? threshold.Low),
        _ => false
    };

    // Outflow rows (transfers excluded, matching every other spending metric) whose magnitude
    // satisfies the parsed comparison, largest first. `complete` is false only when the loaded
    // sample was itself capped -- identical semantics to transactionMatches.
    internal static object BuildThresholdMatches(
        IReadOnlyList<AiTransactionRow> transactions,
        AmountThreshold threshold,
        bool sampleWasComplete)
    {
        var matched = transactions
            .Where(t => t.Amount < 0 && !IsTransfer(t))
            .Select(t => new { Row = t, Magnitude = Math.Abs(t.Amount) })
            .Where(x => MatchesThreshold(x.Magnitude, threshold))
            .OrderByDescending(x => x.Magnitude)
            .ToList();
        return new
        {
            comparator = threshold.Comparator.ToString(),
            low = threshold.Low,
            high = threshold.High,
            count = matched.Count,
            totalOutflow = matched.Sum(x => x.Magnitude),
            complete = sampleWasComplete,
            rows = matched.Take(30).Select(x => new
            {
                x.Row.Id,
                x.Row.Date,
                x.Row.Description,
                x.Row.Category,
                x.Row.LedgerCategory,
                amount = x.Magnitude
            }).ToList()
        };
    }

    // ---------- relative-to-prior-cycle references ----------

    private static readonly Regex CycleBeforeSignal = new(
        @"\b(?:the\s+)?(?:(?:cycle|month|one)\s+)?before\s+(?:that|this|it|the\s+last\s+one)\b|\b(?:the\s+)?(?:previous|prior)\s+one\b|\bone\s+before\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CycleAfterSignal = new(
        @"\b(?:the\s+)?(?:(?:cycle|month|one)\s+)?after\s+(?:that|this|it)\b|\b(?:the\s+)?next\s+one\b|\bone\s+after\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "the one before/after that" is a CYCLE reference, not a transaction reference -- even though
    // it contains "that". Used to stop such a follow-up from being read as pointing at prior
    // matched-transaction ids (which would otherwise trigger an edit/reference resolution).
    internal static bool IsRelativeCyclePhrase(string? message) =>
        !string.IsNullOrWhiteSpace(message) && (CycleBeforeSignal.IsMatch(message) || CycleAfterSignal.IsMatch(message));

    // Resolves a follow-up that references the previous turn's cycle rather than the active cycle:
    // "the one before that", "the cycle after that", "the previous one". Steps from the FIRST
    // (primary) cycle the last turn resolved -- so a chain "this cycle" -> "last cycle" -> "the one
    // before that" walks Jun -> May -> Apr, which stepping from the active cycle each time could
    // never do. Returns null when there is no prior resolved cycle or no relative wording.
    internal static CycleKey? TryResolveRelativeToPriorCycle(string? message, IReadOnlyList<string>? priorCycleKeys)
    {
        // Require a SINGLE anchor cycle. If the prior turn resolved a range/comparison (e.g. "March
        // to May"), "the one before that" has no unambiguous endpoint, so we decline to guess and
        // let the normal resolution / a clarifying reply take over.
        if (string.IsNullOrWhiteSpace(message) || priorCycleKeys is not { Count: 1 }) return null;
        var anchor = ParseCycleKey(priorCycleKeys[0]);
        if (anchor == null) return null;

        int? direction = CycleBeforeSignal.IsMatch(message) ? -1
            : CycleAfterSignal.IsMatch(message) ? 1
            : null;
        if (direction == null) return null;

        var stepped = AddMonths(anchor.Year, anchor.MonthIndex, direction.Value);
        return new CycleKey(stepped.Year, stepped.MonthIndex);
    }

    // ---------- generic ledger-balance target forecast ----------

    internal sealed record LedgerBalanceForecastResult(
        string LedgerCategory,
        decimal Target,
        decimal CurrentBalance,
        decimal Remaining,
        decimal? SavingsPerCycle,
        int? EstimatedCycles,
        string? EstimatedDate,
        string Status,
        IReadOnlyList<decimal> CycleSavings,
        string Assumption);

    // A ledger + target amount to project toward, e.g. "how long until my Growth reaches 50000".
    // Requires a forecast verb, one of the real ledger categories (never Income), and a positive
    // amount; otherwise returns null so an ordinary balance question is unaffected.
    internal static (string Ledger, decimal Target)? TryParseLedgerBalanceForecast(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!Regex.IsMatch(text,
            @"\b(how long|when (?:can|will)|until|reach(?:es|ed)?|hit|get to|to have|grow to|build up to|take to)\b",
            RegexOptions.IgnoreCase))
        {
            return null;
        }
        var ledger = LedgerCategories.FirstOrDefault(c =>
            !c.Equals("Income", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(text, $@"\b{Regex.Escape(c)}\b", RegexOptions.IgnoreCase));
        if (ledger == null) return null;
        var amount = Regex.Match(text, @"[\p{Sc}$]?(?<v>\d[\d,]*(?:\.\d+)?)");
        if (!amount.Success || !TryParseThresholdDecimal(amount.Groups["v"].Value, out var target) || target <= 0m)
        {
            return null;
        }
        return (ledger, target);
    }

    // Same projection the Wishlist page/forecast uses, generalized to an arbitrary ledger: the
    // savings rate is the average POSITIVE per-cycle attribution to that ledger across cycles that
    // had activity (empty cycles skipped, never averaged in as zero); the target date is today +
    // ceil(months * 30) days.
    internal static LedgerBalanceForecastResult ComputeLedgerBalanceForecast(
        string ledgerCategory,
        decimal target,
        decimal currentBalance,
        IReadOnlyList<AiTransactionRow> transactions,
        IReadOnlyList<CycleKey> cycles,
        int cycleDay,
        DateTime today)
    {
        var remaining = Math.Max(0m, target - currentBalance);
        var perCycle = cycles
            .Select(cycle => CyclePositiveLedger(transactions, cycle, cycleDay, ledgerCategory))
            .Where(v => v.HasActivity)
            .Select(v => v.Amount)
            .ToList();

        if (remaining <= 0m)
        {
            return new LedgerBalanceForecastResult(
                ledgerCategory, target, currentBalance, 0m, perCycle.Count > 0 ? perCycle.Sum() / perCycle.Count : null,
                0, null, "already-reached", perCycle,
                $"Your current {ledgerCategory} balance already meets this target.");
        }
        if (perCycle.Count == 0)
        {
            return new LedgerBalanceForecastResult(
                ledgerCategory, target, currentBalance, remaining, null, null, null,
                "insufficient-cycle-data", perCycle,
                $"No cycles with activity were available to estimate a {ledgerCategory} savings rate.");
        }

        var rate = perCycle.Sum() / perCycle.Count;
        if (rate <= 0m)
        {
            return new LedgerBalanceForecastResult(
                ledgerCategory, target, currentBalance, remaining, rate, null, null,
                "not-currently-reachable", perCycle,
                $"Average {ledgerCategory} saved across {perCycle.Count} active cycle(s) is {rate} (<= 0); not currently on track.");
        }

        var months = remaining / rate;
        var estimatedCycles = Math.Max(1, (int)Math.Ceiling(months));
        var days = (int)Math.Ceiling(months * 30m);
        var targetDate = today.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new LedgerBalanceForecastResult(
            ledgerCategory, target, currentBalance, remaining, rate, estimatedCycles, targetDate,
            "estimated-from-completed-cycles", perCycle,
            $"Average positive {ledgerCategory} saved across {perCycle.Count} active cycle(s); remaining = target minus current {ledgerCategory} balance; projected as today + {days} days.");
    }

    private static (decimal Amount, bool HasActivity) CyclePositiveLedger(
        IReadOnlyList<AiTransactionRow> transactions, CycleKey cycle, int cycleDay, string ledgerCategory)
    {
        var range = CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay);
        var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
        var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
        var inCycle = transactions.Where(t => t.Timestamp >= start && t.Timestamp < end).ToList();
        if (inCycle.Count == 0) return (0m, false);
        var positive = inCycle
            .Select(t => CategoryAttributionService.GetCategoryAmount(new Models.Transaction
            {
                Amount = t.Amount,
                LedgerCategory = t.LedgerCategory
            }, ledgerCategory))
            .Where(amount => amount > 0)
            .Sum();
        return (positive, true);
    }

    private static object BuildLedgerBalanceForecast(LedgerBalanceForecastResult result) => new
    {
        ledgerCategory = result.LedgerCategory,
        target = result.Target,
        currentBalance = result.CurrentBalance,
        remaining = result.Remaining,
        savingsPerCycle = result.SavingsPerCycle,
        estimatedCycles = result.EstimatedCycles,
        estimatedTargetDate = result.EstimatedDate,
        cycleSavings = result.CycleSavings,
        status = result.Status,
        assumptions = result.Assumption
    };

    // ---------- recurring cost summary ----------

    // Detects "how much do my subscriptions cost", "total recurring per month/year", etc. Caller
    // has already established a recurring intent; this only decides whether a normalized total is
    // wanted rather than a plain list.
    internal static bool WantsRecurringCostSummary(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        Regex.IsMatch(text,
            @"\b(how much|total|totals|cost|costs|costing|spend|spending|sum|per month|a month|each month|monthly|per year|a year|yearly|annually|annual)\b",
            RegexOptions.IgnoreCase);

    private static decimal MonthlyEquivalent(decimal amount, string frequency) => frequency.ToLowerInvariant() switch
    {
        "weekly" => amount * 52m / 12m,
        "annually" => amount / 12m,
        _ => amount // Monthly (and any unknown cadence) counts once per month
    };

    // Active recurring charges normalized to a common monthly basis and summed, plus the annual
    // figure and a per-ledger monthly breakdown, so cadence differences (weekly vs annually) are
    // reconciled by the server instead of left to the model to get right.
    internal static object BuildRecurringCostSummary(IReadOnlyList<AiRecurringRow> recurring)
    {
        var active = recurring.Where(r => r.Active).ToList();
        var monthlyTotal = active.Sum(r => MonthlyEquivalent(Math.Abs(r.Amount), r.Frequency));
        var perLedger = active
            .GroupBy(r => r.LedgerCategory)
            .Select(g => new
            {
                ledgerCategory = g.Key,
                monthly = Math.Round(g.Sum(r => MonthlyEquivalent(Math.Abs(r.Amount), r.Frequency)), 2)
            })
            .OrderByDescending(x => x.monthly)
            .ToList();
        return new
        {
            note = "Active recurring charges normalized to a monthly basis (weekly x52/12, annually /12).",
            activeCount = active.Count,
            monthlyTotal = Math.Round(monthlyTotal, 2),
            annualTotal = Math.Round(monthlyTotal * 12m, 2),
            perLedgerMonthly = perLedger
        };
    }
}
