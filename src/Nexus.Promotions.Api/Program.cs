using Microsoft.Extensions.Options;
using Nexus.Promotions.Api;
using Nexus.Promotions.B1;
using Nexus.Promotions.Engine;

var builder = WebApplication.CreateBuilder(args);
// A second company on the same machine runs its own copy under its own service name (Service:Name).
builder.Host.UseWindowsService(o => o.ServiceName = builder.Configuration["Service:Name"] ?? "Nexus Promotions API"); // no-op from a console
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables("APE_");
builder.Services.Configure<PromotionsOptions>(builder.Configuration.GetSection("Promotions"));
builder.Services.Configure<ServiceLayerOptions>(builder.Configuration.GetSection("ServiceLayer"));
builder.Services.ConfigureHttpJsonOptions(o => Json.Configure(o.SerializerOptions));

var promotionsOptions = builder.Configuration.GetSection("Promotions").Get<PromotionsOptions>() ?? new PromotionsOptions();
if (promotionsOptions.UsesServiceLayer)
{
    builder.Services.AddSingleton(sp => new ServiceLayer(sp.GetRequiredService<IOptions<ServiceLayerOptions>>().Value));
    builder.Services.AddSingleton(sp => new B1MasterData(sp.GetRequiredService<ServiceLayer>(),
        sp.GetRequiredService<IOptions<ServiceLayerOptions>>().Value.PriceList));
    builder.Services.AddSingleton<IPromotionSource, ServiceLayerPromotionSource>();
    builder.Services.AddSingleton(sp => new PromotionAdmin(sp.GetRequiredService<ServiceLayer>()));
}
else
{
    builder.Services.AddSingleton<IPromotionSource, FilePromotionSource>();
}
// dbo.APE_Settings: one central switch (ModeA) that reaches every workstation running the add-on at once,
// instead of a setting that would have to be changed machine by machine.
builder.Services.AddSingleton(new ApeSettings(builder.Configuration["Sql:ConnectionString"]));
builder.Services.AddSingleton(new EngineOptions
{
    ConflictMode = promotionsOptions.ConflictMode,
    StackingMode = promotionsOptions.StackingMode,
    HashKey = promotionsOptions.HashKey,
});
builder.Services.AddSingleton<PromotionStore>();
builder.Services.AddSingleton(sp => new DocumentEvaluator(
    () => (sp.GetRequiredService<PromotionStore>().Current.Promotions, sp.GetRequiredService<PromotionStore>().Current.Engine),
    sp.GetService<B1MasterData>()));
builder.Services.AddHostedService(sp => new PromotionRefreshService(
    sp.GetRequiredService<PromotionStore>(), TimeSpan.FromSeconds(promotionsOptions.RefreshSeconds),
    sp.GetRequiredService<ILogger<PromotionRefreshService>>()));

var app = builder.Build();
await app.Services.GetRequiredService<PromotionStore>().ReloadAsync();

// Warm the admin app's cached lookups (item groups, manufacturers, customer groups, price lists) in the
// background, one at a time. On this local demo Service Layer, a burst of brand-new connections right after
// a cold start can each queue for several seconds before B1's own worker pool catches up (B1 itself still
// answers instantly once a request lands — this is B1's connection handling, not our client). Once any one
// request gets through, the rest are fast, so doing this sequentially at startup means whoever opens the
// admin app first never sees the delay themselves.
if (app.Services.GetService<PromotionAdmin>() is { } promotionAdmin)
{
    _ = Task.Run(async () =>
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        foreach (var kind in new[] { "itemgroups", "manufacturers", "customergroups", "pricelists", "items" })
        {
            try { await promotionAdmin.LookupAsync(kind, null, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Warm-up lookup {Kind} failed; the admin app will fetch it on demand", kind); }
        }
        logger.LogInformation("Admin app lookups warmed up");
    });
}

// API key check for everything under /api (NFR-05). Health stays open for monitoring.
app.Use(async (ctx, next) =>
{
    var keys = ctx.RequestServices.GetRequiredService<IOptions<PromotionsOptions>>().Value.ApiKeys;
    if (keys.Length > 0 && ctx.Request.Path.StartsWithSegments("/api")
        && !(ctx.Request.Headers.TryGetValue("X-Api-Key", out var key) && keys.Contains(key.ToString())))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

// Company guard: an add-on names the company its B1 client is in; refuse rather than apply another company's promotions.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers.TryGetValue(CompanyGuard.Header, out var asked))
    {
        var serves = promotionsOptions.UsesServiceLayer
            ? ctx.RequestServices.GetRequiredService<IOptions<ServiceLayerOptions>>().Value.CompanyDb
            : null;
        if (CompanyGuard.Check(serves, asked.ToString()) is { } problem)
        {
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            await ctx.Response.WriteAsJsonAsync(new { errors = new[] { problem }, serves });
            return;
        }
    }
    await next();
});

// The promotions admin app (wwwroot): list, edit and simulate promotions stored in the @APE_PROMO UDO.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", (PromotionStore store) =>
    Results.Ok(new { status = "ok", promotions = store.Current.Promotions.Count, loadedAt = store.Current.LoadedAt }));

var api = app.MapGroup("/api/v1");

// FR-10: basket in, adjusted lines out. For POS and e-commerce, which build their own basket.
api.MapPost("/evaluate", (Basket basket, PromotionStore store) =>
{
    if (basket.Lines is null || basket.Lines.Count == 0) return Results.BadRequest(new { error = "Basket has no lines" });
    var s = store.Current;
    return Results.Ok(s.Engine.Evaluate(basket, s.Promotions));
});

// Mode A (add-on) and Mode B (worker): a B1 document as it stands in, the write-back plan out.
// Undoes an earlier APE result first, and looks up item attributes and the customer's group in B1.
api.MapPost("/documents/evaluate", async (DocumentInput document, DocumentEvaluator evaluator, CancellationToken ct) =>
{
    if (document.Lines is null || document.Lines.Count == 0) return Results.BadRequest(new { error = "Document has no lines" });
    return Results.Ok(await evaluator.EvaluateAsync(document, ct));
});

// FR-13: run draft promotions against a basket, alone or together with the active ones.
api.MapPost("/simulate", (SimulationRequest req, PromotionStore store) =>
{
    if (req.Basket?.Lines is null || req.Basket.Lines.Count == 0) return Results.BadRequest(new { error = "Basket has no lines" });
    var s = store.Current;
    var promotions = req.IncludeActive ? s.Promotions.Concat(req.Promotions).ToList() : req.Promotions;
    return Results.Ok(s.Engine.Evaluate(req.Basket, promotions));
});

// Validation helper for integrations and the B1 procedure tests: recompute the hash of a document's lines (FR-45).
api.MapPost("/hash", (IReadOnlyList<ResultLine> lines, IOptions<PromotionsOptions> o) =>
    Results.Ok(new { hash = ResultHasher.Compute(lines, o.Value.HashKey) }));

api.MapGet("/promotions", (PromotionStore store) => Results.Ok(store.Current.Promotions));

api.MapPost("/promotions/reload", async (PromotionStore store, CancellationToken ct) =>
{
    var s = await store.ReloadAsync(ct);
    return Results.Ok(new { promotions = s.Promotions.Count, loadedAt = s.LoadedAt });
});

// ── Admin app API: reads and writes @APE_PROMO through the Service Layer (Promotions:Source = ServiceLayer) ──
var admin = api.MapGroup("/admin");

admin.MapGet("/info", (IOptions<PromotionsOptions> o, IOptions<ServiceLayerOptions> sl, ApeSettings settings) => Results.Ok(new
{
    source = o.Value.Source,
    editable = o.Value.UsesServiceLayer,
    company = o.Value.UsesServiceLayer ? sl.Value.CompanyDb : null,
    // Checked by every add-on before it auto-applies on Add/Update (PromotionApplier.Apply): one flip here
    // reaches every workstation immediately. The manual "Apply Promotions" button always still works.
    modeAEnabled = settings.ModeAEnabled,
    modeASettable = settings.Configured,
}));

// One switch for every workstation's add-on: turns automatic apply-on-save off (or back on) everywhere at once.
// Distinct from a promotion's own Screens restriction (which document types it runs on) — this is the whole-of-Mode-A kill switch.
admin.MapPost("/mode-a", (ModeAChange change, ApeSettings settings) =>
{
    if (!settings.Configured) return Results.BadRequest(new { errors = new[] { "No SQL connection configured (Sql:ConnectionString)." } });
    settings.ModeAEnabled = change.Enabled;
    return Results.Ok(new { modeAEnabled = settings.ModeAEnabled });
});

admin.MapGet("/promotions", async (IServiceProvider sp, PromotionStore store, CancellationToken ct) =>
    sp.GetService<PromotionAdmin>() is { } a
        ? Results.Ok(await a.ListAsync(ct))
        : Results.Ok(store.Current.Promotions.Select(p => new AdminPromotion
            { Code = p.Code, Name = p.Name, Type = p.Type.ToString(), Status = "A", Priority = p.Priority })));

admin.MapGet("/promotions/{code}", async (string code, IServiceProvider sp, CancellationToken ct) =>
    Admin(sp) is { } a && await a.GetAsync(code, ct) is { } p ? Results.Ok(p) : Results.NotFound());

admin.MapPost("/promotions", async (AdminPromotion p, IServiceProvider sp, PromotionStore store, CancellationToken ct) =>
{
    if (Admin(sp) is not { } a) return ReadOnly();
    if (Errors(p) is { Count: > 0 } errors) return Results.BadRequest(new { errors });
    if (await a.GetAsync(p.Code, ct) is not null) return Results.Conflict(new { errors = new[] { $"A promotion {p.Code} already exists." } });
    await a.CreateAsync(p, ct);
    await store.ReloadAsync(ct);
    return Results.Ok(await a.GetAsync(p.Code, ct));
});

admin.MapPut("/promotions/{code}", async (string code, AdminPromotion p, IServiceProvider sp, PromotionStore store, CancellationToken ct) =>
{
    if (Admin(sp) is not { } a) return ReadOnly();
    p = p with { Code = code };
    if (Errors(p) is { Count: > 0 } errors) return Results.BadRequest(new { errors });
    if (await a.GetAsync(code, ct) is null) return Results.NotFound();
    await a.UpdateAsync(p, ct);
    await store.ReloadAsync(ct);
    return Results.Ok(await a.GetAsync(code, ct));
});

// Status changes only: promotions are never hard-deleted (NFR-04); "C" cancels.
admin.MapPost("/promotions/{code}/status", async (string code, StatusChange change, IServiceProvider sp, PromotionStore store, CancellationToken ct) =>
{
    if (Admin(sp) is not { } a) return ReadOnly();
    if (change.Status is not ("D" or "P" or "A" or "S" or "E" or "C")) return Results.BadRequest(new { errors = new[] { "Unknown status." } });
    await a.SetStatusAsync(code, change.Status, ct);
    await store.ReloadAsync(ct);
    return Results.Ok(await a.GetAsync(code, ct));
});

admin.MapGet("/lookups/{kind}", async (string kind, string? q, IServiceProvider sp, CancellationToken ct) =>
    Admin(sp) is { } a ? Results.Ok(await a.LookupAsync(kind, q, ct)) : Results.Ok(Array.Empty<Lookup>()));

// Try a promotion (saved or not) on a sample basket, alone or with the live ones. Prices default to the item's price list.
admin.MapPost("/simulate", async (AdminSimulation req, PromotionStore store, IServiceProvider sp, CancellationToken ct) =>
{
    // No promotion: the live ones only (the simulator page). With one: that draft, alone or with the live ones.
    var draft = req.Promotion?.ToEngine();
    var others = draft is null || req.IncludeActive ? store.Current.Promotions.Where(p => p.Code != draft?.Code) : [];
    var md = sp.GetService<B1MasterData>();
    CustomerInfo? customer = null;
    if (md is not null)
    {
        await md.PrefetchItemsAsync(req.Lines.Select(l => l.ItemCode), ct);
        customer = await md.GetCustomerAsync(req.CardCode, ct);
    }
    var basket = new Basket
    {
        CardCode = req.CardCode,
        CustomerGroup = customer?.GroupCode,
        PriceList = customer?.PriceList,
        Coupons = req.Coupons ?? [],
        Timestamp = req.Timestamp ?? DateTime.Now,
        AmountDecimals = req.AmountDecimals,
        Lines = req.Lines.Select((l, i) => new BasketLine
        {
            LineNum = i,
            ItemCode = l.ItemCode,
            Quantity = l.Quantity,
            UnitPrice = l.UnitPrice ?? store.Current.Catalog.Find(l.ItemCode)?.UnitPrice ?? 0,
        }).ToList(),
    };
    var promotions = draft is null ? others.ToList() : others.Append(draft).ToList();
    return Results.Ok(store.Current.Engine.Evaluate(basket, promotions));
});

app.Run();

static PromotionAdmin? Admin(IServiceProvider sp) => sp.GetService<PromotionAdmin>();

static IResult ReadOnly() => Results.BadRequest(new { errors = new[] { "Promotions come from files; set Promotions:Source to ServiceLayer to edit them." } });

static List<string> Errors(AdminPromotion p) => p.Validate().Where(m => !m.StartsWith("Note:")).ToList();

public sealed record SimulationRequest(Basket Basket, IReadOnlyList<Promotion> Promotions, bool IncludeActive = true);

public sealed record StatusChange(string Status);

public sealed record ModeAChange(bool Enabled);

public sealed record AdminSimulationLine(string ItemCode, decimal Quantity, decimal? UnitPrice);

public sealed record AdminSimulation(AdminPromotion? Promotion, IReadOnlyList<AdminSimulationLine> Lines, string? CardCode,
    string[]? Coupons = null, DateTime? Timestamp = null, bool IncludeActive = false, int AmountDecimals = 2);

public partial class Program;
