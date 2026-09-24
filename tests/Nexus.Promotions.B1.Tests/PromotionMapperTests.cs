using System.Text.Json.Nodes;
using Nexus.Promotions.B1;
using Nexus.Promotions.Engine;

namespace Nexus.Promotions.B1.Tests;

public class PromotionMapperTests
{
    // Trimmed from the Service Layer's actual answer for APE_PROMO('APE-DEMO-10') on SBODemoHO.
    const string Demo = """
        {
          "Code": "APE-DEMO-10", "Name": "APE demo: 10% off A00001",
          "U_Version": 1, "U_NameAR": null, "U_Type": "ItemDiscount", "U_Priority": 100, "U_Stacking": "S", "U_Status": "A",
          "U_ValidFrom": null, "U_ValidFromT": null, "U_ValidTo": null, "U_ValidToT": null, "U_Weekdays": null,
          "U_TimeFrom": null, "U_TimeTo": null, "U_Coupon": null, "U_BuyQty": 0.0, "U_GetQty": 0.0, "U_RewardItem": null,
          "U_FreeMode": "B", "U_RewardUnits": "C", "U_RewardKind": "P", "U_RewardValue": 10.0, "U_MaxApps": null,
          "U_MaxDisc": 0.0, "U_Budget": 0.0, "U_BudgetUsed": 0.0, "U_AllowBelow": "N",
          "APE_PROMO_SCPCollection": [ { "LineId": 1, "U_Role": "T", "U_ScopeType": "I", "U_Value": "A00001", "U_Exclude": "N" } ],
          "APE_PROMO_TIERCollection": [], "APE_PROMO_AUDCollection": []
        }
        """;

    [Fact]
    public void Maps_the_demo_promotion_and_reads_zero_as_no_limit()
    {
        var p = PromotionMapper.Map(JsonNode.Parse(Demo)!);

        Assert.Equal("APE-DEMO-10", p.Code);
        Assert.Equal(PromotionType.ItemDiscount, p.Type);
        Assert.True(p.Active);
        Assert.Equal(["A00001"], p.Scope.ItemCodes);
        Assert.Null(p.RewardScope);
        Assert.Equal(RewardKind.PercentOff, p.RewardKind);
        Assert.Equal(10m, p.RewardValue);
        Assert.Null(p.MaxDiscountPerDocument);
        Assert.Empty(p.DocumentTypes);   // U_Documents absent from the JSON: every screen
        Assert.Null(p.BudgetRemaining);
        Assert.Null(p.MaxApplicationsPerDocument);
        Assert.Empty(p.Weekdays);
    }

    [Fact]
    public void Maps_schedule_scopes_tiers_audience_and_budget()
    {
        var node = JsonNode.Parse(Demo)!;
        node["U_Type"] = "BuyXGetY";
        node["U_Stacking"] = "E";
        node["U_ValidFrom"] = "2027-02-17T00:00:00Z";
        node["U_ValidToT"] = "22:00:00";
        node["U_ValidTo"] = "2027-03-18T00:00:00Z";
        node["U_Weekdays"] = "567";
        node["U_TimeFrom"] = 2200;          // B1 may send times as HHmm numbers
        node["U_TimeTo"] = "02:00:00";
        node["U_FreeMode"] = "N";
        node["U_RewardKind"] = "X";
        node["U_RewardItem"] = "TOTE";
        node["U_Budget"] = 500.0;
        node["U_BudgetUsed"] = 120.0;
        node["U_MaxApps"] = 3;
        node["U_Documents"] = " ORDR, ODLN ";
        node["APE_PROMO_SCPCollection"] = JsonNode.Parse("""
            [ { "U_Role": "T", "U_ScopeType": "G", "U_Value": "100", "U_Exclude": "N" },
              { "U_Role": "T", "U_ScopeType": "I", "U_Value": "A00009", "U_Exclude": "Y" },
              { "U_Role": "T", "U_ScopeType": "P", "U_Value": "12", "U_Exclude": "N" },
              { "U_Role": "R", "U_ScopeType": "M", "U_Value": "4", "U_Exclude": "N" } ]
            """);
        node["APE_PROMO_TIERCollection"] = JsonNode.Parse("""[ { "U_From": 50.0, "U_Value": 5.0 }, { "U_From": 100.0, "U_Value": 10.0 } ]""");
        node["APE_PROMO_AUDCollection"] = JsonNode.Parse("""[ { "U_Dimension": "GRP", "U_Value": "100" }, { "U_Dimension": "CH", "U_Value": "POS" } ]""");

        var p = PromotionMapper.Map(node);

        Assert.Equal(StackClass.Exclusive, p.Stacking);
        Assert.Equal(new DateTime(2027, 2, 17), p.ValidFrom);
        Assert.Equal(new DateTime(2027, 3, 18, 22, 0, 0), p.ValidTo);
        Assert.Equal([DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday], p.Weekdays);
        Assert.Equal(new TimeOnly(22, 0), p.TimeFrom);
        Assert.Equal(new TimeOnly(2, 0), p.TimeTo);
        Assert.Equal(["100"], p.Scope.ItemGroups);
        Assert.Equal(["A00009"], p.Scope.ExcludeItemCodes);
        Assert.Equal([12], p.Scope.Properties);
        Assert.Equal(["4"], p.RewardScope!.Manufacturers);
        Assert.Equal(FreeItemMode.AddNew, p.FreeMode);
        Assert.Equal(RewardKind.Free, p.RewardKind);
        Assert.Equal(2, p.Tiers.Length);
        Assert.Equal(["100"], p.Audience.CustomerGroups);
        Assert.Equal(["POS"], p.Audience.Channels);
        Assert.Equal(380m, p.BudgetRemaining);
        Assert.Equal(3, p.MaxApplicationsPerDocument);
        Assert.Equal(["ORDR", "ODLN"], p.DocumentTypes);
    }

    [Fact]
    public void Draft_promotion_is_inactive()
    {
        var node = JsonNode.Parse(Demo)!;
        node["U_Status"] = "D";
        Assert.False(PromotionMapper.Map(node).Active);
    }
}
