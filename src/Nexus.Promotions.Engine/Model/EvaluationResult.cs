namespace Nexus.Promotions.Engine;

/// <summary>
/// The basket after promotions. <see cref="Lines"/> is the complete new line set: a source line may be split
/// into a paid line and free lines, and reward lines may be added (SourceLineNum = null).
/// </summary>
public sealed record EvaluationResult
{
    public required IReadOnlyList<ResultLine> Lines { get; init; }
    public required IReadOnlyList<AppliedPromotion> Promotions { get; init; }
    public required IReadOnlyList<TraceEntry> Trace { get; init; }
    public required IReadOnlyList<NearMiss> NearMisses { get; init; }
    public decimal GrossTotal { get; init; }
    public decimal DiscountTotal { get; init; }
    public decimal NetTotal { get; init; }
    /// <summary>Stored in U_APE_Hash; recomputed by the B1 validation procedure.</summary>
    public required string Hash { get; init; }
    public double ElapsedMs { get; init; }
}

public sealed record ResultLine
{
    public int? SourceLineNum { get; init; }
    public required string ItemCode { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    /// <summary>Goes into the B1 line DiscPrcnt.</summary>
    public decimal DiscountPercent { get; init; }
    /// <summary>Goes into U_APE_DiscAmt.</summary>
    public decimal DiscountAmount { get; init; }
    public decimal NetTotal { get; init; }
    public bool IsFree { get; init; }
    public bool IsAdded { get; init; }
    /// <summary>Comma-separated codes, for U_APE_Promo.</summary>
    public string PromotionCodes { get; init; } = "";
    /// <summary>For U_APE_Group: links trigger and reward lines.</summary>
    public string? Group { get; init; }
    public IReadOnlyList<LinePromotion> Promotions { get; init; } = [];
}

public enum PromotionRole { Discount, Trigger, Free }

public sealed record LinePromotion(string Code, int Version, PromotionRole Role, decimal Amount, string? Group);

public sealed record AppliedPromotion(string Code, int Version, string Name, int TimesApplied, decimal DiscountAmount);

public sealed record TraceEntry(string Code, int Version, bool Applied, string Message);

public sealed record NearMiss(string Code, string Message);
