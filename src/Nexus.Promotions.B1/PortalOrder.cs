using System.Globalization;
using Nexus.Promotions.Engine;

namespace Nexus.Promotions.B1;

/// <summary>
/// Integration contract for sales channels that create the B1 order themselves through the Service Layer
/// (the Cobalt Customer Portal, e-commerce): "here is the order I am about to create, what do the promotions do to it?".
/// </summary>
/// <param name="CardCode">The customer. Its customer group and price list drive audience rules.</param>
/// <param name="Lines">Quantity and the unit price BEFORE promotions, in the order's currency. LineNum is the channel's own line id (echoed back as SourceLineNum).</param>
/// <param name="Channel">Matches the "Channel" audience rule of a promotion, for example PORTAL or WEB.</param>
public sealed record PortalOrderRequest(
    string? CardCode,
    IReadOnlyList<PortalOrderLine> Lines,
    string Channel = "PORTAL",
    string[]? Coupons = null,
    DateTime? DocDate = null,
    int AmountDecimals = 2);

public sealed record PortalOrderLine(int LineNum, string ItemCode, decimal Quantity, decimal UnitPrice);

/// <param name="Lines">The complete new line set for display: free and reward lines included.</param>
/// <param name="ServiceLayer">Merge this into the Service Layer POST /Orders body so SAP accepts the order (hash) and shows the promotions.</param>
public sealed record PortalOrderResult(
    IReadOnlyList<AppliedPromotion> Applied,
    IReadOnlyList<PortalResultLine> Lines,
    decimal GrossTotal,
    decimal DiscountTotal,
    decimal NetTotal,
    IReadOnlyList<NearMiss> NearMisses,
    string Hash,
    ServiceLayerFields ServiceLayer);

/// <param name="SourceLineNum">The request line this came from; null for a reward line the promotions added.</param>
public sealed record PortalResultLine(
    int? SourceLineNum,
    string ItemCode,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal DiscountAmount,
    decimal NetTotal,
    bool IsFree,
    bool IsAdded,
    string PromotionCodes);

/// <summary>
/// Field names are the Service Layer's (PascalCase, case-sensitive), so a client copies them as they are.
/// <see cref="Header"/> goes at the top level of the order; <see cref="DocumentLines"/> replaces the order's DocumentLines.
/// </summary>
public sealed record ServiceLayerFields(
    Dictionary<string, object?> Header,
    IReadOnlyList<Dictionary<string, object?>> DocumentLines);

public static class PortalOrderPlan
{
    public static DocumentInput ToDocument(PortalOrderRequest r) => new()
    {
        DocumentType = "ORDR",
        CardCode = r.CardCode,
        DocDate = r.DocDate,
        Channel = string.IsNullOrWhiteSpace(r.Channel) ? "PORTAL" : r.Channel.Trim(),
        Coupons = r.Coupons ?? [],
        AmountDecimals = r.AmountDecimals,
        Lines = r.Lines.Select(l => new DocumentLineInput
        {
            LineNum = l.LineNum, ItemCode = l.ItemCode, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
        }).ToList(),
    };

    /// <summary>
    /// Mode "A": the promotions are applied before the order is saved, which is what the B1 validation procedure expects
    /// for an order that arrives already priced. Free lines are separate 100% lines and reward lines are new lines, the
    /// same shape the add-on and the worker write, so SAP's own checks (71001-71006) treat every channel alike.
    /// </summary>
    public static PortalOrderResult Build(DocumentEvaluation e)
    {
        var r = e.Result;
        var lines = e.Lines.Select(p => new PortalResultLine(
            p.Line.SourceLineNum, p.Line.ItemCode, p.Line.Quantity, p.Line.UnitPrice, p.DiscountPercent,
            p.Line.DiscountAmount, p.Line.NetTotal, p.Line.IsFree, p.Line.IsAdded, p.Line.PromotionCodes)).ToList();

        var documentLines = e.Lines.Select(p => new Dictionary<string, object?>
        {
            ["ItemCode"] = p.Line.ItemCode,
            ["Quantity"] = p.Line.Quantity,
            ["UnitPrice"] = p.Line.UnitPrice,
            ["DiscountPercent"] = p.DiscountPercent,
            ["U_APE_Promo"] = p.Line.PromotionCodes.Length > 0 ? p.Line.PromotionCodes : "",
            ["U_APE_DiscAmt"] = p.Line.DiscountAmount,
            ["U_APE_Free"] = p.Line.IsFree ? "Y" : "N",
            ["U_APE_Group"] = p.Line.Group ?? "",
        }).ToList();

        var header = new Dictionary<string, object?>
        {
            ["U_APE_Hash"] = r.Hash,
            ["U_APE_Mode"] = "A",
            ["U_APE_Status"] = "Applied",
            ["U_APE_EvalAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };

        return new PortalOrderResult(r.Promotions, lines, r.GrossTotal, r.DiscountTotal, r.NetTotal, r.NearMisses, r.Hash,
            new ServiceLayerFields(header, documentLines));
    }
}
