using FinancialAppApi.Database;
using FinancialAppApi.Models;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.Investments;

/// <summary>
/// The constant-currency rules every investment reader shares. Each figure lives in its own
/// currency; a total in the app's currency multiplies each currency's figure by today's rate, the
/// same rate for every date, so performance shows the markets and never the exchange rate. That
/// is also why a change of app currency recomputes nothing: only which rate is read changes.
/// </summary>
internal static class InvestmentConstantCurrency
{
    /// <summary>
    /// How old a rate may be and still stand for today: enough to bridge a weekend plus a run of
    /// market holidays to the latest close. An older rate is not today's rate, so it is treated as
    /// missing (the figure shows as unknown) and is never fetched, which would only spend quota.
    /// </summary>
    public const int RateLookbackDays = 7;

    public static DateOnly RateNeedFrom(DateOnly today) => today.AddDays(-RateLookbackDays);

    /// <summary>
    /// The money a cash movement put into each currency's pot. A deposit or withdrawal is one leg.
    /// A conversion is two: it takes money out of one currency and puts the amount actually
    /// received into the other, so the rate you got is already inside the figures and the
    /// converted money counts as put into the currency it now sits in.
    /// </summary>
    public static IEnumerable<(string Currency, decimal Amount)> ContributionLegs(InvestmentCashFlow flow)
    {
        yield return (flow.Currency.ToUpperInvariant(), flow.Amount);
        if (flow.ToCurrency is not null && flow.ToAmount is not null)
            yield return (flow.ToCurrency.ToUpperInvariant(), flow.ToAmount.Value);
    }

    /// <summary>
    /// Only the newest stored bar of every pair that can reach these currencies, directly or via
    /// USD, and only if it is recent enough to stand for today: today's rate is the only one
    /// constant-currency reporting reads, so the read stays the same size however old the account grows.
    /// </summary>
    public static async Task<IReadOnlyList<FxRateBar>> LatestFxBarsAsync(
        AppDbContext context,
        string providerId,
        IReadOnlyCollection<string> currencies,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var codes = currencies.Select(value => value.ToUpperInvariant()).Append("USD").Distinct().ToList();
        var oldest = RateNeedFrom(today);
        return await context.FxRateBars.AsNoTracking()
            .Where(value => value.Provider == providerId && value.MarketDate <= today &&
                            value.MarketDate >= oldest &&
                            (codes.Contains(value.BaseCurrency) || codes.Contains(value.QuoteCurrency)))
            .GroupBy(value => new { value.BaseCurrency, value.QuoteCurrency })
            .Select(group => group.OrderByDescending(value => value.MarketDate).First())
            .ToListAsync(cancellationToken);
    }
}
