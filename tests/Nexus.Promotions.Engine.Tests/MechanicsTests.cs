using static Nexus.Promotions.Engine.Tests.TestData;

namespace Nexus.Promotions.Engine.Tests;

/// <summary>One golden scenario (or more) per promotion type in the PRD catalogue.</summary>
public class MechanicsTests
{
    [Fact]
    public void P01_percent_off_by_manufacturer()
    {
        var promo = new Promotion
        {
            Code = "NIVEA15", Type = PromotionType.ItemDiscount,
            Scope = new ItemScope { Manufacturers = ["NIVEA"] },
            RewardKind = RewardKind.PercentOff, RewardValue = 15,
        };
        var r = Run(Basket(Line(0, "N1", 2, 10m, manufacturer: "NIVEA"), Line(1, "X1", 1, 5m)), promo);

        Assert.Equal(3.00m, r.Paid(0).DiscountAmount);
        Assert.Equal(15m, r.Paid(0).DiscountPercent);
        Assert.Equal("NIVEA15", r.Paid(0).PromotionCodes);
        Assert.Equal(0m, r.Paid(1).DiscountAmount);
        Assert.Equal(25m, r.GrossTotal);
        Assert.Equal(22m, r.NetTotal);
    }

    [Fact]
    public void P02_fixed_price_in_three_decimal_currency()
    {
        var promo = new Promotion
        {
            Code = "COLA", Type = PromotionType.FixedPrice, Scope = Items("COLA15"), RewardValue = 0.500m,
        };
        var basket = Basket(Line(0, "COLA15", 4, 0.650m)) with { AmountDecimals = 3 };
        var r = Run(basket, promo);

        Assert.Equal(0.600m, r.Paid(0).DiscountAmount);
        Assert.Equal(2.000m, r.NetTotal);
    }

    [Fact]
    public void P03_quantity_tier_counts_across_the_scope()
    {
        var promo = new Promotion
        {
            Code = "SHAMPOO-TIER", Type = PromotionType.QuantityTier,
            Scope = new ItemScope { ItemGroups = ["SHAMPOO"] },
            RewardKind = RewardKind.PercentOff,
            Tiers = [new(1, 5), new(6, 8), new(12, 12)],
        };
        var r = Run(Basket(Line(0, "S1", 4, 2m, group: "SHAMPOO"), Line(1, "S2", 3, 3m, group: "SHAMPOO")), promo);

        // 7 units in the scope → 8% tier
        Assert.Equal(0.64m, r.Paid(0).DiscountAmount);
        Assert.Equal(0.72m, r.Paid(1).DiscountAmount);
        Assert.Contains(r.NearMisses, n => n.Code == "SHAMPOO-TIER" && n.Message.Contains("Add 5"));
    }

    [Fact]
    public void P04_buy2_get1_splits_the_line_into_paid_and_free()
    {
        var promo = new Promotion
        {
            Code = "B2G1", Type = PromotionType.BuyXGetXFree, Scope = Items("A"),
            BuyQuantity = 2, GetQuantity = 1,
        };
        var r = Run(Basket(Line(0, "A", 3, 10m)), promo);

        Assert.Equal(2, r.Paid(0).Quantity);
        Assert.Equal(0m, r.Paid(0).DiscountAmount);
        var free = Assert.Single(r.FreeLines());
        Assert.Equal(1, free.Quantity);
        Assert.Equal(100m, free.DiscountPercent);
        Assert.Equal(0, free.SourceLineNum);
        Assert.Equal(r.Paid(0).Group, free.Group);
        Assert.Equal(10m, r.DiscountTotal);
    }

    [Fact]
    public void P04_cheapest_selection_protects_margin_dearest_favours_customer()
    {
        Promotion Promo(UnitSelection s) => new()
        {
            Code = "B2G1", Type = PromotionType.BuyXGetXFree, Scope = Items("A", "B"),
            BuyQuantity = 2, GetQuantity = 1, RewardUnits = s,
        };
        var basket = Basket(Line(0, "A", 3, 10m), Line(1, "B", 3, 6m));

        var cheapest = Run(basket, Promo(UnitSelection.Cheapest));
        Assert.Equal(12m, cheapest.DiscountTotal);                 // two B free
        Assert.All(cheapest.FreeLines(), l => Assert.Equal("B", l.ItemCode));

        var dearest = Run(basket, Promo(UnitSelection.Dearest));
        Assert.Equal(16m, dearest.DiscountTotal);                  // one A and one B free
    }

    [Fact]
    public void P04_add_new_adds_free_units_and_reports_near_miss()
    {
        var promo = new Promotion
        {
            Code = "B2G1-ADD", Type = PromotionType.BuyXGetXFree, Scope = Items("A"),
            BuyQuantity = 2, GetQuantity = 1, FreeMode = FreeItemMode.AddNew,
        };
        var r = Run(Basket(Line(0, "A", 5, 10m)), promo);

        Assert.Equal(5, r.Paid(0).Quantity);
        var added = Assert.Single(r.FreeLines());
        Assert.True(added.IsAdded);
        Assert.Null(added.SourceLineNum);
        Assert.Equal(2, added.Quantity);
        Assert.Equal(20m, r.DiscountTotal);
        Assert.Contains(r.NearMisses, n => n.Message.Contains("Add 1 more A"));
    }

    [Fact]
    public void P05_buy_phone_get_case_half_price_from_basket()
    {
        var promo = new Promotion
        {
            Code = "PHONE-CASE", Type = PromotionType.BuyXGetY, Scope = Items("PHONE"),
            BuyQuantity = 1, GetQuantity = 1, RewardScope = Items("CASE"),
            RewardKind = RewardKind.PercentOff, RewardValue = 50,
        };
        var r = Run(Basket(Line(0, "PHONE", 1, 200m), Line(1, "CASE", 2, 20m)), promo);

        Assert.Equal(10m, r.Paid(1).DiscountAmount);    // one of the two cases at 50%
        Assert.Equal(25m, r.Paid(1).DiscountPercent);
        Assert.Contains(r.Paid(0).Promotions, p => p.Role == PromotionRole.Trigger);
    }

    [Fact]
    public void P05_add_new_reward_item_uses_catalogue_price()
    {
        var promo = new Promotion
        {
            Code = "TOTE", Type = PromotionType.BuyXGetY, Scope = Items("PHONE"),
            BuyQuantity = 1, GetQuantity = 1, FreeMode = FreeItemMode.AddNew,
            RewardItemCode = "TOTE", RewardKind = RewardKind.Free,
        };
        var catalog = new InMemoryItemCatalog([new ItemInfo { ItemCode = "TOTE", UnitPrice = 5m }]);
        var r = new PromotionEngine(catalog: catalog).Evaluate(Basket(Line(0, "PHONE", 1, 200m)), [promo]);

        var tote = Assert.Single(r.FreeLines());
        Assert.Equal("TOTE", tote.ItemCode);
        Assert.Equal(5m, tote.DiscountAmount);
        Assert.True(tote.IsAdded);
    }

    [Fact]
    public void P06_any_three_for_a_set_price_spreads_the_saving()
    {
        var promo = new Promotion
        {
            Code = "SNACK3", Type = PromotionType.MixAndMatch, Scope = new ItemScope { ItemGroups = ["SNACK"] },
            BuyQuantity = 3, RewardKind = RewardKind.FixedPrice, RewardValue = 1.000m,
        };
        var basket = Basket(Line(0, "S1", 2, 0.500m, group: "SNACK"), Line(1, "S2", 2, 0.400m, group: "SNACK"))
            with { AmountDecimals = 3 };
        var r = Run(basket, promo);

        // Cheapest set: 0.4 + 0.4 + 0.5 = 1.3 → saving 0.300
        Assert.Equal(0.300m, r.DiscountTotal);
        Assert.Equal(0.115m, r.Paid(0).DiscountAmount);
        Assert.Equal(0.185m, r.Paid(1).DiscountAmount);
        Assert.Contains(r.NearMisses, n => n.Message.Contains("Add 2 more"));
    }

    [Fact]
    public void P08_spend_threshold_is_spread_proportionally()
    {
        var promo = new Promotion
        {
            Code = "SPEND50", Type = PromotionType.BasketThreshold, RewardKind = RewardKind.PercentOff,
            Tiers = [new(50, 10)],
        };
        var r = Run(Basket(Line(0, "A", 3, 10m), Line(1, "B", 1, 25m)), promo);

        Assert.Equal(3.00m, r.Paid(0).DiscountAmount);
        Assert.Equal(2.50m, r.Paid(1).DiscountAmount);
        Assert.Equal(5.50m, r.DiscountTotal);
    }

    [Fact]
    public void P09_tiered_spend_reports_next_tier()
    {
        var promo = new Promotion
        {
            Code = "TIERS", Type = PromotionType.BasketThreshold, RewardKind = RewardKind.PercentOff,
            Tiers = [new(50, 5), new(100, 10)],
        };
        var r = Run(Basket(Line(0, "A", 3, 10m), Line(1, "B", 1, 25m)), promo);

        Assert.Equal(2.75m, r.DiscountTotal);
        Assert.Contains(r.NearMisses, n => n.Message == "Spend 45.00 more to get 10% off");
    }

    [Fact]
    public void P08_rounding_residue_goes_to_one_line_so_total_is_exact()
    {
        var promo = new Promotion
        {
            Code = "TEN-OFF", Type = PromotionType.BasketThreshold, RewardKind = RewardKind.AmountOff,
            Tiers = [new(30, 10)],
        };
        var r = Run(Basket(Line(0, "A", 1, 10m), Line(1, "B", 1, 10m), Line(2, "C", 1, 10m)), promo);

        Assert.Equal(10.00m, r.DiscountTotal);
        Assert.Equal(3.34m, r.Paid(0).DiscountAmount);
        Assert.Equal(3.33m, r.Paid(1).DiscountAmount);
    }

    [Fact]
    public void Line_discount_and_free_goods_combine_on_the_same_item()
    {
        var tenPercent = new Promotion
        {
            Code = "TEN", Type = PromotionType.ItemDiscount, Scope = Items("A"), RewardValue = 10,
        };
        var b2g1 = new Promotion
        {
            Code = "B2G1", Type = PromotionType.BuyXGetXFree, Scope = Items("A"), BuyQuantity = 2, GetQuantity = 1,
        };
        var r = Run(Basket(Line(0, "A", 3, 10m)), tenPercent, b2g1);

        Assert.Equal(2.00m, r.Paid(0).DiscountAmount);             // 10% on the 2 paid units only
        Assert.Equal(10m, Assert.Single(r.FreeLines()).DiscountAmount);
        Assert.Equal("TEN,B2G1", r.Paid(0).PromotionCodes);
    }
}
