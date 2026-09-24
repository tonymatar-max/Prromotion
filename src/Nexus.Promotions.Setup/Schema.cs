namespace Nexus.Promotions.Setup;

/// <summary>
/// Every B1 metadata object APE needs (PRD section 7). The engine's Promotion model maps onto these:
/// header fields on @APE_PROMO, item scopes on @APE_PROMO_SCP, tiers on @APE_PROMO_TIER, audience on @APE_PROMO_AUD.
/// </summary>
public static class Schema
{
    public sealed record Table(string Name, string Description, string Type);

    public sealed record ValidValue(string Value, string Description);

    public sealed record Field(string Table, string Name, string Description, string Type, string SubType = "st_None",
        int Size = 0, string? Default = null, ValidValue[]? Values = null)
    {
        /// <summary>User-table fields are addressed as "@TABLE" in UserFieldsMD.</summary>
        public string MdTable => Table.StartsWith("APE_") ? "@" + Table : Table;
    }

    public sealed record Udo(string Code, string Name, string Table, string[] Children, string[] FindColumns);

    public static readonly Table[] Tables =
    [
        new("APE_CAMP", "APE Campaigns", "bott_MasterData"),
        new("APE_PROMO", "APE Promotions", "bott_MasterData"),
        new("APE_PROMO_SCP", "APE Promotion Item Scope", "bott_MasterDataLines"),
        new("APE_PROMO_TIER", "APE Promotion Tiers", "bott_MasterDataLines"),
        new("APE_PROMO_AUD", "APE Promotion Audience", "bott_MasterDataLines"),
        new("APE_COUPON", "APE Coupons", "bott_MasterData"),
        new("APE_LEDGER", "APE Promotion Ledger", "bott_NoObjectAutoIncrement"),
        new("APE_OVERRIDE", "APE Promotion Overrides", "bott_NoObjectAutoIncrement"),
    ];

    static readonly ValidValue[] YesNo = [new("Y", "Yes"), new("N", "No")];

    static Field Alpha(string t, string n, string d, int size, string? def = null, ValidValue[]? vv = null) =>
        new(t, n, d, "db_Alpha", "st_None", size, def, vv);
    static Field Int(string t, string n, string d, string? def = null) => new(t, n, d, "db_Numeric", "st_None", 11, def);
    static Field Date(string t, string n, string d) => new(t, n, d, "db_Date");
    static Field Time(string t, string n, string d) => new(t, n, d, "db_Date", "st_Time");
    static Field Float(string t, string n, string d, string sub) => new(t, n, d, "db_Float", sub);

    /// <summary>Marketing document tables. B1 shares document UDFs, so OINV/INV1 come first; the rest are checked and filled if missing.</summary>
    public static readonly string[] DocumentHeaders = ["OINV", "OQUT", "ORDR", "ODLN", "ORIN", "ORDN", "ODRF"];
    public static readonly string[] DocumentLines = ["INV1", "QUT1", "RDR1", "DLN1", "RIN1", "RDN1", "DRF1"];

    public static IEnumerable<Field> Fields()
    {
        const string P = "APE_PROMO";
        yield return Int(P, "Version", "Version", "1");
        yield return Alpha(P, "NameAR", "Name (Arabic)", 100);
        yield return Alpha(P, "Type", "Promotion type", 20, "ItemDiscount",
        [
            new("ItemDiscount", "P01 Item discount"), new("FixedPrice", "P02 Fixed price"),
            new("QuantityTier", "P03 Quantity tier"), new("BuyXGetXFree", "P04 Buy X get X"),
            new("BuyXGetY", "P05 Buy X get Y"), new("MixAndMatch", "P06 Mix and match"),
            new("BasketThreshold", "P08/P09 Spend threshold"),
        ]);
        yield return Int(P, "Priority", "Priority (1 = highest)", "100");
        yield return Alpha(P, "Stacking", "Stacking", 1, "S", [new("S", "Stackable"), new("E", "Exclusive")]);
        yield return Alpha(P, "Status", "Status", 1, "D",
        [
            new("D", "Draft"), new("P", "Pending approval"), new("A", "Active"),
            new("S", "Paused"), new("E", "Expired"), new("C", "Cancelled"),
        ]);
        yield return Date(P, "ValidFrom", "Valid from");
        yield return Time(P, "ValidFromT", "Valid from time");
        yield return Date(P, "ValidTo", "Valid to");
        yield return Time(P, "ValidToT", "Valid to time");
        yield return Alpha(P, "Weekdays", "Weekdays (1=Mon..7=Sun)", 7);
        yield return Time(P, "TimeFrom", "Happy hour from");
        yield return Time(P, "TimeTo", "Happy hour to");
        yield return Alpha(P, "Coupon", "Coupon code", 50);
        // Comma list of document types this promotion auto-applies to (OQUT,ORDR,ODLN,OINV); empty = all screens.
        yield return Alpha(P, "Documents", "Document screens (blank = all)", 40);
        yield return Float(P, "BuyQty", "Buy quantity (X / N)", "st_Quantity");
        yield return Float(P, "GetQty", "Get quantity (Y)", "st_Quantity");
        yield return Alpha(P, "RewardItem", "Reward item (added)", 50);
        yield return Alpha(P, "FreeMode", "Reward units", 1, "B", [new("B", "From basket"), new("N", "Add new line")]);
        yield return Alpha(P, "RewardUnits", "Reward goes to", 1, "C", [new("C", "Cheapest"), new("D", "Dearest")]);
        yield return Alpha(P, "RewardKind", "Reward", 1, "P",
            [new("P", "% off"), new("A", "Amount off"), new("F", "Fixed price"), new("X", "Free")]);
        yield return Float(P, "RewardValue", "Reward value", "st_Price");
        yield return Int(P, "MaxApps", "Max applications per doc");
        yield return Float(P, "MaxDisc", "Max discount per doc", "st_Sum");
        yield return Float(P, "Budget", "Budget", "st_Sum");
        yield return Float(P, "BudgetUsed", "Budget used", "st_Sum");
        yield return Alpha(P, "AllowBelow", "Allow below min price", 1, "N", YesNo);
        yield return Alpha(P, "Campaign", "Campaign", 50);
        yield return Alpha(P, "Supplier", "Funding supplier", 15);
        yield return Float(P, "SupplierShr", "Supplier share %", "st_Percentage");

        const string S = "APE_PROMO_SCP";
        yield return Alpha(S, "Role", "Role", 1, "T", [new("T", "Trigger / discounted"), new("R", "Reward")]);
        yield return Alpha(S, "ScopeType", "Scope type", 1, "I",
            [new("I", "Item"), new("G", "Item group"), new("M", "Manufacturer"), new("P", "Item property")]);
        yield return Alpha(S, "Value", "Value", 50);
        yield return Alpha(S, "Exclude", "Exclude", 1, "N", YesNo);

        const string T = "APE_PROMO_TIER";
        yield return Float(T, "From", "From (qty or amount)", "st_Quantity");
        yield return Float(T, "Value", "Reward value", "st_Price");

        const string A = "APE_PROMO_AUD";
        yield return Alpha(A, "Dimension", "Dimension", 4, "GRP",
        [
            new("CH", "Channel"), new("CARD", "Customer"), new("GRP", "Customer group"),
            new("BR", "Branch"), new("PL", "Price list"),
        ]);
        yield return Alpha(A, "Value", "Value", 50);

        const string C = "APE_CAMP";
        yield return Alpha(C, "NameAR", "Name (Arabic)", 100);
        yield return Date(C, "ValidFrom", "Valid from");
        yield return Date(C, "ValidTo", "Valid to");
        yield return Float(C, "Budget", "Budget", "st_Sum");
        yield return Float(C, "BudgetUsed", "Budget used", "st_Sum");
        yield return Alpha(C, "Status", "Status", 1, "D", [new("D", "Draft"), new("A", "Active"), new("C", "Closed")]);

        const string K = "APE_COUPON";
        yield return Alpha(K, "Promo", "Promotion", 50);
        yield return Alpha(K, "Type", "Type", 1, "G", [new("G", "Generic code"), new("S", "Serial voucher")]);
        yield return Date(K, "ValidFrom", "Valid from");
        yield return Date(K, "ValidTo", "Valid to");
        yield return Int(K, "MaxUses", "Max uses");
        yield return Int(K, "UsedCount", "Used count", "0");
        yield return Alpha(K, "CardCode", "Customer", 15);
        yield return Alpha(K, "Status", "Status", 1, "A", [new("A", "Active"), new("U", "Used"), new("X", "Cancelled")]);

        const string L = "APE_LEDGER";
        yield return Alpha(L, "DocType", "Document type", 20);
        yield return Int(L, "DocEntry", "DocEntry");
        yield return Int(L, "LineNum", "Line");
        yield return Alpha(L, "Promo", "Promotion", 50);
        yield return Int(L, "Version", "Version");
        yield return Alpha(L, "Coupon", "Coupon", 50);
        yield return Alpha(L, "CardCode", "Customer", 15);
        yield return Float(L, "Qty", "Quantity", "st_Quantity");
        yield return Float(L, "GrossAmt", "Gross amount", "st_Sum");
        yield return Float(L, "DiscAmt", "Discount amount", "st_Sum");
        yield return Float(L, "SupplierAmt", "Supplier-funded amount", "st_Sum");
        yield return Alpha(L, "Channel", "Channel", 10);
        yield return Alpha(L, "UserCode", "User", 25);
        yield return Alpha(L, "CreatedAt", "Created at (UTC)", 19);
        yield return Alpha(L, "Reversed", "Reversed", 1, "N", YesNo);

        const string O = "APE_OVERRIDE";
        yield return Alpha(O, "DocType", "Document type", 20);
        yield return Int(O, "DocEntry", "DocEntry");
        yield return Int(O, "LineNum", "Line");
        yield return Alpha(O, "Promo", "Promotion", 50);
        yield return Alpha(O, "Action", "Action", 1, "R", [new("R", "Removed"), new("A", "Added"), new("C", "Changed")]);
        yield return Alpha(O, "Reason", "Reason", 100);
        yield return Alpha(O, "UserCode", "User", 25);
        yield return Alpha(O, "CreatedAt", "Created at (UTC)", 19);

        foreach (var h in DocumentHeaders)
        {
            yield return Alpha(h, "APE_Hash", "APE result hash", 64);
            yield return Alpha(h, "APE_Status", "APE status", 10, null,
                [new("Pending", "Pending"), new("Applied", "Applied"), new("Failed", "Failed"), new("Skipped", "Skipped")]);
            yield return Alpha(h, "APE_Mode", "APE mode", 1, null, [new("A", "Before save (add-on)"), new("B", "After save (worker)")]);
            yield return Alpha(h, "APE_Coupons", "APE coupons", 254);
            yield return Alpha(h, "APE_EvalAt", "APE evaluated at (UTC)", 19);
        }
        foreach (var l in DocumentLines)
        {
            yield return Alpha(l, "APE_Promo", "APE promotions", 254);
            yield return Int(l, "APE_Ver", "APE promotion version");
            yield return Float(l, "APE_DiscAmt", "APE discount amount", "st_Sum");
            yield return Alpha(l, "APE_Free", "APE free line", 1, "N", YesNo);
            yield return Alpha(l, "APE_Group", "APE group", 50);
        }

        yield return Float("OITM", "APE_MinPrice", "APE minimum price", "st_Price");
    }

    public static readonly Udo[] Udos =
    [
        new("APE_CAMP", "APE Campaign", "APE_CAMP", [], ["Code", "Name", "U_Status"]),
        new("APE_PROMO", "APE Promotion", "APE_PROMO", ["APE_PROMO_SCP", "APE_PROMO_TIER", "APE_PROMO_AUD"],
            ["Code", "Name", "U_Type", "U_Status", "U_Campaign"]),
        new("APE_COUPON", "APE Coupon", "APE_COUPON", [], ["Code", "Name", "U_Promo", "U_Status"]),
    ];
}
