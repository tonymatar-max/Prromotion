namespace Nexus.Promotions.Engine.Evaluation;

internal static class Rewards
{
    /// <summary>Unit price after a reward.</summary>
    public static decimal PriceAfter(RewardKind kind, decimal value, decimal price) => kind switch
    {
        RewardKind.PercentOff => price * (1 - value / 100m),
        RewardKind.AmountOff => Math.Max(0, price - value),
        RewardKind.FixedPrice => Math.Min(price, value),
        RewardKind.Free => 0,
        _ => price,
    };

    /// <summary>Discount a reward gives on one unit.</summary>
    public static decimal UnitBenefit(RewardKind kind, decimal value, decimal price) =>
        price - PriceAfter(kind, value, price);

    public static string Describe(RewardKind kind, decimal value) => kind switch
    {
        RewardKind.PercentOff => $"{value:0.##}% off",
        RewardKind.AmountOff => $"{value:0.###} off",
        RewardKind.FixedPrice => $"price {value:0.###}",
        RewardKind.Free => "free",
        _ => kind.ToString(),
    };
}

internal static class EligibilityCheck
{
    /// <summary>Null when the promotion may run on this basket, otherwise the reason it cannot.</summary>
    public static string? Check(Promotion p, Basket b)
    {
        if (!p.Active) return "Promotion is not active";

        var t = b.Timestamp;
        if (p.ValidFrom is { } from && t < from) return $"Starts {from:yyyy-MM-dd HH:mm}";
        if (p.ValidTo is { } to && t > EndOf(to)) return $"Ended {to:yyyy-MM-dd HH:mm}";
        if (p.Weekdays.Length > 0 && Array.IndexOf(p.Weekdays, t.DayOfWeek) < 0) return $"Not valid on {t.DayOfWeek}";
        if (p.TimeFrom is { } tf && p.TimeTo is { } tt)
        {
            var now = TimeOnly.FromDateTime(t);
            var inWindow = tf <= tt ? now >= tf && now <= tt : now >= tf || now <= tt;
            if (!inWindow) return $"Valid only {tf:HH\\:mm}-{tt:HH\\:mm}";
        }

        if (p.DocumentTypes.Length > 0 && !Array.Exists(p.DocumentTypes, d => string.Equals(d, b.DocumentType, StringComparison.OrdinalIgnoreCase)))
            return $"Not enabled for {DocumentName(b.DocumentType)}";

        var a = p.Audience;
        if (!Allowed(a.Channels, b.Channel)) return $"Not valid for channel {b.Channel}";
        if (!Allowed(a.CardCodes, b.CardCode)) return "Customer not eligible";
        if (!Allowed(a.CustomerGroups, b.CustomerGroup)) return "Customer group not eligible";
        if (!Allowed(a.Branches, b.Branch)) return "Branch not eligible";
        if (!Allowed(a.PriceLists, b.PriceList)) return "Price list not eligible";

        if (!string.IsNullOrEmpty(p.CouponCode)
            && !Array.Exists(b.Coupons, c => string.Equals(c, p.CouponCode, StringComparison.OrdinalIgnoreCase)))
            return $"Coupon {p.CouponCode} not presented";

        if (p.Cap <= 0) return "Budget used up";
        return null;
    }

    static string DocumentName(string documentType) => documentType switch
    {
        "OQUT" => "Quotations", "ORDR" => "Sales Orders", "ODLN" => "Deliveries", "OINV" => "Invoices",
        _ => documentType,
    };

    static bool Allowed(string[] allowed, string? value) =>
        allowed.Length == 0
        || (value is not null && Array.Exists(allowed, a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase)));

    static DateTime EndOf(DateTime to) => to.TimeOfDay == TimeSpan.Zero ? to.Date.AddDays(1).AddTicks(-1) : to;
}
