using System.Net.Http.Headers;
using System.Text.Json;
using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services;

public sealed record AiChatMessage(string Role, string Content);
public sealed record AiChatRequest(string Message, IReadOnlyList<AiChatMessage>? History);
public sealed record AiChatResponse(string Reply, IReadOnlyList<AiUiAction> Actions);
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
        "openEditWishlistDraft",
        "setRecurringActive"
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

        var apiKey = _configuration["GeminiApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AiChatResponse("AI chat is not configured on the server.", []);
        }

        var context = await BuildContextAsync();
        var prompt = BuildPrompt(message, request.History ?? [], context);
        var text = await GenerateGeminiTextAsync(prompt, 0.15, 1400, apiKey);
        return ParseAndValidateResponse(text, context);
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

        var recentTransactions = allTransactions.Take(80).ToList();
        var cycleSummaries = BuildCycleWindowSummaries(allTransactions, selectedYear, selectedMonthIndex, cycleDay);

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
            SensitiveMode: setting?.HideSensitive ?? true,
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
            RecurringPayments: recurring,
            WishlistItems: wishlist);
    }

    private static List<object> BuildCycleWindowSummaries(
        IEnumerable<dynamic> transactions,
        int centerYear,
        int centerMonthIndex,
        int cycleDay)
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

    private static string BuildPrompt(string message, IReadOnlyList<AiChatMessage> history, AiContext context)
    {
        var contextJson = JsonSerializer.Serialize(context, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var historyJson = JsonSerializer.Serialize(history.TakeLast(6), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        return $@"You are FinancialApp AI. Return ONLY valid JSON, no markdown.

User message: {JsonSerializer.Serialize(message)}
Recent chat JSON: {historyJson}
App context JSON: {contextJson}

Rules:
- Only fulfill these capabilities: cycle analysis, ledger navigation/filtering, opening add/edit drafts for ledger/recurring/wishlist, wishlist/recurring Q&A, and direct recurring active toggle.
- If outside scope, reply exactly or similarly: ""I'm unable to perform that action.""
- Never modify settings. Never create/update/delete ledger, wishlist, or recurring records. Draft/open modal only.
- Transaction creation means openAddLedgerDraft only; never save/send a transaction.
- Recurring active toggle may be direct only when exactly one matching recurring payment is clear.
- If ambiguous, ask one concise clarification with at most 3 questions and return no actions.
- Use only categories, ledger categories, cycles, and record ids from App context.
- If sensitiveMode is true, do not reveal exact financial amounts in reply. You may still navigate or open drafts.
- Use at most one action unless the user clearly asked for more.

Allowed actions:
- openLedger payload: {{ month, year, allCycles, category, ledgerCategory, txType, search }}
- openAddLedgerDraft payload: {{ description, amount, txType, category, ledgerCategory, date }}
- openAddRecurringDraft payload: {{ name, amount, category, ledgerCategory, startDate, endDate }}
- openAddWishlistDraft payload: {{ name, price, priority, isActive }}
- openEditLedgerDraft payload: {{ id, changes }}
- openEditRecurringDraft payload: {{ id, changes }}
- openEditWishlistDraft payload: {{ id, changes }}
- setRecurringActive payload: {{ id, active }}

Output schema:
{{
  ""reply"": ""short user-facing reply"",
  ""actions"": [
    {{ ""type"": ""one allowed action type"", ""payload"": {{ }} }}
  ]
}}";
    }

    private AiChatResponse ParseAndValidateResponse(string text, AiContext context)
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
                if (!IsActionSafe(type, payload, context)) continue;
                actions.Add(new AiUiAction(type, payload));
            }
        }

        return new AiChatResponse(reply, actions);
    }

    private static bool IsActionSafe(string type, Dictionary<string, object?> payload, AiContext context)
    {
        if (type.Equals("setRecurringActive", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("openEditRecurringDraft", StringComparison.OrdinalIgnoreCase))
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
