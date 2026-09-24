using Nexus.Promotions.Api;

namespace Nexus.Promotions.Api.Tests;

/// <summary>Two companies on one workstation: a server set up for one company must refuse the other's add-on.</summary>
public class CompanyGuardTests
{
    [Fact]
    public void Same_company_passes_whatever_the_case()
    {
        Assert.Null(CompanyGuard.Check("SBODemoHO", "SBODemoHO"));
        Assert.Null(CompanyGuard.Check("SBODemoHO", "sbodemoho"));
        Assert.Null(CompanyGuard.Check(" SBODemoHO ", " SBODemoHO"));
    }

    [Fact]
    public void Another_company_is_refused_with_a_message_naming_both()
    {
        var message = CompanyGuard.Check("SBODemoHO", "SBODemoBr1");

        Assert.NotNull(message);
        Assert.Contains("SBODemoHO", message);
        Assert.Contains("SBODemoBr1", message);
        Assert.Contains("ApiUrl", message);       // says how to fix it
    }

    [Fact]
    public void No_header_or_no_company_to_compare_is_not_checked()
    {
        Assert.Null(CompanyGuard.Check("SBODemoHO", null));            // admin app in a browser, POS, integrations
        Assert.Null(CompanyGuard.Check("SBODemoHO", "  "));
        Assert.Null(CompanyGuard.Check(null, "SBODemoBr1"));           // API reading promotions from files
        Assert.Null(CompanyGuard.Check("", "SBODemoBr1"));
    }

    [Fact]
    public void Non_ascii_company_names_travel_url_encoded()
    {
        var encoded = Uri.EscapeDataString("شركة_الاختبار");   // what the add-on sends: a header must be ASCII

        Assert.Null(CompanyGuard.Check("شركة_الاختبار", encoded));
        Assert.NotNull(CompanyGuard.Check("SBODemoHO", encoded));
    }

    [Fact]
    public void A_malformed_encoding_is_compared_as_written_not_a_crash()
    {
        Assert.NotNull(CompanyGuard.Check("SBODemoHO", "%E0%A4%A"));   // truncated escape sequence
    }
}
