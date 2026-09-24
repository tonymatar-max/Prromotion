using Nexus.Promotions.B1;

namespace Nexus.Promotions.Api;

/// <summary>
/// One promotion, several companies: the promotion is written once against the master company's item groups,
/// manufacturers, items and customer groups, but B1 numbers those per company, so "item group 101" can be printers in
/// one company and something else in the next. Nothing is refused (the person may know better), but the differences
/// are listed so a promotion is never quietly applied to the wrong items.
/// </summary>
public static class CompanyChecks
{
    /// <summary>The ticked companies as the registry spells them, without the master (it is what the promotion was written against).</summary>
    public static List<CompanyContext> Others(AdminPromotion p, CompanyRegistry registry) =>
        Ticked(p, registry).Where(c => !c.IsMaster).ToList();

    /// <summary>The ticked companies as the registry spells them; unknown names are left out (see <see cref="Unknown"/>).</summary>
    public static List<CompanyContext> Ticked(AdminPromotion p, CompanyRegistry registry) =>
        Names(p).Select(registry.Find).OfType<CompanyContext>().DistinctBy(c => c.Db, StringComparer.OrdinalIgnoreCase).ToList();

    public static List<string> Unknown(AdminPromotion p, CompanyRegistry registry) =>
        Names(p).Where(n => registry.Find(n) is null).ToList();

    static IEnumerable<string> Names(AdminPromotion p) =>
        (p.Companies ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The Companies field written the way the registry spells the names (case) and without repeats.</summary>
    public static AdminPromotion Normalize(AdminPromotion p, CompanyRegistry registry)
    {
        var known = Ticked(p, registry).Select(c => c.Db);
        var unknown = Unknown(p, registry);
        var joined = string.Join(",", known.Concat(unknown));
        return p with { Companies = joined.Length == 0 ? null : joined };
    }

    public static async Task<List<string>> NotesAsync(AdminPromotion p, CompanyRegistry registry, CancellationToken ct)
    {
        var notes = new List<string>();
        var master = registry.Master;
        foreach (var other in Others(p, registry))
        {
            if (other.Admin is null || master.Admin is null) continue; // files: nothing to compare against
            try
            {
                await CompareLookup(notes, p, master, other, "G", "itemgroups", "Item group", ct);
                await CompareLookup(notes, p, master, other, "M", "manufacturers", "Manufacturer", ct);
                await CheckItems(notes, p, other, ct);

                foreach (var a in p.Audience.Where(a => a.Dimension is "GRP" or "PL"))
                    await CompareCode(notes, a.Value, a.Dimension == "GRP" ? "customergroups" : "pricelists",
                        a.Dimension == "GRP" ? "Customer group" : "Price list", master, other, ct);

                if (p.Scopes.Any(s => s.ScopeType == "P"))
                    notes.Add($"Item property numbers can mean something different in {other.Label}; check them there.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"Could not check {other.Label}: {ex.Message}");
            }
        }
        return notes;
    }

    static async Task CompareLookup(List<string> notes, AdminPromotion p, CompanyContext master, CompanyContext other,
        string scopeType, string lookup, string label, CancellationToken ct)
    {
        foreach (var value in p.Scopes.Where(s => s.ScopeType == scopeType).Select(s => s.Value).Distinct())
            await CompareCode(notes, value, lookup, label, master, other, ct);
    }

    static async Task CompareCode(List<string> notes, string code, string lookup, string label,
        CompanyContext master, CompanyContext other, CancellationToken ct)
    {
        var mine = (await master.Admin!.LookupAsync(lookup, null, ct)).FirstOrDefault(l => l.Code == code)?.Name;
        var theirs = (await other.Admin!.LookupAsync(lookup, null, ct)).FirstOrDefault(l => l.Code == code)?.Name;

        if (theirs is null) notes.Add($"{label} {code} does not exist in {other.Label}.");
        else if (!string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase))
            notes.Add($"{label} {code} is '{mine}' in {master.Label} but '{theirs}' in {other.Label}.");
    }

    static async Task CheckItems(List<string> notes, AdminPromotion p, CompanyContext other, CancellationToken ct)
    {
        if (other.MasterData is not { } md) return;
        var codes = p.Scopes.Where(s => s.ScopeType == "I").Select(s => s.Value)
            .Append(p.RewardItem ?? "").Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        if (codes.Count == 0) return;

        await md.PrefetchItemsAsync(codes, ct);
        foreach (var code in codes.Where(c => md.Find(c) is null))
            notes.Add($"Item {code} does not exist in {other.Label}.");
    }
}
