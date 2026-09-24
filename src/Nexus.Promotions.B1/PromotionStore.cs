using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Promotions.Engine;

namespace Nexus.Promotions.B1;

/// <summary>
/// In-memory rule cache (PRD 6: no evaluation hits the database). Evaluations read an immutable snapshot;
/// a reload swaps it atomically. Shared by the API and the worker.
///
/// Promotions are read once (from the master company) and each company gets its own view: only the promotions
/// ticked for it (<see cref="CompanyScope"/>), evaluated by an engine that uses THAT company's item, customer and price
/// data and hash key. Without a <see cref="CompanyRegistry"/> there is one view, the whole list, as before.
/// </summary>
public sealed class PromotionStore(IPromotionSource source, EngineOptions engineOptions, ILogger<PromotionStore> logger,
    CompanyRegistry? companies = null)
{
    public sealed record Snapshot(IReadOnlyList<Promotion> Promotions, PromotionEngine Engine, IItemCatalog Catalog, DateTime LoadedAt);

    volatile Dictionary<string, Snapshot>? _views;
    volatile Snapshot? _master;

    /// <summary>The master company's view (the only view when one company is served).</summary>
    public Snapshot Current => _master ?? throw new InvalidOperationException("Promotions are not loaded yet.");

    /// <summary>A company's view, or null for a company this server does not serve. A missing company means the master's.</summary>
    public Snapshot? ViewFor(string? company)
    {
        var views = _views ?? throw new InvalidOperationException("Promotions are not loaded yet.");
        if (string.IsNullOrWhiteSpace(company)) return _master;
        return views.GetValueOrDefault(company.Trim());
    }

    /// <summary>Every active promotion, whichever company it is ticked for.</summary>
    public IReadOnlyList<Promotion> All { get; private set; } = [];

    public async Task<Snapshot> ReloadAsync(CancellationToken ct = default)
    {
        var set = await source.LoadAsync(ct);
        var now = DateTime.UtcNow;
        var views = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);

        if (companies is null)
        {
            var only = new Snapshot(set.Promotions, new PromotionEngine(engineOptions, set.Catalog), set.Catalog, now);
            _master = only;
            _views = views;
            All = set.Promotions;
            logger.LogInformation("Loaded {Count} promotion(s)", set.Promotions.Count);
            return only;
        }

        foreach (var company in companies.All)
        {
            var mine = CompanyScope.Select(set.Promotions, company.Db, companies.Master.Db);
            IItemCatalog catalog = company.Catalog ?? (IItemCatalog?)company.MasterData ?? set.Catalog;

            // Reward items to add (P05) need their price from this company's own item master. One company being
            // unreachable must not stop the others, and the engine copes without the prefetch.
            if (company.MasterData is { } md)
            {
                try { await md.PrefetchItemsAsync(mine.Select(p => p.RewardItemCode).OfType<string>(), ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not prefetch reward items for company {Company}", company.Db);
                }
            }

            var options = engineOptions with { HashKey = company.HashKey ?? engineOptions.HashKey };
            views[company.Db] = new Snapshot(mine, new PromotionEngine(options, catalog), catalog, now);
            logger.LogInformation("Company {Company}: {Count} of {Total} promotion(s)", company.Db, mine.Count, set.Promotions.Count);
        }

        _master = views[companies.Master.Db];
        _views = views;
        All = set.Promotions;
        return _master;
    }
}

/// <summary>Reloads the rules on a timer; a failed reload keeps the previous snapshot.</summary>
public sealed class PromotionRefreshService(PromotionStore store, TimeSpan interval, ILogger<PromotionRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await store.ReloadAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Promotion refresh failed; keeping the previous snapshot");
            }
        }
    }
}
