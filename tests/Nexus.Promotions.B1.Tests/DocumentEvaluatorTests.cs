using Nexus.Promotions.B1;
using Nexus.Promotions.Engine;

namespace Nexus.Promotions.B1.Tests;

/// <summary>Re-evaluating a B1 document: undoing an earlier result, manual discounts, and the write-back plan.</summary>
public class DocumentEvaluatorTests
{
    static readonly Promotion Ten = new()
    {
        Code = "TEN", Type = PromotionType.ItemDiscount, Scope = new ItemScope { ItemCodes = ["A"] }, RewardValue = 10,
    };
    static readonly Promotion B2G1 = new()
    {
        Code = "B2G1", Type = PromotionType.BuyXGetXFree, Scope = new ItemScope { ItemCodes = ["A"] }, BuyQuantity = 2, GetQuantity = 1,
    };
    static readonly Promotion B2G1Add = B2G1 with { Code = "B2G1-ADD", FreeMode = FreeItemMode.AddNew };

    static Task<DocumentEvaluation> Evaluate(DocumentInput doc, params Promotion[] promotions)
    {
        var engine = new PromotionEngine();
        return new DocumentEvaluator(() => (promotions, engine)).EvaluateAsync(doc);
    }

    static DocumentLineInput Line(int n, string item, decimal qty, decimal price, decimal disc = 0,
        string? promo = null, bool free = false, string? group = null) => new()
    {
        LineNum = n, ItemCode = item, Quantity = qty, UnitPrice = price, DiscountPercent = disc,
        PromotionCodes = promo, IsFree = free, Group = group,
    };

    [Fact]
    public void New_order_is_split_into_paid_and_free_lines()
    {
        var e = await_(Evaluate(new DocumentInput { Lines = [Line(0, "A", 3, 100), Line(1, "B", 1, 50)] }, Ten, B2G1));

        Assert.True(e.Changed);
        Assert.Empty(e.RemovedLineNums);
        var paid = e.Lines.Single(p => p.LineNum == 0);
        Assert.Equal(2, paid.Line.Quantity);
        Assert.Equal(10m, paid.DiscountPercent);
        var free = e.Lines.Single(p => p.Line.IsFree);
        Assert.Null(free.LineNum);                // new line
        Assert.Equal(1, free.Line.Quantity);
        Assert.Equal(100m, free.DiscountPercent);
        Assert.NotNull(e.Lines.Single(p => p.LineNum == 1));
    }

    [Fact]
    public void Re_evaluating_an_applied_order_changes_nothing()
    {
        // The document as the worker left it: A split into paid 2 + free 1 of the same group.
        var first = await_(Evaluate(new DocumentInput { Lines = [Line(0, "A", 3, 100), Line(1, "B", 1, 50)] }, Ten, B2G1));
        var group = first.Lines.Single(p => p.Line.IsFree).Line.Group;
        var saved = new DocumentInput
        {
            Lines =
            [
                Line(0, "A", 2, 100, 10, "TEN,B2G1", group: group),
                Line(1, "B", 1, 50),
                Line(2, "A", 1, 100, 100, "B2G1", free: true, group: group),
            ],
        };

        var again = await_(Evaluate(saved, Ten, B2G1));

        Assert.False(again.Changed);                 // same result: nothing to write back
        Assert.Empty(again.RemovedLineNums);
        Assert.Equal(2, again.Lines.Single(p => p.Line.IsFree).LineNum);   // old free line reused
        Assert.Equal(first.Result.Hash, again.Result.Hash);
        Assert.Equal(first.Result.DiscountTotal, again.Result.DiscountTotal);
        Assert.Equal(2, again.Lines.Single(p => p.LineNum == 0).Line.Quantity);
    }

    [Fact]
    public void Added_reward_line_is_dropped_before_evaluation_not_merged()
    {
        // Order 2, 1 was added free. Merging it back would make 3 paid units.
        var saved = new DocumentInput
        {
            Lines = [Line(0, "A", 2, 100, 0, "B2G1-ADD", group: "G"), Line(1, "A", 1, 100, 100, "B2G1-ADD", free: true, group: "G")],
        };

        var e = await_(Evaluate(saved, B2G1Add));

        Assert.Equal(2, e.Lines.Single(p => p.LineNum == 0).Line.Quantity);
        var free = e.Lines.Single(p => p.Line.IsFree);
        Assert.Equal(1, free.Line.Quantity);
        Assert.Equal(1, free.LineNum);               // the added line is reused, not duplicated
        Assert.Empty(e.RemovedLineNums);
    }

    [Fact]
    public void Manual_discount_is_kept_and_blocks_promotions_on_that_line()
    {
        var e = await_(Evaluate(new DocumentInput { Lines = [Line(0, "A", 1, 100, disc: 7.5m), Line(1, "A", 1, 100)] }, Ten));

        var manual = e.Lines.Single(p => p.LineNum == 0);
        Assert.Equal(7.5m, manual.DiscountPercent);
        Assert.Equal("", manual.Line.PromotionCodes);
        Assert.Equal(10m, e.Lines.Single(p => p.LineNum == 1).DiscountPercent);
    }

    [Fact]
    public void Line_that_becomes_entirely_free_is_removed_and_re_added_as_free()
    {
        // Cheapest unit free: B's only unit is the cheapest, so line 1 has no paid part left.
        var cheapest = B2G1 with { Scope = new ItemScope { ItemCodes = ["A", "B"] } };
        var e = await_(Evaluate(new DocumentInput { Lines = [Line(0, "A", 2, 100), Line(1, "B", 1, 50)] }, cheapest));

        Assert.Equal([1], e.RemovedLineNums);
        var free = e.Lines.Single(p => p.Line.IsFree);
        Assert.Equal("B", free.Line.ItemCode);
        Assert.Null(free.LineNum);
    }

    static T await_<T>(Task<T> t) => t.GetAwaiter().GetResult();
}
