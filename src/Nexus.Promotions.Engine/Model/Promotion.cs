using System.Text.Json.Serialization;

namespace Nexus.Promotions.Engine;

/// <summary>Promotion mechanics from the PRD catalogue (section 3). The PRD id is in brackets.</summary>
public enum PromotionType
{
    /// <summary>[P01] % or amount off each qualifying unit.</summary>
    ItemDiscount,
    /// <summary>[P02] Fixed promotional unit price.</summary>
    FixedPrice,
    /// <summary>[P03] Reward by total quantity bought across the item scope (tiers: From = quantity).</summary>
    QuantityTier,
    /// <summary>[P04] Buy X get Y free of the same items.</summary>
    BuyXGetXFree,
    /// <summary>[P05] Buy X from the scope, get Y from the reward scope (or a reward item) free or discounted.</summary>
    BuyXGetY,
    /// <summary>[P06] Any N units from the scope for a set price, % or amount off.</summary>
    MixAndMatch,
    /// <summary>[P08/P09] Spend threshold on the basket (tiers: From = amount).</summary>
    BasketThreshold,
}

public enum StackClass
{
    /// <summary>Combines with other stackable promotions on the same line.</summary>
    Stackable,
    /// <summary>Nothing else applies to the lines it touches.</summary>
    Exclusive,
}

public enum RewardKind { PercentOff, AmountOff, FixedPrice, Free }

/// <summary>How free or discounted reward units are found.</summary>
public enum FreeItemMode
{
    /// <summary>Reward units must already be in the basket (retail: the customer brings 3, pays 2).</summary>
    FromBasket,
    /// <summary>Reward units are added as new lines (B2B: order 2, get 1 added).</summary>
    AddNew,
}

/// <summary>Which units a reward lands on when there is a choice.</summary>
public enum UnitSelection
{
    /// <summary>Lowest-priced units first: protects margin (PRD default).</summary>
    Cheapest,
    /// <summary>Highest-priced units first: best for the customer.</summary>
    Dearest,
}

public sealed record Tier(decimal From, decimal Value);

/// <summary>Which items a promotion looks at. Inclusions are OR-ed; exclusions always win.</summary>
public sealed record ItemScope
{
    public string[] ItemCodes { get; init; } = [];
    public string[] ItemGroups { get; init; } = [];
    public string[] Manufacturers { get; init; } = [];
    /// <summary>B1 item properties 1–64 (QryGroup1..64).</summary>
    public int[] Properties { get; init; } = [];
    public string[] ExcludeItemCodes { get; init; } = [];

    [JsonIgnore]
    public bool IsAllItems =>
        ItemCodes.Length == 0 && ItemGroups.Length == 0 && Manufacturers.Length == 0 && Properties.Length == 0;

    public bool Matches(BasketLine line)
    {
        if (Contains(ExcludeItemCodes, line.ItemCode)) return false;
        if (IsAllItems) return true;
        return Contains(ItemCodes, line.ItemCode)
            || (line.ItemGroup is not null && Contains(ItemGroups, line.ItemGroup))
            || (line.Manufacturer is not null && Contains(Manufacturers, line.Manufacturer))
            || (Properties.Length > 0 && Array.Exists(line.Properties, p => Array.IndexOf(Properties, p) >= 0));
    }

    static bool Contains(string[] set, string value) =>
        Array.Exists(set, s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Who and where a promotion is valid for. An empty list means "all".</summary>
public sealed record Audience
{
    public string[] Channels { get; init; } = [];
    public string[] CardCodes { get; init; } = [];
    public string[] CustomerGroups { get; init; } = [];
    public string[] Branches { get; init; } = [];
    public string[] PriceLists { get; init; } = [];
}

/// <summary>One promotion version, as stored in the @APE_PROMO UDO.</summary>
public sealed record Promotion
{
    public required string Code { get; init; }
    public int Version { get; init; } = 1;
    public string Name { get; init; } = "";
    public string? NameAr { get; init; }
    public PromotionType Type { get; init; }
    /// <summary>1 = highest.</summary>
    public int Priority { get; init; } = 100;
    public StackClass Stacking { get; init; }
    public bool Active { get; init; } = true;

    /// <summary>Inclusive. A ValidTo with no time part covers that whole day.</summary>
    public DateTime? ValidFrom { get; init; }
    public DateTime? ValidTo { get; init; }
    public DayOfWeek[] Weekdays { get; init; } = [];
    /// <summary>Happy-hour window; may cross midnight (22:00–02:00).</summary>
    public TimeOnly? TimeFrom { get; init; }
    public TimeOnly? TimeTo { get; init; }

    public Audience Audience { get; init; } = new();
    /// <summary>
    /// Which B1 document screens auto-apply this promotion (Basket.DocumentType: OQUT/ORDR/ODLN/OINV).
    /// Empty means every screen. POS and other non-B1 channels never carry one of these codes, so they are
    /// unaffected by this restriction and are filtered by Audience.Channels instead.
    /// </summary>
    public string[] DocumentTypes { get; init; } = [];
    /// <summary>
    /// Companies (B1 company database names) this promotion is ticked for when one server serves several. Empty means
    /// the master company only, the safe default; see <c>CompanyScope</c> in Nexus.Promotions.B1. The engine ignores it:
    /// the store hands each company only its own promotions.
    /// </summary>
    public string[] Companies { get; init; } = [];
    public string? CouponCode { get; init; }

    /// <summary>Items that trigger the promotion (and receive it, for price and basket types).</summary>
    public ItemScope Scope { get; init; } = new();

    /// <summary>X for P04/P05, N (set size) for P06.</summary>
    public decimal BuyQuantity { get; init; }
    /// <summary>Y for P04/P05.</summary>
    public decimal GetQuantity { get; init; }
    /// <summary>P05 FromBasket: items that receive the reward.</summary>
    public ItemScope? RewardScope { get; init; }
    /// <summary>P05 AddNew: the item added as reward.</summary>
    public string? RewardItemCode { get; init; }
    public FreeItemMode FreeMode { get; init; } = FreeItemMode.FromBasket;
    public UnitSelection RewardUnits { get; init; } = UnitSelection.Cheapest;

    public RewardKind RewardKind { get; init; } = RewardKind.PercentOff;
    public decimal RewardValue { get; init; }
    /// <summary>P03: From = quantity. P08/P09: From = basket amount. Value replaces RewardValue.</summary>
    public Tier[] Tiers { get; init; } = [];

    public int? MaxApplicationsPerDocument { get; init; }
    public decimal? MaxDiscountPerDocument { get; init; }
    /// <summary>Budget left on the promotion or campaign, supplied by the ledger.</summary>
    public decimal? BudgetRemaining { get; init; }
    /// <summary>Allows the net price to go below the line's minimum price.</summary>
    public bool AllowBelowMinPrice { get; init; }

    [JsonIgnore]
    public decimal Cap => Math.Min(MaxDiscountPerDocument ?? decimal.MaxValue, BudgetRemaining ?? decimal.MaxValue);

    // Identity, not value, equality: each loaded version is one object, and the engine compares
    // promotions in hot loops (value equality over every property made a 500×500 basket take seconds).
    public bool Equals(Promotion? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    internal Tier? BestTier(decimal metric)
    {
        Tier? best = null;
        foreach (var t in Tiers)
            if (t.From <= metric && (best is null || t.From > best.From)) best = t;
        return best;
    }

    internal Tier? NextTier(decimal metric)
    {
        Tier? next = null;
        foreach (var t in Tiers)
            if (t.From > metric && (next is null || t.From < next.From)) next = t;
        return next;
    }
}
