namespace Nexus.Promotions.Engine.Evaluation;

/// <summary>Mutable per-line state while one basket is evaluated.</summary>
internal sealed class WorkLine
{
    public WorkLine(BasketLine source, bool added = false)
    {
        Source = source;
        Added = added;
        NetUnitPrice = source.UnitPrice;
        Locked = source.Locked || added;
    }

    public BasketLine Source { get; }
    public bool Added { get; }
    /// <summary>Unit price after unit-price promotions (phase 1).</summary>
    public decimal NetUnitPrice { get; set; }
    /// <summary>Exclusive promotion or manual discount: no further promotions.</summary>
    public bool Locked { get; set; }
    /// <summary>Whole units used by quantity promotions, as trigger or reward.</summary>
    public decimal Consumed { get; set; }

    // Add through Add(...) so the running totals below stay in step.
    public List<Application> Applications { get; } = [];
    public List<FreeSlice> Free { get; } = [];
    public List<(Promotion Promo, string Group)> Triggers { get; } = [];

    public decimal FreeQty { get; private set; }
    decimal _amounts;

    public void Add(Application a)
    {
        Applications.Add(a);
        _amounts += a.Amount;
    }

    public void Add(FreeSlice f)
    {
        Free.Add(f);
        FreeQty += f.Quantity;
    }

    public decimal PaidQty => Source.Quantity - FreeQty;
    public decimal AvailableUnits => Locked ? 0 : Math.Floor(Source.Quantity) - Consumed;
    public bool HasPromotions => Applications.Count > 0 || Free.Count > 0 || Triggers.Count > 0;

    /// <summary>Current net value of the paid part (before final rounding).</summary>
    public decimal NetPaidValue => PaidQty * NetUnitPrice - _amounts;

    public bool Touches(Promotion p) =>
        Applications.Exists(a => a.Promo == p) || Free.Exists(f => f.Promo == p) || Triggers.Exists(t => t.Promo == p);
}

/// <summary>A discount on the paid part of a line: PerUnit × paid qty + Amount.</summary>
internal sealed class Application(Promotion promo, decimal perUnit, decimal amount, string? group)
{
    public Promotion Promo { get; } = promo;
    public decimal PerUnit { get; } = perUnit;
    public decimal Amount { get; } = amount;
    public string? Group { get; } = group;
    /// <summary>Set when the result is built.</summary>
    public decimal Total { get; set; }
}

internal sealed record FreeSlice(Promotion Promo, decimal Quantity, string Group);

internal readonly record struct Pick(WorkLine Line, decimal Qty);
