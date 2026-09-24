using Nexus.Promotions.Engine;

namespace Nexus.Promotions.B1;

/// <summary>Where active promotions and item data come from.</summary>
public interface IPromotionSource
{
    Task<PromotionSet> LoadAsync(CancellationToken ct);
}

/// <param name="Catalog">Item data for the engine: a fixed list (file source) or B1 on demand (<see cref="B1MasterData"/>).</param>
public sealed record PromotionSet(IReadOnlyList<Promotion> Promotions, IItemCatalog Catalog);

/// <summary>Active promotions from the @APE_PROMO UDO (U_Status = A), with reward items prefetched.</summary>
public sealed class ServiceLayerPromotionSource(ServiceLayer sl, B1MasterData masterData) : IPromotionSource
{
    public async Task<PromotionSet> LoadAsync(CancellationToken ct)
    {
        var rows = await sl.GetAllAsync("APE_PROMO?$filter=U_Status eq 'A'", ct);
        var promotions = new List<Promotion>();
        foreach (var row in rows)
        {
            try { promotions.Add(PromotionMapper.Map(row)); }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                // One badly configured promotion must not stop the others.
                Console.Error.WriteLine($"APE: skipped promotion {row["Code"]}: {ex.Message}");
            }
        }

        await masterData.PrefetchItemsAsync(promotions.Select(p => p.RewardItemCode).OfType<string>(), ct);
        return new PromotionSet(promotions, masterData);
    }
}
