using System.Text.Json;
using Nexus.Promotions.B1;

namespace Nexus.Promotions.Api;

/// <summary>The companies this server serves, kept in companies.json so the admin app can edit them (applied on the next API start).</summary>
public sealed class CompanySettingsFile(string path)
{
    static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public const string Masked = "***";

    public string Path { get; } = path;
    public bool Exists => File.Exists(Path);

    public List<CompanyOptions>? Load() =>
        Exists ? JsonSerializer.Deserialize<List<CompanyOptions>>(File.ReadAllText(Path), Opts) : null;

    public static List<string> Validate(IReadOnlyList<CompanyOptions> list)
    {
        var errors = new List<string>();
        if (list.Count == 0) errors.Add("Add at least one company.");
        if (list.Any(c => string.IsNullOrWhiteSpace(c.Db))) errors.Add("Every company needs its database name.");
        foreach (var g in list.Where(c => !string.IsNullOrWhiteSpace(c.Db)).GroupBy(c => c.Db.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"Company '{g.Key}' is listed twice.");
        if (list.Count > 0 && list.Count(c => c.Master) != 1) errors.Add("Exactly one company must be the master (the one that holds the promotions).");
        return errors;
    }

    /// <summary>Saves the list; a blank or masked password / connection string keeps what is already stored for that company.</summary>
    public void Save(IReadOnlyList<CompanyOptions> incoming, IReadOnlyList<CompanyOptions> current)
    {
        var merged = incoming.Select(c =>
        {
            var old = current.FirstOrDefault(o => string.Equals(o.Db, c.Db, StringComparison.OrdinalIgnoreCase));
            return new CompanyOptions
            {
                Db = c.Db.Trim(),
                Name = string.IsNullOrWhiteSpace(c.Name) ? null : c.Name.Trim(),
                Master = c.Master,
                User = string.IsNullOrWhiteSpace(c.User) ? null : c.User.Trim(),
                Password = string.IsNullOrEmpty(c.Password) ? old?.Password : c.Password,
                SqlConnectionString = string.IsNullOrWhiteSpace(c.SqlConnectionString) ? null
                    : c.SqlConnectionString.Contains(Masked) ? old?.SqlConnectionString : c.SqlConnectionString.Trim(),
                HashKey = string.IsNullOrWhiteSpace(c.HashKey) ? old?.HashKey : c.HashKey,
                PriceList = c.PriceList,
            };
        }).ToList();
        File.WriteAllText(Path, JsonSerializer.Serialize(merged, Opts));
    }

    /// <summary>What the admin app may see: no passwords, no keys, and the password part of a connection string hidden.</summary>
    public static object Redact(CompanyOptions c) => new
    {
        db = c.Db, name = c.Name, master = c.Master, user = c.User, hasPassword = !string.IsNullOrEmpty(c.Password),
        sqlConnectionString = c.SqlConnectionString is null ? null
            : System.Text.RegularExpressions.Regex.Replace(c.SqlConnectionString, @"(?i)(password|pwd)\s*=\s*[^;]*", "$1=" + Masked),
        priceList = c.PriceList,
    };
}
