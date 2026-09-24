using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Promotions.Engine;

/// <summary>
/// Checksum of a promotion result, stored in U_APE_Hash and recomputed by SBO_SP_TransactionNotification
/// to reject documents whose promotion lines were altered (PRD 6.1, FR-45).
///
/// Canonical text, one row per line, rows sorted by ordinal (binary) comparison, UTF-8, "\n" after every row:
///   ItemCode|Quantity|UnitPrice|DiscountAmount|PromotionCodes|Free
/// with numbers as invariant fixed 6 decimals ("3.000000") and Free as Y/N.
/// Hash = uppercase hex SHA-256 of  key + "\n" + text + key  (key empty when not configured).
/// Only fields B1 stores exactly as written are used (quantity, PriceBefDi and the APE UDFs; not DiscPrcnt or totals),
/// so SQL Server (HASHBYTES) and HANA (HASH_SHA256) can rebuild it from the document. Sorting the rows makes the
/// hash independent of line order, so the add-on and worker may append free lines anywhere.
/// DiscPrcnt is not hashed; the validation procedure checks it against U_APE_DiscAmt separately.
/// </summary>
public static class ResultHasher
{
    public static string Compute(IEnumerable<ResultLine> lines, string? key) =>
        Compute(lines.Select(l => (l.ItemCode, l.Quantity, l.UnitPrice, l.DiscountAmount, l.PromotionCodes, l.IsFree)), key);

    public static string Compute(
        IEnumerable<(string ItemCode, decimal Quantity, decimal UnitPrice, decimal DiscountAmount, string PromotionCodes, bool IsFree)> lines,
        string? key)
    {
        var rows = lines.Select(l => string.Concat(
                l.ItemCode, "|",
                l.Quantity.ToString("0.000000", CultureInfo.InvariantCulture), "|",
                l.UnitPrice.ToString("0.000000", CultureInfo.InvariantCulture), "|",
                l.DiscountAmount.ToString("0.000000", CultureInfo.InvariantCulture), "|",
                l.PromotionCodes, "|",
                l.IsFree ? "Y" : "N", "\n"))
            .ToList();
        rows.Sort(string.CompareOrdinal); // = ORDER BY ... COLLATE Latin1_General_BIN2 in SQL

        var sb = new StringBuilder();
        sb.Append(key).Append('\n');
        foreach (var row in rows) sb.Append(row);
        sb.Append(key);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
