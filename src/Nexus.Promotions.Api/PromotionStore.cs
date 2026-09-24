using System.Text.Json;
using Microsoft.Extensions.Options;
using Nexus.Promotions.B1;
using Nexus.Promotions.Engine;

namespace Nexus.Promotions.Api;

public sealed class PromotionsOptions
{
    /// <summary>"File" (JSON samples, for development and demos) or "ServiceLayer" (the @APE_PROMO UDO in B1).</summary>
    public string Source { get; set; } = "File";
    public string PromotionsFile { get; set; } = "samples/promotions.json";
    /// <summary>Optional JSON file with item attributes and prices (reward items, POS lines sent without attributes).</summary>
    public string? ItemsFile { get; set; } = "samples/items.json";
    /// <summary>When non-empty, every /api call needs one of these in the X-Api-Key header.</summary>
    public string[] ApiKeys { get; set; } = [];
    public int RefreshSeconds { get; set; } = 60;
    public ConflictMode ConflictMode { get; set; } = ConflictMode.BestDeal;
    public StackingMode StackingMode { get; set; } = StackingMode.Compound;
    public string? HashKey { get; set; }

    public bool UsesServiceLayer => string.Equals(Source, "ServiceLayer", StringComparison.OrdinalIgnoreCase);
}

public sealed class FilePromotionSource(IOptions<PromotionsOptions> options, IHostEnvironment env) : IPromotionSource
{
    public async Task<PromotionSet> LoadAsync(CancellationToken ct)
    {
        var o = options.Value;
        var promotions = await Read<List<Promotion>>(o.PromotionsFile, ct) ?? [];
        var items = string.IsNullOrEmpty(o.ItemsFile) ? [] : await Read<List<ItemInfo>>(o.ItemsFile, ct) ?? [];
        return new PromotionSet(promotions, new InMemoryItemCatalog(items));
    }

    async Task<T?> Read<T>(string path, CancellationToken ct)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);
        if (!File.Exists(full)) return default;
        await using var stream = File.OpenRead(full);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json.Options, ct);
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions o)
    {
        o.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return o;
    }
}
