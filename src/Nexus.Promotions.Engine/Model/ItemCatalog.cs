namespace Nexus.Promotions.Engine;

/// <summary>Item master data the engine needs: attributes for scope matching and prices for added reward items.</summary>
public interface IItemCatalog
{
    ItemInfo? Find(string itemCode);
}

public sealed record ItemInfo
{
    public required string ItemCode { get; init; }
    public string? ItemGroup { get; init; }
    public string? Manufacturer { get; init; }
    public int[] Properties { get; init; } = [];
    public decimal UnitPrice { get; init; }
    /// <summary>U_APE_MinPrice on the item: the floor promotions may not go below.</summary>
    public decimal? MinUnitPrice { get; init; }
}

public sealed class InMemoryItemCatalog : IItemCatalog
{
    readonly Dictionary<string, ItemInfo> _items;

    public InMemoryItemCatalog(IEnumerable<ItemInfo> items) =>
        _items = items.ToDictionary(i => i.ItemCode, StringComparer.OrdinalIgnoreCase);

    public static InMemoryItemCatalog Empty { get; } = new([]);

    public ItemInfo? Find(string itemCode) => _items.GetValueOrDefault(itemCode);
}
