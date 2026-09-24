using Nexus.Promotions.Engine;

namespace Nexus.Promotions.B1;

/// <summary>A B1 sales document as it stands, including any APE result from an earlier evaluation.</summary>
public sealed record DocumentInput
{
    public string DocumentType { get; init; } = "ORDR";
    public string? CardCode { get; init; }
    public DateTime? DocDate { get; init; }
    public string Channel { get; init; } = "B1";
    public string? Branch { get; init; }
    public string[] Coupons { get; init; } = [];
    public int AmountDecimals { get; init; } = 2;
    public required IReadOnlyList<DocumentLineInput> Lines { get; init; }
}

public sealed record DocumentLineInput
{
    public int LineNum { get; init; }
    public required string ItemCode { get; init; }
    public decimal Quantity { get; init; }
    /// <summary>Price before discount (PriceBefDi / UnitPrice).</summary>
    public decimal UnitPrice { get; init; }
    public decimal DiscountPercent { get; init; }
    /// <summary>U_APE_Promo</summary>
    public string? PromotionCodes { get; init; }
    /// <summary>U_APE_Free = Y</summary>
    public bool IsFree { get; init; }
    /// <summary>U_APE_Group</summary>
    public string? Group { get; init; }
}

/// <param name="LineNum">Existing document line to update, or null for a new line.</param>
/// <param name="DiscountPercent">What to write to DiscPrcnt: the promotion's, or the user's manual discount on a locked line.</param>
public sealed record PlannedLine(int? LineNum, ResultLine Line, decimal DiscountPercent);

/// <param name="RemovedLineNums">Document lines that must go (earlier free lines, lines that became entirely free).</param>
public sealed record DocumentEvaluation(EvaluationResult Result, IReadOnlyList<PlannedLine> Lines, IReadOnlyList<int> RemovedLineNums)
{
    public bool Changed { get; init; }
}

/// <summary>
/// Evaluates a document for Mode A (add-on) and Mode B (worker) alike:
///  1. undoes an earlier APE result: free units taken from the basket go back to their paid line, added reward lines are dropped;
///  2. leaves lines with a manual discount (discount but no promotion) untouched;
///  3. runs the engine;
///  4. plans the write-back: which existing lines to update, which to add, which to delete.
/// </summary>
public sealed class DocumentEvaluator(Func<(IReadOnlyList<Promotion> Promotions, PromotionEngine Engine)> snapshot, B1MasterData? masterData = null)
{
    public async Task<DocumentEvaluation> EvaluateAsync(DocumentInput doc, CancellationToken ct = default)
    {
        var (promotions, engine) = snapshot();
        CustomerInfo? customer = null;
        if (masterData is not null)
        {
            await masterData.PrefetchItemsAsync(doc.Lines.Select(l => l.ItemCode), ct);
            customer = await masterData.GetCustomerAsync(doc.CardCode, ct);
        }

        var basket = BuildBasket(doc, promotions, customer, out var merged);
        var result = engine.Evaluate(basket, promotions);
        return Plan(doc, basket, result, merged);
    }

    /// <summary>Step 1–2. <paramref name="mergedAway"/> are free lines folded back into, or dropped from, the basket.</summary>
    public static Basket BuildBasket(DocumentInput doc, IReadOnlyList<Promotion> promotions, CustomerInfo? customer, out HashSet<int> mergedAway)
    {
        var byCode = promotions.ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);
        var paid = doc.Lines.Where(l => !l.IsFree).ToList();
        var extraQty = new Dictionary<int, decimal>();
        var keptFree = new List<DocumentLineInput>();
        mergedAway = [];

        foreach (var free in doc.Lines.Where(l => l.IsFree))
        {
            var code = free.PromotionCodes?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (code is not null && byCode.TryGetValue(code, out var promo) && promo.FreeMode == FreeItemMode.AddNew)
            {
                mergedAway.Add(free.LineNum); // reward line the engine added: it comes back if still earned
                continue;
            }

            // Units the customer brought: fold back into the paid line of the same application (same group), else same item.
            var target = paid.FirstOrDefault(l => free.Group is not null && l.Group == free.Group && SameItem(l, free))
                      ?? paid.FirstOrDefault(l => SameItem(l, free));
            if (target is null)
            {
                keptFree.Add(free); // nothing to fold into: evaluate it as an ordinary line
                continue;
            }
            extraQty[target.LineNum] = extraQty.GetValueOrDefault(target.LineNum) + free.Quantity;
            mergedAway.Add(free.LineNum);
        }

        var lines = paid.Concat(keptFree).Select(l => new BasketLine
        {
            LineNum = l.LineNum,
            ItemCode = l.ItemCode,
            Quantity = l.Quantity + extraQty.GetValueOrDefault(l.LineNum),
            UnitPrice = l.UnitPrice,
            // A discount without a promotion is the user's own: keep it and keep promotions off the line.
            Locked = l.DiscountPercent != 0 && string.IsNullOrWhiteSpace(l.PromotionCodes) && !l.IsFree,
        }).ToList();

        return new Basket
        {
            DocumentType = doc.DocumentType,
            Timestamp = doc.DocDate?.Date.Add(DateTime.Now.TimeOfDay) ?? DateTime.Now,
            Channel = doc.Channel,
            CardCode = doc.CardCode,
            CustomerGroup = customer?.GroupCode,
            PriceList = customer?.PriceList,
            Branch = doc.Branch,
            Coupons = doc.Coupons,
            AmountDecimals = doc.AmountDecimals,
            Lines = lines,
        };
    }

    /// <summary>
    /// Step 4: each source line is reused once for its paid part, and an earlier free line of the same item is reused
    /// for a free result line, so re-evaluating an unchanged document plans no change. Everything else is new;
    /// unused lines are removed.
    /// </summary>
    public static DocumentEvaluation Plan(DocumentInput doc, Basket basket, EvaluationResult result, IReadOnlySet<int> mergedAway)
    {
        var existing = doc.Lines.ToDictionary(l => l.LineNum);
        var locked = basket.Lines.Where(l => l.Locked).Select(l => l.LineNum).ToHashSet();
        var used = new HashSet<int>();
        var planned = new List<PlannedLine>();

        foreach (var line in result.Lines)
        {
            int? reuse = line.SourceLineNum is { } src && !line.IsFree && existing.ContainsKey(src) && used.Add(src) ? src : null;
            if (line.IsFree)
                reuse = doc.Lines.FirstOrDefault(l => l.IsFree && !used.Contains(l.LineNum)
                    && string.Equals(l.ItemCode, line.ItemCode, StringComparison.OrdinalIgnoreCase))?.LineNum is { } oldFree
                    && used.Add(oldFree) ? oldFree : null;
            var discount = reuse is { } n && locked.Contains(n) ? existing[n].DiscountPercent : line.DiscountPercent;
            planned.Add(new PlannedLine(reuse, line, discount));
        }

        var removed = doc.Lines.Select(l => l.LineNum).Where(n => !used.Contains(n)).ToList();
        var changed = removed.Count > 0
            || planned.Any(p => p.LineNum is null)
            || planned.Any(p => p.LineNum is { } n && Differs(existing[n], p));

        return new DocumentEvaluation(result, planned, removed) { Changed = changed };
    }

    static bool Differs(DocumentLineInput before, PlannedLine after) =>
        before.Quantity != after.Line.Quantity
        || Math.Round(before.DiscountPercent, 4) != Math.Round(after.DiscountPercent, 4)
        || (before.PromotionCodes ?? "") != after.Line.PromotionCodes
        || before.IsFree != after.Line.IsFree;

    static bool SameItem(DocumentLineInput a, DocumentLineInput b) =>
        string.Equals(a.ItemCode, b.ItemCode, StringComparison.OrdinalIgnoreCase);
}
