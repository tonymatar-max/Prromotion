using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Promotions.B1;
using Nexus.Promotions.Engine;

namespace Nexus.Promotions.B1.Tests;

/// <summary>One promotion server for several companies: defined once, ticked per company, each evaluated on its own data.</summary>
public class CompanyTests
{
    static Promotion Promo(string code, params string[] companies) => new()
    {
        Code = code, Type = PromotionType.ItemDiscount, RewardValue = 10, Companies = companies,
        Scope = new ItemScope { ItemGroups = ["100"] },
    };

    // ── the scoping rule ─────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_ticked_means_the_master_company_only()
    {
        var p = Promo("P");
        Assert.True(CompanyScope.Applies(p, "HO", master: "HO"));
        Assert.False(CompanyScope.Applies(p, "BR1", master: "HO"));
    }

    [Fact]
    public void Ticked_companies_get_it_and_the_master_does_not_unless_ticked_too()
    {
        var p = Promo("P", "BR1", "BR2");
        Assert.False(CompanyScope.Applies(p, "HO", master: "HO"));
        Assert.True(CompanyScope.Applies(p, "BR1", master: "HO"));
        Assert.True(CompanyScope.Applies(p, "br2", master: "HO"));     // database names compare case-insensitively
        Assert.True(CompanyScope.Applies(Promo("P", "HO", "BR1"), "HO", master: "HO"));
    }

    [Fact]
    public void Select_keeps_only_a_companys_promotions()
    {
        var all = new[] { Promo("MASTER-ONLY"), Promo("BOTH", "HO", "BR1"), Promo("BR1-ONLY", "BR1") };

        Assert.Equal(["MASTER-ONLY", "BOTH"], CompanyScope.Select(all, "HO", "HO").Select(p => p.Code));
        Assert.Equal(["BOTH", "BR1-ONLY"], CompanyScope.Select(all, "BR1", "HO").Select(p => p.Code));
    }

    // ── the store: a view per company ────────────────────────────────────────────

    sealed class FixedSource(params Promotion[] promotions) : IPromotionSource
    {
        public Task<PromotionSet> LoadAsync(CancellationToken ct) => Task.FromResult(new PromotionSet(promotions, InMemoryItemCatalog.Empty));
    }

    static CompanyContext Company(string db, bool master, string hashKey, string itemGroup) => new()
    {
        Db = db, IsMaster = master, HashKey = hashKey,
        // The same item code lives in a different item group in each company.
        Catalog = new InMemoryItemCatalog([new ItemInfo { ItemCode = "X", ItemGroup = itemGroup, UnitPrice = 100 }]),
    };

    static async Task<PromotionStore> Store(params Promotion[] promotions)
    {
        var registry = new CompanyRegistry([Company("HO", true, "key-HO", "100"), Company("BR1", false, "key-BR1", "200")]);
        var store = new PromotionStore(new FixedSource(promotions), new EngineOptions(), NullLogger<PromotionStore>.Instance, registry);
        await store.ReloadAsync();
        return store;
    }

    static EvaluationResult Evaluate(PromotionStore.Snapshot view) =>
        view.Engine.Evaluate(new Basket { Lines = [new BasketLine { LineNum = 0, ItemCode = "X", Quantity = 1, UnitPrice = 100 }] }, view.Promotions);

    [Fact]
    public async Task Each_company_gets_only_its_own_promotions()
    {
        var store = await Store(Promo("MASTER-ONLY"), Promo("BOTH", "HO", "BR1"));

        Assert.Equal(["MASTER-ONLY", "BOTH"], store.ViewFor("HO")!.Promotions.Select(p => p.Code));
        Assert.Equal(["BOTH"], store.ViewFor("BR1")!.Promotions.Select(p => p.Code));
        Assert.Equal(2, store.All.Count);
    }

    [Fact]
    public async Task A_company_is_evaluated_with_its_own_item_data()
    {
        // "Item group 100" is the printers in Head Office, and something else in Branch 1.
        var store = await Store(Promo("GROUP-100", "HO", "BR1"));

        Assert.Equal(10m, Evaluate(store.ViewFor("HO")!).DiscountTotal);
        Assert.Equal(0m, Evaluate(store.ViewFor("BR1")!).DiscountTotal);
    }

    [Fact]
    public async Task Each_company_signs_its_result_with_its_own_hash_key()
    {
        var store = await Store(Promo("BOTH", "HO", "BR1"));

        var ho = Evaluate(store.ViewFor("HO")!).Hash;
        var br1 = Evaluate(store.ViewFor("BR1")!).Hash;
        Assert.NotEqual(ho, br1);   // identical document lines, different key: B1 in each company validates against its own
    }

    [Fact]
    public async Task Unknown_company_has_no_view_and_no_company_means_the_master()
    {
        var store = await Store(Promo("MASTER-ONLY"));

        Assert.Null(store.ViewFor("SOMEONE-ELSE"));
        Assert.Same(store.ViewFor("HO"), store.ViewFor(null));
        Assert.Same(store.ViewFor("HO"), store.Current);
    }

    [Fact]
    public async Task Without_a_registry_there_is_one_view_with_everything()
    {
        var store = new PromotionStore(new FixedSource(Promo("A"), Promo("B", "SOMEWHERE")), new EngineOptions(), NullLogger<PromotionStore>.Instance);
        await store.ReloadAsync();

        Assert.Equal(2, store.Current.Promotions.Count);   // exactly as before several companies were supported
    }

    // ── the registry ─────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_rejects_two_masters_and_duplicate_companies_and_picks_the_first_master_when_none_is_marked()
    {
        Assert.Throws<InvalidOperationException>(() => new CompanyRegistry([Company("A", true, "k", "1"), Company("B", true, "k", "1")]));
        Assert.Throws<InvalidOperationException>(() => new CompanyRegistry([Company("A", true, "k", "1"), Company("a", false, "k", "1")]));

        var registry = new CompanyRegistry([Company("A", false, "k", "1"), Company("B", false, "k", "1")]);
        Assert.Equal("A", registry.Master.Db);
        Assert.Equal("B", registry.Find("b")!.Db);
        Assert.Null(registry.Find("C"));
        Assert.False(registry.IsSingle);
    }

    [Fact]
    public void Without_a_Companies_section_there_is_one_company_taken_from_the_existing_settings()
    {
        var sl = new ServiceLayerOptions { CompanyDb = "SBODemoHO", Password = "x" };
        var registry = CompanyRegistry.Build([], sl, "Server=.;Database=SBODemoHO", "the-key", useServiceLayer: false);

        Assert.True(registry.IsSingle);
        Assert.Equal("default", registry.Master.Db);          // file mode has no B1 company
        Assert.Equal("the-key", registry.Master.HashKey);
        Assert.True(registry.Master.Settings.Configured);      // Sql:ConnectionString carries over for a single company
    }
}
