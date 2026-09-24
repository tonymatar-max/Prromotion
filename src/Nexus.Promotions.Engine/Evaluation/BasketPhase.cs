namespace Nexus.Promotions.Engine.Evaluation;

/// <summary>
/// Phase 3: basket-level promotions (P08 spend threshold, P09 tiered spend). The discount is spread
/// over the eligible lines in proportion to their net value; it never uses the B1 header discount.
/// </summary>
internal static class BasketPhase
{
    public static void Run(EvaluationContext ctx, IReadOnlyList<Promotion> promotions)
    {
        foreach (var p in promotions)
        {
            if (p.Tiers.Length == 0)
            {
                ctx.Traced(p, false, "Configuration: at least one spend tier is required");
                continue;
            }
            if (p.RewardKind is not (RewardKind.PercentOff or RewardKind.AmountOff))
            {
                ctx.Traced(p, false, "Configuration: basket reward must be PercentOff or AmountOff");
                continue;
            }

            var lines = ctx.Candidates(p, p.Scope).Where(l => !l.Added && l.PaidQty > 0).ToList();
            var spend = lines.Sum(l => l.NetPaidValue);
            var tier = p.BestTier(spend);
            var next = p.NextTier(spend);
            if (next is not null)
                ctx.Missed(p, $"Spend {ctx.Money(next.From - spend)} more to get {Rewards.Describe(p.RewardKind, next.Value)}");

            if (tier is null)
            {
                ctx.Traced(p, false, lines.Count == 0
                    ? "No qualifying items"
                    : $"Spend {ctx.Money(spend)} is below the threshold {ctx.Money(p.Tiers.Min(t => t.From))}");
                continue;
            }

            var benefit = p.RewardKind == RewardKind.PercentOff
                ? spend * tier.Value / 100m
                : Math.Min(spend, tier.Value);
            benefit = ctx.Round(Math.Min(benefit, ctx.RemainingCap(p)));
            if (benefit <= 0 || !ctx.TrySpend(p, benefit))
            {
                ctx.Traced(p, false, "Budget or per-document cap reached");
                continue;
            }

            Spread(ctx, p, lines, spend, benefit);
            ctx.CountApplication(p);
            ctx.Traced(p, true, $"Spend {ctx.Money(spend)} reached tier {ctx.Money(tier.From)}: {Rewards.Describe(p.RewardKind, tier.Value)}");
            ctx.LockTouched(p);
        }
    }

    /// <summary>Proportional split, rounded per line; the rounding residue goes to the highest-value line.</summary>
    static void Spread(EvaluationContext ctx, Promotion p, List<WorkLine> lines, decimal spend, decimal benefit)
    {
        var group = ctx.NextGroup(p);
        var values = lines.Select(l => l.NetPaidValue).ToArray();
        var shares = values.Select(v => ctx.Round(benefit * v / spend)).ToArray();
        var residue = benefit - shares.Sum();
        if (residue != 0)
        {
            var top = 0;
            for (int i = 1; i < values.Length; i++)
                if (values[i] > values[top]) top = i;
            shares[top] += residue;
        }
        for (int i = 0; i < lines.Count; i++)
            if (shares[i] != 0) lines[i].Add(new Application(p, 0, shares[i], group));
    }
}
