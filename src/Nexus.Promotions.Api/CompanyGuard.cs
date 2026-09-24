namespace Nexus.Promotions.Api;

/// <summary>
/// A promotion server serves the companies in its "Companies" configuration (one company when there is none). An add-on
/// tells the API which company its B1 client is logged in to (the X-Company-Db header, URL-encoded because a database
/// name may not be ASCII). A company this server does not serve is refused with 409 instead of being answered with
/// someone else's promotions. Requests without the header (the admin app in a browser, POS, integrations) are not
/// checked and are answered for the master company, and neither is an API reading promotions from files.
/// </summary>
public static class CompanyGuard
{
    public const string Header = "X-Company-Db";

    /// <summary>The company the request names, decoded; null when there is no header.</summary>
    public static string? Asked(string? askedEncoded)
    {
        if (string.IsNullOrWhiteSpace(askedEncoded)) return null;
        try { return Uri.UnescapeDataString(askedEncoded).Trim(); }
        catch (UriFormatException) { return askedEncoded.Trim(); }
    }

    /// <returns>Null when the request may go on, otherwise the message for the 409 answer.</returns>
    public static string? Check(IReadOnlyCollection<string> serves, string? askedEncoded)
    {
        var asked = Asked(askedEncoded);
        if (serves.Count == 0 || asked is null) return null;
        if (serves.Any(s => string.Equals(s.Trim(), asked, StringComparison.OrdinalIgnoreCase))) return null;

        return serves.Count == 1
            ? $"This promotion server is set up for company '{serves.First().Trim()}', but the request comes from company " +
              $"'{asked}'. Set ApiUrl for that company (dbo.APE_Settings) to its own promotion server."
            : $"This promotion server does not serve company '{asked}'. It serves: {string.Join(", ", serves.Select(s => s.Trim()))}. " +
              "Set ApiUrl for that company (dbo.APE_Settings) to the server that serves it, or add the company to this server's Companies.";
    }
}
