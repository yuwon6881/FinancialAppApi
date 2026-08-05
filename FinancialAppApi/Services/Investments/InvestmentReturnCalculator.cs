namespace FinancialAppApi.Services.Investments;

public sealed record DatedInvestmentFlow(DateOnly Date, decimal Amount);

/// <summary>
/// Money-weighted annual return for irregularly dated external portfolio cash flows.
/// Negative amounts are money the investor contributed; positive amounts are money
/// returned to the investor, including the terminal portfolio value.
/// </summary>
public static class InvestmentReturnCalculator
{
    private const double DaysPerYear = 365d;
    private const int MaxIterations = 120;
    private const double Tolerance = 1e-7;
    private const double MinRate = -0.9999;
    private const double MaxRate = 1e5;

    public static decimal? Calculate(IEnumerable<DatedInvestmentFlow> source)
    {
        var flows = source
            .Where(flow => flow.Amount != 0)
            .Select(flow => (flow.Date, Amount: (double)flow.Amount))
            .ToList();
        if (flows.Count < 2 ||
            !flows.Any(flow => flow.Amount < 0) ||
            !flows.Any(flow => flow.Amount > 0))
            return null;

        var start = flows.Min(flow => flow.Date);
        var end = flows.Max(flow => flow.Date);
        if (end == start) return null;

        var normalised = flows
            .Select(flow => (Years: flow.Date.DayNumber - start.DayNumber, flow.Amount))
            .Select(flow => (Years: flow.Years / DaysPerYear, flow.Amount))
            .ToList();

        static double PresentValue(IEnumerable<(double Years, double Amount)> values, double rate)
            => values.Sum(flow => flow.Amount / Math.Pow(1 + rate, flow.Years));

        var low = MinRate;
        var high = MaxRate;
        var lowValue = PresentValue(normalised, low);
        var highValue = PresentValue(normalised, high);
        if (!double.IsFinite(lowValue) || !double.IsFinite(highValue) || lowValue * highValue > 0)
            return null;

        var rate = 0d;
        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            rate = (low + high) / 2;
            var value = PresentValue(normalised, rate);
            if (!double.IsFinite(value)) return null;
            if (Math.Abs(value) < Tolerance || high - low < Tolerance) return (decimal)rate;
            if (value * lowValue < 0)
            {
                high = rate;
            }
            else
            {
                low = rate;
                lowValue = value;
            }
        }

        return Math.Abs(PresentValue(normalised, rate)) < 1e-3 ? (decimal)rate : null;
    }
}
