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

    // From files the admin app is read-only, but it must still be able to open a promotion it lists (was HTTP 404).
    [Fact]
    public async Task Admin_app_opens_a_file_promotion_by_code_and_lists_the_same_ones()
    {
        var client = factory.CreateClient();
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/promotions");
        var codes = list.EnumerateArray().Select(p => p.GetProperty("code").GetString()!).ToList();
        Assert.Contains("NIVEA15", codes);

        foreach (var code in codes)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/admin/promotions/{code}")).StatusCode);

        var nivea = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/promotions/nivea15"); // case does not matter
        Assert.Equal("ItemDiscount", nivea.GetProperty("type").GetString());
        Assert.Equal("P", nivea.GetProperty("rewardKind").GetString());
        Assert.Equal(15m, nivea.GetProperty("rewardValue").GetDecimal());
        Assert.Equal("A", nivea.GetProperty("status").GetString());
        Assert.Contains(nivea.GetProperty("scopes").EnumerateArray(), s => s.GetProperty("scopeType").GetString() == "M");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/admin/promotions/NO-SUCH-CODE")).StatusCode);
    }

    // The Customer Portal contract: order in, discounted lines + the exact Service Layer fields to post out.
    [Fact]
    public async Task Portal_order_gets_promotions_and_service_layer_fields_that_pass_the_hash_check()
    {
        var client = factory.CreateClient();
        var order = new
        {
            cardCode = "C20000",
            channel = "PORTAL",
            lines = new object[]
            {
                new { lineNum = 0, itemCode = "N-CREAM", quantity = 4m, unitPrice = 2.75m },
                new { lineNum = 1, itemCode = "SH-400", quantity = 3m, unitPrice = 1.95m },
                new { lineNum = 2, itemCode = "COLA15", quantity = 1m, unitPrice = 0.65m },
            },
        };
        var res = await client.PostAsJsonAsync("/api/v1/portal/orders/evaluate", order, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);

        var applied = body.GetProperty("applied").EnumerateArray().Select(a => a.GetProperty("code").GetString()).ToList();
        Assert.Contains("NIVEA15", applied);
        var cream = body.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("itemCode").GetString() == "N-CREAM");
        Assert.Equal(15m, cream.GetProperty("discountPercent").GetDecimal());
        Assert.Equal(0, cream.GetProperty("sourceLineNum").GetInt32());
        Assert.True(body.GetProperty("discountTotal").GetDecimal() > 0);
        Assert.Equal(body.GetProperty("grossTotal").GetDecimal() - body.GetProperty("discountTotal").GetDecimal(),
            body.GetProperty("netTotal").GetDecimal());

        // What the portal posts: PascalCase Service Layer names, one document line per result line.
        var sl = body.GetProperty("serviceLayer");
        var docLines = sl.GetProperty("documentLines").EnumerateArray().ToList();
        Assert.Equal(body.GetProperty("lines").GetArrayLength(), docLines.Count);
        foreach (var l in docLines)
            foreach (var field in new[] { "ItemCode", "Quantity", "UnitPrice", "DiscountPercent", "U_APE_Promo", "U_APE_DiscAmt", "U_APE_Free", "U_APE_Group" })
                Assert.True(l.TryGetProperty(field, out _), $"document line is missing {field}");
        Assert.Contains(docLines, l => l.GetProperty("U_APE_Free").GetString() == "Y" && l.GetProperty("ItemCode").GetString() == "SH-400");
        var header = sl.GetProperty("header");
        Assert.Equal("A", header.GetProperty("U_APE_Mode").GetString());

        // SAP recomputes the hash from the posted lines: rebuilding it from ONLY the documented fields must give the same value.
        var hashInput = docLines.Select(l => new
        {
            itemCode = l.GetProperty("ItemCode").GetString(), quantity = l.GetProperty("Quantity").GetDecimal(),
            unitPrice = l.GetProperty("UnitPrice").GetDecimal(), discountAmount = l.GetProperty("U_APE_DiscAmt").GetDecimal(),
            promotionCodes = l.GetProperty("U_APE_Promo").GetString(), isFree = l.GetProperty("U_APE_Free").GetString() == "Y",
        }).ToList();
        var recomputed = await (await client.PostAsJsonAsync("/api/v1/hash", hashInput, JsonOpts)).Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal(header.GetProperty("U_APE_Hash").GetString(), recomputed.GetProperty("hash").GetString());
        Assert.Equal(body.GetProperty("hash").GetString(), header.GetProperty("U_APE_Hash").GetString());
    }

    [Fact]
    public async Task Portal_order_with_bad_lines_is_refused_with_every_reason()
    {
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/portal/orders/evaluate", new
        {
            cardCode = "C20000",
            lines = new object[]
            {
                new { lineNum = 0, itemCode = "N-CREAM", quantity = 0m, unitPrice = 2.75m },
                new { lineNum = 0, itemCode = "", quantity = 1m, unitPrice = -1m },
            },
        }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.Contains("quantity must be above zero", text);
        Assert.Contains("itemCode is required", text);
        Assert.Contains("cannot be negative", text);
        Assert.Contains("lineNum must be different", text);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/portal/orders/evaluate", new { cardCode = "C1", lines = Array.Empty<object>() }, JsonOpts)).StatusCode);
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
