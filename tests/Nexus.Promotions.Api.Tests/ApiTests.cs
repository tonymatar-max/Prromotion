using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Nexus.Promotions.Api;
using Nexus.Promotions.Engine;

namespace Nexus.Promotions.Api.Tests;

public class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly JsonSerializerOptions JsonOpts = Json.Options;

    static Basket SampleBasket()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Nexus.Promotions.Api", "samples", "basket.json");
        return JsonSerializer.Deserialize<Basket>(File.ReadAllText(path), JsonOpts)!;
    }

    [Fact]
    public async Task Health_reports_loaded_promotions()
    {
        var res = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("ok", res.GetProperty("status").GetString());
        Assert.Equal(8, res.GetProperty("promotions").GetInt32());
    }

    [Fact]
    public async Task Evaluate_sample_basket_applies_every_mechanic()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/api/v1/evaluate", SampleBasket(), JsonOpts);
        res.EnsureSuccessStatusCode();
        var result = (await res.Content.ReadFromJsonAsync<EvaluationResult>(JsonOpts))!;

        var applied = result.Promotions.Select(p => p.Code).ToHashSet();
        foreach (var code in new[] { "NIVEA15", "COLA-PRICE", "SHAMPOO-TIER", "SHAMPOO-B2G1", "PHONE-CASE", "SNACK3", "SPEND50", "RAMADAN10" })
            Assert.Contains(code, applied);

        Assert.Equal(2, result.Lines.Where(l => l.IsFree && l.ItemCode == "SH-400").Sum(l => l.Quantity));
        Assert.Equal(result.GrossTotal - result.DiscountTotal, result.NetTotal);
        Assert.Equal(result.DiscountTotal, result.Lines.Sum(l => l.DiscountAmount));
        Assert.Equal(64, result.Hash.Length);
    }

    [Fact]
    public async Task Hash_endpoint_matches_evaluation_hash()
    {
        var client = factory.CreateClient();
        var result = (await (await client.PostAsJsonAsync("/api/v1/evaluate", SampleBasket(), JsonOpts))
            .Content.ReadFromJsonAsync<EvaluationResult>(JsonOpts))!;
        var hash = await (await client.PostAsJsonAsync("/api/v1/hash", result.Lines, JsonOpts))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(result.Hash, hash.GetProperty("hash").GetString());
    }

    [Fact]
    public async Task Simulate_runs_a_draft_promotion_alone()
    {
        var draft = new Promotion
        {
            Code = "DRAFT-PHONE", Type = PromotionType.ItemDiscount,
            Scope = new ItemScope { ItemCodes = ["PH-X1"] }, RewardKind = RewardKind.AmountOff, RewardValue = 20,
        };
        var req = new SimulationRequest(SampleBasket(), [draft], IncludeActive: false);
        var res = await factory.CreateClient().PostAsJsonAsync("/api/v1/simulate", req, JsonOpts);
        var result = (await res.Content.ReadFromJsonAsync<EvaluationResult>(JsonOpts))!;

        Assert.Equal("DRAFT-PHONE", Assert.Single(result.Promotions).Code);
        Assert.Equal(20m, result.DiscountTotal);
    }

    [Fact]
    public async Task Document_evaluate_returns_a_write_back_plan()
    {
        // As the add-on sends it: matrix rows, price before discount, no APE result yet.
        var doc = new Nexus.Promotions.B1.DocumentInput
        {
            CardCode = "C20000",
            AmountDecimals = 3,
            Lines =
            [
                new Nexus.Promotions.B1.DocumentLineInput { LineNum = 0, ItemCode = "SH-400", Quantity = 3, UnitPrice = 1.950m },
                new Nexus.Promotions.B1.DocumentLineInput { LineNum = 1, ItemCode = "COLA15", Quantity = 2, UnitPrice = 0.650m },
            ],
        };
        var res = await factory.CreateClient().PostAsJsonAsync("/api/v1/documents/evaluate", doc, JsonOpts);
        res.EnsureSuccessStatusCode();
        var e = (await res.Content.ReadFromJsonAsync<Nexus.Promotions.B1.DocumentEvaluation>(JsonOpts))!;

        Assert.True(e.Changed);
        Assert.Contains(e.Lines, p => p.LineNum == 0 && p.Line.Quantity == 2);              // SH-400 paid part
        Assert.Contains(e.Lines, p => p.LineNum is null && p.Line.IsFree && p.DiscountPercent == 100);
        Assert.Contains(e.Lines, p => p.LineNum == 1 && p.Line.PromotionCodes.Contains("COLA-PRICE"));
        Assert.Equal(64, e.Result.Hash.Length);
    }

    [Fact]
    public async Task Empty_basket_is_rejected()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/api/v1/evaluate", new Basket { Lines = [] }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Api_key_is_enforced_when_configured()
    {
        var secured = factory.WithWebHostBuilder(b => b.UseSetting("Promotions:ApiKeys:0", "test-key"));
        var client = secured.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/promotions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        client.DefaultRequestHeaders.Add("X-Api-Key", "test-key");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/promotions")).StatusCode);
    }
}
