namespace Nexus.Promotions.Engine.Evaluation;

internal sealed class EvaluationContext
{
    readonly Dictionary<string, decimal> _spent = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _timesApplied = new(StringComparer.Ordinal);
    int _groupSeq;

    public EvaluationContext(Basket basket, EngineOptions options, IEnumerable<BasketLine> lines)
    {
        Basket = basket;
        Options = options;
        Lines = lines.Select(l => new WorkLine(l)).ToList();
    }

    public Basket Basket { get; }
    public EngineOptions Options { get; }
    public List<WorkLine> Lines { get; }
    public List<TraceEntry> Trace { get; } = [];
    public List<NearMiss> NearMisses { get; } = [];

    public string NextGroup(Promotion p) => $"{p.Code}-{++_groupSeq}";

    public void Traced(Promotion p, bool applied, string message) =>
        Trace.Add(new TraceEntry(p.Code, p.Version, applied, message));

    public void Missed(Promotion p, string message) => NearMisses.Add(new NearMiss(p.Code, message));

    public void CountApplication(Promotion p, int times = 1) =>
        _timesApplied[p.Code] = _timesApplied.GetValueOrDefault(p.Code) + times;

    public int TimesApplied(Promotion p) => _timesApplied.GetValueOrDefault(p.Code);

    /// <summary>Discount this promotion may still give on this document (per-document cap and budget).</summary>
    public decimal RemainingCap(Promotion p) =>
        p.Cap == decimal.MaxValue ? decimal.MaxValue : Math.Max(0, p.Cap - _spent.GetValueOrDefault(p.Code));

    /// <summary>Records a benefit if it fits the remaining cap.</summary>
    public bool TrySpend(Promotion p, decimal amount)
    {
        if (amount > RemainingCap(p)) return false;
        _spent[p.Code] = _spent.GetValueOrDefault(p.Code) + amount;
        return true;
    }

    public decimal Round(decimal value) =>
        Math.Round(value, Basket.AmountDecimals, MidpointRounding.AwayFromZero);

    public string Money(decimal value) => Round(value).ToString("N" + Basket.AmountDecimals, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Lines a promotion may use: in scope, not locked, and (for exclusive promotions) untouched.</summary>
    public IEnumerable<WorkLine> Candidates(Promotion p, ItemScope scope) =>
        Lines.Where(l => !l.Locked
                         && scope.Matches(l.Source)
                         && (p.Stacking != StackClass.Exclusive || !l.HasPromotions));

    public void LockTouched(Promotion p)
    {
        if (p.Stacking != StackClass.Exclusive) return;
        foreach (var l in Lines)
            if (l.Touches(p)) l.Locked = true;
    }
}
