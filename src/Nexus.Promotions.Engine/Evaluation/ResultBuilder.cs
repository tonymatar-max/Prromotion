namespace Nexus.Promotions.Engine.Evaluation;

/// <summary>Final pass: caps on unit-price promotions, minimum-price floor, rounding, and the output line set.</summary>
internal static class ResultBuilder
{
    public static EvaluationResult Build(EvaluationContext ctx, IReadOnlyList<Promotion> ran, double elapsedMs)
    {
        foreach (var line in ctx.Lines)
            foreach (var a in line.Applications)
                a.Total = a.PerUnit * line.PaidQty + a.Amount;

        ApplyUnitPriceCaps(ctx, ran);
        ApplyMinimumPrices(ctx);

        var lines = new List<ResultLine>();
        foreach (var line in ctx.Lines)
        {
            if (line.PaidQty > 0) lines.Add(PaidLine(ctx, line));
            foreach (var slice in line.Free.GroupBy(f => (f.Promo, f.Group)))
                lines.Add(FreeLine(ctx, line, slice.Key.Promo, slice.Key.Group, slice.Sum(f => f.Quantity)));
        }

        var amounts = new Dictionary<Promotion, decimal>();
        foreach (var l in ctx.Lines)
        {
            foreach (var a in l.Applications)
                amounts[a.Promo] = amounts.GetValueOrDefault(a.Promo) + a.Total;
            foreach (var f in l.Free)
                amounts[f.Promo] = amounts.GetValueOrDefault(f.Promo) + f.Quantity * l.Source.UnitPrice;
        }

        var promotions = new List<AppliedPromotion>();
        foreach (var p in ran)
        {
            var times = ctx.TimesApplied(p);
            if (times > 0)
                promotions.Add(new AppliedPromotion(p.Code, p.Version, p.Name, times, ctx.Round(amounts.GetValueOrDefault(p))));
        }

        var gross = lines.Sum(l => ctx.Round(l.Quantity * l.UnitPrice));
        var discount = lines.Sum(l => l.DiscountAmount);
        return new EvaluationResult
        {
            Lines = lines,
            Promotions = promotions,
            Trace = ctx.Trace,
            NearMisses = ctx.NearMisses,
            GrossTotal = gross,
            DiscountTotal = discount,
            NetTotal = gross - discount,
            Hash = ResultHasher.Compute(lines, ctx.Options.HashKey),
            ElapsedMs = elapsedMs,
        };
    }

    /// <summary>Phase 1 promotions only know the paid quantity now, so their cap is enforced here by scaling.</summary>
    static void ApplyUnitPriceCaps(EvaluationContext ctx, IReadOnlyList<Promotion> ran)
    {
        foreach (var p in ran)
        {
            if (p.Type is not (PromotionType.ItemDiscount or PromotionType.FixedPrice or PromotionType.QuantityTier)) continue;
            var cap = p.Cap;
            if (cap == decimal.MaxValue) continue;

            var apps = ctx.Lines.SelectMany(l => l.Applications).Where(a => a.Promo == p).ToList();
            var total = apps.Sum(a => a.Total);
            if (total <= cap || total == 0) continue;

            var factor = cap / total;
            foreach (var a in apps) a.Total *= factor;
            ctx.Traced(p, true, $"Discount reduced from {ctx.Money(total)} to the cap {ctx.Money(cap)}");
        }
    }

    /// <summary>Promotions without AllowBelowMinPrice give back discount until the line is at its floor.</summary>
    static void ApplyMinimumPrices(EvaluationContext ctx)
    {
        foreach (var line in ctx.Lines)
        {
            if (line.Source.MinUnitPrice is not { } min || line.PaidQty <= 0 || line.Applications.Count == 0) continue;

            var room = Math.Max(0, line.Source.UnitPrice - min) * line.PaidQty;
            var excess = line.Applications.Sum(a => a.Total) - room;
            for (int i = line.Applications.Count - 1; i >= 0 && excess > 0; i--)
            {
                var a = line.Applications[i];
                if (a.Promo.AllowBelowMinPrice) continue;
                var cut = Math.Min(a.Total, excess);
                a.Total -= cut;
                excess -= cut;
                ctx.Traced(a.Promo, true, $"Line {line.Source.LineNum}: discount reduced by {ctx.Money(cut)} to respect the minimum price {min:0.######}");
            }
        }
    }

    static ResultLine PaidLine(EvaluationContext ctx, WorkLine line)
    {
        var gross = ctx.Round(line.PaidQty * line.Source.UnitPrice);
        var discount = Math.Min(gross, ctx.Round(line.Applications.Sum(a => a.Total)));

        var promos = new List<LinePromotion>();
        foreach (var g in line.Applications.GroupBy(a => a.Promo))
        {
            var amount = ctx.Round(g.Sum(a => a.Total));
            if (amount != 0) promos.Add(new LinePromotion(g.Key.Code, g.Key.Version, PromotionRole.Discount, amount, g.First().Group));
        }
        foreach (var (promo, group) in line.Triggers)
            promos.Add(new LinePromotion(promo.Code, promo.Version, PromotionRole.Trigger, 0, group));

        return new ResultLine
        {
            SourceLineNum = line.Added ? null : line.Source.LineNum,
            ItemCode = line.Source.ItemCode,
            Quantity = line.PaidQty,
            UnitPrice = line.Source.UnitPrice,
            DiscountAmount = discount,
            DiscountPercent = gross == 0 ? 0 : Math.Round(discount / gross * 100m, 6, MidpointRounding.AwayFromZero),
            NetTotal = gross - discount,
            IsAdded = line.Added,
            PromotionCodes = string.Join(",", promos.Select(x => x.Code).Distinct()),
            Group = promos.Select(x => x.Group).FirstOrDefault(g => g is not null),
            Promotions = promos,
        };
    }

    static ResultLine FreeLine(EvaluationContext ctx, WorkLine line, Promotion p, string group, decimal qty)
    {
        var gross = ctx.Round(qty * line.Source.UnitPrice);
        return new ResultLine
        {
            SourceLineNum = line.Added ? null : line.Source.LineNum,
            ItemCode = line.Source.ItemCode,
            Quantity = qty,
            UnitPrice = line.Source.UnitPrice,
            DiscountAmount = gross,
            DiscountPercent = 100,
            NetTotal = 0,
            IsFree = true,
            IsAdded = line.Added,
            PromotionCodes = p.Code,
            Group = group,
            Promotions = [new LinePromotion(p.Code, p.Version, PromotionRole.Free, gross, group)],
        };
    }
}
