using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Promotions.Engine;

namespace Nexus.Promotions.B1;

/// <summary>
/// In-memory rule cache (PRD 6: no evaluation hits the database). Evaluations read an immutable snapshot;
/// a reload swaps it atomically. Shared by the API and the worker.
/// </summary>
public sealed class PromotionStore(IPromotionSource source, EngineOptions engineOptions, ILogger<PromotionStore> logger)
{
    public sealed record Snapshot(IReadOnlyList<Promotion> Promotions, PromotionEngine Engine, IItemCatalog Catalog, DateTime LoadedAt);

    volatile Snapshot? _current;

    public Snapshot Current => _current ?? throw new InvalidOperationException("Promotions are not loaded yet.");

    public async Task<Snapshot> ReloadAsync(CancellationToken ct = default)
    {
        var set = await source.LoadAsync(ct);
        var snapshot = new Snapshot(set.Promotions, new PromotionEngine(engineOptions, set.Catalog), set.Catalog, DateTime.UtcNow);
        _current = snapshot;
        logger.LogInformation("Loaded {Count} promotion(s)", set.Promotions.Count);
        return snapshot;
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
