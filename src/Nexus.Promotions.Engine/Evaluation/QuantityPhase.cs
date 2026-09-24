namespace Nexus.Promotions.Engine.Evaluation;

/// <summary>
/// Phase 2: promotions that consume units (P04 buy X get X, P05 buy X get Y, P06 mix and match).
/// Applied one application at a time, in priority order; a unit used by one cannot trigger another.
/// </summary>
internal static class QuantityPhase
{
    public static void Run(EvaluationContext ctx, IReadOnlyList<Promotion> promotions, IItemCatalog catalog)
    {
        foreach (var p in promotions)
        {
            var problem = Validate(p);
            if (problem is not null)
            {
                ctx.Traced(p, false, problem);
                continue;
            }

            var (times, reason) = p.Type switch
            {
                PromotionType.BuyXGetXFree when p.FreeMode == FreeItemMode.AddNew => SameItemAddNew(ctx, p),
                PromotionType.BuyXGetXFree => SameItemFromBasket(ctx, p),
                PromotionType.BuyXGetY => BuyXGetY(ctx, p, catalog),
                PromotionType.MixAndMatch => MixAndMatch(ctx, p),
                _ => (0, "Unsupported type"),
            };

            if (times > 0)
            {
                ctx.CountApplication(p, times);
                ctx.Traced(p, true, $"Applied {times} time(s)");
                ctx.LockTouched(p);
            }
            else
                ctx.Traced(p, false, reason);
        }
    }

    static string? Validate(Promotion p)
    {
        if (p.BuyQuantity <= 0) return "Configuration: BuyQuantity must be above 0";
        if (p.Type is PromotionType.BuyXGetXFree or PromotionType.BuyXGetY && p.GetQuantity <= 0)
            return "Configuration: GetQuantity must be above 0";
        if (p.Type == PromotionType.BuyXGetY && p.FreeMode == FreeItemMode.FromBasket && p.RewardScope is null)
            return "Configuration: RewardScope is required";
        if (p.Type == PromotionType.BuyXGetY && p.FreeMode == FreeItemMode.AddNew && string.IsNullOrEmpty(p.RewardItemCode))
            return "Configuration: RewardItemCode is required";
        return null;
    }

    static int Max(Promotion p) => p.MaxApplicationsPerDocument ?? int.MaxValue;

    static decimal FreeValue(List<Pick> picks) => picks.Sum(x => x.Qty * x.Line.Source.UnitPrice);

    static void MarkTriggers(List<Pick> picks, Promotion p, string group)
    {
        foreach (var t in picks)
            if (!t.Line.Triggers.Exists(x => x.Promo == p)) t.Line.Triggers.Add((p, group));
    }

    static void AddFree(List<Pick> picks, Promotion p, string group)
    {
        foreach (var f in picks) f.Line.Add(new FreeSlice(p, f.Qty, group));
    }

    /// <summary>P04, reward units already in the basket: buy 2 get 1 → every 3 units, 1 is free.</summary>
    static (int, string) SameItemFromBasket(EvaluationContext ctx, Promotion p)
    {
        var pool = new UnitPool(ctx.Candidates(p, p.Scope));
        var group = ctx.NextGroup(p);
        int n = 0;
        var reason = "No qualifying items";
        while (n < Max(p))
        {
            var triggers = pool.Take(p.BuyQuantity, UnitSelection.Dearest);
            if (triggers is null) break;
            var free = pool.Take(p.GetQuantity, p.RewardUnits);
            if (free is null) { UnitPool.Return(triggers); break; }
            if (!ctx.TrySpend(p, FreeValue(free)))
            {
                UnitPool.Return(free); UnitPool.Return(triggers);
                reason = "Budget or per-document cap reached";
                break;
            }
            MarkTriggers(triggers, p, group);
            AddFree(free, p, group);
            n++;
        }

        var left = pool.Remaining;
        var set = p.BuyQuantity + p.GetQuantity;
        if (n < Max(p) && left >= p.BuyQuantity && left < set)
            ctx.Missed(p, $"Add {set - left:0.###} more qualifying unit(s) to get {p.GetQuantity:0.###} free");
        if (n == 0 && left > 0 && reason == "No qualifying items")
            reason = $"Only {left:0.###} qualifying unit(s); needs {set:0.###}";
        return (n, reason);
    }

    /// <summary>P04, reward units added: buy 2 get 1 → every 2 units bought, 1 more added free, per item.</summary>
    static (int, string) SameItemAddNew(EvaluationContext ctx, Promotion p)
    {
        int n = 0;
        var reason = "No qualifying items";
        var items = ctx.Candidates(p, p.Scope)
            .GroupBy(l => l.Source.ItemCode, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(l => l.Source.UnitPrice))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        foreach (var item in items)
        {
            var pool = new UnitPool(item);
            var first = item.OrderBy(l => l.Source.LineNum).First();
            var group = ctx.NextGroup(p);
            decimal freeQty = 0;
            while (n < Max(p))
            {
                var triggers = pool.Take(p.BuyQuantity, UnitSelection.Dearest);
                if (triggers is null) break;
                if (!ctx.TrySpend(p, p.GetQuantity * first.Source.UnitPrice))
                {
                    UnitPool.Return(triggers);
                    reason = "Budget or per-document cap reached";
                    break;
                }
                MarkTriggers(triggers, p, group);
                freeQty += p.GetQuantity;
                n++;
            }

            if (freeQty > 0)
            {
                var added = new WorkLine(first.Source with { LineNum = -1, Quantity = freeQty, MinUnitPrice = null, Locked = false }, added: true);
                added.Add(new FreeSlice(p, freeQty, group));
                ctx.Lines.Add(added);
            }

            var left = pool.Remaining;
            if (n < Max(p) && left > 0 && left < p.BuyQuantity)
                ctx.Missed(p, $"Add {p.BuyQuantity - left:0.###} more {item.Key} to get {p.GetQuantity:0.###} free");
        }
        return (n, reason);
    }

    /// <summary>P05: buy X from the scope, get Y reward units free or discounted.</summary>
    static (int, string) BuyXGetY(EvaluationContext ctx, Promotion p, IItemCatalog catalog)
    {
        ItemInfo? rewardItem = null;
        if (p.FreeMode == FreeItemMode.AddNew)
        {
            rewardItem = catalog.Find(p.RewardItemCode!);
            if (rewardItem is null) return (0, $"Reward item {p.RewardItemCode} not found in the item catalogue");
        }

        var triggerPool = new UnitPool(ctx.Candidates(p, p.Scope));
        var rewardPool = p.FreeMode == FreeItemMode.FromBasket ? new UnitPool(ctx.Candidates(p, p.RewardScope!)) : null;
        var group = ctx.NextGroup(p);
        int n = 0;
        decimal addedQty = 0;
        var reason = "No qualifying items";

        while (n < Max(p))
        {
            var triggers = triggerPool.Take(p.BuyQuantity, UnitSelection.Dearest);
            if (triggers is null) break;

            if (rewardPool is not null)
            {
                var reward = rewardPool.TakeUpTo(p.GetQuantity, p.RewardUnits);
                if (reward is null)
                {
                    UnitPool.Return(triggers);
                    reason = "Trigger bought but no reward item in the basket";
                    ctx.Missed(p, $"Add {p.GetQuantity:0.###} reward item(s) to get them {Rewards.Describe(p.RewardKind, p.RewardValue)}");
                    break;
                }
                var value = p.RewardKind == RewardKind.Free
                    ? FreeValue(reward)
                    : reward.Sum(r => r.Qty * Rewards.UnitBenefit(p.RewardKind, p.RewardValue, r.Line.NetUnitPrice));
                if (!ctx.TrySpend(p, value))
                {
                    UnitPool.Return(reward); UnitPool.Return(triggers);
                    reason = "Budget or per-document cap reached";
                    break;
                }
                if (p.RewardKind == RewardKind.Free)
                    AddFree(reward, p, group);
                else
                    foreach (var r in reward)
                        r.Line.Add(new Application(p, 0,
                            r.Qty * Rewards.UnitBenefit(p.RewardKind, p.RewardValue, r.Line.NetUnitPrice), group));
            }
            else
            {
                var value = p.GetQuantity * Rewards.UnitBenefit(p.RewardKind, p.RewardValue, rewardItem!.UnitPrice);
                if (!ctx.TrySpend(p, value))
                {
                    UnitPool.Return(triggers);
                    reason = "Budget or per-document cap reached";
                    break;
                }
                addedQty += p.GetQuantity;
            }

            MarkTriggers(triggers, p, group);
            n++;
        }

        if (addedQty > 0)
        {
            var source = new BasketLine
            {
                LineNum = -1,
                ItemCode = rewardItem!.ItemCode,
                ItemGroup = rewardItem.ItemGroup,
                Manufacturer = rewardItem.Manufacturer,
                Properties = rewardItem.Properties,
                Quantity = addedQty,
                UnitPrice = rewardItem.UnitPrice,
            };
            var added = new WorkLine(source, added: true);
            if (p.RewardKind == RewardKind.Free)
                added.Add(new FreeSlice(p, addedQty, group));
            else
                added.Add(new Application(p,
                    Rewards.UnitBenefit(p.RewardKind, p.RewardValue, rewardItem.UnitPrice), 0, group));
            ctx.Lines.Add(added);
        }

        var left = triggerPool.Remaining;
        if (n == 0 && left > 0 && reason == "No qualifying items")
        {
            reason = $"Only {left:0.###} trigger unit(s); needs {p.BuyQuantity:0.###}";
            ctx.Missed(p, $"Add {p.BuyQuantity - left:0.###} more qualifying unit(s) to get the reward");
        }
        return (n, reason);
    }

    /// <summary>P06: every N units from the pool for a set price (or % / amount off the set).</summary>
    static (int, string) MixAndMatch(EvaluationContext ctx, Promotion p)
    {
        var pool = new UnitPool(ctx.Candidates(p, p.Scope));
        var group = ctx.NextGroup(p);
        int n = 0;
        var reason = "No qualifying items";

        while (n < Max(p))
        {
            var set = pool.Take(p.BuyQuantity, p.RewardUnits);
            if (set is null) break;

            var setValue = set.Sum(x => x.Qty * x.Line.NetUnitPrice);
            var benefit = p.RewardKind switch
            {
                RewardKind.FixedPrice => setValue - p.RewardValue,
                RewardKind.PercentOff => setValue * p.RewardValue / 100m,
                RewardKind.AmountOff => Math.Min(setValue, p.RewardValue),
                RewardKind.Free => setValue,
                _ => 0,
            };
            if (benefit <= 0)
            {
                UnitPool.Return(set);
                reason = "Set price is not lower than the items' price";
                break;
            }
            if (!ctx.TrySpend(p, benefit))
            {
                UnitPool.Return(set);
                reason = "Budget or per-document cap reached";
                break;
            }

            // Spread the set benefit over its units in proportion to their price.
            foreach (var x in set)
            {
                var share = setValue == 0 ? 0 : benefit * (x.Qty * x.Line.NetUnitPrice) / setValue;
                x.Line.Add(new Application(p, 0, share, group));
            }
            MarkTriggers(set, p, group);
            n++;
        }

        var left = pool.Remaining;
        if (n < Max(p) && left > 0 && left < p.BuyQuantity)
            ctx.Missed(p, $"Add {p.BuyQuantity - left:0.###} more qualifying unit(s) to complete the set");
        if (n == 0 && left > 0 && reason == "No qualifying items")
            reason = $"Only {left:0.###} qualifying unit(s); set needs {p.BuyQuantity:0.###}";
        return (n, reason);
    }
}
