using Nexus.Promotions.Engine;

namespace Nexus.Promotions.B1;

/// <summary>
/// Which promotions a company sees when one promotion server serves several companies. Promotions are defined once,
/// in the master company; each is ticked for the companies it applies to. Nothing ticked means the master company
/// only: a promotion never starts applying to a company nobody chose. With a single company that company is the
/// master, so an untouched promotion behaves exactly as before.
/// </summary>
public static class CompanyScope
{
    public static bool Applies(Promotion promotion, string company, string master) =>
        promotion.Companies.Length == 0
            ? Same(company, master)
            : Array.Exists(promotion.Companies, c => Same(c, company));

    public static IReadOnlyList<Promotion> Select(IEnumerable<Promotion> all, string company, string master) =>
        all.Where(p => Applies(p, company, master)).ToList();

    /// <summary>Company database names compare case-insensitively, like SQL Server database names do by default.</summary>
    public static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
