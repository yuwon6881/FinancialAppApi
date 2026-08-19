using System.Globalization;

namespace FinancialAppApi.Services.Stability;

/// <summary>
/// The four shares an <c>IncomeSplit:</c> ledger category encodes, held as fractions of 1 rather
/// than percentages so the arithmetic never has to divide by 100 mid-calculation.
/// </summary>
public sealed record IncomeSplitSpec(decimal Essentials, decimal Growth, decimal Stability, decimal Rewards)
{
    public decimal Total => Essentials + Growth + Stability + Rewards;

    /// <summary>
    /// The comma-separated percentage list that follows <c>IncomeSplit:</c> on the wire. Written
    /// with InvariantCulture because <c>CategoryAttributionService.GetCategoryAmount</c> parses it
    /// that way -- a host with a comma decimal separator would otherwise read "12.5" as 125.
    /// Four decimal places matches the client encoder; an emergency-fund top-up rarely lands on a
    /// round percentage, and two places would round it away.
    /// </summary>
    public string ToSpecString()
    {
        return string.Join(",", new[] { Essentials, Growth, Stability, Rewards }
            .Select(share => (share * 100m).ToString("0.####", CultureInfo.InvariantCulture)));
    }

    /// <summary>Parses the percentage list back into fractions. Rejects negatives and bad counts.</summary>
    public static bool TryParseSpecString(string? spec, out IncomeSplitSpec parsed)
    {
        parsed = new IncomeSplitSpec(0m, 0m, 0m, 0m);
        if (string.IsNullOrWhiteSpace(spec)) return false;

        var parts = spec.Split(',');
        if (parts.Length != 4) return false;

        var shares = new decimal[4];
        for (var i = 0; i < 4; i++)
        {
            if (!decimal.TryParse(parts[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var percentage)
                || percentage < 0m)
            {
                return false;
            }
            shares[i] = percentage / 100m;
        }

        parsed = new IncomeSplitSpec(shares[0], shares[1], shares[2], shares[3]);
        return true;
    }
}

/// <summary>
/// The single place income is divided four ways. Pure, EF-free and clock-free so the cap, the
/// redirect and the emergency-fund top-up can be tested directly, and mirrored on the client in
/// <c>lib/incomeSplit.ts</c> the same way <c>lib/savingsGoals.ts</c> mirrors
/// <see cref="SavingsGoals.SavingsGoalPacing"/>.
/// <para>
/// Composition order is fixed and load-bearing: baseline allocations, then the optional recovery
/// top-up, then the target clamp, then the overflow redirect. The clamp must run after the top-up
/// so a top-up can never push the fund past its target, and the redirect must run last so every
/// share the fund could not absorb -- from either source -- lands where the setting says.
/// </para>
/// </summary>
public static class IncomeSplitPlanner
{
    private const string Essentials = "Essentials";
    private const string Growth = "Growth";
    private const string Rewards = "Rewards";

    /// <summary>Rounding floor for share comparisons: half a cent on a 1,000 unit salary.</summary>
    private const decimal Epsilon = 0.0000005m;

    /// <summary>
    /// Which buckets absorb a share the emergency fund cannot take, and in what proportion.
    /// <para>
    /// Parses both shapes the setting uses -- "Growth 100%" and "Split: Essentials 50%, Growth 50%"
    /// -- rather than matching whole strings. The client used to match only the three "X 100%"
    /// values and send every "Split: ..." value to Growth+Rewards, so two of the six options
    /// silently ignored Essentials despite being labelled for it.
    /// </para>
    /// Stability is never a redirect target: the redirect exists precisely because Stability could
    /// not take the money.
    /// </summary>
    public static IReadOnlyList<(string Bucket, decimal Weight)> ResolveRedirectTargets(string? redirect)
    {
        var fallback = new[] { (Growth, 0.5m), (Rewards, 0.5m) };
        if (string.IsNullOrWhiteSpace(redirect)) return fallback;

        var body = redirect.Trim();
        if (body.StartsWith("Split:", StringComparison.OrdinalIgnoreCase))
        {
            body = body["Split:".Length..];
        }

        var parsed = new List<(string Bucket, decimal Weight)>();
        foreach (var token in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = token.EndsWith('%') ? token[..^1].TrimEnd() : token;
            var lastSpace = text.LastIndexOf(' ');
            if (lastSpace <= 0) continue;

            var bucket = NormalizeBucket(text[..lastSpace].Trim());
            if (bucket == null) continue;
            if (!decimal.TryParse(text[(lastSpace + 1)..], NumberStyles.Any, CultureInfo.InvariantCulture, out var weight)
                || weight <= 0m)
            {
                continue;
            }

            parsed.Add((bucket, weight));
        }

        if (parsed.Count == 0) return fallback;

        var total = parsed.Sum(entry => entry.Weight);
        return parsed.Select(entry => (entry.Bucket, entry.Weight / total)).ToList();
    }

    /// <summary>
    /// Builds the split for an income amount from the user's configured allocations, optionally
    /// adding an emergency-fund top-up on top of the usual Stability share.
    /// <para>
    /// <c>requestedTopUp</c> is an absolute currency amount to add to Stability beyond its normal
    /// share, bounded on save by <c>TransactionPersistenceService.MaximumRecoveryTopUp</c>. The
    /// per-bucket committed floor is a client-side default, not a server rule. Pass zero for the
    /// ordinary case.
    /// </para>
    /// </summary>
    public static IncomeSplitSpec Resolve(
        decimal amount,
        decimal essentialsAlloc,
        decimal growthAlloc,
        decimal stabilityAlloc,
        decimal rewardsAlloc,
        decimal stabilityBalance,
        decimal stabilityTarget,
        string? stabilityOverflowRedirect,
        decimal requestedTopUp)
    {
        var baseline = Normalize(new IncomeSplitSpec(essentialsAlloc, growthAlloc, stabilityAlloc, rewardsAlloc));
        if (amount <= 0m) return baseline;

        var withTopUp = ApplyTopUp(baseline, amount, requestedTopUp);
        return ClampToTarget(withTopUp, amount, stabilityBalance, stabilityTarget, stabilityOverflowRedirect);
    }

    /// <summary>
    /// Takes a split the client proposed and holds it to the same rules the server would have
    /// applied itself, reporting whether it had to change.
    /// <para>
    /// Clamped rather than rejected on purpose: a proposal can arrive from an offline replay whose
    /// balance was stale by the time it drained, and rejecting it would fail a salary entry the
    /// user has no way to redo. Clamping saves the money in the nearest legal place and sends the
    /// excess wherever the redirect setting points, which is that setting's entire purpose.
    /// </para>
    /// </summary>
    public static (IncomeSplitSpec Spec, bool WasClamped) ClampProposed(
        IncomeSplitSpec proposed,
        decimal amount,
        decimal stabilityBalance,
        decimal stabilityTarget,
        string? stabilityOverflowRedirect)
    {
        var normalized = Normalize(proposed);
        if (amount <= 0m) return (normalized, false);

        var clamped = ClampToTarget(normalized, amount, stabilityBalance, stabilityTarget, stabilityOverflowRedirect);
        return (clamped, Math.Abs(clamped.Stability - normalized.Stability) > Epsilon);
    }

    private static IncomeSplitSpec ApplyTopUp(IncomeSplitSpec spec, decimal amount, decimal requestedTopUp)
    {
        if (requestedTopUp <= 0m) return spec;

        // A top-up can only move money the other three buckets were actually going to receive.
        var others = spec.Essentials + spec.Growth + spec.Rewards;
        if (others <= 0m) return spec;

        var topUpShare = Math.Min(requestedTopUp / amount, others);
        var drawn = topUpShare / others;

        return new IncomeSplitSpec(
            spec.Essentials * (1m - drawn),
            spec.Growth * (1m - drawn),
            spec.Stability + topUpShare,
            spec.Rewards * (1m - drawn));
    }

    private static IncomeSplitSpec ClampToTarget(
        IncomeSplitSpec spec,
        decimal amount,
        decimal stabilityBalance,
        decimal stabilityTarget,
        string? stabilityOverflowRedirect)
    {
        // A non-positive target means "no target set", not "cap the fund at nothing". Capping at
        // zero would quietly stop all Stability funding for anyone who never picked a figure, which
        // is far worse than not capping. FinancialService reads stabilityPercentReached with the
        // same guard.
        if (stabilityTarget <= 0m) return Normalize(spec);

        var headroom = Math.Max(0m, stabilityTarget - stabilityBalance);
        var wanted = spec.Stability * amount;
        // Normalized even when nothing is clamped: the top-up step divides by the other three
        // buckets' combined share, which is routinely a repeating decimal, and an un-normalized
        // spec would carry that drift onto the wire.
        if (wanted <= headroom) return Normalize(spec);

        var allowedShare = Math.Max(0m, Math.Min(spec.Stability, headroom / amount));
        var redirectShare = spec.Stability - allowedShare;

        var essentials = spec.Essentials;
        var growth = spec.Growth;
        var rewards = spec.Rewards;

        foreach (var (bucket, weight) in ResolveRedirectTargets(stabilityOverflowRedirect))
        {
            var added = redirectShare * weight;
            if (bucket == Essentials) essentials += added;
            else if (bucket == Growth) growth += added;
            else if (bucket == Rewards) rewards += added;
        }

        return Normalize(new IncomeSplitSpec(essentials, growth, allowedShare, rewards));
    }

    /// <summary>
    /// Forces the four shares to sum to exactly 1, pushing any rounding drift onto the largest
    /// share so it is never visible as a missing cent. A spec that sums to nothing at all falls
    /// back to Essentials, which is where money is least likely to be missed.
    /// </summary>
    private static IncomeSplitSpec Normalize(IncomeSplitSpec spec)
    {
        var shares = new[]
        {
            Math.Max(0m, spec.Essentials),
            Math.Max(0m, spec.Growth),
            Math.Max(0m, spec.Stability),
            Math.Max(0m, spec.Rewards)
        };

        var total = shares.Sum();
        if (total <= 0m) return new IncomeSplitSpec(1m, 0m, 0m, 0m);

        for (var i = 0; i < shares.Length; i++)
        {
            shares[i] = Math.Round(shares[i] / total, 6, MidpointRounding.AwayFromZero);
        }

        var largest = 0;
        for (var i = 1; i < shares.Length; i++)
        {
            if (shares[i] > shares[largest]) largest = i;
        }
        shares[largest] += 1m - shares.Sum();

        return new IncomeSplitSpec(shares[0], shares[1], shares[2], shares[3]);
    }

    private static string? NormalizeBucket(string value)
    {
        if (string.Equals(value, Essentials, StringComparison.OrdinalIgnoreCase)) return Essentials;
        if (string.Equals(value, Growth, StringComparison.OrdinalIgnoreCase)) return Growth;
        if (string.Equals(value, Rewards, StringComparison.OrdinalIgnoreCase)) return Rewards;
        return null;
    }
}
