using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Nexus.Promotions.Api;
using Nexus.Promotions.Engine;

namespace Nexus.Promotions.Api.Tests;

/// <summary>One server, two companies (Head Office is the master): each request is answered for the company it names.</summary>
public class MultiCompanyApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    static readonly JsonSerializerOptions JsonOpts = Json.Options;
    readonly string _file = Path.Combine(Path.GetTempPath(), $"ape-multi-{Guid.NewGuid():N}.json");
    readonly WebApplicationFactory<Program> _factory;

    public MultiCompanyApiTests(WebApplicationFactory<Program> factory)
    {
        File.WriteAllText(_file, """
        [
          { "code": "MASTER-ONLY", "name": "Head Office only", "type": "ItemDiscount", "scope": { "itemCodes": ["A"] }, "rewardKind": "PercentOff", "rewardValue": 10 },
          { "code": "BOTH",        "name": "Both companies",   "type": "ItemDiscount", "scope": { "itemCodes": ["B"] }, "rewardKind": "PercentOff", "rewardValue": 5, "companies": ["HO", "BR1"] },
          { "code": "BR1-ONLY",    "name": "Branch 1 only",    "type": "ItemDiscount", "scope": { "itemCodes": ["A"] }, "rewardKind": "PercentOff", "rewardValue": 20, "companies": ["BR1"] }
        ]
        """);
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Promotions:PromotionsFile", _file);
            b.UseSetting("Promotions:ItemsFile", "");
            b.UseSetting("Companies:0:Db", "HO");
            b.UseSetting("Companies:0:Name", "Head Office");
            b.UseSetting("Companies:0:Master", "true");
            b.UseSetting("Companies:1:Db", "BR1");
            b.UseSetting("Companies:1:Name", "Branch 1");
        });
    }

    public void Dispose() { if (File.Exists(_file)) File.Delete(_file); }

    HttpClient Client(string? company = null)
    {
        var c = _factory.CreateClient();
        if (company is not null) c.DefaultRequestHeaders.Add("X-Company-Db", Uri.EscapeDataString(company));
        return c;
    }

    static async Task<string[]> Codes(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/v1/promotions")).EnumerateArray().Select(p => p.GetProperty("code").GetString()!).ToArray();

    [Fact]
    public async Task Each_company_is_answered_with_its_own_promotions_and_no_company_means_the_master()
    {
        Assert.Equal(["MASTER-ONLY", "BOTH"], await Codes(Client("HO")));
        Assert.Equal(["BOTH", "BR1-ONLY"], await Codes(Client("BR1")));
        Assert.Equal(["MASTER-ONLY", "BOTH"], await Codes(Client()));          // admin app, POS, integrations
        Assert.Equal(["BOTH", "BR1-ONLY"], await Codes(Client("br1")));       // case does not matter
    }

    [Fact]
    public async Task A_company_this_server_does_not_serve_is_refused_with_what_it_does_serve()
    {
        var res = await Client("BR9").GetAsync("/api/v1/promotions");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var message = body.GetProperty("errors")[0].GetString()!;
        Assert.Contains("BR9", message);
        Assert.Contains("HO, BR1", message);
    }

    [Fact]
    public async Task Evaluation_uses_only_that_companys_promotions()
    {
        var basket = new Basket { Lines = [new BasketLine { LineNum = 0, ItemCode = "A", Quantity = 1, UnitPrice = 100 }] };

        async Task<decimal> Discount(string company)
        {
            var res = await Client(company).PostAsJsonAsync("/api/v1/evaluate", basket, JsonOpts);
            res.EnsureSuccessStatusCode();
            return (await res.Content.ReadFromJsonAsync<EvaluationResult>(JsonOpts))!.DiscountTotal;
        }

        Assert.Equal(10m, await Discount("HO"));    // MASTER-ONLY, 10%
        Assert.Equal(20m, await Discount("BR1"));   // BR1-ONLY, 20%: it never applies in Head Office
    }

    [Fact]
    public async Task The_admin_app_is_told_there_are_several_companies_and_which_is_the_master()
    {
        var info = await Client().GetFromJsonAsync<JsonElement>("/api/v1/admin/info");
        Assert.True(info.GetProperty("multiCompany").GetBoolean());
        Assert.Equal("HO", info.GetProperty("master").GetString());

        var companies = await Client().GetFromJsonAsync<JsonElement>("/api/v1/admin/companies");
        Assert.Equal(["HO", "BR1"], companies.EnumerateArray().Select(c => c.GetProperty("db").GetString()!).ToArray());
        Assert.Equal(["Head Office", "Branch 1"], companies.EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray());
        Assert.True(companies[0].GetProperty("master").GetBoolean());
        Assert.False(companies[1].GetProperty("master").GetBoolean());
    }

    [Fact]
    public async Task The_promotion_list_carries_each_promotions_companies()
    {
        var list = await Client().GetFromJsonAsync<JsonElement>("/api/v1/admin/promotions");
        var both = list.EnumerateArray().Single(p => p.GetProperty("code").GetString() == "BOTH");

        Assert.Equal("HO,BR1", both.GetProperty("companies").GetString());
    }
}
