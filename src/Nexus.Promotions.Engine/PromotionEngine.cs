using System.Diagnostics;
using Nexus.Promotions.Engine.Evaluation;

namespace Nexus.Promotions.Engine;

/// <summary>
/// Stateless promotion evaluation: basket in, adjusted lines out. Thread-safe; the caller owns the rule cache.
/// Order (PRD 4.2): unit-price promotions → quantity promotions → basket promotions, each by priority.
/// </summary>
public sealed class PromotionEngine
{
    readonly EngineOptions _options;
    readonly IItemCatalog _catalog;

    public PromotionEngine(EngineOptions? options = null, IItemCatalog? catalog = null)
    {
        _options = options ?? new EngineOptions();
        _catalog = catalog ?? InMemoryItemCatalog.Empty;
    }

    public EvaluationResult Evaluate(Basket basket, IReadOnlyList<Promotion> promotions)
    {
        var sw = Stopwatch.StartNew();
        var ctx = new EvaluationContext(basket, _options, basket.Lines.Select(Enrich));

        var eligible = new List<Promotion>();
        foreach (var p in promotions.OrderBy(p => p.Priority).ThenBy(p => p.Code, StringComparer.Ordinal))
        {
            var reason = EligibilityCheck.Check(p, basket);
            if (reason is null) eligible.Add(p);
            else ctx.Traced(p, false, reason);
        }

        UnitPricePhase.Run(ctx, eligible.Where(p => p.Type is PromotionType.ItemDiscount or PromotionType.FixedPrice or PromotionType.QuantityTier).ToList());
        QuantityPhase.Run(ctx, eligible.Where(p => p.Type is PromotionType.BuyXGetXFree or PromotionType.BuyXGetY or PromotionType.MixAndMatch).ToList(), _catalog);
        BasketPhase.Run(ctx, eligible.Where(p => p.Type == PromotionType.BasketThreshold).ToList());

        foreach (var code in basket.Coupons)
            if (!promotions.Any(p => string.Equals(p.CouponCode, code, StringComparison.OrdinalIgnoreCase)))
                ctx.Trace.Add(new TraceEntry(code, 0, false, $"Coupon {code} does not match any promotion"));

        return ResultBuilder.Build(ctx, eligible, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Fills item attributes the caller did not send (POS and the add-on often send only the item code).</summary>
    BasketLine Enrich(BasketLine line)
    {
        var item = _catalog.Find(line.ItemCode);
        if (item is null) return line;
        return line with
        {
            ItemGroup = line.ItemGroup ?? item.ItemGroup,
            Manufacturer = line.Manufacturer ?? item.Manufacturer,
            Properties = line.Properties.Length > 0 ? line.Properties : item.Properties,
            MinUnitPrice = line.MinUnitPrice ?? item.MinUnitPrice,
        };
    }
}
