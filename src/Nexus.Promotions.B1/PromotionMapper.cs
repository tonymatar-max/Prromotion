using System.Globalization;
using System.Text.Json.Nodes;
using Nexus.Promotions.Engine;

namespace Nexus.Promotions.B1;

/// <summary>
/// Maps one @APE_PROMO UDO entity (as the Service Layer returns it, with its child collections) to the engine's
/// Promotion. The field list is Setup/Schema.cs. Empty numbers come back as 0, so 0 means "no limit".
/// </summary>
public static class PromotionMapper
{
    public static Promotion Map(JsonNode p)
    {
        var scopes = Rows(p, "APE_PROMO_SCPCollection");
        var audience = Rows(p, "APE_PROMO_AUDCollection");
        var budget = Dec(p, "U_Budget");

        return new Promotion
        {
            Code = Str(p, "Code") ?? throw new FormatException("Promotion without Code"),
            Version = Int(p, "U_Version") ?? 1,
            Name = Str(p, "Name") ?? "",
            NameAr = Str(p, "U_NameAR"),
            Type = Enum.Parse<PromotionType>(Str(p, "U_Type") ?? nameof(PromotionType.ItemDiscount), ignoreCase: true),
            Priority = Int(p, "U_Priority") ?? 100,
            Stacking = Str(p, "U_Stacking") == "E" ? StackClass.Exclusive : StackClass.Stackable,
            Active = Str(p, "U_Status") == "A",
            ValidFrom = DateAndTime(Str(p, "U_ValidFrom"), Str(p, "U_ValidFromT")),
            ValidTo = DateAndTime(Str(p, "U_ValidTo"), Str(p, "U_ValidToT")),
            Weekdays = Weekdays(Str(p, "U_Weekdays")),
            TimeFrom = Time(Str(p, "U_TimeFrom")),
            TimeTo = Time(Str(p, "U_TimeTo")),
            CouponCode = Str(p, "U_Coupon"),
            DocumentTypes = (Str(p, "U_Documents") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            Companies = (Str(p, "U_Companies") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            Scope = Scope(scopes.Where(r => Str(r, "U_Role") != "R")),
            RewardScope = scopes.Any(r => Str(r, "U_Role") == "R") ? Scope(scopes.Where(r => Str(r, "U_Role") == "R")) : null,
            BuyQuantity = Dec(p, "U_BuyQty"),
            GetQuantity = Dec(p, "U_GetQty"),
            RewardItemCode = Str(p, "U_RewardItem"),
            FreeMode = Str(p, "U_FreeMode") == "N" ? FreeItemMode.AddNew : FreeItemMode.FromBasket,
            RewardUnits = Str(p, "U_RewardUnits") == "D" ? UnitSelection.Dearest : UnitSelection.Cheapest,
            RewardKind = Str(p, "U_RewardKind") switch
            {
                "A" => RewardKind.AmountOff,
                "F" => RewardKind.FixedPrice,
                "X" => RewardKind.Free,
                _ => RewardKind.PercentOff,
            },
            RewardValue = Dec(p, "U_RewardValue"),
            Tiers = Rows(p, "APE_PROMO_TIERCollection").Select(r => new Tier(Dec(r, "U_From"), Dec(r, "U_Value"))).ToArray(),
            Audience = new Audience
            {
                Channels = Values(audience, "CH"),
                CardCodes = Values(audience, "CARD"),
                CustomerGroups = Values(audience, "GRP"),
                Branches = Values(audience, "BR"),
                PriceLists = Values(audience, "PL"),
            },
            MaxApplicationsPerDocument = Int(p, "U_MaxApps") is > 0 and var max ? max : null,
            MaxDiscountPerDocument = Dec(p, "U_MaxDisc") is > 0 and var cap ? cap : null,
            BudgetRemaining = budget > 0 ? Math.Max(0, budget - Dec(p, "U_BudgetUsed")) : null,
            AllowBelowMinPrice = Str(p, "U_AllowBelow") == "Y",
        };
    }

    static ItemScope Scope(IEnumerable<JsonNode> rows)
    {
        var list = rows.ToList();
        string[] Of(string type, bool exclude) => list
            .Where(r => Str(r, "U_ScopeType") == type && (Str(r, "U_Exclude") == "Y") == exclude)
            .Select(r => Str(r, "U_Value")).OfType<string>().ToArray();
        return new ItemScope
        {
            ItemCodes = Of("I", false),
            ItemGroups = Of("G", false),
            Manufacturers = Of("M", false),
            Properties = Of("P", false).Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray(),
            ExcludeItemCodes = Of("I", true),
        };
    }

    static string[] Values(List<JsonNode> rows, string dimension) =>
        rows.Where(r => Str(r, "U_Dimension") == dimension).Select(r => Str(r, "U_Value")).OfType<string>().ToArray();

    static List<JsonNode> Rows(JsonNode p, string collection) =>
        p[collection]?.AsArray().OfType<JsonNode>().ToList() ?? [];

    static string? Str(JsonNode n, string name)
    {
        var v = n[name];
        if (v is null) return null;
        var s = v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    // Parsed from the JSON text, so a value is read the same whether it came from the wire or was built in code.
    static decimal Dec(JsonNode n, string name) =>
        n[name] is { } v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? decimal.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture) : 0m;

    static int? Int(JsonNode n, string name) =>
        n[name] is { } v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number ? (int)Dec(n, name) : null;

    static DateTime? DateAndTime(string? date, string? time)
    {
        if (date is null) return null;
        var d = DateTime.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).Date;
        return Time(time) is { } t ? d + t.ToTimeSpan() : d;
    }

    /// <summary>B1 time fields come back as "14:30:00", or as a number such as 1430.</summary>
    internal static TimeOnly? Time(string? value)
    {
        if (value is null) return null;
        if (TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out var t)) return t;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hhmm) && hhmm is >= 0 and < 2400)
            return new TimeOnly(hhmm / 100, hhmm % 100);
        return null;
    }

    /// <summary>"135" = Monday, Wednesday, Friday (1 = Monday … 7 = Sunday).</summary>
    internal static DayOfWeek[] Weekdays(string? value) =>
        value is null ? [] : value.Where(c => c is >= '1' and <= '7').Distinct().Select(c => (DayOfWeek)((c - '0') % 7)).ToArray();
}
