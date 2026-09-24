using System.Globalization;
using System.Text.Json.Nodes;
using Nexus.Promotions.Engine;

namespace Nexus.Promotions.B1;

/// <summary>
/// A promotion as the admin app edits it: one field per @APE_PROMO UDO field (Setup/Schema.cs), any status.
/// The engine never sees this type; it goes through <see cref="ToUdo"/> and <see cref="PromotionMapper"/>, so a
/// promotion simulates exactly as it will run.
/// </summary>
public sealed record AdminPromotion
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? NameAr { get; init; }
    public int Version { get; init; } = 1;
    public string Type { get; init; } = nameof(PromotionType.ItemDiscount);
    /// <summary>D draft, P pending approval, A active, S paused, E expired, C cancelled.</summary>
    public string Status { get; init; } = "D";
    public int Priority { get; init; } = 100;
    /// <summary>S stackable, E exclusive.</summary>
    public string Stacking { get; init; } = "S";
    public string? ValidFrom { get; init; }       // yyyy-MM-dd
    public string? ValidFromTime { get; init; }   // HH:mm
    public string? ValidTo { get; init; }
    public string? ValidToTime { get; init; }
    public string? Weekdays { get; init; }        // "12345" = Mon–Fri
    public string? TimeFrom { get; init; }
    public string? TimeTo { get; init; }
    public string? Coupon { get; init; }
    /// <summary>Comma list of document screens this auto-applies to (OQUT,ORDR,ODLN,OINV); blank = every screen.</summary>
    public string? Documents { get; init; }
    /// <summary>Comma list of company databases this applies to when one server serves several; blank = the master company only.</summary>
    public string? Companies { get; init; }
    public decimal BuyQty { get; init; }
    public decimal GetQty { get; init; }
    public string? RewardItem { get; init; }
    /// <summary>B from basket, N add new line.</summary>
    public string FreeMode { get; init; } = "B";
    /// <summary>C cheapest, D dearest.</summary>
    public string RewardUnits { get; init; } = "C";
    /// <summary>P % off, A amount off, F fixed price, X free.</summary>
    public string RewardKind { get; init; } = "P";
    public decimal RewardValue { get; init; }
    public int? MaxApps { get; init; }
    public decimal MaxDisc { get; init; }
    public decimal Budget { get; init; }
    public decimal BudgetUsed { get; init; }
    public bool AllowBelow { get; init; }
    public string? Campaign { get; init; }
    public List<AdminScope> Scopes { get; init; } = [];
    public List<AdminTier> Tiers { get; init; } = [];
    public List<AdminAudience> Audience { get; init; } = [];
    public string? UpdatedAt { get; init; }

    /// <summary>The UDO body for POST/PATCH (child collections included).</summary>
    public Dictionary<string, object?> ToUdo(bool includeCode = true)
    {
        var body = new Dictionary<string, object?>
        {
            ["Name"] = Name,
            ["U_NameAR"] = Empty(NameAr),
            ["U_Version"] = Version,
            ["U_Type"] = Type,
            ["U_Status"] = Status,
            ["U_Priority"] = Priority,
            ["U_Stacking"] = Stacking,
            ["U_ValidFrom"] = Empty(ValidFrom),
            ["U_ValidFromT"] = Empty(ValidFromTime),
            ["U_ValidTo"] = Empty(ValidTo),
            ["U_ValidToT"] = Empty(ValidToTime),
            ["U_Weekdays"] = Empty(Weekdays),
            ["U_TimeFrom"] = Empty(TimeFrom),
            ["U_TimeTo"] = Empty(TimeTo),
            ["U_Coupon"] = Empty(Coupon),
            ["U_Documents"] = Empty(Documents),
            ["U_Companies"] = Empty(Companies),
            ["U_BuyQty"] = BuyQty,
            ["U_GetQty"] = GetQty,
            ["U_RewardItem"] = Empty(RewardItem),
            ["U_FreeMode"] = FreeMode,
            ["U_RewardUnits"] = RewardUnits,
            ["U_RewardKind"] = RewardKind,
            ["U_RewardValue"] = RewardValue,
            ["U_MaxApps"] = MaxApps,
            ["U_MaxDisc"] = MaxDisc,
            ["U_Budget"] = Budget,
            ["U_AllowBelow"] = AllowBelow ? "Y" : "N",
            ["U_Campaign"] = Empty(Campaign),
            ["APE_PROMO_SCPCollection"] = Scopes.Select(s => new Dictionary<string, object?>
            {
                ["U_Role"] = s.Role, ["U_ScopeType"] = s.ScopeType, ["U_Value"] = s.Value, ["U_Exclude"] = s.Exclude ? "Y" : "N",
            }).ToArray(),
            ["APE_PROMO_TIERCollection"] = Tiers.Select(t => new Dictionary<string, object?>
            {
                ["U_From"] = t.From, ["U_Value"] = t.Value,
            }).ToArray(),
            ["APE_PROMO_AUDCollection"] = Audience.Select(a => new Dictionary<string, object?>
            {
                ["U_Dimension"] = a.Dimension, ["U_Value"] = a.Value,
            }).ToArray(),
        };
        if (includeCode) body["Code"] = Code;
        return body;
    }

    public static AdminPromotion FromUdo(JsonNode n) => new()
    {
        Code = Str(n, "Code") ?? "",
        Name = Str(n, "Name") ?? "",
        NameAr = Str(n, "U_NameAR"),
        Version = (int)Dec(n, "U_Version") is > 0 and var v ? v : 1,
        Type = Str(n, "U_Type") ?? nameof(PromotionType.ItemDiscount),
        Status = Str(n, "U_Status") ?? "D",
        Priority = (int)Dec(n, "U_Priority"),
        Stacking = Str(n, "U_Stacking") ?? "S",
        ValidFrom = Date(Str(n, "U_ValidFrom")),
        ValidFromTime = Time(Str(n, "U_ValidFromT")),
        ValidTo = Date(Str(n, "U_ValidTo")),
        ValidToTime = Time(Str(n, "U_ValidToT")),
        Weekdays = Str(n, "U_Weekdays"),
        TimeFrom = Time(Str(n, "U_TimeFrom")),
        TimeTo = Time(Str(n, "U_TimeTo")),
        Coupon = Str(n, "U_Coupon"),
        Documents = Str(n, "U_Documents"),
        Companies = Str(n, "U_Companies"),
        BuyQty = Dec(n, "U_BuyQty"),
        GetQty = Dec(n, "U_GetQty"),
        RewardItem = Str(n, "U_RewardItem"),
        FreeMode = Str(n, "U_FreeMode") ?? "B",
        RewardUnits = Str(n, "U_RewardUnits") ?? "C",
        RewardKind = Str(n, "U_RewardKind") ?? "P",
        RewardValue = Dec(n, "U_RewardValue"),
        MaxApps = (int)Dec(n, "U_MaxApps") is > 0 and var m ? m : null,
        MaxDisc = Dec(n, "U_MaxDisc"),
        Budget = Dec(n, "U_Budget"),
        BudgetUsed = Dec(n, "U_BudgetUsed"),
        AllowBelow = Str(n, "U_AllowBelow") == "Y",
        Campaign = Str(n, "U_Campaign"),
        Scopes = Rows(n, "APE_PROMO_SCPCollection").Select(r => new AdminScope(
            Str(r, "U_Role") ?? "T", Str(r, "U_ScopeType") ?? "I", Str(r, "U_Value") ?? "", Str(r, "U_Exclude") == "Y")).ToList(),
        Tiers = Rows(n, "APE_PROMO_TIERCollection").Select(r => new AdminTier(Dec(r, "U_From"), Dec(r, "U_Value"))).ToList(),
        Audience = Rows(n, "APE_PROMO_AUDCollection").Select(r => new AdminAudience(Str(r, "U_Dimension") ?? "GRP", Str(r, "U_Value") ?? "")).ToList(),
        UpdatedAt = Date(Str(n, "UpdateDate")) is { } d ? $"{d} {Str(n, "UpdateTime")}" : null,
    };

    /// <summary>The engine's view, through the same mapper the live rule cache uses.</summary>
    public Promotion ToEngine() => PromotionMapper.Map(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(ToUdo()))!) with
    {
        Active = true, // simulate a draft as if it were live
    };

    /// <summary>Problems that would make the promotion do nothing or something unexpected. Messages starting "Note:" are warnings, not errors.</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Code)) problems.Add("Code is required.");
        else if (Code.Length > 50) problems.Add("Code is at most 50 characters.");
        if (string.IsNullOrWhiteSpace(Name)) problems.Add("Name is required.");
        if (!Enum.TryParse<PromotionType>(Type, out var type)) { problems.Add($"Unknown type {Type}."); return problems; }

        var triggers = Scopes.Count(s => s.Role == "T" && !s.Exclude);
        switch (type)
        {
            case PromotionType.ItemDiscount or PromotionType.FixedPrice:
                if (RewardValue <= 0) problems.Add("Set the reward value.");
                break;
            case PromotionType.QuantityTier or PromotionType.BasketThreshold:
                if (Tiers.Count == 0) problems.Add("Add at least one tier.");
                if (type == PromotionType.BasketThreshold && RewardKind is not ("P" or "A")) problems.Add("A spend threshold gives % off or an amount off.");
                break;
            case PromotionType.BuyXGetXFree:
                if (BuyQty <= 0 || GetQty <= 0) problems.Add("Set how many to buy and how many they get.");
                break;
            case PromotionType.BuyXGetY:
                if (BuyQty <= 0 || GetQty <= 0) problems.Add("Set how many to buy and how many they get.");
                if (FreeMode == "N" && string.IsNullOrWhiteSpace(RewardItem)) problems.Add("Choose the reward item to add.");
                if (FreeMode == "B" && !Scopes.Any(s => s.Role == "R")) problems.Add("Add the reward items (item scope with role Reward).");
                break;
            case PromotionType.MixAndMatch:
                if (BuyQty <= 0) problems.Add("Set the set size.");
                if (triggers == 0) problems.Add("Choose the items in the pool; a mix and match on everything is rarely intended.");
                break;
        }
        if (type != PromotionType.BasketThreshold && triggers == 0 && Scopes.All(s => s.Role != "T"))
            problems.Add("Note: no items chosen, so the promotion applies to every item.");
        if (ValidFrom is not null && ValidTo is not null && string.CompareOrdinal(ValidFrom, ValidTo) > 0)
            problems.Add("Valid to is before valid from.");
        return problems;
    }

    static object? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static string? Date(string? s) =>
        s is null ? null : DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d.ToString("yyyy-MM-dd") : null;

    static string? Time(string? s) => PromotionMapper.Time(s)?.ToString("HH:mm", CultureInfo.InvariantCulture);

    static List<JsonNode> Rows(JsonNode n, string c) => n[c]?.AsArray().OfType<JsonNode>().ToList() ?? [];

    static string? Str(JsonNode n, string name)
    {
        var v = n[name];
        if (v is null) return null;
        var s = v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    static decimal Dec(JsonNode n, string name) =>
        n[name] is { } v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? decimal.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture) : 0m;
}

/// <param name="Role">T trigger/discounted, R reward.</param>
/// <param name="ScopeType">I item, G item group, M manufacturer, P item property.</param>
public sealed record AdminScope(string Role, string ScopeType, string Value, bool Exclude);

public sealed record AdminTier(decimal From, decimal Value);

/// <param name="Dimension">CH channel, CARD customer, GRP customer group, BR branch, PL price list.</param>
public sealed record AdminAudience(string Dimension, string Value);

/// <summary>Reads and writes @APE_PROMO for the admin app.</summary>
public sealed class PromotionAdmin(ServiceLayer sl)
{
    // The demo Service Layer install this runs against only handles a few new connections at a time; opening
    // several at once (the editor page loads 5 lookups in parallel) can queue for several seconds each even
    // though B1 itself answers instantly once a request lands. Item groups, manufacturers, customer groups and
    // price lists are rarely edited, so caching them turns most page loads into zero B1 calls instead of four.
    static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (List<Lookup> Rows, DateTime At)> _cache = new();

    public async Task<List<AdminPromotion>> ListAsync(CancellationToken ct) =>
        (await sl.GetAllAsync("APE_PROMO?$orderby=Code", ct)).Select(AdminPromotion.FromUdo).ToList();

    public async Task<AdminPromotion?> GetAsync(string code, CancellationToken ct) =>
        await sl.GetAsync($"APE_PROMO('{Esc(code)}')", ct) is { } n ? AdminPromotion.FromUdo(n) : null;

    public Task CreateAsync(AdminPromotion p, CancellationToken ct) => sl.PostAsync("APE_PROMO", p.ToUdo(), ct);

    /// <summary>Replaces the child rows too (B1S-ReplaceCollectionsOnPatch), so removed scope/tier rows really go.</summary>
    public Task UpdateAsync(AdminPromotion p, CancellationToken ct) =>
        sl.PatchAsync($"APE_PROMO('{Esc(p.Code)}')", p.ToUdo(includeCode: false), replaceCollections: true, ct);

    public Task SetStatusAsync(string code, string status, CancellationToken ct) =>
        sl.PatchAsync($"APE_PROMO('{Esc(code)}')", new Dictionary<string, object?> { ["U_Status"] = status }, ct: ct);

    static readonly HashSet<string> Cacheable = ["itemgroups", "manufacturers", "customergroups", "pricelists", "items", "customers"];

    public async Task<List<Lookup>> LookupAsync(string kind, string? search, CancellationToken ct)
    {
        // A typed search always goes to B1 fresh; the plain, no-search list behind each field (used to fill
        // the dropdown before the user types) is the same query every time and is cached like the rest.
        if (string.IsNullOrWhiteSpace(search) && Cacheable.Contains(kind)
            && _cache.TryGetValue(kind, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
            return cached.Rows;

        var q = string.IsNullOrWhiteSpace(search) ? null : Esc(search.Trim());
        (string path, string key, string label) = kind switch
        {
            "items" => ("Items?$select=ItemCode,ItemName&$top=30&$filter=" + Uri.EscapeDataString(
                q is null ? "SalesItem eq 'tYES'" : $"SalesItem eq 'tYES' and (contains(ItemCode,'{q}') or contains(ItemName,'{q}'))"),
                "ItemCode", "ItemName"),
            "itemgroups" => ("ItemGroups?$select=Number,GroupName", "Number", "GroupName"),
            "manufacturers" => ("Manufacturers?$select=Code,ManufacturerName", "Code", "ManufacturerName"),
            "customergroups" => ("BusinessPartnerGroups?$select=Code,Name&$filter=" + Uri.EscapeDataString("Type eq 'bbpgt_CustomerGroup'"), "Code", "Name"),
            "customers" => ("BusinessPartners?$select=CardCode,CardName&$top=30&$filter=" + Uri.EscapeDataString(
                q is null ? "CardType eq 'cCustomer'" : $"CardType eq 'cCustomer' and (contains(CardCode,'{q}') or contains(CardName,'{q}'))"),
                "CardCode", "CardName"),
            "pricelists" => ("PriceLists?$select=PriceListNo,PriceListName", "PriceListNo", "PriceListName"),
            _ => throw new ArgumentException("Unknown lookup " + kind),
        };
        var rows = kind is "items" or "customers" ? (await sl.GetAsync(path, ct))?["value"]?.AsArray().OfType<JsonNode>().ToList() ?? []
                                                  : await sl.GetAllAsync(path, ct);
        var result = rows.Select(r => new Lookup(Text(r[key]), Text(r[label]))).Where(l => l.Code.Length > 0).ToList();
        if (string.IsNullOrWhiteSpace(search) && Cacheable.Contains(kind)) _cache[kind] = (result, DateTime.UtcNow);
        return result;
    }

    static string Text(JsonNode? n) => n is null ? "" : n.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString();

    static string Esc(string s) => s.Replace("'", "''");
}

public sealed record Lookup(string Code, string Name);
