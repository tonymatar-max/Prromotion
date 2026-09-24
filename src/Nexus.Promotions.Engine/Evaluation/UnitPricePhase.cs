namespace Nexus.Promotions.Engine.Evaluation;

/// <summary>
/// Phase 1: promotions that change a unit price (P01 item discount, P02 fixed price, P03 quantity tier).
/// Per line, the exclusive candidates compete with the stack of stackable ones (PRD 4.2/4.3).
/// </summary>
internal static class UnitPricePhase
{
    public static void Run(EvaluationContext ctx, IReadOnlyList<Promotion> promotions)
    {
        if (promotions.Count == 0) return;

        // P03 tiers count the quantity across the whole scope, not per line.
        var tierQty = promotions
            .Where(p => p.Type == PromotionType.QuantityTier)
            .ToDictionary(p => p.Code, p => ctx.Lines.Where(l => !l.Locked && p.Scope.Matches(l.Source)).Sum(l => l.Source.Quantity));

        var lost = new HashSet<Promotion>();
        foreach (var line in ctx.Lines)
        {
            if (line.Locked) continue;
            var basePrice = line.Source.UnitPrice;

            var candidates = new List<Promotion>();
            foreach (var p in promotions)
                if (p.Scope.Matches(line.Source) && PriceAfter(p, basePrice, tierQty) < basePrice)
                    candidates.Add(p);
            if (candidates.Count == 0) continue;

            var chosen = Choose(ctx.Options, basePrice, candidates, tierQty);
            foreach (var p in candidates)
                if (!chosen.Contains(p)) lost.Add(p);

            var perUnit = Stack(ctx.Options.StackingMode, basePrice, chosen, tierQty);
            for (int i = 0; i < chosen.Count; i++)
                line.Add(new Application(chosen[i], perUnit[i], 0, null));
            line.NetUnitPrice = basePrice - perUnit.Sum();
            if (chosen.Exists(p => p.Stacking == StackClass.Exclusive)) line.Locked = true;
        }

        foreach (var p in promotions)
        {
            var lines = ctx.Lines.Count(l => l.Applications.Exists(a => a.Promo == p));
            if (lines > 0)
            {
                ctx.CountApplication(p, lines);
                ctx.Traced(p, true, $"Applied to {lines} line(s)");
                if (p.Type == PromotionType.QuantityTier && p.NextTier(tierQty[p.Code]) is { } higher)
                    ctx.Missed(p, $"Add {higher.From - tierQty[p.Code]:0.###} more to get {Rewards.Describe(p.RewardKind, higher.Value)}");
            }
            else if (lost.Contains(p))
                ctx.Traced(p, false, ctx.Options.ConflictMode == ConflictMode.BestDeal
                    ? "A better deal for the customer won on the qualifying lines"
                    : "A higher-priority promotion won on the qualifying lines");
            else if (p.Type == PromotionType.QuantityTier && tierQty.TryGetValue(p.Code, out var qty) && qty > 0)
            {
                ctx.Traced(p, false, $"Quantity {qty:0.###} is below the first tier");
                if (p.NextTier(qty) is { } next)
                    ctx.Missed(p, $"Add {next.From - qty:0.###} more to get {Rewards.Describe(p.RewardKind, next.Value)}");
            }
            else
                ctx.Traced(p, false, "No qualifying items");
        }
    }

    static decimal PriceAfter(Promotion p, decimal price, Dictionary<string, decimal> tierQty) => p.Type switch
    {
        PromotionType.ItemDiscount => Rewards.PriceAfter(p.RewardKind, p.RewardValue, price),
        PromotionType.FixedPrice => Math.Min(price, p.RewardValue),
        PromotionType.QuantityTier => p.BestTier(tierQty[p.Code]) is { } tier
            ? Rewards.PriceAfter(p.RewardKind, tier.Value, price)
            : price,
        _ => price,
    };

    /// <summary>Per-unit discount of each chosen promotion, in order.</summary>
    static List<decimal> Stack(StackingMode mode, decimal basePrice, List<Promotion> chosen, Dictionary<string, decimal> tierQty)
    {
        var result = new List<decimal>(chosen.Count);
        var current = basePrice;
        foreach (var p in chosen)
        {
            var from = mode == StackingMode.Compound ? current : basePrice;
            var cut = Math.Min(from - PriceAfter(p, from, tierQty), current); // never below zero
            result.Add(cut);
            current -= cut;
        }
        return result;
    }

    static List<Promotion> Choose(EngineOptions options, decimal basePrice, List<Promotion> candidates, Dictionary<string, decimal> tierQty)
    {
        // Candidates arrive in priority order. Options: each exclusive alone, or all stackables together.
        var stack = candidates.Where(p => p.Stacking == StackClass.Stackable).ToList();
        var all = candidates
            .Where(p => p.Stacking == StackClass.Exclusive)
            .Select(p => (Priority: p.Priority, Promos: new List<Promotion> { p }))
            .ToList();
        if (stack.Count > 0) all.Add((stack[0].Priority, stack));
        var opts = all.OrderBy(o => o.Priority).ToList(); // stable: equal priority keeps exclusives first, so results are deterministic

        if (options.ConflictMode == ConflictMode.PriorityWins) return opts[0].Promos;

        List<Promotion>? best = null;
        var bestPrice = decimal.MaxValue;
        foreach (var o in opts)
        {
            var price = basePrice - Stack(options.StackingMode, basePrice, o.Promos, tierQty).Sum();
            if (price < bestPrice) { bestPrice = price; best = o.Promos; }
        }
        return best!;
    }
}
