using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text.Json;
using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public sealed record AiChatMessage(string Role, string Content);
public sealed record AiChatRequest(string Message, IReadOnlyList<AiChatMessage>? History);
public sealed record AiChatResponse(string Reply, IReadOnlyList<AiUiAction> Actions, bool CloseChat = false);
public sealed record AiUiAction(string Type, Dictionary<string, object?> Payload);

public class AiAssistantService
{
    private static readonly string[] LedgerCategories = ["Essentials", "Growth", "Stability", "Rewards", "Income"];
    private static readonly HashSet<string> AllowedActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openLedger",
        "openAddLedgerDraft",
        "openAddRecurringDraft",
        "openAddWishlistDraft",
        "openEditLedgerDraft",
        "openEditRecurringDraft",
        "openEditWishlistDraft"
    };

    private readonly HttpClient _httpClient;
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AiAssistantService> _logger;

    public AiAssistantService(
        HttpClient httpClient,
        AppDbContext context,
        IConfiguration configuration,
        ILogger<AiAssistantService> logger)
    {
        _httpClient = httpClient;
        _context = context;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AiChatResponse> ChatAsync(AiChatRequest request)
    {
        var message = (request.Message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return new AiChatResponse("Please ask a financial question or tell me what you want to open.", []);
        }
        if (LooksLikeDeleteCommand(message))
        {
            return new AiChatResponse("I'm unable to delete records. You can delete it manually from the app if sensitive mode is off.", []);
        }

        var apiKey = _configuration["GeminiApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AiChatResponse("AI chat is not configured on the server.", []);
        }

        var context = await BuildContextAsync();
        var resolvedEdit = await TryResolveLedgerEditAsync(message, context.SensitiveMode);
        if (resolvedEdit != null)
        {
            return resolvedEdit;
        }

        var prompt = BuildPrompt(message, request.History ?? [], context);
        var text = await GenerateGeminiTextAsync(prompt, 0.15, 1400, apiKey);
        return ParseAndValidateResponse(text, context, message);
    }

    private async Task<AiContext> BuildContextAsync()
    {
        var setting = await _context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync();
        var cycleDay = setting?.CycleDay ?? 28;
        var selectedMonth = setting?.SelectedMonth ?? DateTime.Now.ToString("MMM");
        var selectedYear = setting?.SelectedYear ?? DateTime.Now.Year;
        var selectedMonthIndex = Array.IndexOf(FinancialConstants.MonthAbbreviations, selectedMonth) + 1;
        if (selectedMonthIndex <= 0) selectedMonthIndex = DateTime.Now.Month;

        var categories = await _context.TransactionCategories
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => c.Name)
            .ToListAsync();

        var sensitiveMode = setting?.HideSensitive ?? true;

        var recurring = await _context.RecurringPayments
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new
            {
                r.Id,
                r.Name,
                Amount = r.Amount,
                r.Category,
                r.LedgerCategory,
                r.StartDate,
                r.EndDate,
                r.DueDate,
                r.Active
            })
            .ToListAsync();
        var recurringContext = sensitiveMode
            ? recurring.Select(r => new
            {
                r.Id,
                r.Name,
                r.Category,
                r.LedgerCategory,
                r.StartDate,
                r.EndDate,
                r.DueDate,
                r.Active
            }).ToList()
            : (object)recurring;

        var wishlist = await _context.WishlistItems
            .AsNoTracking()
            .OrderBy(w => w.IsPurchased)
            .ThenByDescending(w => w.IsActive)
            .ThenByDescending(w => w.CreatedAt)
            .Select(w => new
            {
                w.Id,
                w.Name,
                Price = w.Price,
                w.Priority,
                w.IsActive,
                w.IsPurchased,
                w.CreatedAt
            })
            .ToListAsync();
        var wishlistContext = sensitiveMode
            ? wishlist.Select(w => new
            {
                w.Id,
                w.Name,
                w.Priority,
                w.IsActive,
                w.IsPurchased,
                w.CreatedAt
            }).ToList()
            : (object)wishlist;

        var allTransactions = await _context.Transactions
            .AsNoTracking()
            .Where(t => t.LedgerCategory != "Discarded")
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .Take(500)
            .Select(t => new
            {
                t.Id,
                Date = TransactionDate.ToDateOnly(t.Date).ToString("yyyy-MM-dd"),
                t.Description,
                t.Category,
                t.LedgerCategory,
                t.Amount
            })
            .ToListAsync();

        var recentTransactions = sensitiveMode
            ? allTransactions.Take(80).Select(t => new
            {
                t.Id,
                t.Date,
                t.Description,
                t.Category,
                t.LedgerCategory
            }).ToList()
            : (object)allTransactions.Take(80).ToList();
        var cycleSummaries = BuildCycleWindowSummaries(allTransactions, selectedYear, selectedMonthIndex, cycleDay, !sensitiveMode);

        var availableCycles = allTransactions
            .Select(t =>
            {
                if (!DateTime.TryParse(t.Date, out var date)) return null;
                var (year, monthIndex) = CategoryAttributionService.GetCycleYearAndMonthIndexForDate(DateOnly.FromDateTime(date), cycleDay);
                return new CycleKey(year, monthIndex);
            })
            .Where(c => c != null)
            .Select(c => c!)
            .Distinct()
            .OrderBy(c => c.Year)
            .ThenBy(c => c.MonthIndex)
            .Select(c => new
            {
                month = FinancialConstants.MonthAbbreviations[c.MonthIndex - 1],
                year = c.Year,
                label = CategoryAttributionService.GetCycleRange(c.Year, c.MonthIndex, cycleDay).label
            })
            .ToList();

        return new AiContext(
            Currency: setting?.Currency ?? "USD",
            Today: DateTime.Now.ToString("yyyy-MM-dd"),
            SensitiveMode: sensitiveMode,
            ActiveCycle: new
            {
                month = selectedMonth,
                year = selectedYear,
                label = CategoryAttributionService.GetCycleRange(selectedYear, selectedMonthIndex, cycleDay).label
            },
            Categories: categories,
            LedgerCategories: LedgerCategories,
            AvailableCycles: availableCycles,
            CycleSummaries: cycleSummaries,
            RecentTransactions: recentTransactions,
            RecurringPayments: recurringContext,
            WishlistItems: wishlistContext);
    }

    private static List<object> BuildCycleWindowSummaries(
        IEnumerable<dynamic> transactions,
        int centerYear,
        int centerMonthIndex,
        int cycleDay,
        bool includeAmounts)
    {
        var summaries = new List<object>();
        var txList = transactions.ToList();
        for (var offset = -3; offset <= 3; offset++)
        {
            var (year, monthIndex) = AddMonths(centerYear, centerMonthIndex, offset);
            var range = CategoryAttributionService.GetCycleRange(year, monthIndex, cycleDay);
            var start = TransactionDate.StartOfDate(DateOnly.FromDateTime(range.start));
            var end = TransactionDate.ExclusiveEndOfDate(DateOnly.FromDateTime(range.end));
            var txs = txList
                .Where(t => DateTime.Parse((string)t.Date) >= start && DateTime.Parse((string)t.Date) < end)
                .ToList();
            if (txs.Count == 0) continue;

            if (!includeAmounts)
            {
                summaries.Add(new
                {
                    month = FinancialConstants.MonthAbbreviations[monthIndex - 1],
                    year,
                    label = range.label,
                    transactionCount = txs.Count,
                    categoryCounts = txs
                        .GroupBy(t => (string)t.Category)
                        .Select(g => new { category = g.Key, count = g.Count() })
                        .OrderByDescending(x => x.count)
                        .Take(8)
                        .ToList(),
                    ledgerCounts = txs
                        .GroupBy(t => (string)t.LedgerCategory)
                        .Select(g => new { ledgerCategory = g.Key, count = g.Count() })
                        .OrderByDescending(x => x.count)
                        .Take(8)
                        .ToList(),
                    recentTransactions = txs
                        .OrderByDescending(t => (string)t.Date)
                        .Take(8)
                        .Select(t => new
                        {
                            t.Id,
                            t.Date,
                            t.Description,
                            t.Category,
                            t.LedgerCategory
                        })
                        .ToList()
                });
                continue;
            }

            var categorySpend = txs
                .Where(t => (decimal)t.Amount < 0)
                .GroupBy(t => (string)t.Category)
                .Select(g => new { category = g.Key, outflow = Math.Abs(g.Sum(t => (decimal)t.Amount)) })
                .OrderByDescending(x => x.outflow)
                .Take(8)
                .ToList();

            var ledgerNet = LedgerCategories
                .Where(c => c != "Income")
                .Select(c => new
                {
                    ledgerCategory = c,
                    net = txs.Sum(t => CategoryAttributionService.GetCategoryAmount(new Models.Transaction
                    {
                        Amount = (decimal)t.Amount,
                        LedgerCategory = (string)t.LedgerCategory
                    }, c))
                })
                .ToList();

            summaries.Add(new
            {
                month = FinancialConstants.MonthAbbreviations[monthIndex - 1],
                year,
                label = range.label,
                transactionCount = txs.Count,
                income = txs.Where(t => (decimal)t.Amount > 0 && !((string)t.LedgerCategory).StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase)).Sum(t => (decimal)t.Amount),
                outflow = Math.Abs(txs.Where(t => (decimal)t.Amount < 0).Sum(t => (decimal)t.Amount)),
                netChange = txs.Where(t => !((string)t.LedgerCategory).StartsWith("Transfer:", StringComparison.OrdinalIgnoreCase)).Sum(t => (decimal)t.Amount),
                categorySpend,
                ledgerNet,
                largestTransactions = txs
                    .OrderByDescending(t => Math.Abs((decimal)t.Amount))
                    .Take(5)
                    .Select(t => new
                    {
                        t.Id,
                        t.Date,
                        t.Description,
                        t.Category,
                        t.LedgerCategory,
                        t.Amount
                    })
                    .ToList()
            });
        }
        return summaries;
    }

    private static (int Year, int MonthIndex) AddMonths(int year, int monthIndex, int offset)
    {
        var zeroBased = (monthIndex - 1) + offset;
        year += (int)Math.Floor(zeroBased / 12.0);
        var month = ((zeroBased % 12) + 12) % 12 + 1;
        return (year, month);
    }

    private async Task<AiChatResponse?> TryResolveLedgerEditAsync(string message, bool sensitiveMode)
    {
        if (!LooksLikeLedgerEditCommand(message))
        {
            return null;
        }

        var setting = await _context.FinancialSettings.AsNoTracking().FirstOrDefaultAsync();
        var defaultYear = setting?.SelectedYear ?? DateTime.Now.Year;
        if (!TryExtractDate(message, defaultYear, out var targetDate, out var matchedDateText))
        {
            return null;
        }

        var searchText = ExtractLedgerEditSearchText(message, matchedDateText);
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return null;
        }

        if (sensitiveMode)
        {
            return new AiChatResponse("Unhide balances before using AI to edit ledger records.", []);
        }

        var dateStart = TransactionDate.StartOfDate(targetDate);
        var dateEnd = TransactionDate.ExclusiveEndOfDate(targetDate);
        var normalizedSearch = searchText.ToLowerInvariant();
        var matches = await _context.Transactions
            .AsNoTracking()
            .Where(t =>
                t.LedgerCategory != "Discarded" &&
                t.Date >= dateStart &&
                t.Date < dateEnd &&
                (t.Description.ToLower().Contains(normalizedSearch) ||
                 t.Category.ToLower().Contains(normalizedSearch) ||
                 t.LedgerCategory.ToLower().Contains(normalizedSearch)))
            .OrderByDescending(t => t.Date)
            .ThenByDescending(t => t.Id)
            .Select(t => new
            {
                t.Id,
                t.Description
            })
            .Take(4)
            .ToListAsync();

        var formattedDate = FormatDateForReply(targetDate);
        if (matches.Count == 0)
        {
            return new AiChatResponse($"I couldn't find a ledger record matching \"{searchText}\" on {formattedDate}.", []);
        }

        if (matches.Count > 1)
        {
            return new AiChatResponse($"I found {matches.Count} ledger records matching \"{searchText}\" on {formattedDate}. Please specify which one to edit.", []);
        }

        var payload = new Dictionary<string, object?>
        {
            ["id"] = matches[0].Id,
            ["changes"] = new Dictionary<string, object?>()
        };
        return new AiChatResponse($"Opened the \"{matches[0].Description}\" record from {formattedDate}.", [new AiUiAction("openEditLedgerDraft", payload)]);
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
        var withoutDate = Regex.Replace(message, Regex.Escape(matchedDateText), " ", RegexOptions.IgnoreCase);
        var beforeChangeTarget = Regex.Split(withoutDate, @"\s+\b(to|into|as)\b\s+", RegexOptions.IgnoreCase)[0];
        var cleaned = Regex.Replace(
            beforeChangeTarget,
            @"\b(edit|update|change|modify|ledger|record|transaction|entry|on|at|for|please|can|you|the|my|a|an)\b",
            " ",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"[^\p{L}\p{N}\s'-]", " ");
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    private static string FormatDateForReply(DateOnly date)
    {
        return date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
    }

    private static string BuildPrompt(string message, IReadOnlyList<AiChatMessage> history, AiContext context)
    {
        var contextJson = JsonSerializer.Serialize(context, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var historyJson = JsonSerializer.Serialize(history.TakeLast(6), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        return $@"You are FinancialApp AI. Return ONLY valid JSON, no markdown.

User message: {JsonSerializer.Serialize(message)}
Recent chat JSON: {historyJson}
App context JSON: {contextJson}

Rules:
- Only fulfill these capabilities: cycle analysis, ledger navigation/filtering, opening add/edit drafts for ledger/recurring/wishlist, and wishlist/recurring Q&A.
- If outside scope, reply exactly or similarly: ""I'm unable to perform that action.""
- Never modify settings. Never create/update/delete ledger, wishlist, or recurring records. Draft/open modal only.
- Transaction creation means openAddLedgerDraft only; never save/send a transaction.
- Never directly toggle recurring active state. If the user asks to turn a recurring payment on or off, explain that AI cannot perform that direct toggle.
- Delete requests are unsupported. Reply that AI cannot delete records.
- If ambiguous about target record, category, cycle, action type, amount, or whether the user wants ledger vs recurring vs wishlist, ask one concise clarification with at most 3 questions, return no actions, and set closeChat false.
- Use only categories, ledger categories, cycles, and record ids from App context.
- For ledger edit requests, return openEditLedgerDraft when you can identify one exact transaction. Do not return openLedger just to search unless the user explicitly asks to show/filter/navigate.
- If the user asks a question (for example ""how many"", ""what"", ""why"", ""compare"", ""analyze""), answer the question and return no actions unless the user explicitly asks to open/show/filter/navigate the ledger.
- If sensitiveMode is true, exact amounts/prices/balances are not available and must not be asked for or revealed. Refuse amount-specific questions briefly. Do not return edit actions in sensitiveMode.
- Use at most one action unless the user clearly asked for more.
- Set closeChat true only when the request is fully handled by a non-edit returned action and your reply contains no follow-up question. For edit actions, Q&A, analysis, rejected, or clarification replies, set closeChat false.
- Do not end replies with optional follow-up offers or questions like ""would you like a summary?"".

Allowed actions:
- openLedger payload: {{ month, year, allCycles, category, ledgerCategory, txType, search, date }}
- openAddLedgerDraft payload: {{ description, amount, txType, category, ledgerCategory, date }}
- openAddRecurringDraft payload: {{ name, amount, category, ledgerCategory, startDate, endDate }}
- openAddWishlistDraft payload: {{ name, price, priority, isActive }}
- openEditLedgerDraft payload: {{ id, changes }}
- openEditRecurringDraft payload: {{ id, changes }}
- openEditWishlistDraft payload: {{ id, changes }}

Output schema:
{{
  ""reply"": ""short user-facing reply"",
  ""closeChat"": false,
  ""actions"": [
    {{ ""type"": ""one allowed action type"", ""payload"": {{ }} }}
  ]
}}";
    }

    private AiChatResponse ParseAndValidateResponse(string text, AiContext context, string userMessage)
    {
        text = StripJsonFence(text.Trim());
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var reply = root.TryGetProperty("reply", out var replyProp) ? replyProp.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(reply)) reply = "I'm unable to perform that action.";

        var actions = new List<AiUiAction>();
        if (root.TryGetProperty("actions", out var actionsProp) && actionsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var actionEl in actionsProp.EnumerateArray().Take(3))
            {
                if (!actionEl.TryGetProperty("type", out var typeProp)) continue;
                var type = typeProp.GetString() ?? "";
                if (!AllowedActionTypes.Contains(type)) continue;
                var payload = actionEl.TryGetProperty("payload", out var payloadProp) && payloadProp.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(payloadProp.GetRawText()) ?? []
                    : [];
                if (context.SensitiveMode && type.StartsWith("openEdit", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsQuestionOnlyRequest(userMessage) && type.Equals("openLedger", StringComparison.OrdinalIgnoreCase)) continue;
                if (context.SensitiveMode)
                {
                    RemoveSensitivePayloadFields(payload);
                }
                if (!IsActionSafe(type, payload, context)) continue;
                actions.Add(new AiUiAction(type, payload));
            }
        }

        var closeChat = false;
        if (actions.Count > 0 &&
            root.TryGetProperty("closeChat", out var closeChatProp) &&
            closeChatProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            closeChat = closeChatProp.GetBoolean() &&
                actions.All(action => !action.Type.StartsWith("openEdit", StringComparison.OrdinalIgnoreCase)) &&
                !LooksLikeFollowUp(reply);
        }

        return new AiChatResponse(reply, actions, closeChat);
    }

    private static void RemoveSensitivePayloadFields(Dictionary<string, object?> payload)
    {
        payload.Remove("amount");
        payload.Remove("price");
        if (payload.TryGetValue("changes", out var changesObj) && changesObj is JsonElement changesElement && changesElement.ValueKind == JsonValueKind.Object)
        {
            var changes = JsonSerializer.Deserialize<Dictionary<string, object?>>(changesElement.GetRawText()) ?? [];
            changes.Remove("amount");
            changes.Remove("price");
            payload["changes"] = changes;
        }
        else if (changesObj is Dictionary<string, object?> changes)
        {
            changes.Remove("amount");
            changes.Remove("price");
        }
    }

    private static bool IsQuestionOnlyRequest(string message)
    {
        var lower = message.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower)) return false;

        var asksQuestion = lower.Contains('?') ||
            lower.StartsWith("how ") ||
            lower.StartsWith("what ") ||
            lower.StartsWith("why ") ||
            lower.StartsWith("when ") ||
            lower.StartsWith("where ") ||
            lower.StartsWith("who ") ||
            lower.StartsWith("which ") ||
            lower.StartsWith("can you tell") ||
            lower.StartsWith("tell me") ||
            lower.StartsWith("analyze") ||
            lower.StartsWith("compare");
        if (!asksQuestion) return false;

        return !(lower.Contains("open ") ||
            lower.Contains("show me ") ||
            lower.Contains("go to ") ||
            lower.Contains("navigate") ||
            lower.Contains("filter") ||
            lower.Contains("apply filter") ||
            lower.Contains("take me"));
    }

    private static bool LooksLikeDeleteCommand(string message)
    {
        var lower = message.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower)) return false;

        var startsAsDelete = lower.StartsWith("delete ") ||
            lower.StartsWith("remove ") ||
            lower.StartsWith("erase ") ||
            lower.StartsWith("cancel ") ||
            lower.StartsWith("discard ");
        if (!startsAsDelete) return false;

        return !(lower.StartsWith("what ") ||
            lower.StartsWith("which ") ||
            lower.StartsWith("why ") ||
            lower.StartsWith("how ") ||
            lower.Contains(" redundant") ||
            lower.Contains(" should i "));
    }

    private static bool LooksLikeFollowUp(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;
        if (reply.Contains('?')) return true;

        var lower = reply.ToLowerInvariant();
        return lower.Contains("clarify") ||
            lower.Contains("which ") ||
            lower.Contains("what ") ||
            lower.Contains("please choose") ||
            lower.Contains("please specify") ||
            lower.Contains("do you want") ||
            lower.Contains("not sure") ||
            lower.Contains("unclear");
    }

    private static bool IsActionSafe(string type, Dictionary<string, object?> payload, AiContext context)
    {
        if (type.Equals("openEditRecurringDraft", StringComparison.OrdinalIgnoreCase))
        {
            return HasKnownId(payload, "id", context.RecurringPayments);
        }
        if (type.Equals("openEditWishlistDraft", StringComparison.OrdinalIgnoreCase))
        {
            return HasKnownId(payload, "id", context.WishlistItems);
        }
        if (type.Equals("openEditLedgerDraft", StringComparison.OrdinalIgnoreCase))
        {
            return HasKnownId(payload, "id", context.RecentTransactions);
        }
        return true;
    }

    private static bool HasKnownId(Dictionary<string, object?> payload, string key, object records)
    {
        if (!payload.TryGetValue(key, out var idObj) || idObj == null) return false;
        var id = idObj.ToString();
        if (string.IsNullOrWhiteSpace(id)) return false;
        var json = JsonSerializer.Serialize(records);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array &&
            doc.RootElement.EnumerateArray().Any(e =>
                (e.TryGetProperty("id", out var p) || e.TryGetProperty("Id", out p)) &&
                string.Equals(p.ToString(), id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> GenerateGeminiTextAsync(string prompt, double temperature, int maxOutputTokens, string apiKey)
    {
        var requestBody = new
        {
            contents = new[]
            {
                new { parts = new object[] { new { text = prompt } } }
            },
            generationConfig = new { temperature, maxOutputTokens }
        };

        var primaryModel = _configuration["GeminiModel"];
        if (string.IsNullOrWhiteSpace(primaryModel)) primaryModel = "gemini-3.1-flash-lite";
        const string fallbackModel = "gemini-3.5-flash";

        try
        {
            return await CallGeminiAsync(primaryModel, requestBody, apiKey);
        }
        catch (GeminiUnavailableException)
        {
            _logger.LogWarning("Primary model {PrimaryModel} unavailable, retrying with fallback {FallbackModel}.", primaryModel, fallbackModel);
            return await CallGeminiAsync(fallbackModel, requestBody, apiKey);
        }
    }

    private async Task<string> CallGeminiAsync(string model, object requestBody, string apiKey)
    {
        var geminiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, geminiUrl);
        httpRequest.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(requestBody));
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await _httpClient.SendAsync(httpRequest);
        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            throw new GeminiUnavailableException();
        }
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Gemini API error {Status} (model {Model}): {Body}", response.StatusCode, model, errorBody);
            throw new AiAssistantUserException("AI service returned an error. Please try again.");
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        using var geminiDoc = JsonDocument.Parse(responseBody);
        var candidate = geminiDoc.RootElement.GetProperty("candidates")[0];
        return candidate.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
    }

    private static string StripJsonFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstNewline = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && lastFence > firstNewline
            ? text.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim()
            : text;
    }

    private sealed record CycleKey(int Year, int MonthIndex);

    private sealed record AiContext(
        string Currency,
        string Today,
        bool SensitiveMode,
        object ActiveCycle,
        IReadOnlyList<string> Categories,
        IReadOnlyList<string> LedgerCategories,
        object AvailableCycles,
        object CycleSummaries,
        object RecentTransactions,
        object RecurringPayments,
        object WishlistItems);

    private sealed class GeminiUnavailableException : Exception { }

    public sealed class AiAssistantUserException : Exception
    {
        public AiAssistantUserException(string message) : base(message) { }
    }
}
