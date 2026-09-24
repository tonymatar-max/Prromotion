using Nexus.Promotions.Api;

namespace Nexus.Promotions.Api.Tests;

/// <summary>A server serves certain companies; an add-on from any other company is refused, never answered with someone else's promotions.</summary>
public class CompanyGuardTests
{
    static readonly string[] One = ["SBODemoHO"];
    static readonly string[] Two = ["SBODemoHO", "SBODemoBr1"];

    [Fact]
    public void A_served_company_passes_whatever_the_case()
    {
        Assert.Null(CompanyGuard.Check(One, "SBODemoHO"));
        Assert.Null(CompanyGuard.Check(One, "sbodemoho"));
        Assert.Null(CompanyGuard.Check([" SBODemoHO "], " SBODemoHO"));
        Assert.Null(CompanyGuard.Check(Two, "SBODEMOBR1"));
    }

    [Fact]
    public void A_company_that_is_not_served_is_refused_naming_it()
    {
        var single = CompanyGuard.Check(One, "SBODemoBr1");
        Assert.NotNull(single);
        Assert.Contains("SBODemoHO", single);
        Assert.Contains("SBODemoBr1", single);
        Assert.Contains("ApiUrl", single);            // says how to fix it

        var several = CompanyGuard.Check(Two, "SBODemoBr2");
        Assert.NotNull(several);
        Assert.Contains("SBODemoBr2", several);
        Assert.Contains("SBODemoHO, SBODemoBr1", several);   // lists what it does serve
    }

    [Fact]
    public void No_header_or_nothing_to_compare_against_is_not_checked()
    {
        Assert.Null(CompanyGuard.Check(One, null));                     // admin app in a browser, POS, integrations
        Assert.Null(CompanyGuard.Check(One, "  "));
        Assert.Null(CompanyGuard.Check([], "SBODemoBr1"));              // API reading promotions from files
    }

    [Fact]
    public void Non_ascii_company_names_travel_url_encoded()
    {
        var encoded = Uri.EscapeDataString("شركة_الاختبار");   // what the add-on sends: a header must be ASCII

        Assert.Null(CompanyGuard.Check(["شركة_الاختبار"], encoded));
        Assert.NotNull(CompanyGuard.Check(One, encoded));
        Assert.Equal("شركة_الاختبار", CompanyGuard.Asked(encoded));
    }

    [Fact]
    public void A_malformed_encoding_is_compared_as_written_not_a_crash()
    {
        Assert.NotNull(CompanyGuard.Check(One, "%E0%A4%A"));   // truncated escape sequence
    }
}
