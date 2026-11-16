namespace FinancialAppApi.Services.Investments;

public sealed record DatedInvestmentFlow(DateOnly Date, decimal Amount);

/// <summary>
/// Money-weighted annual return for dated portfolio cash flows and the terminal value.
/// Negative amounts are money put in. Positive amounts are money taken out or still held.
/// </summary>
public static class InvestmentReturnCalculator
{
    private const double DaysPerYear = 365d;
    private const int MaxIterations = 120;
    private const double Tolerance = 1e-7;
    private const double MinRate = -0.9999;
    private const double MaxRate = 1e5;
    private const int ScanIntervals = 512;

    public static decimal? Calculate(IEnumerable<DatedInvestmentFlow> source)
    {
        var flows = source
            .GroupBy(flow => flow.Date)
            .Select(day => new DatedInvestmentFlow(day.Key, day.Sum(flow => flow.Amount)))
            .Where(flow => flow.Amount != 0)
            .ToList();
        if (flows.Count < 2 ||
            !flows.Any(flow => flow.Amount < 0) ||
            !flows.Any(flow => flow.Amount > 0))
            return null;

        var start = flows.Min(flow => flow.Date);
        var scale = flows.Max(flow => Math.Abs(flow.Amount));
        var breaksEven = flows.Sum(flow => flow.Amount) == 0;
        // Changing currency units should not affect the root or the stopping condition.
        var normalised = flows
            .Select(flow => ((flow.Date.DayNumber - start.DayNumber) / DaysPerYear, (double)(flow.Amount / scale)))
            .ToList();

        static double PresentValue(IEnumerable<(double Years, double Amount)> values, double rate)
            => values.Sum(flow => flow.Amount / Math.Pow(1 + rate, flow.Years));

        static double Bisect(
            IReadOnlyList<(double Years, double Amount)> values,
            double low,
            double high,
            double lowValue)
        {
            var rate = 0d;
            for (var iteration = 0; iteration < MaxIterations; iteration++)
            {
                rate = (low + high) / 2;
                var value = PresentValue(values, rate);
                if (value == 0 || high - low < Tolerance * (1 + Math.Abs(rate))) return rate;
                if (Math.Sign(value) != Math.Sign(lowValue)) high = rate;
                else
                {
                    low = rate;
                    lowValue = value;
                }
            }
            return rate;
        }

        var roots = new List<double>();
        var minLog = Math.Log(1 + MinRate);
        var maxLog = Math.Log(1 + MaxRate);
        var previousRate = MinRate;
        var previousValue = PresentValue(normalised, previousRate);
        if (!double.IsFinite(previousValue)) return null;

        for (var index = 1; index <= ScanIntervals; index++)
        {
            var rate = Math.Exp(minLog + (maxLog - minLog) * index / ScanIntervals) - 1;
            var value = PresentValue(normalised, rate);
            if (!double.IsFinite(value)) return null;
            if (previousValue == 0) roots.Add(previousRate);
            else if (value != 0 && Math.Sign(previousValue) != Math.Sign(value))
                roots.Add(breaksEven && previousRate < 0 && rate > 0
                    ? 0
                    : Bisect(normalised, previousRate, rate, previousValue));
            previousRate = rate;
            previousValue = value;
        }
        if (previousValue == 0) roots.Add(previousRate);

        var distinctRoots = roots
            .Order()
            .Aggregate(new List<double>(), (values, rate) =>
            {
                if (values.Count == 0 || Math.Abs(values[^1] - rate) > Tolerance * (1 + Math.Abs(rate)))
                    values.Add(rate);
                return values;
            });
        return distinctRoots.Count == 1 ? (decimal)distinctRoots[0] : null;
    }
}
