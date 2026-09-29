using System.Text.Json.Nodes;

namespace FinancialAppApi.Services.AI.Tools;

// Defense in depth for sensitive mode. Each tool already omits money at its source; this pass
// strips any money-bearing property that slipped through, at any depth, before the model sees it.
public static class AiSensitiveMasker
{
    // Property names that carry a money figure wherever they appear in a tool result. A tool
    // that emits money under another name declares it in IAiTool.AmountProperties.
    public static readonly IReadOnlySet<string> MoneyProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "amount", "balance", "price", "total", "totalOutflow", "totalInflow", "totalIncome",
        "outflow", "inflow", "income", "otherInflow", "netChange", "net", "spent", "remaining",
        "limit", "target", "targetAmount", "value", "cost", "monthlyTotal", "annualTotal",
        "averageDailySpend", "projectedSpend", "pendingCommitted", "principal", "payment",
        "minAmount", "maxAmount"
    };

    public static void Mask(JsonNode? node, IReadOnlySet<string> extraProperties)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToList())
                {
                    if (MoneyProperties.Contains(key) || extraProperties.Contains(key))
                    {
                        obj.Remove(key);
                        continue;
                    }
                    Mask(obj[key], extraProperties);
                }
                break;
            case JsonArray array:
                foreach (var item in array) Mask(item, extraProperties);
                break;
        }
    }
}
