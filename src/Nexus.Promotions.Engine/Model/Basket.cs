namespace Nexus.Promotions.Engine;

/// <summary>The document or POS ticket to evaluate. Prices follow the document's price mode (net or gross).</summary>
public sealed record Basket
{
    /// <summary>B1 object table (OQUT, ORDR, ODLN, OINV, POS...), for the trace and ledger.</summary>
    public string DocumentType { get; init; } = "ORDR";
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Channel { get; init; } = "B1";
    public string? CardCode { get; init; }
    public string? CustomerGroup { get; init; }
    public string? Branch { get; init; }
    public string? PriceList { get; init; }
    public string[] Coupons { get; init; } = [];
    /// <summary>Currency decimals: 3 for KWD/BHD/OMR, 2 for AED/SAR/QAR.</summary>
    public int AmountDecimals { get; init; } = 2;
    public required IReadOnlyList<BasketLine> Lines { get; init; }
}

public sealed record BasketLine
{
    public int LineNum { get; init; }
    public required string ItemCode { get; init; }
    public string? ItemGroup { get; init; }
    public string? Manufacturer { get; init; }
    public int[] Properties { get; init; } = [];
    public decimal Quantity { get; init; }
    /// <summary>Price before promotions (after B1 special prices and period/volume discounts).</summary>
    public decimal UnitPrice { get; init; }
    /// <summary>Floor price for this item (UDF); promotions cannot go below it unless allowed.</summary>
    public decimal? MinUnitPrice { get; init; }
    /// <summary>User entered a manual discount: the engine leaves the line alone.</summary>
    public bool Locked { get; init; }
}

public enum ConflictMode
{
    /// <summary>The option with the highest customer benefit wins (PRD default).</summary>
    BestDeal,
    /// <summary>The highest-priority promotion decides.</summary>
    PriorityWins,
}

public enum StackingMode
{
    /// <summary>Each stacked % applies to the already-discounted price.</summary>
    Compound,
    /// <summary>Stacked discounts are each computed from the original price and added.</summary>
    Additive,
}

public sealed record EngineOptions
{
    public ConflictMode ConflictMode { get; init; } = ConflictMode.BestDeal;
    public StackingMode StackingMode { get; init; } = StackingMode.Compound;
    /// <summary>Secret mixed into the result hash, shared with the B1 validation procedure.</summary>
    public string? HashKey { get; init; }
}
