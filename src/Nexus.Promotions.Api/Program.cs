using Microsoft.Extensions.Options;
using Nexus.Promotions.Api;
using Nexus.Promotions.B1;
using Nexus.Promotions.Engine;

var builder = WebApplication.CreateBuilder(args);
// A second server on the same machine runs its own copy under its own service name (Service:Name).
builder.Host.UseWindowsService(o => o.ServiceName = builder.Configuration["Service:Name"] ?? "Nexus Promotions API"); // no-op from a console
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables("APE_");
builder.Services.Configure<PromotionsOptions>(builder.Configuration.GetSection("Promotions"));
builder.Services.Configure<ServiceLayerOptions>(builder.Configuration.GetSection("ServiceLayer"));
builder.Services.ConfigureHttpJsonOptions(o => Json.Configure(o.SerializerOptions));

var promotionsOptions = builder.Configuration.GetSection("Promotions").Get<PromotionsOptions>() ?? new PromotionsOptions();

// The companies this server serves. Promotions are defined once in the master company and ticked per company; each
// company is evaluated with its own item, customer and price data. Without a "Companies" section there is one company
// (ServiceLayer:CompanyDb) and everything behaves as it did before several companies were supported.
var companyFile = new CompanySettingsFile(Path.Combine(builder.Environment.ContentRootPath, "companies.json"));
var configuredCompanies = companyFile.Load() ?? builder.Configuration.GetSection("Companies").Get<List<CompanyOptions>>() ?? [];
builder.Services.AddSingleton(companyFile);
var registry = CompanyRegistry.Build(
    configuredCompanies,
    builder.Configuration.GetSection("ServiceLayer").Get<ServiceLayerOptions>() ?? new ServiceLayerOptions(),
    builder.Configuration["Sql:ConnectionString"],
    promotionsOptions.HashKey,
    promotionsOptions.UsesServiceLayer);
builder.Services.AddSingleton(registry);

if (promotionsOptions.UsesServiceLayer)
    builder.Services.AddSingleton<IPromotionSource>(new ServiceLayerPromotionSource(registry.Master.ServiceLayer!, registry.Master.MasterData!));
else
    builder.Services.AddSingleton<IPromotionSource, FilePromotionSource>();

builder.Services.AddSingleton(new EngineOptions
{
    ConflictMode = promotionsOptions.ConflictMode,
    StackingMode = promotionsOptions.StackingMode,
    HashKey = promotionsOptions.HashKey,
});
builder.Services.AddSingleton<PromotionStore>();
builder.Services.AddHostedService(sp => new PromotionRefreshService(
    sp.GetRequiredService<PromotionStore>(), TimeSpan.FromSeconds(promotionsOptions.RefreshSeconds),
    sp.GetRequiredService<ILogger<PromotionRefreshService>>()));

var app = builder.Build();
var store = app.Services.GetRequiredService<PromotionStore>();
await store.ReloadAsync();

// One evaluator per company: the same document logic, on that company's own rules view and item/customer data.
foreach (var company in registry.All)
{
    var c = company;
    c.Evaluator = new DocumentEvaluator(() => { var v = store.ViewFor(c.Db)!; return (v.Promotions, v.Engine); }, c.MasterData);
}

// Warm the admin app's cached lookups (item groups, manufacturers, customer groups, price lists) in the
// background, one at a time. On this local demo Service Layer, a burst of brand-new connections right after
// a cold start can each queue for several seconds before B1's own worker pool catches up (B1 itself still
// answers instantly once a request lands — this is B1's connection handling, not our client). Once any one
// request gets through, the rest are fast, so doing this sequentially at startup means whoever opens the
// admin app first never sees the delay themselves.
if (registry.Master.Admin is { } promotionAdmin)
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

// Company guard: an add-on names the company its B1 client is in; refuse a company this server does not serve.
// (A file-based API with no Companies configured serves whatever asks.)
var served = promotionsOptions.UsesServiceLayer || configuredCompanies.Count > 0
    ? registry.All.Select(c => c.Db).ToList()
    : new List<string>();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers.TryGetValue(CompanyGuard.Header, out var asked)
        && CompanyGuard.Check(served, asked.ToString()) is { } problem)
    {
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await ctx.Response.WriteAsJsonAsync(new { errors = new[] { problem }, serves = served });
        return;
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
api.MapPost("/evaluate", (Basket basket, HttpContext ctx, PromotionStore store) =>
{
    if (basket.Lines is null || basket.Lines.Count == 0) return Results.BadRequest(new { error = "Basket has no lines" });
    var s = store.ViewFor(CompanyOf(ctx).Db) ?? store.Current;
    return Results.Ok(s.Engine.Evaluate(basket, s.Promotions));
});

// Mode A (add-on) and Mode B (worker): a B1 document as it stands in, the write-back plan out.
// Undoes an earlier APE result first, and looks up item attributes and the customer's group in the company's own B1.
api.MapPost("/documents/evaluate", async (DocumentInput document, HttpContext ctx, CancellationToken ct) =>
{
    if (document.Lines is null || document.Lines.Count == 0) return Results.BadRequest(new { error = "Document has no lines" });
    return Results.Ok(await CompanyOf(ctx).Evaluator!.EvaluateAsync(document, ct));
});

// FR-13: run draft promotions against a basket, alone or together with the active ones.
api.MapPost("/simulate", (SimulationRequest req, HttpContext ctx, PromotionStore store) =>
{
    if (req.Basket?.Lines is null || req.Basket.Lines.Count == 0) return Results.BadRequest(new { error = "Basket has no lines" });
    var s = store.ViewFor(CompanyOf(ctx).Db) ?? store.Current;
    var promotions = req.IncludeActive ? s.Promotions.Concat(req.Promotions).ToList() : req.Promotions;
    return Results.Ok(s.Engine.Evaluate(req.Basket, promotions));
});

// Validation helper for integrations and the B1 procedure tests: recompute the hash of a document's lines (FR-45),
// with the hash key of the company asking.
api.MapPost("/hash", (IReadOnlyList<ResultLine> lines, HttpContext ctx, IOptions<PromotionsOptions> o) =>
    Results.Ok(new { hash = ResultHasher.Compute(lines, CompanyOf(ctx).HashKey ?? o.Value.HashKey) }));

api.MapGet("/promotions", (HttpContext ctx, PromotionStore store) =>
    Results.Ok((store.ViewFor(CompanyOf(ctx).Db) ?? store.Current).Promotions));

api.MapPost("/promotions/reload", async (PromotionStore store, CancellationToken ct) =>
{
    var s = await store.ReloadAsync(ct);
    return Results.Ok(new { promotions = s.Promotions.Count, loadedAt = s.LoadedAt });
});

// ── Admin app API: reads and writes @APE_PROMO in the MASTER company through its Service Layer (Promotions:Source = ServiceLayer) ──
var admin = api.MapGroup("/admin");

admin.MapGet("/info", (HttpContext ctx, IOptions<PromotionsOptions> o, CompanyRegistry reg) =>
{
    var asking = CompanyOf(ctx);
    return Results.Ok(new
    {
        source = o.Value.Source,
        editable = o.Value.UsesServiceLayer,
        company = o.Value.UsesServiceLayer ? reg.Master.Db : null,
        master = reg.Master.Db,
        multiCompany = !reg.IsSingle,
        // Checked by every add-on before it auto-applies on Add/Update (PromotionApplier.Apply), for ITS company: one flip
        // in the admin app reaches every workstation of that company immediately. The manual button always still works.
        modeAEnabled = asking.Settings.ModeAEnabled,
        modeASettable = asking.Settings.Configured,
    });
});

// The companies this server serves, for the admin app: which is the master and each one's central switch.
admin.MapGet("/companies", (CompanyRegistry reg) => Results.Ok(reg.All.Select(c =>
{
    bool? modeA = null; string? error = null;
    try { modeA = c.Settings.Configured ? c.Settings.ModeAEnabled : null; }
    catch (Exception ex) { error = ex.Message; }   // one company's database being unreachable must not hide the others
    return new { db = c.Db, name = c.Label, master = c.IsMaster, modeAEnabled = modeA, modeASettable = c.Settings.Configured, error };
})));

// One switch per company for every workstation's add-on: turns automatic apply-on-save off (or back on) there at once.
// Distinct from a promotion's own Screens restriction (which document types it runs on) — this is the whole-of-Mode-A kill switch.
admin.MapPost("/mode-a", (ModeAChange change, HttpContext ctx, CompanyRegistry reg) =>
{
    var target = reg.Find(change.Company) ?? CompanyOf(ctx);
    if (!target.Settings.Configured) return Results.BadRequest(new { errors = new[] { $"No SQL connection configured for company {target.Db}." } });
    target.Settings.ModeAEnabled = change.Enabled;
    return Results.Ok(new { company = target.Db, modeAEnabled = target.Settings.ModeAEnabled });
});

// Settings > Companies: the list this server serves, edited in the admin app and saved to companies.json.
// It is read when the API starts, so a change applies after the API is restarted.
admin.MapGet("/settings/companies", (CompanySettingsFile file, CompanyRegistry reg) =>
    Results.Ok(new
    {
        companies = (file.Load() ?? reg.All.Select(c => new CompanyOptions { Db = c.Db, Name = c.Name, Master = c.IsMaster }).ToList())
            .Select(CompanySettingsFile.Redact),
        fromFile = file.Exists,
    }));

admin.MapPut("/settings/companies", (List<CompanyOptions> incoming, CompanySettingsFile file, CompanyRegistry reg) =>
{
    var errors = CompanySettingsFile.Validate(incoming);
    if (errors.Count > 0) return Results.BadRequest(new { errors });
    var current = file.Load() ?? reg.All.Select(c => new CompanyOptions { Db = c.Db, Name = c.Name, Master = c.IsMaster }).ToList();
    file.Save(incoming, current);
    return Results.Ok(new { saved = true, restartRequired = true });
});

admin.MapGet("/promotions", async (CompanyRegistry reg, PromotionStore store, CancellationToken ct) =>
    reg.Master.Admin is { } a
        ? Results.Ok(await a.ListAsync(ct))
        : Results.Ok(store.All.Select(p => new AdminPromotion
        {
            Code = p.Code, Name = p.Name, Type = p.Type.ToString(), Status = "A", Priority = p.Priority,
            Companies = p.Companies.Length == 0 ? null : string.Join(",", p.Companies),
        })));

admin.MapGet("/promotions/{code}", async (string code, CompanyRegistry reg, CancellationToken ct) =>
    reg.Master.Admin is { } a && await a.GetAsync(code, ct) is { } p ? Results.Ok(p) : Results.NotFound());

admin.MapPost("/promotions", async (AdminPromotion p, CompanyRegistry reg, PromotionStore store, CancellationToken ct) =>
{
    if (reg.Master.Admin is not { } a) return ReadOnly();
    p = CompanyChecks.Normalize(p, reg);
    if (Errors(p, reg) is { Count: > 0 } errors) return Results.BadRequest(new { errors });
    if (await a.GetAsync(p.Code, ct) is not null) return Results.Conflict(new { errors = new[] { $"A promotion {p.Code} already exists." } });
    await a.CreateAsync(p, ct);
    await store.ReloadAsync(ct);
    return Results.Ok(await a.GetAsync(p.Code, ct));
});

admin.MapPut("/promotions/{code}", async (string code, AdminPromotion p, CompanyRegistry reg, PromotionStore store, CancellationToken ct) =>
{
    if (reg.Master.Admin is not { } a) return ReadOnly();
    p = CompanyChecks.Normalize(p with { Code = code }, reg);
    if (Errors(p, reg) is { Count: > 0 } errors) return Results.BadRequest(new { errors });
    if (await a.GetAsync(code, ct) is null) return Results.NotFound();
    await a.UpdateAsync(p, ct);
    await store.ReloadAsync(ct);
    return Results.Ok(await a.GetAsync(code, ct));
});

// Not blocking: what would go wrong or surprise, including, for a promotion ticked for several companies, where the item
// groups, manufacturers, items and customer groups it names are not the same thing in the other companies.
admin.MapPost("/promotions/check", async (AdminPromotion p, CompanyRegistry reg, CancellationToken ct) =>
{
    p = CompanyChecks.Normalize(p, reg);
    var notes = p.Validate().Where(m => m.StartsWith("Note:")).Select(m => m["Note:".Length..].Trim()).ToList();
    notes.AddRange(await CompanyChecks.NotesAsync(p, reg, ct));
    return Results.Ok(new { errors = Errors(p, reg), notes });
});

// Status changes only: promotions are never hard-deleted (NFR-04); "C" cancels.
admin.MapPost("/promotions/{code}/status", async (string code, StatusChange change, CompanyRegistry reg, PromotionStore store, CancellationToken ct) =>
{
    if (reg.Master.Admin is not { } a) return ReadOnly();
    if (change.Status is not ("D" or "P" or "A" or "S" or "E" or "C")) return Results.BadRequest(new { errors = new[] { "Unknown status." } });
    await a.SetStatusAsync(code, change.Status, ct);
    await store.ReloadAsync(ct);
    return Results.Ok(await a.GetAsync(code, ct));
});

// Lookups in a company (default: the master), for the fields that name its items, groups and customers.
admin.MapGet("/lookups/{kind}", async (string kind, string? q, string? company, CompanyRegistry reg, CancellationToken ct) =>
    (reg.Find(company) ?? reg.Master).Admin is { } a ? Results.Ok(await a.LookupAsync(kind, q, ct)) : Results.Ok(Array.Empty<Lookup>()));

// Try a promotion (saved or not) on a sample basket, alone or with the live ones, in a chosen company (default: the master),
// with that company's own item data and prices. Prices default to the item's price list.
admin.MapPost("/simulate", async (AdminSimulation req, HttpContext ctx, CompanyRegistry reg, PromotionStore store, CancellationToken ct) =>
{
    var company = reg.Find(req.Company) ?? reg.Master;
    var view = store.ViewFor(company.Db) ?? store.Current;

    // No promotion: the live ones only (the simulator page). With one: that draft, alone or with the live ones.
    var draft = req.Promotion?.ToEngine();
    var others = draft is null || req.IncludeActive ? view.Promotions.Where(p => p.Code != draft?.Code) : [];
    var md = company.MasterData;
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
            UnitPrice = l.UnitPrice ?? view.Catalog.Find(l.ItemCode)?.UnitPrice ?? 0,
        }).ToList(),
    };
    var promotions = draft is null ? others.ToList() : others.Append(draft).ToList();
    return Results.Ok(view.Engine.Evaluate(basket, promotions));
});

app.Run();

/// <summary>The company a request is for: the one named in X-Company-Db, or the master when there is none.</summary>
static CompanyContext CompanyOf(HttpContext ctx)
{
    var registry = ctx.RequestServices.GetRequiredService<CompanyRegistry>();
    return registry.Find(CompanyGuard.Asked(ctx.Request.Headers[CompanyGuard.Header].ToString())) ?? registry.Master;
}

static IResult ReadOnly() => Results.BadRequest(new { errors = new[] { "Promotions come from files; set Promotions:Source to ServiceLayer to edit them." } });

static List<string> Errors(AdminPromotion p, CompanyRegistry reg)
{
    var errors = p.Validate().Where(m => !m.StartsWith("Note:")).ToList();
    foreach (var name in CompanyChecks.Unknown(p, reg))
        errors.Add($"Unknown company '{name}'. This server serves: {string.Join(", ", reg.All.Select(c => c.Db))}.");
    return errors;
}

public sealed record SimulationRequest(Basket Basket, IReadOnlyList<Promotion> Promotions, bool IncludeActive = true);

public sealed record StatusChange(string Status);

public sealed record ModeAChange(bool Enabled, string? Company = null);

public sealed record AdminSimulationLine(string ItemCode, decimal Quantity, decimal? UnitPrice);

public sealed record AdminSimulation(AdminPromotion? Promotion, IReadOnlyList<AdminSimulationLine> Lines, string? CardCode,
    string[]? Coupons = null, DateTime? Timestamp = null, bool IncludeActive = false, int AmountDecimals = 2, string? Company = null);

public partial class Program;
