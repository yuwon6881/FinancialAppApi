using System.Globalization;
using System.Text.RegularExpressions;
using FinancialAppApi.Database;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private async Task<AiChatResponse?> TryResolveLedgerEditAsync(
        string message,
        AiContext context,
        IReadOnlyList<CycleKey> targetCycles,
        int cycleDay,
        int defaultYear,
        IReadOnlyList<string>? referencedTransactionIds,
        CancellationToken cancellationToken)
    {
        var selectionFollowUp = referencedTransactionIds is { Count: > 0 } &&
            Regex.IsMatch(message, @"\b(alone|only|just|that one|this one|the one|the (?:first|second|third|fourth|last|former|latter|largest|biggest|smallest|cheapest|latest|earliest|pure|only) one|the former|the latter)\b", RegexOptions.IgnoreCase);
        if (!LooksLikeLedgerEditCommand(message) && !selectionFollowUp)
        {
            return null;
        }

        // defaultYear is threaded in from the already-loaded FinancialSetting during context
        // build, so this path no longer re-queries the settings row on every edit command.
        var hasReferencedIds = referencedTransactionIds is { Count: > 0 };
        var hasExactDate = TryExtractDate(message, defaultYear, out var targetDate, out var matchedDateText);
        if (!hasReferencedIds && !hasExactDate && targetCycles.Count == 0) return null;

        var searchText = hasReferencedIds && selectionFollowUp
            ? Regex.Replace(message, @"\b(the|one|alone|only|just|that|this|record|transaction|entry)\b", " ", RegexOptions.IgnoreCase).Trim()
            : hasReferencedIds ? null : ExtractLedgerEditSearchText(message, matchedDateText);
        if (!hasReferencedIds && string.IsNullOrWhiteSpace(searchText) && !hasExactDate)
        {
            return null;
        }

        if (context.SensitiveMode)
        {
            return new AiChatResponse("Unhide balances before using AI to edit ledger records.", []);
        }

        DateTime? scopeStart = null;
        DateTime? scopeEnd = null;
        string scopeLabel;
        if (hasReferencedIds)
        {
            scopeLabel = "for the referenced record";
        }
        else if (hasExactDate)
        {
            var dateStart = TransactionDate.StartOfDate(targetDate);
            var dateEnd = TransactionDate.ExclusiveEndOfDate(targetDate);
            scopeStart = dateStart;
            scopeEnd = dateEnd;
            scopeLabel = $"on {FormatDateForReply(targetDate)}";
        }
        else
        {
            var ranges = targetCycles
                .Select(cycle => CategoryAttributionService.GetCycleRange(cycle.Year, cycle.MonthIndex, cycleDay))
                .Select(range => new
                {
                    Start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start)),
                    End = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end)),
                    range.label
                })
                .ToList();
            var minStart = ranges.Min(range => range.Start);
            var maxEnd = ranges.Max(range => range.End);
            scopeStart = minStart;
            scopeEnd = maxEnd;
            scopeLabel = ranges.Count == 1 ? $"in {ranges[0].label}" : "in the requested cycles";
        }

        var matches = await FindLedgerEditMatchesAsync(
            searchText,
            referencedTransactionIds ?? [],
            scopeStart,
            scopeEnd,
            cancellationToken);

        // A selection follow-up ("the one named X", "that one") narrows a prior candidate set.
        // Free-form descriptors rarely substring-match a record's description, so if the text
        // filter eliminated everything, fall back to the referenced candidates themselves and let
        // the user pick, rather than falsely reporting "no matching record".
        if (matches.Count == 0 && selectionFollowUp && hasReferencedIds)
        {
            matches = await FindLedgerEditMatchesAsync(
                null,
                referencedTransactionIds!,
                scopeStart,
                scopeEnd,
                cancellationToken);
        }

        if (matches.Count == 0)
        {
            var targetDescription = string.IsNullOrWhiteSpace(searchText) ? "a ledger record" : $"a ledger record matching \"{searchText}\"";
            return new AiChatResponse($"I couldn't find {targetDescription} {scopeLabel}.", []);
        }

        if (matches.Count > 1)
        {
            var choices = string.Join(", ", matches.Select(match =>
                $"\"{match.Description}\" on {TransactionDate.ToDateOnly(match.Date):yyyy-MM-dd}"));
            // Carry the exact candidate ids so a follow-up ("the one named X", "that one", "the
            // second one") narrows against this same set. The query-plan path does not populate
            // matched ids for an edit intent, so the clarification attaches them explicitly.
            var clarificationState = new AiConversationState(
                LastIntent: "ledger.edit",
                LastSearchText: searchText,
                LastCycleHint: null,
                LastWishlistReference: null,
                LastResolvedCycle: null,
                LastMatchedTransactionIds: matches.Select(m => m.Id).ToList(),
                LastWishlistItemId: null,
                LastCategory: null);
            return new AiChatResponse(
                $"I found multiple matches {scopeLabel}: {choices}. Please specify which one to edit.",
                [],
                State: clarificationState);
        }

        var changes = ExtractRequestedLedgerChanges(message, context.Categories, context.LedgerCategories, defaultYear);
        var payload = new Dictionary<string, object?>
        {
            ["id"] = matches[0].Id,
            ["changes"] = changes
        };
        var changeReply = changes.Count > 0 ? " with your requested changes ready for review" : "";
        return new AiChatResponse($"Opened the \"{matches[0].Description}\" record {scopeLabel}{changeReply}.", [new AiUiAction("openEditLedgerDraft", payload)]);
    }

    private static bool LooksLikeLedgerEditCommand(string message)
    {
        var lower = message.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(lower, @"\b(edit|update|change|modify)\b", RegexOptions.IgnoreCase))
        {
            return false;
        }

        return !(lower.Contains("recurring") ||
            lower.Contains("subscription") ||
            lower.Contains("wishlist") ||
            lower.Contains("wish list") ||
            lower.Contains("goal"));
    }

    private static bool TryExtractDate(string message, int defaultYear, out DateOnly date, out string matchedText)
    {
        var isoMatch = Regex.Match(message, @"\b(?<year>\d{4})-(?<month>\d{1,2})-(?<day>\d{1,2})\b", RegexOptions.IgnoreCase);
        if (isoMatch.Success &&
            TryCreateDate(
                int.Parse(isoMatch.Groups["year"].Value, CultureInfo.InvariantCulture),
                int.Parse(isoMatch.Groups["month"].Value, CultureInfo.InvariantCulture),
                int.Parse(isoMatch.Groups["day"].Value, CultureInfo.InvariantCulture),
                out date))
        {
            matchedText = isoMatch.Value;
            return true;
        }

        const string monthPattern = "jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";
        var monthDayMatch = Regex.Match(
            message,
            $@"\b(?<month>{monthPattern})\s+(?<day>\d{{1,2}})(?:st|nd|rd|th)?(?:,?\s+(?<year>\d{{4}}))?\b",
            RegexOptions.IgnoreCase);
        if (monthDayMatch.Success &&
            TryCreateDate(
                GetMonthNumber(monthDayMatch.Groups["month"].Value),
                monthDayMatch.Groups["day"].Value,
                monthDayMatch.Groups["year"].Success ? monthDayMatch.Groups["year"].Value : null,
                defaultYear,
                out date))
        {
            matchedText = monthDayMatch.Value;
            return true;
        }

        var dayMonthMatch = Regex.Match(
            message,
            $@"\b(?<day>\d{{1,2}})(?:st|nd|rd|th)?\s+(?<month>{monthPattern})(?:,?\s+(?<year>\d{{4}}))?\b",
            RegexOptions.IgnoreCase);
        if (dayMonthMatch.Success &&
            TryCreateDate(
                GetMonthNumber(dayMonthMatch.Groups["month"].Value),
                dayMonthMatch.Groups["day"].Value,
                dayMonthMatch.Groups["year"].Success ? dayMonthMatch.Groups["year"].Value : null,
                defaultYear,
                out date))
        {
            matchedText = dayMonthMatch.Value;
            return true;
        }

        date = default;
        matchedText = string.Empty;
        return false;
    }

    private static bool TryCreateDate(int month, string dayText, string? yearText, int defaultYear, out DateOnly date)
    {
        var year = string.IsNullOrWhiteSpace(yearText)
            ? defaultYear
            : int.Parse(yearText, CultureInfo.InvariantCulture);
        var day = int.Parse(dayText, CultureInfo.InvariantCulture);
        return TryCreateDate(year, month, day, out date);
    }

    private static bool TryCreateDate(int year, int month, int day, out DateOnly date)
    {
        try
        {
            date = new DateOnly(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            date = default;
            return false;
        }
    }

    private static int GetMonthNumber(string month)
    {
        return month[..3].ToLowerInvariant() switch
        {
            "jan" => 1,
            "feb" => 2,
            "mar" => 3,
            "apr" => 4,
            "may" => 5,
            "jun" => 6,
            "jul" => 7,
            "aug" => 8,
            "sep" => 9,
            "oct" => 10,
            "nov" => 11,
            "dec" => 12,
            _ => 0
        };
    }

    private static string ExtractLedgerEditSearchText(string message, string matchedDateText)
    {
        var withoutDate = string.IsNullOrWhiteSpace(matchedDateText)
            ? message
            : Regex.Replace(message, Regex.Escape(matchedDateText), " ", RegexOptions.IgnoreCase);
        withoutDate = Regex.Replace(withoutDate, $@"\b(?:{MonthNamePattern})(?:\s+(?:19|20)\d{{2}})?\b", " ", RegexOptions.IgnoreCase);
        withoutDate = Regex.Replace(withoutDate, @"\b(this|current|previous|prior|last|past)\s+(?:\d+\s+|few\s+)?(cycle|cycles|month|months)\b", " ", RegexOptions.IgnoreCase);
        withoutDate = Regex.Replace(withoutDate, @"\b(cycle|cycles|month|months)\b", " ", RegexOptions.IgnoreCase);
        var beforeChangeTarget = Regex.Split(withoutDate, @"\s+\b(to|into|as)\b\s+", RegexOptions.IgnoreCase)[0];
        var cleaned = Regex.Replace(
            beforeChangeTarget,
            @"\b(edit|update|change|modify|ledger|record|transaction|entry|on|at|in|for|from|please|can|you|the|my|a|an|and|with|amount|price|category|date|description)\b",
            " ",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"[^\p{L}\p{N}\s'-]", " ");
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    private static Dictionary<string, object?> ExtractRequestedLedgerChanges(
        string message,
        IReadOnlyList<string> categories,
        IReadOnlyList<string> ledgerCategories,
        int defaultYear)
    {
        var changes = new Dictionary<string, object?>();

        var amountMatch = Regex.Match(
            message,
            @"\b(?:(?:amount|price|value|total|cost)\s*(?:to|as|=)\s*(?:[A-Z]{3}\s*)?[^\d-]*|to\s*(?:[A-Z]{3}\s*)?[\p{Sc}]?\s*)(?<amount>\d+(?:[.,]\d{1,2})?)\b",
            RegexOptions.IgnoreCase);
        if (amountMatch.Success &&
            decimal.TryParse(amountMatch.Groups["amount"].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            changes["amount"] = Math.Abs(amount);
        }

        var ledgerCategory = FindRequestedCanonicalValue(message, "ledger category", ledgerCategories);
        if (ledgerCategory != null) changes["ledgerCategory"] = ledgerCategory;
        var category = FindRequestedCanonicalValue(message, "category", categories, disallowPrefix: "ledger");
        if (category != null) changes["category"] = category;

        var txTypeMatch = Regex.Match(message, @"\b(?:type\s*)?(?:to|as|=)\s*(?<type>inflow|outflow|transfer)\b", RegexOptions.IgnoreCase);
        if (txTypeMatch.Success) changes["txType"] = txTypeMatch.Groups["type"].Value.ToLowerInvariant();

        var descriptionMatch = Regex.Match(
            message,
            @"\b(?:description|merchant|name)\s*(?:to|as|=)\s*[\""']?(?<value>[\p{L}\p{N}][\p{L}\p{N}\s&.'-]{0,100}?)[\""']?(?:\s+(?:and|with)\b|$)",
            RegexOptions.IgnoreCase);
        if (descriptionMatch.Success) changes["description"] = descriptionMatch.Groups["value"].Value.Trim();

        var dateChangeMatch = Regex.Match(message, @"\bdate\s*(?:to|as|=)\s*(?<date>.+)$", RegexOptions.IgnoreCase);
        if (dateChangeMatch.Success && TryExtractDate(dateChangeMatch.Groups["date"].Value, defaultYear, out var newDate, out _))
        {
            changes["date"] = newDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return changes;
    }

    private static string? FindRequestedCanonicalValue(
        string message,
        string fieldName,
        IReadOnlyList<string> allowed,
        string? disallowPrefix = null)
    {
        var match = Regex.Match(
            message,
            $@"\b(?<prefix>\w+\s+)?{Regex.Escape(fieldName)}\s*(?:to|as|=)\s*(?<value>[\p{{L}}\p{{N}}][\p{{L}}\p{{N}}\s&'-]{{0,100}})",
            RegexOptions.IgnoreCase);
        if (!match.Success ||
            (!string.IsNullOrWhiteSpace(disallowPrefix) && match.Groups["prefix"].Value.Trim().Equals(disallowPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var requested = match.Groups["value"].Value.Trim();
        return allowed
            .OrderByDescending(value => value.Length)
            .FirstOrDefault(value => requested.StartsWith(value, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatDateForReply(DateOnly date)
    {
        return date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
    }
}
