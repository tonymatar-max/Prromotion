using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Nexus.Promotions.Engine;

namespace Nexus.Promotions.B1;

public sealed record CustomerInfo(string CardCode, string? GroupCode, string? PriceList);

/// <summary>
/// Items and customers read from B1 on demand and cached (10 minutes). The engine's catalogue lookups are
/// synchronous, so callers prefetch a document's item codes first; unknown codes simply return null.
/// </summary>
public sealed class B1MasterData(ServiceLayer sl, int priceList) : IItemCatalog
{
    static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    const int Batch = 20;

    readonly ConcurrentDictionary<string, (ItemInfo? Item, DateTime At)> _items = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, (CustomerInfo? Customer, DateTime At)> _customers = new(StringComparer.OrdinalIgnoreCase);

    static readonly string ItemSelect = "ItemCode,ItemsGroupCode,Manufacturer,U_APE_MinPrice,ItemPrices,"
        + string.Join(",", Enumerable.Range(1, 64).Select(i => "Properties" + i));

    public ItemInfo? Find(string itemCode) => _items.TryGetValue(itemCode, out var e) ? e.Item : null;

    public async Task PrefetchItemsAsync(IEnumerable<string> itemCodes, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var missing = itemCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => !_items.TryGetValue(c, out var e) || now - e.At > Ttl)
            .ToList();

        foreach (var chunk in missing.Chunk(Batch))
        {
            var filter = string.Join(" or ", chunk.Select(c => $"ItemCode eq '{c.Replace("'", "''")}'"));
            var rows = await sl.GetAllAsync($"Items?$select={ItemSelect}&$filter={Uri.EscapeDataString(filter)}", ct);
            var found = rows.Select(MapItem).ToDictionary(i => i.ItemCode, StringComparer.OrdinalIgnoreCase);
            foreach (var code in chunk)
                _items[code] = (found.GetValueOrDefault(code), now);
        }
    }

    public async Task<CustomerInfo?> GetCustomerAsync(string? cardCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cardCode)) return null;
        if (_customers.TryGetValue(cardCode, out var e) && DateTime.UtcNow - e.At < Ttl) return e.Customer;

        var bp = await sl.GetAsync($"BusinessPartners('{cardCode.Replace("'", "''")}')?$select=CardCode,GroupCode,PriceListNum", ct);
        var customer = bp is null ? null : new CustomerInfo(cardCode, Text(bp["GroupCode"]), Text(bp["PriceListNum"]));
        _customers[cardCode] = (customer, DateTime.UtcNow);
        return customer;
    }

    ItemInfo MapItem(JsonNode n)
    {
        var properties = Enumerable.Range(1, 64).Where(i => n["Properties" + i]?.GetValue<string>() == "tYES").ToArray();
        var price = n["ItemPrices"]?.AsArray()
            .FirstOrDefault(p => p?["PriceList"]?.GetValue<int>() == priceList)?["Price"]?.GetValue<decimal>() ?? 0m;
        var min = n["U_APE_MinPrice"]?.GetValue<decimal>();
        return new ItemInfo
        {
            ItemCode = n["ItemCode"]!.GetValue<string>(),
            ItemGroup = Text(n["ItemsGroupCode"]),
            Manufacturer = Text(n["Manufacturer"]),
            Properties = properties,
            UnitPrice = price,
            MinUnitPrice = min > 0 ? min : null,
        };
    }

    static string? Text(JsonNode? n) => n?.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.Number => n.GetValue<decimal>().ToString(CultureInfo.InvariantCulture),
        System.Text.Json.JsonValueKind.String => n.GetValue<string>(),
        _ => null,
    };
}
