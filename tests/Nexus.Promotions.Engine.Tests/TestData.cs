namespace Nexus.Promotions.Engine.Tests;

internal static class TestData
{
    /// <summary>Monday 5 Oct 2026, 10:00.</summary>
    public static readonly DateTime Monday10am = new(2026, 10, 5, 10, 0, 0);

    public static BasketLine Line(int lineNum, string item, decimal qty, decimal price,
        string? group = null, string? manufacturer = null, decimal? minPrice = null) => new()
    {
        LineNum = lineNum,
        ItemCode = item,
        Quantity = qty,
        UnitPrice = price,
        ItemGroup = group,
        Manufacturer = manufacturer,
        MinUnitPrice = minPrice,
    };

    public static Basket Basket(params BasketLine[] lines) => new()
    {
        Timestamp = Monday10am,
        CardCode = "C001",
        CustomerGroup = "Retail",
        Lines = lines,
    };

    public static ItemScope Items(params string[] codes) => new() { ItemCodes = codes };

    public static EvaluationResult Run(Basket basket, params Promotion[] promotions) =>
        new PromotionEngine().Evaluate(basket, promotions);

    public static ResultLine Paid(this EvaluationResult r, int lineNum) =>
        r.Lines.Single(l => l.SourceLineNum == lineNum && !l.IsFree);

    public static IEnumerable<ResultLine> FreeLines(this EvaluationResult r) => r.Lines.Where(l => l.IsFree);
}
