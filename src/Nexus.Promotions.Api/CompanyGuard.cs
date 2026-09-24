namespace Nexus.Promotions.Api;

/// <summary>
/// One promotion API instance serves ONE company: its promotions, item groups, price lists, customer groups and
/// hash key are that company's. An add-on tells the API which company its B1 client is logged in to (the
/// X-Company-Db header, URL-encoded because a database name may not be ASCII), and a mismatch is refused with
/// 409 instead of quietly applying another company's promotions. Requests without the header (the admin app in a
/// browser, POS, integrations) are not affected, and neither is an API reading promotions from files.
/// </summary>
public static class CompanyGuard
{
    public const string Header = "X-Company-Db";

    /// <returns>Null when the request may go on, otherwise the message for the 409 answer.</returns>
    public static string? Check(string? serves, string? askedEncoded)
    {
        if (string.IsNullOrWhiteSpace(serves) || string.IsNullOrWhiteSpace(askedEncoded)) return null;

        string asked;
        try { asked = Uri.UnescapeDataString(askedEncoded); }
        catch (UriFormatException) { asked = askedEncoded; }

        if (string.Equals(serves.Trim(), asked.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return $"This promotion server is set up for company '{serves.Trim()}', but the request comes from company " +
               $"'{asked.Trim()}'. Set ApiUrl for that company (dbo.APE_Settings) to its own promotion server.";
    }
}
