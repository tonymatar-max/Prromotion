namespace Nexus.Promotions.Engine.Evaluation;

/// <summary>
/// Whole units available to one quantity promotion, sorted dearest first. Availability is read live from
/// <see cref="WorkLine.Consumed"/>, so two pools over overlapping lines (P05 trigger and reward) stay consistent.
/// </summary>
internal sealed class UnitPool
{
    readonly List<WorkLine> _lines;

    public UnitPool(IEnumerable<WorkLine> lines) =>
        _lines = lines
            .OrderByDescending(l => l.NetUnitPrice)
            .ThenBy(l => l.Source.LineNum)
            .ToList();

    public decimal Remaining => _lines.Sum(l => l.AvailableUnits);

    /// <summary>Takes exactly <paramref name="qty"/> units, or nothing (null) if there are not enough.</summary>
    public List<Pick>? Take(decimal qty, UnitSelection selection) => TakeCore(qty, selection, exact: true);

    /// <summary>Takes up to <paramref name="qty"/> units; null if none are available.</summary>
    public List<Pick>? TakeUpTo(decimal qty, UnitSelection selection) => TakeCore(qty, selection, exact: false);

    List<Pick>? TakeCore(decimal qty, UnitSelection selection, bool exact)
    {
        if (qty <= 0) return null;
        var picks = new List<Pick>();
        var left = qty;
        for (int n = 0; n < _lines.Count && left > 0; n++)
        {
            var line = _lines[selection == UnitSelection.Dearest ? n : _lines.Count - 1 - n];
            var take = Math.Min(line.AvailableUnits, left);
            if (take <= 0) continue;
            picks.Add(new Pick(line, take));
            left -= take;
        }
        if (picks.Count == 0 || (exact && left > 0)) return null;
        foreach (var p in picks) p.Line.Consumed += p.Qty;
        return picks;
    }

    public static void Return(List<Pick>? picks)
    {
        if (picks is null) return;
        foreach (var p in picks) p.Line.Consumed -= p.Qty;
    }
}
